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
/// Posts and friends, as the JavaScript page makes them (sharing its settings, keys and friend list in the browser).
/// POSTS: each month is a public chain everyone keeps ("square"): posts, replies (comments on a post) and likes (a vote,
/// paid to the author at the like price; only paid ones count).
/// FRIENDS: your friends page is a "circle" chain carrying your encryption key (ECDH P-256). A post there is SEALED:
/// encrypted with a fresh key, and that key wrapped for you and each chosen friend with AES-GCM under the ECDH key
/// between your encryption key and theirs (each wrap tagged sha256("opentube-seal:" + their key)[..24]), so only they
/// can read it. Replies are sealed to the same people. The friend list stays on your device (settings.friends).
/// </summary>
public sealed class Social
{
    readonly ClientSession s; readonly IClientStore store;
    static readonly ECDomainParameters Domain = new(NistNamedCurves.GetByName("P-256"));
    public event Action? Changed;
    public Social(ClientSession s, IClientStore store) { this.s = s; this.store = store; }

    async Task<JsonObject> Settings() => await store.Get("settings") is { } j && JsonNode.Parse(j) is JsonObject o ? o : [];
    async Task SaveSettings(JsonObject o) { await store.Set("settings", o.ToJsonString()); await Refresh(); Changed?.Invoke(); }
    /// <summary>Tell notifications whose posts to watch: friends and followed people, and friends' pages.</summary>
    public async Task Refresh()
    {
        var st = await Settings(); var fr = await Friends();
        s.Notes.Following = [.. fr.Select(f => f.Key), .. (st["following"] as JsonArray ?? []).Select(x => (string?)x).OfType<string>()];
        s.Notes.FriendPages = [.. fr.Select(f => CircleOf(f.Key)).OfType<string>()];
    }
    /// <summary>Tags and ad categories you're not interested in (settings.muted, kept only on this device).</summary>
    public async Task<(HashSet<string> Tags, HashSet<string> Cats)> Muted()
    {
        var m = (await Settings())["muted"] as JsonObject;
        HashSet<string> Of(string k) => (m?[k] as JsonArray ?? []).Select(x => (string?)x).OfType<string>().ToHashSet();
        return (Of("tags"), Of("cats"));
    }
    public async Task SetMuted(string kind, string value, bool on)
    {
        var st = await Settings(); var m = st["muted"] as JsonObject ?? new JsonObject { ["tags"] = new JsonArray(), ["cats"] = new JsonArray() }; st["muted"] = m;
        var list = (m[kind] as JsonArray ?? []).Select(x => (string?)x).OfType<string>().ToList(); list.Remove(value); if (on) list.Add(value);
        m[kind] = new JsonArray(list.Select(x => (JsonNode?)x).ToArray());
        await SaveSettings(st);
    }
    public async Task<string> MyName() => (string?)(await Settings())["name"] is { Length: > 0 } n ? n : "viewer-" + s.Me.pub[..6];

