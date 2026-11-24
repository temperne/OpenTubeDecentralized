using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>
/// Notifications, made on this device from what arrives (as the JavaScript page makes them, sharing its list and
/// settings in the browser): new videos and posts from what you subscribe to, live streams starting, comments and
/// replies on your things, likes, posts by people you follow, organizations, the market, swaps and coins you receive.
/// Each kind can be on or off, each thing you subscribe to Default / All / None, and a kind overridden for one thing.
/// The phone app turns each into a system notification in that kind's own Android notification channel.
/// </summary>
public sealed class Notifications
{
    /// <summary>The kinds: key, label, on by default. (Each is an Android notification channel of its own.)</summary>
    public static readonly (string Key, string Label, bool On)[] Types =
    [
        ("video", "New videos", true), ("live", "Live streams starting", true), ("post", "New posts (channels, groups, lists)", true),
        ("comment", "Comments on your videos and posts", true), ("reply", "Replies to you", true), ("mention", "@@mentions of you", true),
        ("like", "Likes on your things", false), ("friend", "Friends’ posts", true), ("follow", "Posts by people you follow", true),
        ("message", "Private messages", true), ("market", "Market: bids, sales, escrow, disputes", true), ("swap", "Exchange: swaps for you", true),
        ("org", "Organizations: flags, filings, roles", true), ("coins", "Coins you receive", false),
    ];
    public static string LabelOf(string type) => Types.FirstOrDefault(t => t.Key == type).Label ?? type;

    public sealed record Note(string Id, string Type, string Text, string Href, string? Obj, double Ts, bool Read);
    /// <summary>A new notification (the phone shows it as a system notification).</summary>
    public event Action<Note>? Arrived;
    public event Action? Changed;

    readonly Engine engine; readonly IClientStore store; readonly Func<string, bool> isMine; readonly string me;
    readonly object gate = new();
    List<Note> list = [];
    readonly HashSet<string> seen = [];
    JsonObject settings = [], subs = [];
    JsonObject Notif => settings["notif"] as JsonObject ?? (JsonObject)(settings["notif"] = new JsonObject());
    double Since => Js.Num(Notif["since"]) ?? 0;

    /// <param name="isMine">Your own keys (your identity, your channels' keys).</param>
    public Notifications(Engine engine, IClientStore store, string me, Func<string, bool> isMine) { this.engine = engine; this.store = store; this.me = me; this.isMine = isMine; }
    public Notifications(ClientSession s, IClientStore store) : this(s.Engine, store, s.Me.pub, s.IsMine) { }
    /// <summary>The subscriptions and settings as they're stored (what the app sends its node, so the node notifies the same way).</summary>
    public JsonObject Export() => new() { ["subs"] = subs.DeepClone(), ["notif"] = Notif.DeepClone() };
    /// <summary>Take subscriptions and settings sent by the app (on a node).</summary>
    public void Import(JsonObject x)
    {
        lock (gate)
        {
            if (x["subs"] is JsonObject sb) subs = (JsonObject)sb.DeepClone();
            if (x["notif"] is JsonObject n) { var since = Notif["since"]?.DeepClone(); settings["notif"] = n.DeepClone(); Notif["since"] ??= since; foreach (var k in new[] { "types", "objects", "typeObj" }) if (Notif[k] is not JsonObject) Notif[k] = new JsonObject(); }
        }
    }
    /// <summary>The chains these notifications follow (to keep loaded on a node).</summary>
    public List<string> Subscribed { get { lock (gate) return subs.Select(kv => kv.Key).ToList(); } }

