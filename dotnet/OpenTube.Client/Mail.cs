using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>
/// Private messages, as the JavaScript page writes and reads them (and sharing its keys and sent copies in the browser).
/// An identity publishes MESSAGING KEYS (ECDH P-256) with msgkey transactions. To write, the sender makes a one-time key
/// pair and does ECDH with the recipient's newest messaging key; the shared secret encrypts the message (AES-GCM) and,
/// hashed with a random salt, makes its TAG: sha256("opentube-mail:" + hex(secret) + ":" + salt). On the month's message
/// chain a message is just { tag, salt, eph, iv, ct, hints }, signed by a key used once: who it's from and whom it's for
/// aren't visible. Inside: { from, to, text, at, sig } with sig over "opentube-msg:" + JSON [to, text, at, eph].
/// The hints let the recipient's own computers notice it (MailHints) and push a notification.
/// (BouncyCastle for the elliptic curve and AES-GCM: the same code runs in the browser, on phones and on computers.)
/// </summary>
public sealed class Mail
{
    public sealed record Message(string From, string To, string Text, double At, bool Incoming, string Other, string? Hash);
    readonly ClientSession s; readonly IClientStore store;
    readonly Dictionary<string, Message?> seen = [];   // block hash -> a message for you, or null
    List<(string Pub, ECPrivateKeyParameters Priv, bool Retired)> keys = [];
    public event Action? Changed;
    static readonly ECDomainParameters Domain = new(NistNamedCurves.GetByName("P-256"));

    public Mail(ClientSession s, IClientStore store) { this.s = s; this.store = store; }
    /// <summary>Watch for messages while the app runs (new ones become notifications), and publish your messaging key.</summary>
    public void Start() => _ = Task.Run(async () =>
    {
        await Task.Delay(8000);
        try { await Publish(); } catch { }
        while (true) { try { if (!s.Paused) { var n = seen.Count; await All(); if (seen.Count != n) Changed?.Invoke(); } } catch { } await Task.Delay(20000); }
    });

    // ---- your messaging keys: in the identity record ({pub, priv, msg: [{pub, priv (JWK), at, retired}]}), shared with the JavaScript page
    public async Task<List<string>> Keys()
    {
        var id = await store.Get("identity") is { } j ? JsonNode.Parse(j) as JsonObject : null;
        if (id == null) return [];
        if (id["msg"] is not JsonArray msg || msg.Count == 0)
        {
            var g = new ECKeyPairGenerator(); g.Init(new ECKeyGenerationParameters(Domain, new SecureRandom()));
            var kp = g.GenerateKeyPair(); var pub = (ECPublicKeyParameters)kp.Public; var pr = (ECPrivateKeyParameters)kp.Private;
            byte[] X = Pad(pub.Q.Normalize().AffineXCoord.GetEncoded()), Y = Pad(pub.Q.Normalize().AffineYCoord.GetEncoded());
            msg = [new JsonObject
            {
                ["pub"] = B64u.Encode([0x04, .. X, .. Y]), ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["priv"] = new JsonObject { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = B64u.Encode(X), ["y"] = B64u.Encode(Y), ["d"] = B64u.Encode(Pad(pr.D.ToByteArrayUnsigned())), ["ext"] = true, ["key_ops"] = new JsonArray("deriveBits") },
            }];
            id["msg"] = msg; await store.Set("identity", id.ToJsonString());
        }
        keys = msg.OfType<JsonObject>().Select(k => ((string)k["pub"]!, new ECPrivateKeyParameters(new BigInteger(1, B64u.Decode((string)k["priv"]!["d"]!)), Domain), Js.IsTrue(k["retired"]))).ToList();
        return keys.Select(k => k.Pub).ToList();
    }
    static byte[] Pad(byte[] b) => b.Length >= 32 ? b[^32..] : [.. new byte[32 - b.Length], .. b];