    // ---------------------------------------------------------------- posts (the monthly public chains)
    public static string Month(DateTimeOffset t) => t.ToString("yyyy-MM");
    public static string PrevMonth(string m) => DateTimeOffset.ParseExact(m + "-01", "yyyy-MM-dd", null).AddMonths(-1).ToString("yyyy-MM");
    public string? SquareId(string m) => s.Engine.Read(e => ChainStore.SquareMonths(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 40 * 86400000.0).Contains(m)) ? (string?)ChainStore.SquareGenesis(m, "square")["hash"] : null;
    /// <summary>A month's posts chain (opened, and asked for from peers the first time).</summary>
    public Chain? Square(string m)
    {
        if (SquareId(m) is not { } id) return null;
        var c = s.Engine.OpenChain(id); if (c != null && c.Blocks.Count <= 1) s.Engine.RequestChain(c); return c;
    }
    public sealed record Post(string Chain, JsonObject Block, string Author, string Name, string Text, double Ts, int Likes, int Replies, bool Mine);
    public List<Post> Posts(string month, bool popular = false)
    {
        if (Square(month) is not { } c) return [];
        var list = s.Engine.Read(e =>
        {
            var d = Derived.Of(c);
            var replies = c.Blocks.Values.Where(b => (string?)b["payload"]?["type"] == "comment").GroupBy(b => (string?)b["payload"]?["target"] ?? "").ToDictionary(g => g.Key, g => g.Count());
            return d.Posts.Where(b => !d.Hidden.Contains((string)b["hash"]!)).Select(b => Make(c, d, b, replies.GetValueOrDefault((string)b["hash"]!))).ToList();
        });
        double Score(Post p) => (p.Likes + 2.0 * p.Replies + 1) / Math.Pow((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - p.Ts) / 3600000 + 2, 1.5);
        return popular ? list.OrderByDescending(Score).ToList() : list.OrderByDescending(p => p.Ts).ToList();
    }
    public List<Post> Replies(string month, string post)
    {
        if (Square(month) is not { } c) return [];
        return s.Engine.Read(e => { var d = Derived.Of(c); return c.Blocks.Values.Where(b => (string?)b["payload"]?["type"] == "comment" && (string?)b["payload"]?["target"] == post && !d.Hidden.Contains((string)b["hash"]!)).Select(b => Make(c, d, (JsonObject)b, 0)).OrderBy(p => p.Ts).ToList(); });
    }
    public Post? PostOf(string month, string hash)
    {
        if (Square(month) is not { } c || !c.Blocks.TryGetValue(hash, out var b)) return null;
        return s.Engine.Read(e => Make(c, Derived.Of(c), b, 0));
    }
    Post Make(Chain c, Derived d, JsonObject b, int replies)
    {
        var author = (string)b["author"]!; var p = b["payload"]!;
        var likes = d.Votes.TryGetValue((string)b["hash"]!, out var t) ? t.Blocks.Count(v => (Js.Num(v["payload"]?["v"]) ?? 0) > 0 && s.Engine.VotePaid(v)) : 0;
        var name = s.NameOf(author); if (name.Length <= 8 && (string?)p["name"] is { Length: > 0 } n) name = n;
        return new Post(c.Id, (JsonObject)b.DeepClone(), author, name, (string?)p["text"] ?? "", Js.Num(b["ts"]) ?? 0, likes, replies, s.IsMine(author));
    }
    /// <summary>A public post (or a reply to one): anti-spam proof of work, then it's on the month's chain for good.</summary>
    public async Task PostTo(string month, string text, string? replyTo = null)
    {
        text = text.Trim(); if (text.Length == 0) throw new ArgumentException("Write something first");
        if (text.Length > 1000) throw new ArgumentException("At most 1000 characters");
        var c = Square(month) ?? throw new InvalidOperationException("No public feed for that month");
        var payload = new JsonObject { ["type"] = replyTo != null ? "comment" : "post", ["text"] = text, ["name"] = (await MyName())[..Math.Min(60, (await MyName()).Length)] };
        if (replyTo != null) payload["target"] = replyTo;
        await Task.Run(() => s.Engine.Append(c.Id, payload, s.Me)); Changed?.Invoke();
    }
    /// <summary>Like a post: a vote, and its payment to the author (only paid likes count).</summary>
    public async Task Like(string chain, JsonObject post)
    {
        var author = (string)post["author"]!; if (s.IsMine(author)) throw new InvalidOperationException("That’s yours");
        var vb = await Task.Run(() => s.Engine.Append(chain, new JsonObject { ["type"] = "vote", ["target"] = (string)post["hash"]!, ["v"] = 1 }, s.Me));
        var price = s.Engine.Read(e => Math.Max(1, Math.Ceiling(e.Store.Ledger.Prices["like"])));   // whole units (priceOf)
        await s.Engine.SubmitTx(new JsonObject { ["vote"] = new JsonObject { ["chain"] = chain, ["target"] = (string)vb["hash"]!, ["to"] = author, ["v"] = 1, ["amt"] = price } }, s.Me);
        Changed?.Invoke();
    }
    public async Task Delete(string chain, string hash) { await Task.Run(() => s.Engine.Append(chain, new JsonObject { ["type"] = "hide", ["target"] = hash }, s.Me)); Changed?.Invoke(); }