    public async Task Load()
    {
        try { if (await store.Get("settings") is { } st && JsonNode.Parse(st) is JsonObject o) settings = o; } catch { }
        try { if (await store.Get("subs") is { } sb && JsonNode.Parse(sb) is JsonObject o) subs = o; } catch { }
        try
        {
            if (await store.Get("notifications") is { } ns && JsonNode.Parse(ns) is JsonArray a)
                list = a.OfType<JsonObject>().Select(x => new Note((string?)x["id"] ?? "", (string?)x["type"] ?? "", (string?)x["text"] ?? "", (string?)x["href"] ?? "", (string?)x["obj"], Js.Num(x["ts"]) ?? 0, Js.IsTrue(x["read"]))).ToList();
        }
        catch { }
        foreach (var n in list) seen.Add(n.Id);
        foreach (var k in new[] { "types", "objects", "typeObj" }) if (Notif[k] is not JsonObject) Notif[k] = new JsonObject();
        if (Notif["since"] == null) { Notif["since"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); await SaveSettings(); }
    }
    /// <summary>Watch the chains for things to notify about (after Load and once the engine runs).</summary>
    public void Start() => engine.Store.BlockAdded += Watch;
    public void Stop() => engine.Store.BlockAdded -= Watch;
    void Watch(Chain c, JsonObject b) { try { OnBlock(c, b); } catch { } }

    // ---- the list
    public List<Note> All { get { lock (gate) return [.. list]; } }
    public int Unread { get { lock (gate) return list.Count(n => !n.Read); } }
    public void ClearAll() { lock (gate) list = []; SaveList(); Changed?.Invoke(); }
    /// <summary>A per-thing override of one kind: null (as the general setting), true or false.</summary>
    public bool? TypeObj(string obj, string type) => Notif["typeObj"]?[obj + "|" + type] is JsonNode v ? Js.IsTrue(v) : null;
    public async Task SetTypeObj(string obj, string type, bool? on) { var o = (JsonObject)Notif["typeObj"]!; if (on == null) o.Remove(obj + "|" + type); else o[obj + "|" + type] = on.Value; await SaveSettings(); Changed?.Invoke(); }
    public List<string> Objects { get { lock (gate) return subs.Select(kv => kv.Key).Concat((Notif["objects"] as JsonObject ?? []).Select(kv => kv.Key)).Distinct().ToList(); } }
    public bool Desktop => Js.IsTrue(Notif["desktop"]);
    public async Task SetDesktop(bool on) { Notif["desktop"] = on; await SaveSettings(); Changed?.Invoke(); }
    public void MarkAllRead() { lock (gate) list = list.Select(n => n with { Read = true }).ToList(); SaveList(); Changed?.Invoke(); }
    void SaveList()
    {
        string json; lock (gate) json = new JsonArray(list.Select(n => (JsonNode)new JsonObject { ["id"] = n.Id, ["type"] = n.Type, ["text"] = n.Text, ["href"] = n.Href, ["obj"] = n.Obj, ["ts"] = n.Ts, ["read"] = n.Read }).ToArray()).ToJsonString();
        _ = store.Set("notifications", json);
    }

    // ---- settings: the most specific wins (thing + kind > thing > kind)
    public bool TypeOn(string type) => Notif["types"]?[type] is JsonNode v ? Js.IsTrue(v) : Types.FirstOrDefault(t => t.Key == type).On;
    public async Task SetType(string type, bool on) { ((JsonObject)Notif["types"]!)[type] = on; await SaveSettings(); Changed?.Invoke(); }
    /// <summary>"default", "all" or "none" for one thing you subscribe to.</summary>
    public string ObjectMode(string obj) => (string?)Notif["objects"]?[obj] ?? "default";
    public async Task SetObject(string obj, string mode) { var o = (JsonObject)Notif["objects"]!; if (mode == "default") o.Remove(obj); else o[obj] = mode; await SaveSettings(); Changed?.Invoke(); }
    bool On(string type, string? obj)
    {
        if (obj != null && Notif["typeObj"]?[obj + "|" + type] is JsonNode v) return Js.IsTrue(v);
        if (obj != null && ObjectMode(obj) == "none") return false;
        if (obj != null && ObjectMode(obj) == "all") return true;
        return TypeOn(type);
    }
    /// <summary>Only the notification settings change here: the rest of the record (the friend list, …) is read fresh first.</summary>
    async Task SaveSettings()
    {
        var cur = await store.Get("settings") is { } j && JsonNode.Parse(j) is JsonObject o ? o : [];
        lock (gate) cur["notif"] = Notif.DeepClone();
        await store.Set("settings", cur.ToJsonString());
    }
    /// <summary>Your friends and the people you follow (their public posts notify you), kept current by the friends page.</summary>
    public HashSet<string> Following { get; set; } = [];
    /// <summary>Your friends' pages (their sealed posts to you notify you).</summary>
    public HashSet<string> FriendPages { get; set; } = [];

