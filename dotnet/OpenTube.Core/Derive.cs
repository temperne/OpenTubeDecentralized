using System.Text.Json.Nodes;
using static OpenTube.Core.Js;

namespace OpenTube.Core;

/// <summary>A video's votes: the latest vote per voter (by height, then time).</summary>
public sealed record VoteTally(int Up, int Down, Dictionary<string, double> By, List<(double Ts, double V)> Recent, List<JsonObject> Blocks);

/// <summary>
/// derive(chain): what a chain says right now — its profile, videos, posts, live streams, who may comment, what was
/// hidden or blocked, the votes. Ported from the JavaScript core; OpenTube.Tests compares the two on built-up chains.
/// </summary>
public sealed class Derived
{
    public required string Id, Kind; public string? Owner;
    public required HashSet<string> ByOwner, Relays, Allowed, Revoked, Hidden;
    public required JsonObject Profile;
    public required List<JsonObject> Videos, Posts, Sealed, Adds, Ads, Sponsors, Streams, LiveNow, Visible;
    public required Dictionary<string, string?> LiveEnds; public required Dictionary<string, JsonNode?> LiveNext, LiveStats;
    public required Dictionary<string, JsonObject> Recordings, NodeRecs;
    public required Dictionary<string, VoteTally> Votes;
    public required Dictionary<string, JsonNode?> VideoPolicy;
    public int BlockCount;
    public required List<(string Key, OrderedSet Before)> Revokes;

    public JsonNode? PolicyFor(string target) => VideoPolicy.TryGetValue(target, out var p) && p != null ? p : Profile["comments"];
    bool Allows(string key, string target) { var pol = Str(PolicyFor(target)); return pol == "anyone" || (pol == "approved" && Allowed.Contains(key)); }
    public IEnumerable<JsonObject> CommentsFor(string target) => Visible.Where(b => Str(Get(Get(b, "payload"), "type")) == "comment" && Str(Get(Get(b, "payload"), "target")) == target);
    public bool CanComment(string key, string target) => key == Owner || (!Revokes.Any(r => r.Key == key) && (Kind == "group" ? (Str(Profile["join"]) == "open" || Allowed.Contains(key)) : Allows(key, target)));
    public bool CanPost(string key) => key == Owner || (Kind == "group" && !Revokes.Any(r => r.Key == key) && (Str(Profile["join"]) == "open" || Allowed.Contains(key)));

    static readonly string[] ProfileKeys = ["name", "about", "avatar", "comments", "join", "links", "location", "place", "orgType", "details", "people", "jurisdiction"];

