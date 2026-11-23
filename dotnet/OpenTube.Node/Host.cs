using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTube.Core;
using OpenTube.Core.BitTorrent;

namespace OpenTube.Node;

/// <summary>
/// Video hosting (as the JavaScript node's host.mjs): keeps the videos of the channels its owner (and the people it's
/// shared with for hosting) joined or own, in a folder with the browser's layout (&lt;videos&gt;/&lt;Channel name&gt;/,
/// channel.json, .opentube/&lt;infohash&gt;.torrent), and shares them over BitTorrent: with browsers over WebRTC on the
/// meet places (entry nodes' trackers), the network's and the site's trackers, and with other computers over TCP.
/// The owner changes what it keeps from the site (Manage files): pin, drop, restore, keep a channel or single video,
/// stop a channel, remove a channel's files, the size limit; these live in &lt;videos&gt;/.opentube-node.json.
/// Over the limit, the most widely held videos go first; never the owner's own channels, pinned videos, or videos with
/// fewer than 2 other proven copies. OBS and relayed recordings are seeded from the channel's folder too.
/// </summary>
public sealed class Host : IAsyncDisposable
{
    const int MinCopies = 2;
    readonly OpenTubeNode node; readonly string videoDir; readonly double capGB; readonly Action<string> log;
    public readonly BtClient Bt; public readonly WebTorrentPeers Web; public readonly PieceTabs Tabs;
    sealed class Folder { public required string Dir, Name; public required JsonObject Meta; public bool OverCap; }
    readonly Dictionary<string, Folder> folders = [];
    readonly Dictionary<string, (string Chain, TorrentData T)> torrents = [];
    readonly Dictionary<string, Dictionary<string, double>> proven = [];
    readonly JsonObject ov; readonly string ovFile;
    readonly object gate = new();
    readonly CancellationTokenSource stop = new();
    List<string> trackers = [];

