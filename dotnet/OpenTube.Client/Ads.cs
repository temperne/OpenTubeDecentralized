using System.Text;
using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>
/// Ads (as the JavaScript page): an advertiser publishes an ad (a small video and signed terms: price per view, how many
/// views, how long it is, whether other viewers must confirm it); channels and lists sponsor advertisers, whose ads then
/// play before their videos. Everyone who starts the same ad in the same 10-second slot watches it in sync: each second
/// they send a hash of that second of the ad's bytes, and at the end each signs a witness for the others it saw. The
/// viewer then claims the view (a transaction): half the price to them as an ad-view certificate, half to the creator.
/// </summary>
public static class Ads
{
    public const double SlotMs = 10000, SyncRatio = 0.8; public const int MaxDuration = 120;
    public static string TermsId(JsonNode terms) => Blocks.Sha256Hex("opentube-adterms:" + Blocks.Canon(Without(terms, "sig")));
    public static string SessionId(string tid, long slot) => Blocks.Sha256Hex($"opentube-session:{tid}:{slot}");
    static JsonObject Without(JsonNode o, string key) { var c = (JsonObject)o.DeepClone(); c.Remove(key); return c; }

    /// <summary>Advertisers whose ads run on a channel: its own, its sponsors, and the sponsors of lists that include it.</summary>
    public static List<string> SponsorsFor(this ClientSession s, string chain)
    {
        var ids = new HashSet<string>(); var d = s.Chain(chain);
        if (d?.Ads.Count > 0) ids.Add(chain);
        foreach (var b in d?.Sponsors ?? []) if ((string?)b["payload"]?["advertiser"] is { } a) ids.Add(a);
        foreach (var l in s.Directory().Where(c => c.Kind == "list"))
            if (s.Chain(l.Id) is { } ld && ld.Adds.Any(a => (string?)a["payload"]?["target"] == chain))
                foreach (var b in ld.Sponsors) if ((string?)b["payload"]?["advertiser"] is { } a) ids.Add(a);
        return [.. ids];
    }
    public sealed record Candidate(JsonObject Block, JsonObject Terms, string Tid, string Channel, string Name);
    /// <summary>An ad to show before a video on this channel (waits up to ~8 s for sponsors' chains to load), or null.</summary>
    public static async Task<Candidate?> PickAd(this ClientSession s, string chain, HashSet<string>? mutedTags = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (true)
        {
            var cands = new List<Candidate>(); var advs = s.SponsorsFor(chain);
            foreach (var a in advs)
            {
                var d = s.Chain(a); if (d == null) continue;
                foreach (var b in d.Ads)
                {
                    var t = b["payload"]?["terms"] as JsonObject; if (t == null) continue;
                    var tid = TermsId(t); var adv = (string?)t["advertiser"] ?? "";
                    var (views, bal) = s.Engine.Read(e => (e.Store.Ledger.AdViews.GetValueOrDefault(tid), e.Store.Ledger.Balance(adv)));
                    if (views >= (Js.Num(t["maxViews"]) ?? 0) || bal < (Js.Num(t["perView"]) ?? 0) || s.IsMine(adv)) continue;
                    if (mutedTags != null && (b["payload"]?["tags"] as JsonArray ?? []).Any(x => mutedTags.Contains((string?)x ?? ""))) continue;
                    cands.Add(new((JsonObject)b.DeepClone(), (JsonObject)t.DeepClone(), tid, a, (string?)d.Profile["name"] ?? ""));
                }
            }
            if (cands.Count > 0) return cands[Random.Shared.Next(cands.Count)];
            if (advs.Count == 0 && s.Chain(chain) != null) return null;
            if (DateTime.UtcNow > deadline) return null;
            await Task.Delay(400);
        }
    }