    public static Derived Of(Chain c, double? nowMs = null)
    {
        var now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var owner = c.Owner; var kind = Str(Get(Get(c.Genesis, "payload"), "kind")) ?? "";
        var blocks = c.Blocks.Values.Where(b => Str(b["hash"]) != c.Id)
            .OrderBy(b => Num(b["height"])).ThenBy(b => Num(b["ts"])).ThenBy(b => Str(b["hash"]), StringComparer.Ordinal).ToList();
        var byOwner = blocks.Where(c.IsOwnerBlock).Select(b => Str(b["hash"])!).ToHashSet();
        var profile = (JsonObject)Get(c.Genesis, "payload")!.DeepClone();
        var allowed = new HashSet<string>(); var revokes = new List<(string Key, OrderedSet Before)>(); var relays = new HashSet<string>();
        var videoPolicy = new Dictionary<string, JsonNode?>();
        string KeyOf(JsonNode? n) => Str(n) ?? "#" + (n == null ? "null" : Blocks.Canon(n));
        foreach (var b in blocks)
        {
            if (!byOwner.Contains(Str(b["hash"])!)) continue;
            var p = (JsonObject)b["payload"]!; var type = Str(Get(p, "type"));
            if (type == "video" && Truthy(Get(p, "comments"))) videoPolicy[Str(b["hash"])!] = Get(p, "comments")!.DeepClone();
            if (type == "settings") { var t = KeyOf(Get(p, "target")); if (Str(Get(p, "comments")) == "default") videoPolicy.Remove(t); else videoPolicy[t] = Get(p, "comments")?.DeepClone(); }
            if (type == "profile") foreach (var k in ProfileKeys) if (p.ContainsKey(k)) profile[k] = p[k]?.DeepClone();
            if (type == "allow") { var k = KeyOf(Get(p, "key")); allowed.Add(k); for (int i = revokes.Count - 1; i >= 0; i--) if (revokes[i].Key == k) revokes.RemoveAt(i); }
            if (type == "relay") { if (Truthy(Get(p, "on"))) relays.Add(KeyOf(Get(p, "key"))); else relays.Remove(KeyOf(Get(p, "key"))); }
            if (type == "revoke") { var k = KeyOf(Get(p, "key")); allowed.Remove(k); revokes.Add((k, c.Ancestors([Str(b["hash"])!]))); }
        }
        var d = new Derived
        {
            Id = c.Id, Kind = kind, Owner = owner, ByOwner = byOwner, Relays = relays, Allowed = allowed, Revoked = revokes.Select(r => r.Key).ToHashSet(),
            Profile = profile, VideoPolicy = videoPolicy, Revokes = revokes, BlockCount = c.Blocks.Count,
            Hidden = [], Videos = [], Posts = [], Sealed = [], Adds = [], Ads = [], Sponsors = [], Streams = [], LiveNow = [], Visible = [],
            LiveEnds = [], LiveNext = [], LiveStats = [], Recordings = [], NodeRecs = [], Votes = [],
        };
        bool isGroup = kind == "group";
        bool RevokedAt(JsonObject b) => revokes.Any(r => r.Key == KeyOf(Get(b, "author")) && !r.Before.Contains(Str(b["hash"])!));
        bool Permitted(JsonObject b)
        {
            if (byOwner.Contains(Str(b["hash"])!)) return true;
            if (isGroup || kind is "square" or "circle") return !RevokedAt(b);
            var type = Str(Get(Get(b, "payload"), "type"));
            if (type is "live" or "liveend") return true;
            if (RevokedAt(b)) return false;
            if (type is "hide" or "vote") return true;
            return Str(Get(Get(b, "payload"), "target")) is { } t ? d.Allows(KeyOf(Get(b, "author")), t) : d.AllowsUndefinedTarget(KeyOf(Get(b, "author")));
        }
        var visible = blocks.Where(Permitted).ToList(); d.Visible.AddRange(visible);
        foreach (var b in visible)
        {
            var p = Get(b, "payload"); if (Str(Get(p, "type")) != "hide") continue;
            if (Str(Get(p, "target")) is { } th && c.Blocks.TryGetValue(th, out var t)
                && ((byOwner.Contains(Str(b["hash"])!) && kind != "square") || KeyOf(Get(t, "author")) == KeyOf(Get(b, "author")))) d.Hidden.Add(Str(t["hash"])!);
        }
        var live = visible.Where(b => !d.Hidden.Contains(Str(b["hash"])!)).ToList();
        string TypeOf(JsonObject b) => Str(Get(Get(b, "payload"), "type")) ?? "";
        bool Own(JsonObject b) => byOwner.Contains(Str(b["hash"])!);
        List<JsonObject> Newest(Func<JsonObject, bool> f) { var l = live.Where(f).ToList(); l.Reverse(); return l; }
        d.Videos = Newest(b => TypeOf(b) == "video" && Own(b));
        d.Posts = Newest(b => TypeOf(b) == "post" && (isGroup || kind == "square" || Own(b)));
        d.Sealed = Newest(b => TypeOf(b) == "sealed");
        d.Adds = Newest(b => TypeOf(b) == "add" && Own(b));
        d.Ads = Newest(b => TypeOf(b) == "ad" && Own(b));
        d.Sponsors = Newest(b => TypeOf(b) == "sponsor" && Own(b));
        // live streams: started, not yet ended (and less than a day old)
        foreach (var b in live)
        {
            var p = Get(b, "payload")!; var type = TypeOf(b);
            if (!(Own(b) || (type == "liveend" && relays.Contains(KeyOf(Get(b, "author")))))) continue;
            var stream = KeyOf(Get(p, "stream"));
            if (type == "liveend")
            {
                d.LiveEnds[stream] = Str(Get(p, "outcome")); if (Truthy(Get(p, "next"))) d.LiveNext[stream] = Get(p, "next")!.DeepClone();
                if (Truthy(Get(p, "stats")) && !d.LiveStats.ContainsKey(stream)) d.LiveStats[stream] = Get(p, "stats")!.DeepClone();
                var r = Get(p, "rec");
                if (Truthy(r) && r is JsonObject ro && IsHex(Get(ro, "infohash"), 40) && IsStr(Get(ro, "file"), 200) && IsSafeInteger(Get(ro, "size")) && Num(Get(ro, "size")) > 0 && IsStr(Get(ro, "mime"), 80) && IsImg(Get(ro, "thumb") ?? JsonValue.Create("")))
                { var rec = (JsonObject)ro.DeepClone(); rec["by"] = Get(b, "author")?.DeepClone(); rec["at"] = Get(b, "ts")?.DeepClone(); d.NodeRecs[stream] = rec; }
            }
            if (type == "video" && Truthy(Get(p, "stream"))) d.Recordings[stream] = b;
        }
        d.Streams = Newest(b => TypeOf(b) == "live" && (Own(b) || relays.Contains(KeyOf(Get(b, "author")))));
        d.LiveNow = d.Streams.Where(b => !d.LiveEnds.ContainsKey(KeyOf(Get(Get(b, "payload"), "stream"))) && now - Num(b["ts"])!.Value < 24 * 3600000 && !Truthy(Get(Get(b, "payload"), "group"))).ToList();
        d.Votes = TallyVotes(visible);
        return d;
    }
    // allows(key, undefined): the policy for an undefined target is the channel's default
    bool AllowsUndefinedTarget(string key) { var pol = Str(Profile["comments"]); return pol == "anyone" || (pol == "approved" && Allowed.Contains(key)); }