    /// <summary>Make sure the network knows your messaging keys (a msgkey transaction for each one not yet published).</summary>
    public async Task Publish()
    {
        var mine = await Keys();
        var on = s.Engine.Read(e => e.Store.Ledger.MsgKeys.TryGetValue(s.Me.pub, out var l) ? l.ToList() : []);
        foreach (var k in keys.Where(k => !k.Retired && !on.Contains(k.Pub)))
            try { await s.Engine.SubmitTx(new JsonObject { ["msgkey"] = new JsonObject { ["key"] = k.Pub, ["on"] = true } }, s.Me); }
            catch (Exception e) { s.Log?.Invoke("msgkey: " + e.Message); }
    }
    /// <summary>Your messaging keys: (key, published on the network, retired).</summary>
    public async Task<List<(string Pub, bool Published, bool Retired)>> KeyList()
    {
        await Keys(); var on = s.Engine.Read(e => e.Store.Ledger.MsgKeys.TryGetValue(s.Me.pub, out var l) ? l.ToList() : []);
        return keys.Select(k => (k.Pub, on.Contains(k.Pub), k.Retired)).ToList();
    }
    /// <summary>Retire a messaging key that may have leaked: new messages use your others.</summary>
    public async Task RetireKey(string pub)
    {
        await s.Engine.SubmitTx(new JsonObject { ["msgkey"] = new JsonObject { ["key"] = pub, ["on"] = false } }, s.Me);
        var id = JsonNode.Parse((await store.Get("identity"))!) as JsonObject ?? []; foreach (var k in (id["msg"] as JsonArray ?? []).OfType<JsonObject>()) if ((string?)k["pub"] == pub) k["retired"] = true;
        await store.Set("identity", id.ToJsonString()); await Keys(); Changed?.Invoke();
    }
    /// <summary>A new messaging key (published; messages to you use the newest).</summary>
    public async Task NewKey()
    {
        var id = JsonNode.Parse((await store.Get("identity"))!) as JsonObject ?? [];
        var g = new ECKeyPairGenerator(); g.Init(new ECKeyGenerationParameters(Domain, new SecureRandom()));
        var kp = g.GenerateKeyPair(); var pub = (ECPublicKeyParameters)kp.Public; var pr = (ECPrivateKeyParameters)kp.Private;
        byte[] X = Pad(pub.Q.Normalize().AffineXCoord.GetEncoded()), Y = Pad(pub.Q.Normalize().AffineYCoord.GetEncoded());
        var list = id["msg"] as JsonArray ?? (JsonArray)(id["msg"] = new JsonArray());
        list.Add(new JsonObject { ["pub"] = B64u.Encode([0x04, .. X, .. Y]), ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["priv"] = new JsonObject { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = B64u.Encode(X), ["y"] = B64u.Encode(Y), ["d"] = B64u.Encode(Pad(pr.D.ToByteArrayUnsigned())), ["ext"] = true, ["key_ops"] = new JsonArray("deriveBits") } });
        await store.Set("identity", id.ToJsonString()); await Publish(); Changed?.Invoke();
    }
    public bool CanReceive(string who) => s.Engine.Read(e => e.Store.Ledger.MsgKeys.TryGetValue(e.Store.Ledger.Resolve(who), out var l) && l.Count > 0);

