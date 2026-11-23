using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>
/// Your keys, as the JavaScript page manages them (same files, same records: a backup made on either imports on the
/// other). Your personal key (your identity, with your encryption and messaging keys) and a key for each channel or
/// list exist only on your devices: whoever holds a channel's key controls it.
/// - Back up all keys: one file ("opentube-keys"); a single channel's key ("opentube-key").
/// - Import: a backup or a single key file; every key pair is checked before it's used.
/// - Binding: each channel key is bound to your identity (a bind transaction both sign), so coins paid to a channel
///   land in your balance. Automatic, every minute.
/// - Retire your personal key (if it may have leaked): a new one takes over your coins, channels and pages.
/// - Replace a channel's key: the chain is handed to a fresh key (a rekey block the new key agrees to).
/// </summary>
public sealed class Keys
{
    readonly ClientSession s; readonly IClientStore store;
    public event Action? Changed;
    public Keys(ClientSession s, IClientStore store) { this.s = s; this.store = store; }

    async Task<JsonObject> Identity() => (await store.Get("identity") is { } j ? JsonNode.Parse(j) as JsonObject : null) ?? throw new InvalidOperationException("no identity");
    public async Task<bool> PersonalBackedUp() => await store.Get("personalExported") is { } v && v.Trim() is "true";
    /// <summary>How many of your keys aren't in a backup yet (your personal key and your channel keys).</summary>
    public async Task<int> NotBackedUp() => (await PersonalBackedUp() ? 0 : 1) + s.ChannelKeys.Select(kv => kv.Value).OfType<JsonObject>().Count(k => !Js.IsTrue(k["exported"]));
    async Task MarkAllExported()
    {
        await store.Set("personalExported", "true");
        var ck = s.ChannelKeys; foreach (var (_, k) in ck) if (k is JsonObject o) o["exported"] = true;
        await s.SaveChannelKeys(ck); Changed?.Invoke();
    }

    /// <summary>The chains a key controls (registered with it).</summary>
    List<(string Chain, string Kind, string Name)> Owned(string pub) => s.Engine.Read(e => e.Store.Directory.Where(kv => (string?)kv.Value.Genesis["author"] == pub)
        .Select(kv => (kv.Key, (string?)kv.Value.Genesis["payload"]?["kind"] ?? "channel", (string?)kv.Value.Genesis["payload"]?["name"] ?? "")).ToList());
    public sealed record ChannelKey(string Pub, string? Chain, string Name, string Kind, bool Exported);
    public List<ChannelKey> Channels() => s.ChannelKeys.Select(kv => kv.Value).OfType<JsonObject>().Select(k =>
    {
        var pub = (string)k["pub"]!; var chain = (string?)k["chain"] ?? Owned(pub).FirstOrDefault().Chain;
        var g = chain != null ? s.Genesis(chain) : null;
        return new ChannelKey(pub, chain, (string?)g?["payload"]?["name"] ?? (string?)k["name"] ?? "", (string?)g?["payload"]?["kind"] ?? "channel", Js.IsTrue(k["exported"]));
    }).ToList();