    /// <summary>tallyVotes(blocks): per target, the latest vote of each voter.</summary>
    public static Dictionary<string, VoteTally> TallyVotes(IEnumerable<JsonObject> blocks)
    {
        var latest = new Dictionary<string, Dictionary<string, JsonObject>>();
        foreach (var b in blocks)
        {
            var p = Get(b, "payload"); if (Str(Get(p, "type")) != "vote") continue;
            var target = Str(Get(p, "target")) ?? "#" + Blocks.Canon(Get(p, "target")); var author = Str(Get(b, "author")) ?? "#null";
            var m = latest.TryGetValue(target, out var mm) ? mm : latest[target] = [];
            if (!m.TryGetValue(author, out var prev) || Num(b["height"]) > Num(prev["height"]) || (Num(b["height"]) == Num(prev["height"]) && Num(b["ts"]) > Num(prev["ts"]))) m[author] = b;
        }
        var o = new Dictionary<string, VoteTally>();
        foreach (var (video, m) in latest)
        {
            int up = 0, down = 0; var by = new Dictionary<string, double>(); var recent = new List<(double, double)>(); var bl = new List<JsonObject>();
            foreach (var (author, b) in m)
            {
                var v = Num(Get(Get(b, "payload"), "v")) ?? double.NaN;
                if (v > 0) up++; else if (v < 0) down++;
                by[author] = v;
                if (v != 0 && !double.IsNaN(v)) { recent.Add((Num(b["ts"])!.Value, v)); bl.Add(b); }
            }
            o[video] = new VoteTally(up, down, by, recent, bl);
        }
        return o;
    }
}