    // ---- subscriptions (shared with the JavaScript page: chain -> "sub" | "join")
    public string? Subscription(string chain) => (string?)subs[chain];
    /// <summary>Subscribe ("sub"), join ("join": your computers host it), or neither (null).</summary>
    public async Task SetSubscription(string chain, string? mode) { if (mode != null) subs[chain] = mode; else subs.Remove(chain); await store.Set("subs", subs.ToJsonString()); Changed?.Invoke(); }
    public List<string> Joined { get { lock (gate) return subs.Where(kv => (string?)kv.Value == "join").Select(kv => kv.Key).ToList(); } }
    public async Task Subscribe(string chain, bool on) { if (on) subs[chain] = subs[chain]?.DeepClone() ?? "sub"; else subs.Remove(chain); await store.Set("subs", subs.ToJsonString()); Changed?.Invoke(); }

    // ---- making them
    /// <summary>A notification made elsewhere (a private message this device found for you).</summary>
    public void Add(string type, string text, string href, string? obj, double ts, string? id = null) => Notify(type, text, href, obj, ts, id);
    void Notify(string type, string text, string href, string? obj, double ts, string? id = null)
    {
        id ??= type + ":" + href + ":" + text[..Math.Min(40, text.Length)];
        Note n;
        lock (gate)
        {
            if (seen.Contains(id) || ts < Since - 3600000 || !On(type, obj)) return;
            seen.Add(id); n = new Note(id, type, text, href, obj, ts, false);
            list.Insert(0, n); if (list.Count > 400) list.RemoveRange(400, list.Count - 400);
        }
        SaveList(); Arrived?.Invoke(n); Changed?.Invoke();
    }
    bool Mine(string? k) => k != null && (isMine(k) || engine.Read(e => e.Store.Ledger.Resolve(k)) == me);
    string Name(string? k)
    {
        if (k == null) return "someone";
        return engine.Read(e =>
        {
            var who = e.Store.Ledger.Resolve(k);
            var page = e.Store.Chains.Values.FirstOrDefault(c => c != e.Store.Main && (c.Owner == who || c.Owner == k) && (string?)c.Genesis["payload"]?["kind"] == "channel");
            return page != null ? (string?)Derived.Of(page).Profile["name"] ?? who[..8] : who[..Math.Min(8, who.Length)];
        });
    }
    static string Short(string? t, int n) => t == null ? "" : t.Length <= n ? t : t[..n] + "…";
    static string Coins(double units) => (units / 100).ToString("0.##");