    /// <summary>Publish an ad on your channel: the ad file is already shared (its torrent); the terms are signed by the channel's key.</summary>
    public static async Task CreateAd(this ClientSession s, string chain, string infohash, string file, long size, int duration, string title, string url, string category, List<string> tags, string thumb, long perView, int maxViews, bool confirm)
    {
        var d = s.Chain(chain) ?? throw new InvalidOperationException("The channel hasn’t loaded");
        var key = s.KeyOf(d.Owner!) ?? throw new InvalidOperationException("You don’t hold this channel’s key");
        if (title.Trim().Length == 0) throw new ArgumentException("Give it a title");
        if (category.Length == 0) throw new ArgumentException("Choose what kind of product or service the ad is for");
        if (duration < 1 || duration > MaxDuration) throw new ArgumentException($"Ads must be 1–{MaxDuration} seconds");
        if (url.Trim().Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(url.Trim(), @"^https://\S+$")) throw new ArgumentException("The link must start with https://");
        if (perView < 2 || maxViews < 1) throw new ArgumentException("Check the price and maximum views");
        var terms = new JsonObject { ["v"] = 1, ["kind"] = "adterms", ["advertiser"] = d.Owner, ["channel"] = chain, ["infohash"] = infohash, ["size"] = size, ["duration"] = duration, ["perView"] = perView, ["maxViews"] = maxViews, ["created"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["witnesses"] = confirm ? 1 : 0 };
        terms["sig"] = key.Sign("opentube-adterms:" + Blocks.Canon(terms));
        var p = new JsonObject { ["type"] = "ad", ["title"] = title.Trim()[..Math.Min(140, title.Trim().Length)], ["file"] = file[..Math.Min(200, file.Length)], ["thumb"] = thumb, ["url"] = url.Trim(), ["terms"] = terms, ["category"] = category };
        if (tags.Count > 0) p["tags"] = new JsonArray(tags.Take(8).Select(t => (JsonNode?)t).ToArray());
        await Task.Run(() => s.Engine.Append(chain, p));
    }
    /// <summary>Where an advertiser's ads run (channels and lists that sponsor it).</summary>
    public static List<string> WhereAdsRun(this ClientSession s, string advertiser) =>
        s.Directory().Where(c => s.Chain(c.Id)?.Sponsors.Any(b => (string?)b["payload"]?["advertiser"] == advertiser) == true).Select(c => c.Id).ToList();
    public static double AdViews(this ClientSession s, string tid) => s.Engine.Read(e => e.Store.Ledger.AdViews.GetValueOrDefault(tid));

    /// <summary>One ad viewing, in sync with everyone else who starts the same ad in the same slot.</summary>
    public sealed class Viewing
    {
        readonly ClientSession s; readonly Candidate ad; readonly byte[] bytes; readonly string video, creator;
        readonly Dictionary<string, HashSet<int>> matches = []; readonly Dictionary<string, JsonObject> witnesses = [];
        readonly Dictionary<int, string> mine = [];
        public string Status { get; private set; } = "";
        public int InSync { get { lock (matches) return matches.Values.Count(x => x.Count >= 1); } }
        public bool Skipped { get; set; }
        long start; string session = ""; long slot; int duration;
        public Viewing(ClientSession s, Candidate ad, byte[] bytes, string video, string creator) { this.s = s; this.ad = ad; this.bytes = bytes; this.video = video; this.creator = creator; }
        static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string HashAt(int i)
        {
            if (mine.TryGetValue(i, out var h)) return h;
            var size = (long)(Js.Num(ad.Terms["size"]) ?? bytes.Length); var off = (int)Math.Floor((double)i * size / duration);
            var part = bytes.AsSpan(Math.Min(off, bytes.Length), Math.Max(0, Math.Min(1024, bytes.Length - off)));
            return mine[i] = Blocks.Sha256Hex(session + ":" + i + ":" + Convert.ToHexStringLower(part));
        }
        void OnWatch(JsonObject m)
        {
            if ((string?)m["s"] != session) return;
            if (m["w"] is JsonObject w && (string?)w["witness"] is { } wk && wk != s.Me.pub && (string?)w["session"] == session && (w["saw"] as JsonArray ?? []).Any(x => (string?)x == s.Me.pub))
                lock (witnesses) witnesses[wk] = (JsonObject)w.DeepClone();
            if ((string?)m["k"] is { } k && k != s.Me.pub && Js.Num(m["i"]) is double di && di >= 0 && di < duration && (string?)m["h"] is { } hh)
            {
                int i = (int)di; var late = Math.Abs(Now() - (start + i * 1000L));
                if (late <= 3000 && hh == HashAt(i)) lock (matches) { if (!matches.TryGetValue(k, out var set)) matches[k] = set = []; set.Add(i); }
            }
        }
        /// <summary>Waits for the slot (calls <paramref name="starting"/> just before it), then follows the ad second by second:
        /// <paramref name="playing"/>(i) says whether it's playing and visible at second i. Returns (ok, message).</summary>
        public async Task<(bool Ok, string Text)> Run(Func<Task> starting, Func<int, Task<bool>> playing, Action<string> status, CancellationToken ct)
        {
            var t = ad.Terms; duration = (int)(Js.Num(t["duration"]) ?? 1); var need = (int)Math.Ceiling(duration * SyncRatio);
            var solo = (Js.Num(t["witnesses"]) ?? 0) == 0;
            slot = solo ? (long)Math.Floor(Now() / SlotMs) : (long)Math.Ceiling((Now() + 1500) / SlotMs);
            start = solo ? Now() + 300 : slot * (long)SlotMs;
            session = SessionId(ad.Tid, slot);
            s.Engine.OnWatch += OnWatch;
            try
            {
                while (Now() < start - 200) { ct.ThrowIfCancellationRequested(); status($"Ad starts in {Math.Ceiling((start - Now()) / 1000.0)} s · syncing with other viewers of this ad…"); await Task.Delay(250, ct); }
                await starting();
                int sent = 0;
                for (int i = 0; i < duration; i++)
                {
                    var due = start + i * 1000L + 300; var wait = due - Now(); if (wait > 0) await Task.Delay((int)wait, ct);
                    if (Skipped) break;
                    if (await playing(i)) { s.Engine.SendWatch(new JsonObject { ["s"] = session, ["k"] = s.Me.pub, ["i"] = i, ["h"] = HashAt(i) }); sent++; }
                    status($"Ad · {duration - i} s left · in sync with {InSync} other viewer(s)");
                }
                List<string> saw; lock (matches) saw = matches.Where(kv => kv.Value.Count >= need).Select(kv => kv.Key).ToList();
                if (sent >= need)
                {
                    var w = new JsonObject { ["v"] = 1, ["kind"] = "witness", ["session"] = session, ["terms"] = ad.Tid, ["slot"] = slot, ["video"] = video, ["creator"] = creator, ["witness"] = s.Me.pub, ["saw"] = new JsonArray(saw.Select(x => (JsonNode?)x).ToArray()) };
                    w["sig"] = s.Me.Sign("opentube-witness:" + Blocks.Canon(w));
                    s.Engine.SendWatch(new JsonObject { ["s"] = session, ["w"] = w });
                }
                status(solo ? "Ad finished · getting paid…" : "Ad finished · collecting confirmations from the other viewers…");
                if (!solo) await Task.Delay(4000, ct);
                List<JsonObject> ws; lock (witnesses) ws = witnesses.Values.Take(20).ToList();
                if (sent < Math.Min(need, 3)) return (false, "Not counted: the ad was not playing (and visible) for long enough.");
                var needW = (int)(Js.Num(t["witnesses"]) ?? 0);
                if (ws.Count < needW) return (false, $"Not counted: this ad pays only views that {needW} other viewer(s) watched in sync with you, and nobody else was watching it right now.");
                var size = (long)(Js.Num(t["size"]) ?? bytes.Length);
                var got = Skipped ? Math.Max(1, (long)Math.Floor((double)size * Math.Min(sent, duration) / duration)) : size;
                await s.Engine.SubmitTx(new JsonObject { ["claim"] = new JsonObject { ["terms"] = t.DeepClone(), ["slot"] = slot, ["video"] = video, ["creator"] = creator, ["witnesses"] = new JsonArray(ws.Select(x => (JsonNode?)x).ToArray()), ["bytes"] = got } }, s.Me);
                var pay = Math.Max(1, Math.Floor((Js.Num(t["perView"]) ?? 0) * got / size)); var half = Math.Floor(pay / 2);
                return (true, $"✅ {(ws.Count > 0 ? $"View confirmed by {ws.Count} other viewer(s). " : "")}You get an ad-view certificate worth {ChainActions.Coins(half)} coins (Market → Ad views) and the creator gets {ChainActions.Coins(pay - half)}.");
            }
            catch (OperationCanceledException) { return (false, ""); }
            catch (Exception e) { return (false, "Claim rejected: " + e.Message); }
            finally { s.Engine.OnWatch -= OnWatch; }
        }
    }
}
