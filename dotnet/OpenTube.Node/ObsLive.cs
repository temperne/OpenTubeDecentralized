using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Node;

/// <summary>
/// Going live from OBS through this computer (the JavaScript node's goLiveSink): OBS's stream key picks whose stream it
/// is; the stream goes live on the channel that person linked this computer to (a `relay` block on the channel, made
/// from the site: Go live → OBS → pick this computer). This computer posts the live block, signs every piece of video,
/// sends the pieces to viewers (its own live hub when it has a public address, else an entry node's hub), records the
/// stream to a file, and posts the end of the stream when OBS stops.
/// Stream keys live in obs.json, in the JavaScript node's format, so a computer's settings carry over.
/// </summary>
public sealed class ObsLive
{
    readonly OpenTubeNode node;
    readonly string file;
    public JsonObject Store { get; }
    public ObsLive(OpenTubeNode node)
    {
        this.node = node; file = Path.Combine(node.Options.DataDir, "obs.json");
        Store = File.Exists(file) ? (JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? []) : [];
        Store["profiles"] ??= new JsonObject(); Store["users"] ??= new JsonObject();
    }
    public void Save() { File.WriteAllText(file, Store.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    static string NewKey() => "ot-" + B64u.Encode(RandomNumberGenerator.GetBytes(12));

    /// <summary>A person's OBS settings on this computer (made on first use): their stream key, linked channel, title.</summary>
    public JsonObject ProfileOf(string user)
    {
        var profiles = (JsonObject)Store["profiles"]!;
        if (profiles[user] is not JsonObject p) { p = new JsonObject(); profiles[user] = p; }
        p["key"] ??= NewKey(); if (!p.ContainsKey("channel")) p["channel"] = null; p["title"] ??= "Live";
        p["youtube"] ??= new JsonObject { ["key"] = "", ["on"] = false }; p["tile"] ??= new JsonObject { ["on"] = false, ["n"] = 4 };
        return p;
    }
    /// <summary>Channels whose owner linked this computer (it may go live on them).</summary>
    public List<string> LinkedChannels() => node.Engine.Read(e => e.Store.Chains.Values.Where(c => c != e.Store.Main && (string?)c.Genesis["payload"]?["kind"] == "channel")
        .Where(c => Derived.Of(c).Relays.Contains(node.NodeKey!)).Select(c => c.Id).ToList());

    /// <summary>The channel a person's OBS stream goes live on: the one they picked (if this computer is linked to it), else one they own that's linked.</summary>
    public string? ChannelFor(string user, JsonObject profile)
    {
        var linked = LinkedChannels();
        return (string?)profile["channel"] is { } c && linked.Contains(c) ? c : linked.FirstOrDefault(ch => node.Engine.Read(e => e.Store.Chains.TryGetValue(ch, out var x) ? x.Owner : null) is { } o && (o == user || node.Engine.Read(e => e.Store.Ledger.Resolve(o)) == node.Engine.Read(e => e.Store.Ledger.Resolve(user))))
            ?? (user == node.OwnerUser ? linked.FirstOrDefault() : null);
    }
    bool MayObs(string user) => user == node.OwnerUser || (Store["users"]?[user] is JsonObject u && (u["can"] as JsonArray ?? []).Any(x => (string?)x == "obs") && !(Js.Num(u["until"]) < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    public string NameOf(string user) => (string?)Store["users"]?[user]?["name"] is { Length: > 0 } n ? n : user == node.OwnerUser ? "the owner" : user[..8];
    public readonly Dictionary<string, GroupStudio> Groups = [];
    public GroupStudio GroupOf(string hostUser) { lock (Groups) return Groups.TryGetValue(hostUser, out var g) ? g : Groups[hostUser] = new GroupStudio(node, this, hostUser); }

    /// <summary>
    /// OBS published with this stream key: a sink for the stream, or why not. A person's key, or key-2…key-9 (more
    /// cameras into their group stream); an invited guest's key goes into their host's group stream.
    /// </summary>
    public object OnPublish(string key)
    {
        key = key.Trim(); string? user = null; int slot = 0;
        foreach (var (u, pn) in (JsonObject)Store["profiles"]!)
        {
            if (pn is not JsonObject p || !MayObs(u)) continue;
            var pk = (string?)p["key"]; var sl = key == pk ? 1 : Enumerable.Range(2, 8).FirstOrDefault(i => key == pk + "-" + i);
            if (sl > 0) { user = u; slot = sl; break; }
        }
        if (user == null) return "Wrong stream key. Copy it from Go live (or Devices) on the site, or from this computer’s window.";
        var who = NameOf(user);
        node.Log($"[obs] stream from {who}");
        var suffix = slot > 1 ? "-" + slot : "";
        if ((string?)Store["users"]?[user]?["guestOf"] is { } host)
        {   // an invited guest: into the host's group stream
            var hp = ProfileOf(host);
            if (!Js.IsTrue(hp["tile"]?["on"])) return "The group stream you were invited to isn’t on right now.";
            if (!Media.HasFfmpeg) return "Group streams need ffmpeg on this computer.";
            return (object?)GroupOf(host).AddInput("obs:" + user + suffix, who + (slot > 1 ? " " + slot : ""), user, "obs", "mp4", null) ?? "The group picture is full (9).";
        }
        var profile = ProfileOf(user);
        if (Js.IsTrue(profile["tile"]?["on"]))
        {
            if (!Media.HasFfmpeg) return "Group streams need ffmpeg on this computer.";
            if (ChannelFor(user, profile) == null) { node.Log("[obs] refused: no channel picked for this stream key (Devices → OBS → Channel)"); return "No channel picked for this stream key: on the site, Devices → OBS → Channel."; }
            return (object?)GroupOf(user).AddInput("obs:" + user + suffix, "host" + (slot > 1 ? $" (camera {slot})" : ""), user, "obs", "mp4", null) ?? "The group picture is full (9).";
        }
        if (slot > 1) return "The group stream is off on this computer: use the main stream key.";
        var chain = ChannelFor(user, profile);
        if (chain == null) return "Not linked to a channel yet. On the site: Go live → OBS → pick this computer and link it to your channel.";
        return NewSink(chain, (string?)profile["title"] ?? "Live", profile);
    }
    public Sink NewSink(string chain, string title, JsonObject profile)
    {
        var yt = profile["youtube"] as JsonObject;
        return new Sink(node, chain, title, Js.IsTrue(yt?["on"]) && (string?)yt?["key"] is { Length: > 0 } yk ? yk : null);
    }

    public sealed class Sink(OpenTubeNode node, string chain, string title, string? youtubeKey) : IStreamSink
    {
        readonly string stream = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        /// <summary>The live block's hash, once it's on the channel.</summary>
        public string? Block { get; private set; }
        public string Stream => stream;
        public string Chain => chain;
        /// <summary>The group picture restarted (its encoder): same stream, a new start.</summary>
        public void Reinit(byte[] header)
        {
            Piece(header, true);
            if (youtubeKey != null) { youtube?.End(); youtube = YouTube.Start(youtubeKey, node.Log); youtube?.Write(header); }
        }
        long n, offset; (Action<JsonObject> Seg, Action End, Action<JsonObject> Chunk)? local; RemoteBroadcast? remote; Chunker? chunks; YouTube? youtube; FileStream? rec; string? recPath, recFile;
        readonly object gate = new();

        public void Init(byte[] header, string mime)
        {
            // the hub viewers find the stream on: this node's own when it's public, else an entry node's (named in the live block)
            var hubNode = node.PublicUrl != null ? node.NodeKey : null; string? hubUrl = null;
            if (hubNode == null)
            {
                var entry = node.Engine.Read(e => e.Nodes.Values.Where(x => x["roles"] is JsonArray r && r.Any(v => (string?)v == "entry") && (string?)x["node"] != node.NodeKey)
                    .OrderByDescending(x => (double)x["at"]!).Select(x => (Key: (string?)x["node"], Url: (x["addrs"] as JsonArray)?.Select(a => (string?)a).FirstOrDefault(a => a != null && a.StartsWith("https://")))).FirstOrDefault(x => x.Url != null));
                if (entry.Url != null) { hubNode = entry.Key; hubUrl = entry.Url.TrimEnd('/').Replace("https://", "wss://") + "/live"; }
                else if (node.Options.Site != null) hubUrl = node.Options.Site.TrimEnd('/').Replace("https://", "wss://").Replace("http://", "ws://") + "/live";
            }
            var payload = new JsonObject { ["type"] = "live", ["title"] = title[..Math.Min(140, title.Length)], ["stream"] = stream, ["mime"] = mime };
            if (hubNode != null) payload["hub"] = hubNode;
            try { Block = (string?)node.Engine.Append(chain, payload)["hash"]; node.MyLive[stream] = true; }
            catch (Exception e) { node.Log("[obs] couldn’t go live: " + e.Message); return; }
            var name = node.Engine.Read(e => e.Store.Chains.TryGetValue(chain, out var c) ? (string?)Derived.Of(c).Profile["name"] : null) ?? chain[..8];
            node.Log($"[obs] live on {name}: “{title}”" + (hubUrl != null ? $" (viewers through {hubUrl.Replace("wss://", "").Replace("ws://", "").Replace("/live", "")})" : " (viewers through this computer)"));
            local = node.Hub.Local(stream, chain, mime);
            if (hubUrl != null) remote = new RemoteBroadcast(node, hubUrl, stream, chain, mime);
            var lc = local.Value.Chunk; var rb = remote;
            chunks = new Chunker(node, stream, c => { if (rb != null) rb.Chunk(c); else lc(c); });
            // the recording: OBS's stream as one MP4 file
            // (with hosting on, in the channel's folder, shared and offered to the channel's owner for publishing)
            if (node.Hosting != null) { (recPath, recFile) = node.Hosting.RecordingPath(chain, title, ".mp4"); }
            else
            {
                var dir = Path.Combine(node.Options.DataDir, "recordings"); Directory.CreateDirectory(dir);
                recPath = Path.Combine(dir, $"{string.Concat(title.Where(ch => char.IsLetterOrDigit(ch) || " .()-_".Contains(ch))).Trim() switch { "" => "Live", var t => t }} {DateTime.Now:yyyy-MM-dd-HH-mm}.mp4");
            }
            rec = new FileStream(recPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            if (youtubeKey != null) youtube = YouTube.Start(youtubeKey, node.Log);
            Piece(header, false);
            node.Log($"[obs] recording to {recPath}");
        }
        public void Fragment(byte[] fragment, bool key) => Piece(fragment, key);
        void Piece(byte[] bytes, bool key)
        {
            lock (gate)
            {
                if (local == null) return;
                var h = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var g = new JsonObject { ["stream"] = stream, ["n"] = n, ["o"] = offset, ["h"] = h };
                g["sig"] = node.Identity!.Sign($"opentube-live:{stream}:{n}:{offset}:{h}"); g["d"] = Convert.ToBase64String(bytes); if (key) g["k"] = 1;
                n++; offset += bytes.Length;
                local.Value.Seg((JsonObject)g.DeepClone()); node.Engine.PublishLiveSeg((JsonObject)g.DeepClone()); remote?.Seg(g); chunks?.Seg(g);
                try { rec?.Write(bytes); } catch { }
                youtube?.Write(bytes);
            }
        }
        public void End()
        {
            lock (gate)
            {
                if (local == null) return;
                var lend = local.Value.End; var rem = remote; var ch = chunks;
                _ = (ch?.End() ?? Task.CompletedTask).ContinueWith(_ => { lend(); rem?.Close(); });   // the last chunk first, then leave
                rec?.Dispose(); youtube?.End(); local = null;
                node.Log($"[obs] stream ended; recording saved: {recPath} ({offset / 1e6:0.0} MB)");
            }
            var end = new JsonObject { ["type"] = "liveend", ["stream"] = stream, ["outcome"] = "ended" };
            if (node.Hosting != null && recFile != null && offset >= 1000)
                try
                {
                    var r = node.Hosting.SeedRecording(chain, recFile, title, stream);
                    end["rec"] = new JsonObject { ["infohash"] = r["infohash"]?.DeepClone(), ["file"] = recFile.Length > 200 ? recFile[..200] : recFile, ["size"] = r["size"]?.DeepClone(), ["mime"] = "video/mp4", ["thumb"] = Media.Thumb(recPath!) };
                    node.Log("[obs] recording ready: the channel’s owner can publish it from the site");
                }
                catch (Exception e) { node.Log("[obs] couldn’t share the recording: " + e.Message); }
            try { node.Engine.Append(chain, end); } catch (Exception e) { node.Log("[obs] couldn’t post the end of the stream: " + e.Message); }
        }
    }

    /// <summary>Forwarding the stream to YouTube unchanged (fragmented MP4, H.264 + AAC → RTMP), with ffmpeg.</summary>
    sealed class YouTube
    {
        readonly System.Diagnostics.Process ff; readonly Stream input;
        YouTube(System.Diagnostics.Process p) { ff = p; input = p.StandardInput.BaseStream; }
        public static YouTube? Start(string key, Action<string> log)
        {
            if (!Media.HasFfmpeg) { log("[youtube] ffmpeg isn’t installed, so the stream isn’t forwarded to YouTube"); return null; }
            var url = (Environment.GetEnvironmentVariable("YOUTUBE_RTMP") ?? "rtmp://a.rtmp.youtube.com/live2/") + key;   // (YOUTUBE_RTMP: another server, for tests)
            try
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ffmpeg") { ArgumentList = { "-hide_banner", "-loglevel", "error", "-f", "mp4", "-i", "pipe:0", "-c", "copy", "-f", "flv", url }, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false })!;
                p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log("[youtube] " + e.Data.Trim()[..Math.Min(200, e.Data.Trim().Length)]); };
                p.BeginErrorReadLine(); p.BeginOutputReadLine();
                log("[youtube] forwarding the stream to YouTube");
                return new YouTube(p);
            }
            catch (Exception e) { log("[youtube] couldn’t start ffmpeg: " + e.Message); return null; }
        }
        public void Write(byte[] b) { try { input.Write(b); input.Flush(); } catch { } }
        public void End() { try { input.Close(); } catch { } _ = Task.Delay(10000).ContinueWith(_ => { try { if (!ff.HasExited) ff.Kill(); } catch { } }); }
    }

    /// <summary>Sending a stream to another computer's live hub as its broadcaster (this node signs it, as the channel's linked computer).</summary>
    sealed class RemoteBroadcast
    {
        readonly OpenTubeNode node; readonly string url, stream, chain, mime;
        readonly System.Threading.Channels.Channel<string> outbox = System.Threading.Channels.Channel.CreateUnbounded<string>();
        readonly List<string> pending = []; bool on, closed; ClientWebSocket? ws;
        // viewers the hub hangs under this computer get the segments over WebRTC data channels (the segment tree)
        readonly LiveKids kids;
        public RemoteBroadcast(OpenTubeNode node, string url, string stream, string chain, string mime)
        {
            this.node = node; this.url = url; this.stream = stream; this.chain = chain; this.mime = mime;
            kids = new LiveKids(stream, m => outbox.Writer.TryWrite(m.ToJsonString()), node.Log);
            _ = Task.Run(Run);
        }
        public int Children => kids.OpenCount;
        public void Chunk(JsonObject c) => outbox.Writer.TryWrite(new JsonObject { ["t"] = "lchunk", ["stream"] = stream, ["chunk"] = c }.ToJsonString());
        public void Seg(JsonObject g) { var m = new JsonObject { ["t"] = "seg", ["seg"] = g.DeepClone() }.ToJsonString(); lock (pending) { if (on) outbox.Writer.TryWrite(m); else { pending.Add(m); if (pending.Count > 120) pending.RemoveAt(0); } } kids.Seg(g); }
        public void Close() { closed = true; outbox.Writer.TryWrite(new JsonObject { ["t"] = "leave", ["stream"] = stream }.ToJsonString()); _ = Task.Delay(1000).ContinueWith(_ => { kids.Close(); try { ws?.Abort(); } catch { } }); }
        async Task Run()
        {
            int refused = 0;
            while (!closed)
            {
                try
                {
                    ws = new ClientWebSocket(); await ws.ConnectAsync(new Uri(url), CancellationToken.None);
                    var buf = new byte[1 << 16];
                    _ = Task.Run(async () => { await foreach (var m in outbox.Reader.ReadAllAsync()) { if (ws.State != WebSocketState.Open) break; await ws.SendAsync(Encoding.UTF8.GetBytes(m), WebSocketMessageType.Text, true, CancellationToken.None); } });
                    while (ws.State == WebSocketState.Open)
                    {
                        using var ms = new MemoryStream(); WebSocketReceiveResult r;
                        do { r = await ws.ReceiveAsync(buf, CancellationToken.None); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage && r.MessageType != WebSocketMessageType.Close);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        var m = JsonNode.Parse(ms.ToArray())!;
                        var t = (string?)m["t"];
                        if (t == "hi")
                        {
                            outbox.Writer.TryWrite(new JsonObject { ["t"] = "auth", ["key"] = node.NodeKey, ["sig"] = node.Identity!.Sign("opentube-live-auth:" + (string?)m["nonce"]) }.ToJsonString());
                            outbox.Writer.TryWrite(Hello());
                        }
                        else if (t == "broadcasting") { lock (pending) { on = true; refused = 0; foreach (var p in pending) outbox.Writer.TryWrite(p); pending.Clear(); } node.Log("[obs] streaming through " + url.Replace("wss://", "").Replace("ws://", "").Replace("/live", "")); }
                        else if (t == "viewers" && m is JsonObject vm) node.NoteViewers(stream, vm);
                        else if (t == "child" && Core.Js.IsTrue(m["dc"]) && (string?)m["peer"] is { } kp) kids.Child(kp);
                        else if (t == "unchild" && (string?)m["peer"] is { } up) kids.Unchild(up);
                        else if (t == "signal" && (string?)m["from"] is { } sp) kids.Signal(sp, m["data"]);
                        else if (t == "error")
                        {   // usually: that hub hasn't received our live block yet. Ask again.
                            if (++refused == 1 || refused % 15 == 0) node.Log($"[obs] the hub refused the stream: {(string?)m["error"]}{(refused < 60 ? " (asking again)" : "")}");
                            if (refused < 60 && !closed) { await Task.Delay(2000); outbox.Writer.TryWrite(Hello()); }
                        }
                    }
                }
                catch { }
                lock (pending) on = false; kids.Close();
                if (!closed) await Task.Delay(3000);
            }
        }
        string Hello() => new JsonObject { ["t"] = "broadcast", ["stream"] = stream, ["chain"] = chain, ["mime"] = mime, ["rtc"] = false, ["dcSlots"] = 8 }.ToJsonString();
    }
}