    void OnBlock(Chain chain, JsonObject b)
    {
        if (b["payload"] is not JsonObject p || (Js.Num(b["ts"]) ?? 0) < Since - 3600000) return;
        var ts = Js.Num(b["ts"]) ?? 0; var hash = (string?)b["hash"] ?? ""; var author = (string?)b["author"];
        if (chain == engine.Store.Main) { OnMain(p, ts); return; }
        if (Mine(author)) return;
        var kind = (string?)chain.Genesis["payload"]?["kind"]; var id = chain.Id; var type = (string?)p["type"];
        var d = Derived.Of(chain); var name = (string?)d.Profile["name"] ?? id[..8];
        var subscribed = subs.ContainsKey(id); var ownerBlock = chain.IsOwnerBlock(b);
        var target = (string?)p["target"] is { } tg && chain.Blocks.TryGetValue(tg, out var tb) ? tb : null;
        string Title(JsonNode? x) => (string?)x?["payload"]?["title"] ?? Short((string?)x?["payload"]?["text"], 60);
        if (kind == "channel" && subscribed && ownerBlock)
        {
            if (type == "video") Notify("video", $"{name} posted “{(string?)p["title"]}”", $"#/v/{id}/{hash}", id, ts, hash);
            if (type == "post") Notify("post", $"{name}: {Short((string?)p["text"], 80)}", $"#/c/{id}/posts", id, ts, hash);
        }
        if (kind == "channel" && type == "live" && (subscribed || Mine(d.Owner))) Notify("live", $"🔴 {name} is live: {(string?)p["title"]}", $"#/live/{id}/{hash}", id, ts, hash);
        if (kind == "group" && subscribed && type == "post") Notify("post", $"{name}: {Name(author)} posted “{Short((string?)p["text"], 60)}”", $"#/c/{id}/discussion", id, ts, hash);
        if (kind == "list" && subscribed && type == "add" && ownerBlock) Notify("post", $"{name} added {(p["video"] != null ? "a video" : "a channel")}", $"#/c/{id}/feed", id, ts, hash);
        if (type == "comment" && target != null && Mine((string?)target["author"]))
            Notify((string?)target["payload"]?["type"] == "comment" ? "reply" : "comment", $"{Name(author)}: “{Short((string?)p["text"], 80)}” on {(Title(target) is { Length: > 0 } t ? "“" + t + "”" : "your post")}",
                (string?)target["payload"]?["type"] == "video" ? $"#/v/{id}/{(string?)target["hash"]}" : $"#/c/{id}", id, ts, hash);
        if (type == "vote" && (Js.Num(p["v"]) ?? 0) > 0 && target != null && Mine((string?)target["author"]))
            Notify("like", $"{Name(author)} liked {(Title(target) is { Length: > 0 } t2 ? "“" + t2 + "”" : "your post")}", (string?)target["payload"]?["type"] == "video" ? $"#/v/{id}/{(string?)target["hash"]}" : $"#/c/{id}", id, ts, hash);
        if (kind == "square" && type == "post" && Following.Contains(engine.Read(e => e.Store.Ledger.Resolve(author ?? ""))))
            Notify("follow", $"{Name(author)}: {Short((string?)p["text"], 90)}", $"#/square/{(string?)chain.Genesis["payload"]?["month"]}/{hash}", null, ts, hash);
        if (kind == "circle" && type == "sealed" && p["target"] == null && FriendPages.Contains(id)) Notify("friend", $"{Name(author)} shared something with friends", "#/friends", id, ts, hash);
        if (kind == "circle" && type == "sealed" && target != null && Mine((string?)target["author"])) Notify("reply", $"{Name(author)} replied to your friends post", "#/friends", id, ts, hash);
        if (kind == "org")
        {
            if (type == "flag" && Js.IsTrue(p["on"]) && (string?)p["ref"]?["chain"] is { } rc && Mine(engine.Read(e => e.Store.Chains.TryGetValue(rc, out var x) ? x.Owner : null)))
                Notify("org", $"{name} flagged your video as {(string?)p["cls"]}", $"#/v/{rc}/{(string?)p["ref"]?["block"]}", id, ts, hash);
            if (type == "role" && Js.IsTrue(p["on"]) && Mine((string?)p["key"])) Notify("org", $"{name} gave you the role {((string?)p["role"] == "judge" ? "arbitrator" : (string?)p["role"])}", $"#/c/{id}/people", id, ts, hash);
        }
    }
    void OnMain(JsonObject p, double ts)
    {
        foreach (var t in (p["txs"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var from = (string?)t["from"]; if (Mine(from)) continue;
            var th = (string?)t["hash"] ?? "";
            foreach (var o in (t["pay"] as JsonArray ?? []).OfType<JsonObject>())
                if (Mine((string?)o["to"])) Notify("coins", $"{Name(from)} sent you {Coins(Js.Num(o["amt"]) ?? 0)} coins", "#/wallet", null, ts, th + (string?)o["to"]);
            if (t["htlc"] is JsonObject h && Mine((string?)h["to"])) Notify("swap", $"{Name(from)} locked {Coins(Js.Num(h["amt"]) ?? 0)} coins for you (swap)", "#/exchange/swaps", null, ts, th);
            foreach (var k in new[] { "bid", "buy" })
                if (t[k] is JsonObject x && (string?)x["id"] is { } lid && engine.Read(e => e.Store.Ledger.Listings.TryGetValue(lid, out var L) ? L : null) is { } listing && Mine((string?)listing["seller"]))
                    Notify("market", k == "bid" ? $"New bid: {Coins(Js.Num(x["amt"]) ?? 0)} coins on “{(string?)listing["title"]}”" : $"{Name(from)} bought “{(string?)listing["title"]}”", $"#/market/item/{lid}", null, ts, th);
        }
    }
}
