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

    readonly ClientSession s; readonly IClientStore store;
    readonly object gate = new();
    List<Note> list = [];
    readonly HashSet<string> seen = [];
    JsonObject settings = [], subs = [];
    JsonObject Notif => settings["notif"] as JsonObject ?? (JsonObject)(settings["notif"] = new JsonObject());
    double Since => Js.Num(Notif["since"]) ?? 0;

    public Notifications(ClientSession s, IClientStore store) { this.s = s; this.store = store; }

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
    public void Start() => s.Engine.Store.BlockAdded += (c, b) => { try { OnBlock(c, b); } catch { } };

    // ---- the list
    public List<Note> All { get { lock (gate) return [.. list]; } }
    public int Unread { get { lock (gate) return list.Count(n => !n.Read); } }
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
    Task SaveSettings() => store.Set("settings", settings.ToJsonString());

    // ---- subscriptions (shared with the JavaScript page: chain -> "sub" | "join")
    public string? Subscription(string chain) => (string?)subs[chain];
    public async Task Subscribe(string chain, bool on) { if (on) subs[chain] = subs[chain]?.DeepClone() ?? "sub"; else subs.Remove(chain); await store.Set("subs", subs.ToJsonString()); Changed?.Invoke(); }

    // ---- making them
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
    bool Mine(string? k) => k != null && (s.IsMine(k) || s.Engine.Read(e => e.Store.Ledger.Resolve(k)) == s.Me.pub);
    string Name(string? k)
    {
        if (k == null) return "someone";
        var who = s.Engine.Read(e => e.Store.Ledger.Resolve(k));
        var page = s.Directory().FirstOrDefault(c => c.Owner == who || c.Owner == k);
        return page?.Name ?? who[..Math.Min(8, who.Length)];
    }
    static string Short(string? t, int n) => t == null ? "" : t.Length <= n ? t : t[..n] + "…";
    static string Coins(double units) => (units / 100).ToString("0.##");

    void OnBlock(Chain chain, JsonObject b)
    {
        if (b["payload"] is not JsonObject p || (Js.Num(b["ts"]) ?? 0) < Since - 3600000) return;
        var ts = Js.Num(b["ts"]) ?? 0; var hash = (string?)b["hash"] ?? ""; var author = (string?)b["author"];
        if (chain == s.Engine.Store.Main) { OnMain(p, ts); return; }
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
        if (kind == "org")
        {
            if (type == "flag" && Js.IsTrue(p["on"]) && (string?)p["ref"]?["chain"] is { } rc && Mine(s.Engine.Read(e => e.Store.Chains.TryGetValue(rc, out var x) ? x.Owner : null)))
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
                if (t[k] is JsonObject x && (string?)x["id"] is { } lid && s.Engine.Read(e => e.Store.Ledger.Listings.TryGetValue(lid, out var L) ? L : null) is { } listing && Mine((string?)listing["seller"]))
                    Notify("market", k == "bid" ? $"New bid: {Coins(Js.Num(x["amt"]) ?? 0)} coins on “{(string?)listing["title"]}”" : $"{Name(from)} bought “{(string?)listing["title"]}”", $"#/market/item/{lid}", null, ts, th);
        }
    }
}