    // ---- the elliptic curve and the cipher
    static byte[] Ecdh(ECPrivateKeyParameters priv, string pubB64u)
    {
        var q = Domain.Curve.DecodePoint(B64u.Decode(pubB64u));
        var a = new ECDHBasicAgreement(); a.Init(priv);
        return Pad(a.CalculateAgreement(new ECPublicKeyParameters(q, Domain)).ToByteArrayUnsigned());
    }
    static byte[] Gcm(bool encrypt, byte[] key, byte[] iv, byte[] data)
    {
        var g = new GcmBlockCipher(new AesEngine()); g.Init(encrypt, new AeadParameters(new KeyParameter(key), 128, iv));
        var o = new byte[g.GetOutputSize(data.Length)]; var n = g.ProcessBytes(data, 0, data.Length, o, 0); g.DoFinal(o, n); return o;
    }
    static string Tag(byte[] secret, string salt) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("opentube-mail:" + Convert.ToHexStringLower(secret) + ":" + salt)));
    static string Signed(string to, string text, double at, string eph) => "opentube-msg:" + Blocks.Canon(new JsonArray(to, text, at, eph));

    static string Month(DateTimeOffset t) => t.ToString("yyyy-MM");
    Chain? MailChain(string m)
    {
        var id = (string?)ChainStore.SquareGenesis(m, "mail")["hash"]; if (id == null) return null;
        var c = s.Engine.OpenChain(id); if (c != null && c.Blocks.Count <= 1) s.Engine.RequestChain(c); return c;
    }

    /// <summary>Your messages (both directions), oldest first: this month's and last month's.</summary>
    public async Task<List<Message>> All()
    {
        if (keys.Count == 0) await Keys();
        var me = s.Me.pub; var list = new List<Message>();
        var now = DateTimeOffset.UtcNow;
        foreach (var m in new[] { Month(now), Month(now.AddMonths(-1)) })
        {
            var c = MailChain(m); if (c == null) continue;
            var blocks = s.Engine.Read(_ => c.Blocks.Values.Where(b => (string?)b["payload"]?["type"] == "msg").Select(b => (JsonObject)b.DeepClone()).ToList());
            foreach (var b in blocks)
            {
                var hash = (string)b["hash"]!;
                if (!seen.TryGetValue(hash, out var got)) { got = Open(b); seen[hash] = got; if (got != null && got.Incoming) s.Notes.Add("message", "You have a new private message", "#/messages", null, got.At, "msg:" + hash); }
                if (got != null) list.Add(got);
            }
        }
        foreach (var x in (await store.Get("sentMessages") is { } j ? JsonNode.Parse(j) as JsonArray ?? [] : []).OfType<JsonObject>())
            if ((string?)x["to"] is { } to && (string?)x["text"] is { } text)
                list.Add(new Message(me, to, text, Js.Num(x["at"]) ?? 0, false, s.Engine.Read(e => e.Store.Ledger.Resolve(to)), null));
        return list.OrderBy(x => x.At).ToList();
    }
    /// <summary>One message block: yours (decrypted, its signature checked), or null.</summary>
    Message? Open(JsonObject b)
    {
        var p = b["payload"]!; string? eph = (string?)p["eph"], salt = (string?)p["salt"], tag = (string?)p["tag"];
        if (eph == null || salt == null || tag == null) return null;
        foreach (var k in keys)
            try
            {
                var secret = Ecdh(k.Priv, eph);
                if (Tag(secret, salt) != tag) continue;   // not for this key
                var body = JsonNode.Parse(Encoding.UTF8.GetString(Gcm(false, secret, B64u.Decode((string)p["iv"]!), B64u.Decode((string)p["ct"]!)))) as JsonObject;
                if (body == null || (string?)body["from"] is not { } from || (string?)body["to"] is not { } to || (string?)body["text"] is not { } text || Js.Num(body["at"]) is not double at) return null;
                if (!Rules.VerifySig(from, Signed(to, text, at, eph), body["sig"])) return null;
                return new Message(from, to, text, at, true, s.Engine.Read(e => e.Store.Ledger.Resolve(from)), (string?)b["hash"]);
            }
            catch { }
        return null;
    }

    /// <summary>Write to someone (their user ID, or a channel owner's key): encrypted to their newest messaging key.</summary>
    public async Task Send(string to, string text)
    {
        text = text.Trim(); if (text.Length == 0) throw new ArgumentException("Write something first");
        if (text.Length > 20000) throw new ArgumentException("That’s too long for one message");
        var rid = s.Engine.Read(e => e.Store.Ledger.Resolve(to));
        var theirs = s.Engine.Read(e => e.Store.Ledger.MsgKeys.TryGetValue(rid, out var l) ? l.ToList() : []);
        if (theirs.Count == 0) throw new InvalidOperationException("They haven’t published a messaging key yet (they need to open Messages once)");
        var g = new ECKeyPairGenerator(); g.Init(new ECKeyGenerationParameters(Domain, new SecureRandom()));
        var kp = g.GenerateKeyPair(); var ephPub = (ECPublicKeyParameters)kp.Public; var ephPriv = (ECPrivateKeyParameters)kp.Private;
        var eph = B64u.Encode(ephPub.Q.GetEncoded(false));
        var secret = Ecdh(ephPriv, theirs[^1]); var salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var at = (double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var body = new JsonObject { ["from"] = s.Me.pub, ["to"] = to, ["text"] = text, ["at"] = at, ["sig"] = s.Me.Sign(Signed(to, text, at, eph)) };
        var iv = RandomNumberGenerator.GetBytes(12);
        var ct = Gcm(true, secret, iv, Encoding.UTF8.GetBytes(body.ToJsonString()));
        // hints for the recipient's computers (MailHints), padded to three
        var hints = s.Engine.Read(e => e.Nodes.Values.Where(n => ((string?)n["grant"]?["user"] == rid || n["info"]?["users"]?[rid] is JsonValue) && (string?)n["info"]?["notify"] is { Length: > 0 })
            .OrderByDescending(n => Js.Num(n["at"]) ?? 0).Take(MailHints.Count).Select(n => (string)n["info"]!["notify"]!).ToList())
            .Select(nk => { try { return MailHints.Of(Ecdh(ephPriv, nk), salt, rid); } catch { return null; } }).OfType<string>().ToList();
        while (hints.Count < MailHints.Count) hints.Add(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
        hints.Sort(StringComparer.Ordinal);
        var chain = MailChain(Month(DateTimeOffset.UtcNow)) ?? throw new InvalidOperationException("messages haven’t started yet");
        var payload = new JsonObject
        {
            ["type"] = "msg", ["tag"] = Tag(secret, salt), ["salt"] = salt, ["eph"] = eph, ["iv"] = B64u.Encode(iv), ["ct"] = B64u.Encode(ct),
            ["hints"] = new JsonArray(hints.Select(x => (JsonNode?)x).ToArray()),
        };
        await Task.Run(() => s.Engine.Append(chain.Id, payload, Identity.New()));   // signed by a key used once, not by you
        var sent = await store.Get("sentMessages") is { } j ? JsonNode.Parse(j) as JsonArray ?? [] : [];
        sent.Add(new JsonObject { ["from"] = s.Me.pub, ["to"] = to, ["text"] = text, ["at"] = at });
        while (sent.Count > 2000) sent.RemoveAt(0);
        await store.Set("sentMessages", sent.ToJsonString());   // your own copy stays on this device
        Changed?.Invoke();
    }
}