    /// <summary>Everything in one file (keep it private): your personal key with its encryption and messaging keys, and every channel and list key.</summary>
    public async Task<(string Name, string Json)> BackupAll()
    {
        var id = await Identity();
        var personal = new JsonObject { ["pub"] = id["pub"]?.DeepClone(), ["priv"] = id["priv"]?.DeepClone() };
        if (id["enc"] != null) personal["enc"] = id["enc"]!.DeepClone();
        if (id["msg"] != null) personal["msg"] = id["msg"]!.DeepClone();
        personal["controls"] = new JsonArray(Owned(s.Me.pub).Select(o => (JsonNode)new JsonObject { ["chain"] = o.Chain, ["kind"] = o.Kind, ["name"] = o.Name }).ToArray());
        var chans = new JsonArray();
        foreach (var (_, kn) in s.ChannelKeys)
        {
            if (kn is not JsonObject k) continue; var pub = (string)k["pub"]!;
            var chain = (string?)k["chain"] ?? Owned(pub).FirstOrDefault().Chain; var g = chain != null ? s.Genesis(chain) : null;
            chans.Add(new JsonObject { ["chain"] = chain, ["kind"] = (string?)g?["payload"]?["kind"] ?? "channel", ["name"] = (string?)g?["payload"]?["name"] ?? (string?)k["name"] ?? "", ["pub"] = pub, ["priv"] = k["priv"]?.DeepClone() });
        }
        var file = new JsonObject { ["format"] = "opentube-keys", ["created"] = DateTimeOffset.UtcNow.ToString("o"), ["personal"] = personal, ["channels"] = chans };
        await MarkAllExported();
        return ($"opentube-all-keys-{DateTime.UtcNow:yyyy-MM-dd}.json", file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
    /// <summary>One channel's (or list's) key in a file of its own, to hand that channel to another device or person.</summary>
    public async Task<(string Name, string Json)> ExportChannel(string pub)
    {
        var ck = s.ChannelKeys; if (ck[pub] is not JsonObject k) throw new InvalidOperationException("This device doesn’t hold that key");
        var info = Channels().First(c => c.Pub == pub);
        var file = new JsonObject { ["format"] = "opentube-key", ["kind"] = info.Kind, ["chain"] = info.Chain, ["name"] = info.Name, ["pub"] = pub, ["priv"] = k["priv"]?.DeepClone() };
        k["exported"] = true; await s.SaveChannelKeys(ck); Changed?.Invoke();
        var safe = string.Concat(info.Name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return ($"opentube-{info.Kind}-{(safe.Length > 0 ? safe[..Math.Min(40, safe.Length)] : "key")}-{pub[..6]}.json", file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>A key pair is real: its private key signs, and the public key verifies.</summary>
    static bool Valid(JsonNode? k)
    {
        try { if (!Js.IsKey(k?["pub"]) || (string?)k!["priv"] is not { } priv) return false; var id = new Core.Identity((string)k["pub"]!, priv); return Rules.VerifySig(id.pub, "opentube-check", id.Sign("opentube-check")); }
        catch { return false; }
    }
    public sealed record ImportResult(int Channels, string? PersonalPub, int Controls, string? FirstChain);
    /// <summary>Read a key file: a full backup, a channel/list key, or a personal key. Channel keys are added at once;
    /// a different personal key is returned (to confirm), then <see cref="UsePersonal"/>.</summary>
    public async Task<(ImportResult Result, JsonObject? Personal)> Import(string text)
    {
        JsonObject f; try { f = JsonNode.Parse(text) as JsonObject ?? throw new Exception(); } catch { throw new ArgumentException("That file isn’t an OpenTube key file"); }
        var backup = (string?)f["format"] == "opentube-keys"; var kind = (string?)f["kind"];
        var chans = backup ? (f["channels"] as JsonArray ?? []).OfType<JsonObject>().ToList() : (kind is "channel" or "list") && !Js.IsTrue(f["sharedWithPersonalKey"]) ? [f] : [];
        var personal = backup ? f["personal"] as JsonObject : (kind == "personal" || Js.IsTrue(f["sharedWithPersonalKey"]) || kind == null) ? f : null;
        int added = 0; string? first = null;
        var ck = s.ChannelKeys;
        foreach (var k in chans)
        {
            if (!Valid(k)) continue;
            var pub = (string)k["pub"]!; var chain = Js.IsHex(k["chain"], 64) ? (string?)k["chain"] : null;
            var e = ck[pub] as JsonObject ?? new JsonObject(); e["pub"] = pub; e["priv"] = k["priv"]!.DeepClone(); if (chain != null) e["chain"] = chain;
            e["name"] = ((string?)k["name"] ?? "")[..Math.Min(60, ((string?)k["name"] ?? "").Length)]; e["exported"] = true; ck[pub] = e;
            added++; first ??= chain;
        }
        if (added > 0) { await s.SaveChannelKeys(ck); if (first != null) s.Chain(first); Changed?.Invoke(); }
        JsonObject? other = null;
        if (personal != null && (string?)personal["pub"] != s.Me.pub)
        {
            if (!Valid(personal)) throw new ArgumentException("The personal key in that file isn’t valid");
            other = personal;
        }
        if (added == 0 && personal == null) throw new ArgumentException("No valid OpenTube keys found in that file");
        return (new ImportResult(added, (string?)other?["pub"], (other?["controls"] as JsonArray)?.Count ?? 0, first), other);
    }
    /// <summary>Use the personal key from a file on this device (replacing the current one: back it up first). The app restarts.</summary>
    public async Task UsePersonal(JsonObject personal)
    {
        var id = new JsonObject { ["pub"] = personal["pub"]!.DeepClone(), ["priv"] = personal["priv"]!.DeepClone() };
        if (personal["enc"] != null) id["enc"] = personal["enc"]!.DeepClone();
        if (personal["msg"] != null) id["msg"] = personal["msg"]!.DeepClone();
        await store.Set("identity", id.ToJsonString()); await store.Set("personalExported", "true");
    }

    // ---- binding channel keys to your identity (every minute, as the JavaScript page does)
    readonly HashSet<string> binding = [];
    public void Start() => _ = Task.Run(async () => { await Task.Delay(8000); while (true) { try { if (!s.Paused) await Bind(); } catch { } await Task.Delay(60000); } });
    public async Task Bind()
    {
        foreach (var (pub, kn) in s.ChannelKeys)
        {
            if (kn is not JsonObject k || pub == s.Me.pub) continue;
            var done = s.Engine.Read(e => e.Store.Ledger.Retired.Contains(pub) || e.Store.Ledger.Resolve(pub) == s.Me.pub || e.Store.TxPool.Values.Any(t => (string?)t["bind"]?["key"] == pub));
            if (done || !binding.Add(pub)) continue;
            try
            {
                var key = new Core.Identity(pub, (string)k["priv"]!);
                await s.Engine.SubmitTx(new JsonObject { ["bind"] = new JsonObject { ["key"] = pub, ["sig"] = key.Sign($"opentube-bind:{s.Me.pub}:{pub}") } }, s.Me);
            }
            catch (Exception e) { s.Log?.Invoke("bind: " + e.Message); }
            finally { _ = Task.Delay(120000).ContinueWith(_ => binding.Remove(pub)); }
        }
    }
    /// <summary>Channel keys bound to you (on the main chain) out of those you hold.</summary>
    public (int Bound, int All) BoundCount() { var keys = s.ChannelKeys.Select(kv => kv.Key).ToList(); return (s.Engine.Read(e => keys.Count(k => e.Store.Ledger.Resolve(k) == s.Me.pub)), keys.Count); }

    // ---- retiring your personal key, replacing a channel's key
    /// <summary>A new personal key takes over: your coins (retire), the chains your personal key owns (rekey blocks).
    /// The old one is kept as retired; your encryption and messaging keys stay, so friends still read you. Then the app restarts.</summary>
    public async Task RetirePersonal(Action<string> status)
    {
        var fresh = Core.Identity.New(); var old = s.Me;
        status("Signing the hand-over…");
        await s.Engine.SubmitTx(new JsonObject { ["retire"] = new JsonObject { ["to"] = fresh.pub, ["sig"] = fresh.Sign($"opentube-retire:{old.pub}:{fresh.pub}") } }, old);
        foreach (var id in s.Engine.Read(e => e.Store.Chains.Values.Where(c => c != e.Store.Main && c.Owner == old.pub && (string?)c.Genesis["payload"]?["kind"] is not ("square" or "mail")).Select(c => c.Id).ToList()))
        {
            status("Handing over " + ((string?)s.Genesis(id)?["payload"]?["name"] ?? id[..8]) + "…");
            await Task.Run(() => s.Engine.Append(id, new JsonObject { ["type"] = "rekey", ["key"] = fresh.pub, ["proof"] = fresh.Sign($"opentube-rekey:{id}:{fresh.pub}") }, old));
        }
        var retired = await store.Get("retiredIdentities") is { } j && JsonNode.Parse(j) is JsonArray a ? a : [];
        retired.Add(new JsonObject { ["pub"] = old.pub, ["priv"] = old.priv, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        await store.Set("retiredIdentities", retired.ToJsonString());
        var cur = await Identity(); var next = new JsonObject { ["pub"] = fresh.pub, ["priv"] = fresh.priv };
        if (cur["enc"] != null) next["enc"] = cur["enc"]!.DeepClone(); if (cur["msg"] != null) next["msg"] = cur["msg"]!.DeepClone();
        await store.Set("identity", next.ToJsonString()); await store.Set("personalExported", "false");
        status("Done: your new key is in use. Back up your keys again.");
    }
    /// <summary>Give a channel (or list) a fresh key: the current key hands the chain over, and the new key is kept here (export it).</summary>
    public async Task RekeyChannel(string chain)
    {
        var d = s.Chain(chain) ?? throw new InvalidOperationException("that channel isn’t loaded yet");
        if (!s.IsMine(d.Owner)) throw new InvalidOperationException("This device doesn’t hold that channel’s key");
        var fresh = Core.Identity.New();
        await s.AddChannelKey(fresh, (string?)d.Profile["name"] ?? "", chain);
        await Task.Run(() => s.Engine.Append(chain, new JsonObject { ["type"] = "rekey", ["key"] = fresh.pub, ["proof"] = fresh.Sign($"opentube-rekey:{chain}:{fresh.pub}") }));
        _ = Bind(); Changed?.Invoke();
    }

    // ---- your display name (on comments and posts)
    public async Task<string> Name() => await s.Social.MyName();
    public async Task SetName(string name)
    {
        var st = await store.Get("settings") is { } j && JsonNode.Parse(j) is JsonObject o ? o : [];
        st["name"] = name.Trim()[..Math.Min(60, name.Trim().Length)]; await store.Set("settings", st.ToJsonString()); Changed?.Invoke();
    }
}