    public Host(OpenTubeNode node, string videoDir, double capGB)
    {
        this.node = node; this.videoDir = Path.GetFullPath(videoDir); this.capGB = capGB; log = node.Log;
        Bt = new BtClient(SHA1.HashData(Encoding.UTF8.GetBytes("opentube-node:" + node.NodeKey))) { Log = _ => { } };   // browsers recognize this node's bytes
        Web = new WebTorrentPeers(Bt, log);
        Tabs = new PieceTabs(node); Bt.OnSession = Tabs.Attach;   // pay per piece on every connection
        Directory.CreateDirectory(this.videoDir);
        ovFile = Path.Combine(this.videoDir, ".opentube-node.json");
        ov = (File.Exists(ovFile) ? Try(() => JsonNode.Parse(File.ReadAllText(ovFile)) as JsonObject) : null) ?? [];
        foreach (var k in new[] { "pinned", "dropped", "channels", "unchannels", "videos", "recs" }) ov[k] ??= new JsonArray();
        foreach (var dir in Directory.GetDirectories(this.videoDir))
        {
            var meta = Try(() => JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "channel.json"))) as JsonObject);
            if (meta != null && (string?)meta["app"] == "OpenTube" && (string?)meta["network"] == Network.MainId && (string?)meta["chain"] is { } c)
            { meta["videos"] ??= new JsonObject(); folders[c] = new Folder { Dir = dir, Name = Path.GetFileName(dir), Meta = meta }; }
        }
    }
    static T? Try<T>(Func<T> f) { try { return f(); } catch { return default; } }
    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static string CleanName(string? s, string fallback = "Untitled")
    {
        var t = new string((s ?? "").Select(c => "\\/:*?\"<>|".Contains(c) || c < 32 || c == 127 ? ' ' : c).ToArray());
        t = string.Join(' ', t.Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimStart('.');
        t = t.Length > 100 ? t[..100].Trim() : t.Trim();
        return t.Length == 0 ? fallback : t;
    }
    JsonArray L(string k) => (JsonArray)ov[k]!;
    static bool In(JsonArray a, string x) => a.Any(v => (string?)v == x);
    static void Toggle(JsonArray a, string x, bool on) { var i = a.ToList().FindIndex(v => (string?)v == x); if (on && i < 0) a.Add(x); if (!on && i >= 0) a.RemoveAt(i); }
    void SaveOv() { try { File.WriteAllText(ovFile, ov.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); } catch (Exception e) { log("[host] overrides: " + e.Message); } }
    void SaveMeta(Folder f) { try { File.WriteAllText(Path.Combine(f.Dir, "channel.json"), f.Meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); } catch (Exception e) { log("[host] channel.json: " + e.Message); } }
    double Cap() => (Js.Num(ov["capGB"]) ?? capGB) * 1e9;

    public async Task Run()
    {
        Bt.Listen(0);
        trackers = node.MeshTrackerUrls();
        log($"[host] hosting for {node.OwnerUser?[..8]}… in {videoDir} ({trackers.Count} trackers, BitTorrent on TCP port {Bt.Port})");
        _ = Task.Run(async () => { while (!stop.IsCancellationRequested) { await Task.Delay(4000, stop.Token).ContinueWith(_ => { }); try { Tabs.Tick(Bt.Sessions); } catch (Exception e) { log("[pay] tabs: " + e.Message); } } });
        await Task.Delay(3000, stop.Token).ContinueWith(_ => { });
        int tick = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (tick % 2 == 0) UpdateTrackers();
                Follow(); SettleRecordings();
            }
            catch (Exception e) { log("[host] " + e.Message); }
            tick++;
            await Task.Delay(15000, stop.Token).ContinueWith(_ => { });
        }
    }
    /// <summary>Meet places that went offline stop being used; new ones are announced on at once.</summary>
    void UpdateTrackers()
    {
        var now = node.MeshTrackerUrls(); var gone = trackers.Except(now).ToList(); trackers = now;
        if (gone.Count > 0) { Web.Drop(gone); log($"[host] meet place{(gone.Count > 1 ? "s" : "")} no longer used: {string.Join(", ", gone.Select(u => u.Replace("wss://", "").Replace("ws://", "").Replace("/tracker", "")))}"); }
        lock (gate) foreach (var (_, x) in torrents) Web.Announce(x.T, trackers);
    }

    // ---- what to keep: the owner's and hosting people's joined and own channels, and the owner's overrides
    List<string> People() => new[] { node.OwnerUser }.Concat(((JsonObject?)node.Obs?.Store["users"])?.Where(kv => (kv.Value?["can"] as JsonArray ?? []).Any(x => (string?)x == "host")).Select(kv => kv.Key) ?? []).Where(x => x != null).Select(x => x!).ToList();
    (HashSet<string> Joined, HashSet<string> Own, bool Found) Membership() => node.Engine.Read(e =>
    {
        var j = new HashSet<string>(); var o = new HashSet<string>(); var found = false;
        foreach (var u in People())
        {
            if (!e.Memberships.TryGetValue(u, out var m)) continue; found = true;
            foreach (var x in m["joined"] as JsonArray ?? []) if ((string?)x is { } s) j.Add(s);
            foreach (var x in m["own"] as JsonArray ?? []) if ((string?)x is { } s) o.Add(s);
        }
        return (j, o, found);
    });
    HashSet<string> Wanted()
    {
        var (j, _, _) = Membership();
        var w = new HashSet<string>(j.Concat(L("channels").Select(x => (string)x!)).Concat(L("videos").Select(v => (string)v!["chain"]!)));
        foreach (var x in L("unchannels")) w.Remove((string)x!);
        return w;
    }
    public void Follow()
    {
        var (joined, _, found) = Membership();
        if (!found) foreach (var u in People()) node.Engine.RequestMembership(u);
        var want = Wanted();
        var extraOnly = L("videos").Select(v => (string)v!["chain"]!).Where(c => !joined.Contains(c) && !In(L("channels"), c)).ToHashSet();
        foreach (var chain in want)
        {
            var c = node.Engine.OpenChain(chain); if (c == null) continue;
            node.Engine.RequestChain(c);
            var vids = node.Engine.Read(e => e.Store.Chains.TryGetValue(chain, out var ch) && (string?)ch.Genesis["payload"]?["kind"] == "channel" ? Derived.Of(ch).Videos.Select(b => (JsonObject)b.DeepClone()).ToList() : []);
            foreach (var b in vids)
            {
                if (!node.Engine.PostPaid(b)) continue;
                var ih = (string?)b["payload"]?["infohash"]; if (ih == null) continue;
                if (extraOnly.Contains(chain) && !L("videos").Any(v => (string?)v!["ih"] == ih)) continue;   // only the single videos asked for
                Ensure(chain, (JsonObject)b["payload"]!);
            }
        }
        foreach (var r in L("recs").OfType<JsonObject>())
            Ensure((string)r["chain"]!, new JsonObject { ["infohash"] = r["ih"]?.DeepClone(), ["file"] = r["file"]?.DeepClone(), ["title"] = r["title"]?.DeepClone(), ["size"] = r["size"]?.DeepClone() });
        lock (gate)
            foreach (var (ih, x) in torrents.ToList())
                if (!want.Contains(x.Chain) && !In(L("pinned"), ih)) { Bt.Remove(ih); Web.Forget(ih); torrents.Remove(ih); }   // left: stop sharing (files stay)
    }

    Folder FolderFor(string chain)
    {
        lock (gate)
        {
            if (folders.TryGetValue(chain, out var f)) return f;
            var (title, owner) = node.Engine.Read(e => e.Store.Chains.TryGetValue(chain, out var c) ? ((string?)Derived.Of(c).Profile["name"], c.Owner) : (null, null));
            title ??= "Channel " + chain[..8];
            var name = CleanName(title, "Channel");
            if (Directory.Exists(Path.Combine(videoDir, name))) name = $"{name} ({chain[..8]})";
            f = new Folder
            {
                Dir = Path.Combine(videoDir, name), Name = name,
                Meta = new JsonObject { ["app"] = "OpenTube", ["note"] = "Videos of this OpenTube channel, kept and shared by your OpenTube node.", ["network"] = Network.MainId, ["chain"] = chain, ["key"] = owner, ["name"] = title, ["videos"] = new JsonObject() },
            };
            Directory.CreateDirectory(Path.Combine(f.Dir, ".opentube"));
            folders[chain] = f; SaveMeta(f);
            return f;
        }
    }
    JsonObject Videos(Folder f) => (JsonObject)f.Meta["videos"]!;

    /// <summary>One video: share it if the file is here, otherwise download it (into the channel's folder), then keep sharing.</summary>
    void Ensure(string chain, JsonObject v)
    {
        var ih = (string?)v["infohash"]; if (ih == null || !Js.IsHex(v["infohash"], 40)) return;
        lock (gate) if (torrents.ContainsKey(ih) || In(L("dropped"), ih)) return;
        var f = FolderFor(chain);
        TorrentData t;
        lock (gate)
        {
            if (torrents.ContainsKey(ih)) return;
            var vids = Videos(f);
            var entry = vids[ih] as JsonObject;
            if (entry == null) { entry = new JsonObject { ["file"] = CleanName((string?)v["file"], "video"), ["name"] = v["file"]?.DeepClone(), ["title"] = v["title"]?.DeepClone(), ["size"] = v["size"]?.DeepClone(), ["complete"] = false, ["ts"] = Now() }; vids[ih] = entry; SaveMeta(f); }
            var target = Path.GetFullPath(Path.Combine(f.Dir, (string)entry["file"]!));
            if (!target.StartsWith(Path.GetFullPath(f.Dir) + Path.DirectorySeparatorChar)) { log($"[host] refused {ih}: bad file name"); return; }
            var infoFile = Path.Combine(f.Dir, ".opentube", ih + ".torrent");
            var meta = File.Exists(infoFile) ? Try(() => Bencode.RawValue(File.ReadAllBytes(infoFile), "info") is { } info ? TorrentMeta.FromInfoBytes(info) : null) : null;
            t = TorrentData.Fetch(ih, target, meta);
            torrents[ih] = (chain, t);
            var e = entry; var title = (string?)e["title"] ?? (string?)e["file"];
            if (t.Complete) log($"[host] sharing “{title}” from {f.Name}/{e["file"]}");
            else log($"[host] {(Js.IsTrue(e["complete"]) ? "checking" : "downloading")} “{title}” into {f.Name}/");
            t.MetaReady += td => { try { File.WriteAllBytes(infoFile, TorrentFile(td.Meta!)); } catch { } };
            t.Done += td =>
            {
                lock (gate) { if (!Js.IsTrue(e["complete"])) log($"[host] saved “{title}” to {f.Name}/"); e["complete"] = true; e["size"] = td.Meta!.Length; SaveMeta(f); }
                Trim(chain);
            };
            if (t.Complete && !Js.IsTrue(e["complete"])) { e["complete"] = true; e["size"] = t.Meta!.Length; SaveMeta(f); }
        }
        Bt.Add(t); Web.Announce(t, trackers);
    }
    /// <summary>A .torrent file (bencoded {info, announce-list}) so the next start needs no metadata from peers.</summary>
    byte[] TorrentFile(TorrentMeta m)
    {
        var head = Bencode.Encode(new Dictionary<string, object?> { ["announce"] = trackers.FirstOrDefault() ?? "", ["created by"] = "OpenTube" });
        // splice the exact info bytes in (its hash is the infohash): d…announce…created by…4:info<raw>e
        return [.. head[..^1], .. Encoding.ASCII.GetBytes("4:info"), .. m.InfoBytes, (byte)'e'];
    }
    void Remove(string ih, string why)
    {
        lock (gate)
        {
            if (torrents.Remove(ih)) { Bt.Remove(ih); Web.Forget(ih); }
            foreach (var f in folders.Values)
            {
                if (Videos(f)[ih] is not JsonObject e) continue;
                Try(() => { File.Delete(Path.Combine(f.Dir, (string)e["file"]!)); File.Delete(Path.Combine(f.Dir, ".opentube", ih + ".torrent")); return 0; });
                Videos(f).Remove(ih); SaveMeta(f); log($"[host] removed “{(string?)e["title"] ?? (string?)e["file"]}” ({why})");
            }
        }
    }
    (int Proven, int Seeds, int Others) Copies(string ih)
    {
        lock (gate)
        {
            var fresh = proven.TryGetValue(ih, out var m) ? m.Values.Count(at => Now() - at < 2 * 3600000) : 0;
            return (fresh, 0, fresh);
        }
    }
    /// <summary>Keep each channel under the size limit (see the class summary for what's never removed).</summary>
    void Trim(string chain)
    {
        Folder? f; lock (gate) if (!folders.TryGetValue(chain, out f)) return;
        if (Membership().Own.Contains(chain)) return;
        List<(string Ih, JsonObject E)> vids; lock (gate) vids = Videos(f).Select(kv => (kv.Key, (JsonObject)kv.Value!)).ToList();
        double total = vids.Sum(x => Js.Num(x.E["size"]) ?? 0);
        if (total <= Cap()) { f.OverCap = false; return; }
        foreach (var (ih, e) in vids.Where(x => !In(L("pinned"), x.Ih) && Copies(x.Ih).Others >= MinCopies).OrderByDescending(x => Copies(x.Ih).Others).ThenBy(x => Js.Num(x.E["ts"]) ?? 0))
        {
            if (total <= Cap()) break;
            total -= Js.Num(e["size"]) ?? 0; Remove(ih, $"over the {Cap() / 1e9} GB limit for {f.Name}; {Copies(ih).Others} other copies exist");
        }
        f.OverCap = total > Cap();
    }
    public void NoteProof(string ih, string key, bool ok) { lock (gate) { var m = proven.TryGetValue(ih, out var x) ? x : proven[ih] = []; if (ok) m[key] = Now(); else m.Remove(key); } }

    // ---- the owner's file manager (commands arrive signed and checked by NodeControl)
    public JsonObject Files()
    {
        var (joined, own, _) = Membership(); var want = Wanted();
        lock (gate)
        {
            var chans = new JsonArray();
            foreach (var chain in folders.Keys.Concat(want).Distinct())
            {
                folders.TryGetValue(chain, out var f);
                var name = (string?)f?.Meta["name"] ?? node.Engine.Read(e => e.Store.Chains.TryGetValue(chain, out var c) ? (string?)Derived.Of(c).Profile["name"] : null) ?? chain[..8];
                var vids = new JsonArray(); double used = 0;
                foreach (var (ih, en) in f != null ? Videos(f) : [])
                {
                    var e = (JsonObject)en!; torrents.TryGetValue(ih, out var x); var t = x.T; var c = Copies(ih);
                    var size = Js.Num(e["size"]) ?? t?.Meta?.Length ?? 0; var complete = Js.IsTrue(e["complete"]);
                    if (complete) used += size;
                    vids.Add(new JsonObject
                    {
                        ["ih"] = ih, ["title"] = e["title"]?.DeepClone(), ["file"] = e["file"]?.DeepClone(), ["size"] = size, ["complete"] = complete,
                        ["progress"] = t?.Meta != null ? (int)Math.Round(100.0 * t.HaveCount / Math.Max(1, t.Meta.PieceCount)) : complete ? 100 : 0,
                        ["sharing"] = t != null, ["peers"] = t != null ? Bt.PeersOf(ih) : 0, ["pinned"] = In(L("pinned"), ih), ["proven"] = c.Proven, ["seeds"] = c.Seeds,
                    });
                }
                chans.Add(new JsonObject
                {
                    ["chain"] = chain, ["name"] = name, ["folder"] = f?.Name, ["own"] = own.Contains(chain), ["joined"] = joined.Contains(chain),
                    ["extra"] = In(L("channels"), chain), ["stopped"] = In(L("unchannels"), chain), ["overCap"] = f?.OverCap == true, ["used"] = used, ["videos"] = vids,
                });
            }
            return new JsonObject { ["videoDir"] = videoDir, ["capGB"] = Js.Num(ov["capGB"]) ?? capGB, ["minCopies"] = MinCopies, ["dropped"] = L("dropped").DeepClone(), ["channels"] = chans };
        }
    }
    public JsonObject Command(string cmd, JsonObject a)
    {
        string Ih() => Js.IsHex(a["ih"], 40) ? (string)a["ih"]! : throw new Exception("bad video");
        string Chain() => Js.IsHex(a["chain"], 64) ? (string)a["chain"]! : throw new Exception("bad channel");
        var on = !(a["on"] is JsonValue ov2 && ov2.GetValueKind() == JsonValueKind.False);
        switch (cmd)
        {
            case "files": return Files();
            case "pin": Toggle(L("pinned"), Ih(), on); break;
            case "drop":   // delete it here and don't download it again (undo with restore)
            {
                var ih = Ih(); Toggle(L("dropped"), ih, true); Toggle(L("pinned"), ih, false);
                foreach (var v in L("videos").Where(v => (string?)v!["ih"] == ih).ToList()) L("videos").Remove(v);
                Remove(ih, "dropped by the owner"); break;
            }
            case "restore": Toggle(L("dropped"), Ih(), false); break;
            case "keepChannel": { var c = Chain(); Toggle(L("channels"), c, on); if (on) Toggle(L("unchannels"), c, false); break; }
            case "stopChannel": { var c = Chain(); Toggle(L("unchannels"), c, on); if (on) Toggle(L("channels"), c, false); break; }
            case "keepVideo":
            {
                var c = Chain(); var ih = Ih();
                if (!on) { foreach (var v in L("videos").Where(v => (string?)v!["ih"] == ih).ToList()) L("videos").Remove(v); }
                else if (!L("videos").Any(v => (string?)v!["ih"] == ih)) L("videos").Add(new JsonObject { ["chain"] = c, ["ih"] = ih });
                Toggle(L("dropped"), ih, false); break;
            }
            case "removeChannelFiles":
            {   // delete the files of a channel this node no longer hosts
                var c = Chain(); Folder? f; lock (gate) folders.TryGetValue(c, out f); if (f == null) break;
                var want = Wanted();
                foreach (var ih in Videos(f).Select(kv => kv.Key).ToList()) { bool sharing; lock (gate) sharing = torrents.ContainsKey(ih); if (!sharing || !want.Contains(c)) Remove(ih, "removed by the owner"); }
                break;
            }
            case "cap": { var gb = Js.Num(a["gb"]); if (!(gb > 0 && gb <= 100000)) throw new Exception("bad limit"); ov["capGB"] = gb; break; }
            default: throw new Exception("unknown command " + cmd);
        }
        SaveOv(); Follow(); foreach (var c in folders.Keys.ToList()) Trim(c);
        return Files();
    }
    public static readonly string[] Commands = ["files", "pin", "drop", "restore", "keepChannel", "stopChannel", "keepVideo", "removeChannelFiles", "cap"];

    // ---- recordings: OBS and relayed streams are written into the channel's folder and shared like any video; the
    // channel's owner then publishes it (a video block) or discards it (deleted here)
    public (string Full, string File) RecordingPath(string chain, string title, string ext = ".mp4")
    {
        var f = FolderFor(chain); var b = CleanName(title, "Live"); var name = b + ext;
        for (int i = 2; File.Exists(Path.Combine(f.Dir, name)); i++) name = $"{b} ({i}){ext}";
        return (Path.Combine(f.Dir, name), name);
    }
    public JsonObject SeedRecording(string chain, string file, string title, string stream)
    {
        var f = FolderFor(chain); var full = Path.Combine(f.Dir, file);
        var t = TorrentData.Seed(full, file); var ih = t.InfoHashHex;
        lock (gate)
        {
            torrents[ih] = (chain, t);
            Videos(f)[ih] = new JsonObject { ["file"] = file, ["name"] = file, ["title"] = title, ["size"] = t.Meta!.Length, ["complete"] = true, ["own"] = true, ["live"] = stream, ["ts"] = Now() };
            if (!In(L("pinned"), ih)) { L("pinned").Add(ih); SaveOv(); }   // kept until published or discarded
            Try(() => { File.WriteAllBytes(Path.Combine(f.Dir, ".opentube", ih + ".torrent"), TorrentFile(t.Meta)); return 0; });
            SaveMeta(f);
        }
        Bt.Add(t); Web.Announce(t, trackers);
        log($"[host] recorded “{title}” to {f.Name}/{file} ({t.Meta.Length / 1e6:0.0} MB), sharing it");
        return new JsonObject { ["infohash"] = ih, ["size"] = t.Meta.Length, ["file"] = file };
    }
    void SettleRecordings()
    {
        List<(string Chain, Folder F)> fs; lock (gate) fs = folders.Select(kv => (kv.Key, kv.Value)).ToList();
        foreach (var (chain, f) in fs)
        {
            var ends = node.Engine.Read(e => e.Store.Chains.TryGetValue(chain, out var c) ? new Dictionary<string, string?>(Derived.Of(c).LiveEnds) : null);
            if (ends == null) continue;
            List<(string Ih, JsonObject E)> vids; lock (gate) vids = Videos(f).Select(kv => (kv.Key, (JsonObject)kv.Value!)).ToList();
            foreach (var (ih, e) in vids)
            {
                if ((string?)e["live"] is not { } s || !ends.TryGetValue(s, out var outcome)) continue;
                if (outcome == "discarded") { Toggle(L("pinned"), ih, false); SaveOv(); Remove(ih, "the owner discarded this recording"); }
                else if (outcome == "published") lock (gate) { e.Remove("live"); SaveMeta(f); }
            }
        }
    }
    /// <summary>"Keep this video": someone allowed to use this computer uploaded it from a browser without a video folder; it's downloaded from their browser right away and kept.</summary>
    public async Task StoreVideo(string chain, string ih)
    {
        if (node.Engine.OpenChain(chain) is { } c) node.Engine.RequestChain(c);
        for (int i = 0; i < 60; i++)
        {
            var b = node.Engine.Read(e => e.Store.Chains.TryGetValue(chain, out var ch) ? Derived.Of(ch).Videos.FirstOrDefault(v => (string?)v["payload"]?["infohash"] == ih)?.DeepClone() as JsonObject : null);
            if (b != null)
            {
                if (!L("videos").Any(v => (string?)v!["ih"] == ih)) L("videos").Add(new JsonObject { ["chain"] = chain, ["ih"] = ih });
                Toggle(L("pinned"), ih, true); Toggle(L("dropped"), ih, false); SaveOv();
                Ensure(chain, (JsonObject)b["payload"]!); return;
            }
            await Task.Delay(500);
        }
        throw new Exception("this computer hasn’t received the video’s block yet: try again in a moment");
    }
    public JsonObject? ProgressOf(string ih)
    {
        lock (gate)
        {
            if (!torrents.TryGetValue(ih, out var x)) return null; var t = x.T;
            return new JsonObject { ["progress"] = t.Meta == null ? 0 : (double)t.HaveCount / Math.Max(1, t.Meta.PieceCount), ["peers"] = Bt.PeersOf(ih), ["done"] = t.Complete, ["size"] = t.Meta?.Length };
        }
    }
    /// <summary>Live chunks: a few seconds of a stream as one small torrent, shared for 6 hours.</summary>
    public string SeedChunk(byte[] bytes, string name)
    {
        var dir = Path.Combine(videoDir, ".live-chunks"); Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, name); File.WriteAllBytes(file, bytes);
        var t = TorrentData.Seed(file, name); Bt.Add(t); Web.Announce(t, trackers);
        _ = Task.Delay(TimeSpan.FromHours(6)).ContinueWith(_ => { Bt.Remove(t.InfoHashHex); Web.Forget(t.InfoHashHex); Try(() => { File.Delete(file); return 0; }); });
        return t.InfoHashHex;
    }
    /// <summary>A recording another of the owner's computers made (a flash computer that will be deleted): a copy is kept here, pinned, shared like any video.</summary>
    public void KeepRecording(string chain, JsonObject rec)
    {
        if (!Js.IsHex(rec["infohash"], 40)) return; var ih = (string)rec["infohash"]!;
        if (!L("recs").Any(r => (string?)r!["ih"] == ih))
        {
            L("recs").Add(new JsonObject { ["chain"] = chain, ["ih"] = ih, ["file"] = (Js.Str(rec["file"]) ?? "recording.mp4")[..Math.Min(200, (Js.Str(rec["file"]) ?? "recording.mp4").Length)], ["title"] = Js.Str(rec["title"]) ?? Js.Str(rec["file"]) ?? "Recording", ["size"] = rec["size"]?.DeepClone() });
            Toggle(L("pinned"), ih, true); SaveOv(); log($"[host] keeping a copy of the flash computer’s recording “{rec["file"]}”");
        }
        Ensure(chain, new JsonObject { ["infohash"] = ih, ["file"] = rec["file"]?.DeepClone(), ["title"] = rec["title"]?.DeepClone() ?? rec["file"]?.DeepClone(), ["size"] = rec["size"]?.DeepClone() });
    }
    public int? PieceLengthOf(string ih) { lock (gate) return torrents.TryGetValue(ih, out var x) ? x.T.Meta?.PieceLength : null; }
    public bool HasComplete(string ih) { lock (gate) return torrents.TryGetValue(ih, out var x) && x.T.Complete; }
    /// <summary>Storage proofs: one piece of a video this node has completely.</summary>
    public byte[]? Piece(string ih, int index)
    {
        TorrentData? t; lock (gate) t = torrents.TryGetValue(ih, out var x) && x.T.Complete ? x.T : null;
        if (t?.Meta == null || index < 0 || index >= t.Meta.PieceCount) return null;
        var n = t.Meta.SizeOf(index); var b = new byte[n];
        using var fs = new FileStream(t.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); fs.Position = (long)index * t.Meta.PieceLength; fs.ReadExactly(b); return b;
    }
    public List<(string Ih, string Chain)> Complete() { lock (gate) return torrents.Where(kv => kv.Value.T.Complete).Select(kv => (kv.Key, kv.Value.Chain)).ToList(); }
    public JsonObject Status()
    {
        lock (gate)
        {
            var list = torrents.Values.Select(x => x.T).ToList(); var done = list.Where(t => t.Complete).ToList();
            return new JsonObject
            {
                ["at"] = Now(), ["videoDir"] = videoDir, ["channels"] = Wanted().Count, ["found"] = Membership().Found, ["hosting"] = done.Count,
                ["hostedBytes"] = done.Sum(t => t.Meta!.Length), ["downloading"] = list.Count - done.Count, ["peers"] = Bt.PeerCount,
                ["overCap"] = folders.Values.Any(f => f.OverCap), ["held"] = new JsonArray(done.Select(t => (JsonNode?)t.InfoHashHex).ToArray()),
            };
        }
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); await Web.DisposeAsync(); await Bt.DisposeAsync(); }
}
