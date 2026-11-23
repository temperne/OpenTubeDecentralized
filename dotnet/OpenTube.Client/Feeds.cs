using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>What the Home, Explore and tag pages show (as the JavaScript page does).</summary>
public static class Feeds
{
    /// <summary>One ranked video: chain, video hash, title, channel name, votes, time, score, and the waves
    /// [rising, today, this week, all time].</summary>
    public sealed record Item(string Chain, string Video, string Title, string Channel, int Up, int Down, double Ts, double Score, double[] Waves);
    public sealed record Ranking(List<Item> Items, bool Local, string? List, string? ListName, double At, string? Algo);
    public static readonly (string Key, string Label)[] Views = [("hot", "Hot"), ("rising", "Rising"), ("today", "Today"), ("week", "This week"), ("all", "All time")];

    static Item ItemOf(JsonNode? i)
    {
        var w = (i?["w"] as JsonArray)?.Select(x => Js.Num(x) ?? 0).ToArray() ?? [];
        return new((string?)i?["c"] ?? "", (string?)i?["v"] ?? "", (string?)i?["t"] ?? "", (string?)i?["n"] ?? "", (int)(Js.Num(i?["up"]) ?? 0), (int)(Js.Num(i?["down"]) ?? 0), Js.Num(i?["ts"]) ?? 0, Js.Num(i?["s"]) ?? 0, w);
    }
    /// <summary>Ranking lists that have published a ranking.</summary>
    public static List<(string List, string Name)> Rankers(this ClientSession s) => s.Engine.Read(e => e.Rankings.Keys.ToList())
        .Select(l => (l, (string?)s.Genesis(l)?["payload"]?["name"])).Where(x => x.Item2 != null).Select(x => (x.l, x.Item2!)).ToList();
    /// <summary>Your chosen ranking list's ranking; without one, every registered channel is loaded and ranked here.</summary>
    static readonly Dictionary<string, (DateTime At, Ranking R)> cache = [];
    /// <summary>The ranking, worked out at most every few seconds (pages redraw often).</summary>
    public static Ranking Rank(this ClientSession s, string? ranker)
    {
        lock (cache) if (cache.TryGetValue(ranker ?? "", out var c) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(4)) return c.R;
        var r = RankNow(s, ranker);
        lock (cache) cache[ranker ?? ""] = (DateTime.UtcNow, r);
        return r;
    }
    static Ranking RankNow(ClientSession s, string? ranker)
    {
        if (ranker != null && s.Engine.Read(e => e.Rankings.TryGetValue(ranker, out var r) ? (JsonObject)r.DeepClone() : null) is { } r)
            return new(((r["items"] as JsonArray) ?? []).Select(ItemOf).ToList(), false, ranker, (string?)s.Genesis(ranker)?["payload"]?["name"], Js.Num(r["at"]) ?? 0, (string?)r["algo"]);
        foreach (var c in s.Directory()) if (c.Kind == "channel") s.Chain(c.Id);   // load each channel (asks peers once)
        return new(s.Engine.Rank().Select(ItemOf).ToList(), true, null, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);
    }
    public static double ViewKey(Item i, string view) => view switch
    {
        "rising" => i.Waves.Length > 0 ? i.Waves[0] : i.Score, "today" => i.Waves.Length > 1 ? i.Waves[1] : i.Score,
        "week" => i.Waves.Length > 2 ? i.Waves[2] : i.Score, "all" => i.Waves.Length > 3 ? i.Waves[3] : i.Up - i.Down, _ => i.Score,
    };
    /// <summary>Live streams on every channel loaded here, newest first.</summary>
    public static List<(string Chain, string Name, JsonObject Block)> LiveEverywhere(this ClientSession s) => s.LiveNow().OrderByDescending(x => Js.Num(x.Block["ts"]) ?? 0).ToList();

    public sealed record TimelineItem(string Kind, string Chain, JsonObject Block, double Ts, string? Via, string? Month);
    /// <summary>Home: videos and posts from what you subscribe to, and public posts from who you follow and your friends; newest first.</summary>
    public static async Task<List<TimelineItem>> Timeline(this ClientSession s)
    {
        var outp = new List<TimelineItem>(); var seen = new HashSet<string>();
        var keep = (await s.Social.FollowingList()).Concat((await s.Social.Friends()).Select(f => f.Key)).ToHashSet();
        foreach (var id in s.Notes.Subscribed)
        {
            var d = s.Chain(id); if (d == null) continue;
            if (d.Kind == "channel")
            {
                foreach (var b in d.Videos) if (s.Engine.PostPaid(b)) outp.Add(new("video", id, b, Js.Num(b["ts"]) ?? 0, null, null));
                foreach (var b in d.Posts) outp.Add(new("cpost", id, b, Js.Num(b["ts"]) ?? 0, null, null));
            }
            if (d.Kind == "group") foreach (var b in d.Posts) if (!d.Hidden.Contains((string)b["hash"]!)) outp.Add(new("gpost", id, b, Js.Num(b["ts"]) ?? 0, null, null));
            if (d.Kind == "list" && s.Genesis(id)?["payload"]?["ranking"] == null) foreach (var (c, v) in s.ListFeed(id).Take(40)) outp.Add(new("video", c, v, Js.Num(v["ts"]) ?? 0, id, null));
        }
        var m = Social.Month(DateTimeOffset.UtcNow);
        foreach (var p in s.Social.Posts(m)) if (keep.Contains(s.Engine.Read(e => e.Store.Ledger.Resolve(p.Author))) || p.Mine) outp.Add(new("square", p.Chain, p.Block, p.Ts, null, m));
        return outp.Where(x => x.Kind != "video" || seen.Add((string)x.Block["hash"]!)).OrderByDescending(x => x.Ts).ToList();
    }
    /// <summary>Videos with a tag on the channels loaded here, newest first.</summary>
    public static List<(string Chain, string Name, JsonObject Video)> Tagged(this ClientSession s, string tag) => s.Engine.Read(e => e.Store.Chains.Values
        .Where(c => c != e.Store.Main && (string?)c.Genesis["payload"]?["kind"] == "channel")
        .SelectMany(c => { var d = Derived.Of(c); return d.Videos.Where(v => (v["payload"]?["tags"] as JsonArray ?? []).Any(t => (string?)t == tag)).Select(v => (c.Id, (string?)d.Profile["name"] ?? "", (JsonObject)v.DeepClone())); })
        .OrderByDescending(x => Js.Num(x.Item3["ts"]) ?? 0).ToList());
    /// <summary>Public and group posts that mention #tag.</summary>
    public static List<(string Chain, string Kind, JsonObject Post)> TaggedPosts(this ClientSession s, string tag)
    {
        var re = new Regex(@"(^|[^\w#])#" + Regex.Escape(tag) + @"(?![\w-])", RegexOptions.IgnoreCase);
        return s.Engine.Read(e => e.Store.Chains.Values.Where(c => (string?)c.Genesis["payload"]?["kind"] is "square" or "group")
            .SelectMany(c => { var d = Derived.Of(c); return d.Posts.Where(b => re.IsMatch((string?)b["payload"]?["text"] ?? "") && !d.Hidden.Contains((string)b["hash"]!)).Select(b => (c.Id, d.Kind, (JsonObject)b.DeepClone())); })
            .OrderByDescending(x => Js.Num(x.Item3["ts"]) ?? 0).Take(50).ToList());
    }
}