    // ---------------------------------------------------------------- friends: the encryption key, sealing
    (ECPrivateKeyParameters Priv, string Pub)? enc;
    /// <summary>Your encryption key (identity.enc: {priv (JWK), pub}), made the first time.</summary>
    public async Task<string> EncKey()
    {
        if (enc is { } e0) return e0.Pub;
        var id = (await store.Get("identity") is { } j ? JsonNode.Parse(j) as JsonObject : null) ?? throw new InvalidOperationException("no identity");
        if (id["enc"] is not JsonObject e)
        {
            var g = new ECKeyPairGenerator(); g.Init(new ECKeyGenerationParameters(Domain, new SecureRandom()));
            var kp = g.GenerateKeyPair(); var pub = (ECPublicKeyParameters)kp.Public; var pr = (ECPrivateKeyParameters)kp.Private;
            byte[] X = Pad(pub.Q.Normalize().AffineXCoord.GetEncoded()), Y = Pad(pub.Q.Normalize().AffineYCoord.GetEncoded());
            e = new JsonObject
            {
                ["priv"] = new JsonObject { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = B64u.Encode(X), ["y"] = B64u.Encode(Y), ["d"] = B64u.Encode(Pad(pr.D.ToByteArrayUnsigned())), ["ext"] = true, ["key_ops"] = new JsonArray("deriveKey") },
                ["pub"] = B64u.Encode([0x04, .. X, .. Y]),
            };
            id["enc"] = e; await store.Set("identity", id.ToJsonString());
        }
        enc = (new ECPrivateKeyParameters(new BigInteger(1, B64u.Decode((string)e["priv"]!["d"]!)), Domain), (string)e["pub"]!);
        return enc.Value.Pub;
    }
    static byte[] Pad(byte[] b) => b.Length >= 32 ? b[^32..] : [.. new byte[32 - b.Length], .. b];
    readonly Dictionary<string, byte[]> shared = [];
    byte[] SharedWith(string pubB64u)
    {
        lock (shared)
        {
            if (shared.TryGetValue(pubB64u, out var k)) return k;
            var a = new ECDHBasicAgreement(); a.Init(enc!.Value.Priv);
            k = Pad(a.CalculateAgreement(new ECPublicKeyParameters(Domain.Curve.DecodePoint(B64u.Decode(pubB64u)), Domain)).ToByteArrayUnsigned());
            return shared[pubB64u] = k;   // (WebCrypto's deriveKey to AES-256: the raw x coordinate is the key)
        }
    }
    static byte[] Gcm(bool encrypt, byte[] key, byte[] iv, byte[] data)
    {
        var g = new GcmBlockCipher(new AesEngine()); g.Init(encrypt, new AeadParameters(new KeyParameter(key), 128, iv));
        var o = new byte[g.GetOutputSize(data.Length)]; var n = g.ProcessBytes(data, 0, data.Length, o, 0); g.DoFinal(o, n); return o;
    }
    static string TagOf(string pubB64u) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("opentube-seal:" + pubB64u)))[..24];
    /// <summary>seal(content, recipients): { iv, ct, keys: [{ t, iv, w }] } (you are always among the recipients).</summary>
    async Task<JsonObject> Seal(JsonObject content, IEnumerable<string> recipientEncs)
    {
        var mine = await EncKey();
        var raw = RandomNumberGenerator.GetBytes(32); var iv = RandomNumberGenerator.GetBytes(12);
        var ct = Gcm(true, raw, iv, Encoding.UTF8.GetBytes(content.ToJsonString()));
        var keys = new JsonArray();
        foreach (var pub in new[] { mine }.Concat(recipientEncs).Distinct())
        {
            var wiv = RandomNumberGenerator.GetBytes(12);
            keys.Add(new JsonObject { ["t"] = TagOf(pub), ["iv"] = B64u.Encode(wiv), ["w"] = B64u.Encode(Gcm(true, SharedWith(pub), wiv, raw)) });
        }
        return new JsonObject { ["iv"] = B64u.Encode(iv), ["ct"] = B64u.Encode(ct), ["keys"] = keys };
    }
    readonly Dictionary<string, JsonObject?> opened = [];
    async Task<JsonObject?> Unseal(JsonObject block)
    {
        var hash = (string)block["hash"]!; lock (opened) if (opened.TryGetValue(hash, out var o)) return o;
        var mine = await EncKey(); var p = block["payload"]!;
        JsonObject? got = null;
        try
        {
            var authorEnc = EncOf((string)block["author"]!);
            var k = (p["keys"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(x => (string?)x["t"] == TagOf(mine));
            if (authorEnc != null && k != null)
            {
                var raw = Gcm(false, SharedWith(authorEnc), B64u.Decode((string)k["iv"]!), B64u.Decode((string)k["w"]!));
                got = JsonNode.Parse(Encoding.UTF8.GetString(Gcm(false, raw, B64u.Decode((string)p["iv"]!), B64u.Decode((string)p["ct"]!)))) as JsonObject;
            }
        }
        catch { }
        lock (opened) opened[hash] = got; return got;
    }

    // ---------------------------------------------------------------- friends pages and the friend list
    /// <summary>Someone's friends page ("circle" chain) id, from the registered chains (or your own, waiting to be).</summary>
    public string? CircleOf(string key) => s.Engine.Read(e =>
    {
        foreach (var (id, d) in e.Store.Directory) if ((string?)d.Genesis["payload"]?["kind"] == "circle" && (string?)d.Genesis["author"] is { } a && (a == key || e.Store.Ledger.Resolve(a) == key)) return id;
        foreach (var g in e.Store.Mempool.Values) if ((string?)g["payload"]?["kind"] == "circle" && (string?)g["author"] == key) return (string?)g["hash"];
        return null;
    });
    string? EncOf(string key)
    {
        var id = CircleOf(key) ?? CircleOf(s.Engine.Read(e => e.Store.Ledger.Resolve(key)));
        return id == null ? null : (string?)s.Genesis(id)?["payload"]?["enc"];
    }
    public string? MyPage => CircleOf(s.Me.pub);
    /// <summary>Start your friends page (registers a circle chain carrying your encryption key).</summary>
    public async Task<string> StartPage()
    {
        if (MyPage is { } id) return id;
        var enc0 = await EncKey(); var name = await MyName();
        var g = await s.CreateGenesis(new JsonObject { ["type"] = "genesis", ["kind"] = "circle", ["name"] = name[..Math.Min(60, name.Length)], ["about"] = "", ["enc"] = enc0 });
        return (string)g["hash"]!;
    }
    public sealed record Friend(string Key, string Name, string Level, bool HasPage);
    public static readonly (string Key, string Label)[] Levels = [("close", "Close friends"), ("friends", "Friends"), ("acquaintances", "Acquaintances")];
    public async Task<List<Friend>> Friends() => ((await Settings())["friends"] as JsonArray ?? []).OfType<JsonObject>()
        .Select(f => new Friend((string?)f["key"] ?? "", (string?)f["name"] is { Length: > 0 } n ? n : s.NameOf((string?)f["key"] ?? ""), (string?)f["level"] ?? "friends", CircleOf((string?)f["key"] ?? "") != null))
        .Where(f => f.Key.Length > 0).ToList();
    public async Task AddFriend(string key, string name, string level = "friends")
    {
        key = key.Trim(); if (!Js.IsKey(JsonValue.Create(key)) || key == s.Me.pub) throw new ArgumentException("Paste your friend’s user ID");
        var st = await Settings(); var list = st["friends"] as JsonArray ?? (JsonArray)(st["friends"] = new JsonArray());
        if (!list.OfType<JsonObject>().Any(f => (string?)f["key"] == key)) list.Add(new JsonObject { ["key"] = key, ["name"] = name.Trim()[..Math.Min(60, name.Trim().Length)], ["level"] = level });
        await SaveSettings(st);
    }
    public async Task SetLevel(string key, string level) { var st = await Settings(); foreach (var f in (st["friends"] as JsonArray ?? []).OfType<JsonObject>()) if ((string?)f["key"] == key) f["level"] = level; await SaveSettings(st); }
    public async Task RemoveFriend(string key)
    {
        var st = await Settings(); var list = st["friends"] as JsonArray ?? [];
        foreach (var f in list.OfType<JsonObject>().Where(f => (string?)f["key"] == key).ToList()) list.Remove(f);
        await SaveSettings(st);
    }
    /// <summary>Post to friends: sealed to you and the friends in the audience (close ⊂ friends ⊂ acquaintances) who have a page.</summary>
    public async Task PostToFriends(string text, string audience)
    {
        text = text.Trim(); if (text.Length == 0) throw new ArgumentException("Write something first");
        var page = MyPage ?? throw new InvalidOperationException("Start your friends page first");
        int Rank(string l) => l switch { "close" => 0, "friends" => 1, _ => 2 };
        var pick = (await Friends()).Where(f => Rank(f.Level) <= Rank(audience)).Select(f => (f.Key, Enc: EncOf(f.Key))).Where(f => f.Enc != null).ToList();
        if (pick.Count == 0) throw new InvalidOperationException("nobody in that audience has a friends page yet");
        var content = new JsonObject { ["text"] = text, ["to"] = new JsonArray([s.Me.pub, .. pick.Select(f => (JsonNode?)f.Key)]), ["name"] = await MyName() };
        var payload = new JsonObject { ["type"] = "sealed" }; foreach (var (k, v) in await Seal(content, pick.Select(f => f.Enc!))) payload[k] = v?.DeepClone();
        await Task.Run(() => s.Engine.Append(page, payload, s.Me)); Changed?.Invoke();
    }
    /// <summary>A reply to a friends post: sealed to the same people, on the page it answers.</summary>
    public async Task ReplyToFriends(string page, JsonObject post, JsonObject content, string text)
    {
        var encs = (content["to"] as JsonArray ?? []).Select(k => (string?)k).OfType<string>().Select(k => k == s.Me.pub ? null : EncOf(k)).OfType<string>().ToList();
        var payload = new JsonObject { ["type"] = "sealed", ["target"] = (string)post["hash"]! };
        foreach (var (k, v) in await Seal(new JsonObject { ["text"] = text.Trim(), ["name"] = await MyName() }, encs)) payload[k] = v?.DeepClone();
        await Task.Run(() => s.Engine.Append(page, payload, s.Me)); Changed?.Invoke();
    }
    public sealed record Sealed(string Page, JsonObject Block, string Author, double Ts, JsonObject? Content, List<(string Author, double Ts, JsonObject? Content)> Replies);
    /// <summary>The friends feed: your page and your friends' pages, newest first (posts not shared with you show as locked).</summary>
    public async Task<List<Sealed>> Feed()
    {
        await EncKey();
        var pages = new List<string?> { MyPage }.Concat((await Friends()).Select(f => CircleOf(f.Key))).OfType<string>().Distinct().ToList();
        var items = new List<(string Page, JsonObject B, List<JsonObject> R)>();
        foreach (var id in pages)
        {
            var d = s.Chain(id); if (d == null) continue;
            foreach (var b in d.Sealed.Where(b => b["payload"]?["target"] == null && !d.Hidden.Contains((string)b["hash"]!)))
                items.Add((id, (JsonObject)b.DeepClone(), d.Sealed.Where(r => (string?)r["payload"]?["target"] == (string)b["hash"]! && !d.Hidden.Contains((string)r["hash"]!)).Select(r => (JsonObject)r.DeepClone()).OrderBy(r => Js.Num(r["ts"])).ToList()));
        }
        var o = new List<Sealed>();
        foreach (var (page, b, rs) in items.OrderByDescending(x => Js.Num(x.B["ts"])).Take(50))
        {
            var replies = new List<(string, double, JsonObject?)>();
            foreach (var r in rs) replies.Add(((string)r["author"]!, Js.Num(r["ts"]) ?? 0, await Unseal(r)));
            o.Add(new Sealed(page, b, (string)b["author"]!, Js.Num(b["ts"]) ?? 0, await Unseal(b), replies));
        }
        return o;
    }
    public async Task<string> FriendName(string key) => key == s.Me.pub ? "You" : (await Friends()).FirstOrDefault(f => f.Key == key)?.Name ?? s.NameOf(key);
}
