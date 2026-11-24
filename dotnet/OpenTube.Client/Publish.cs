using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using OpenTube.Core;
using OpenTube.Core.BitTorrent;

namespace OpenTube.Client;

/// <summary>
/// Putting videos on your channels, as the JavaScript page does: the file's torrent (the infohash WebTorrent would
/// give it), a video block {type, title, desc, infohash, file, size, mime, thumb} and its posting fee (priced by size).
/// A phone (or a browser) doesn't stay on: one of your computers (or one shared with you for hosting) downloads the
/// whole file ("store") and shares it from then on.
/// </summary>
public sealed class Publish
{
    readonly ClientSession s;
    public Publish(ClientSession s) => this.s = s;

    /// <summary>Your channels (those whose key is on this device).</summary>
    public List<ChainInfo> MyChannels() => s.Directory().Where(c => c.Kind == "channel" && s.IsMine(c.Owner)).ToList();
    public double PostCost(double size) => s.Engine.Read(e => Coin.PostCost(e.Store.Ledger.Prices["post"], size));
    /// <summary>Computers that can keep your videos: yours, or shared with you for hosting, and online.</summary>
    public List<(string Node, string Name)> StorageNodes() => s.MyNodes().Where(n => n.Perms.Contains('h') && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - n.At < 120000
        && s.Engine.Read(e => e.Nodes.TryGetValue(n.Node, out var x) && (x["roles"] as JsonArray ?? []).Any(r => (string?)r == "host")))
        .Select(n => (n.Node, s.Engine.Read(e => (string?)e.Nodes[n.Node]["info"]?["name"]) ?? "your computer")).ToList();

    /// <summary>"This channel is mine", signed by the channel's key, for that computer.</summary>
    internal JsonObject ChanProof(string node, string chain)
    {
        var owner = s.Engine.Read(e => e.Store.Chains.TryGetValue(chain, out var c) ? c.Owner : null);
        var key = owner == null ? null : s.KeyOf(owner);
        return key == null ? [] : new JsonObject { ["channelSig"] = key.Sign($"opentube-obs-channel:{node}:{s.Me.pub}:{chain}") };
    }

    /// <summary>Publish a video file (already shared by this device: its infohash). Returns the video block.</summary>
    public async Task<JsonObject> Video(string chain, TorrentMeta meta, string file, long size, string mime, string title, string desc, string thumb, Action<string> status)
    {
        if (title.Trim().Length == 0) throw new ArgumentException("Add a title");
        var payload = new JsonObject { ["type"] = "video", ["title"] = title.Trim()[..Math.Min(140, title.Trim().Length)], ["desc"] = desc.Trim()[..Math.Min(5000, desc.Trim().Length)], ["infohash"] = meta.InfoHash, ["file"] = file[..Math.Min(200, file.Length)], ["size"] = size, ["mime"] = mime[..Math.Min(60, mime.Length)], ["thumb"] = thumb };
        status("Adding it to your channel…");
        var b = await Task.Run(() => s.Engine.Append(chain, payload));
        status("Paying the posting fee…");
        await s.Engine.SubmitTx(new JsonObject { ["post"] = new JsonObject { ["chain"] = chain, ["video"] = (string)b["hash"]!, ["size"] = size, ["amt"] = PostCost(size) } }, s.Me);
        return b;
    }
    /// <summary>Hand the video to a computer, which downloads it from this device and keeps it; waits until it has all of it.</summary>
    public async Task Store(string node, string chain, string infohash, Action<string> status)
    {
        JsonObject? p = null; Exception? last = null;
        for (int i = 0; i < 20 && p == null; i++)
            try { var args = ChanProof(node, chain); args["chain"] = chain; args["ih"] = infohash; p = await s.NodeCtl(node, "store", args); }
            catch (Exception e) { last = e; status("Your computer: " + e.Message + " (retrying)"); await Task.Delay(3000); }
        if (p == null) throw new InvalidOperationException("your computer didn’t take the video: " + last?.Message);
        while (!Js.IsTrue(p["done"]))
        {
            status($"Your computer has {Math.Round((Js.Num(p["progress"]) ?? 0) * 100)}% of it… keep this page open until it’s done");
            await Task.Delay(3000);
            try { p = await s.NodeCtl(node, "store.status", new JsonObject { ["ih"] = infohash }); } catch { }
        }
    }
    /// <summary>Ask your computers to relay a live stream to viewers (one of them also records it).</summary>
    public void Relay(string chain, string stream, string title)
    {
        var first = true;
        foreach (var (node, _) in StorageNodes())
        {
            var args = new JsonObject { ["stream"] = stream, ["chain"] = chain };
            if (first) { args["record"] = true; args["title"] = title; first = false; }
            _ = s.NodeCtl(node, "relay", args).ContinueWith(_ => { });
        }
    }
}

/// <summary>
/// Going live from this device's camera: the browser's (or the phone's web view's) MediaRecorder gives WebM chunks
/// each second; each becomes a segment {stream, n, o, h, sig, d} signed by the channel's key, sent to the hub
/// (the site's) as the stream's broadcaster; the live block announces it, and the liveend block closes it. Your
/// computers relay it to viewers and one records it, to publish afterwards.
/// </summary>
public sealed class LiveBroadcast : IAsyncDisposable
{
    readonly ClientSession s; readonly Identity key;
    public string Chain { get; }
    public string Stream { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public string Mime { get; }
    public string Status { get; private set; } = "Starting…";
    public long Bytes { get; private set; }
    public int Viewers { get; private set; }
    public event Action? Changed;
    long n, o; bool on, closed;
    readonly Channel<string> outbox = Channel.CreateUnbounded<string>();
    readonly List<string> pending = [];

    LiveBroadcast(ClientSession s, string chain, string mime, Identity key) { this.s = s; Chain = chain; Mime = mime; this.key = key; }
    /// <param name="group">Join this computer's group stream on camera (it pulls your stream into its picture) instead of going live alone.</param>
    public static async Task<LiveBroadcast> Start(ClientSession s, string chain, string title, string mime, string? group = null)
    {
        var owner = s.Engine.Read(e => e.Store.Chains.TryGetValue(chain, out var c) ? c.Owner : null) ?? throw new InvalidOperationException("that channel isn’t loaded yet");
        var key = s.KeyOf(owner) ?? throw new InvalidOperationException("this device doesn’t hold that channel’s key");
        var b = new LiveBroadcast(s, chain, mime, key);
        _ = Task.Run(b.Run);
        var lb = new JsonObject { ["type"] = "live", ["title"] = title.Trim()[..Math.Min(140, Math.Max(1, title.Trim().Length))], ["stream"] = b.Stream, ["mime"] = mime };
        if (group != null) lb["group"] = group;
        await Task.Run(() => s.Engine.Append(chain, lb));
        if (group == null) s.Publish.Relay(chain, b.Stream, title);
        else
        {
            b.Group = group;
            var args = s.Publish.ChanProof(group, chain); args["stream"] = b.Stream; args["chain"] = chain; args["hub"] = null;
            _ = Task.Run(async () => { try { await s.NodeCtl(group, "group.join", args); b.Say("You’re in the group stream."); } catch (Exception e) { b.Say("Group stream: " + e.Message); } });
        }
        return b;
    }
    /// <summary>The computer whose group stream this joined, if any.</summary>
    public string? Group { get; private set; }
    void Say(string st) { Status = st; Changed?.Invoke(); }
    async Task Run()
    {
        var url = s.Site.Replace("https://", "wss://").Replace("http://", "ws://") + "/live";
        while (!closed)
        {
            var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri(url), CancellationToken.None);
                var sender = Task.Run(async () => { await foreach (var m in outbox.Reader.ReadAllAsync()) { if (ws.State != WebSocketState.Open) break; await ws.SendAsync(Encoding.UTF8.GetBytes(m), WebSocketMessageType.Text, true, CancellationToken.None); } });
                var buf = new byte[1 << 16];
                while (ws.State == WebSocketState.Open && !closed)
                {
                    using var ms = new MemoryStream(); WebSocketReceiveResult r;
                    do { r = await ws.ReceiveAsync(buf, CancellationToken.None); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage && r.MessageType != WebSocketMessageType.Close);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    if (JsonNode.Parse(ms.ToArray()) is not JsonObject m) continue;
                    switch ((string?)m["t"])
                    {
                        case "hi":   // prove the channel is ours, then broadcast
                            foreach (var k in new[] { key, s.Me }) Out(new JsonObject { ["t"] = "auth", ["key"] = k.pub, ["sig"] = k.Sign("opentube-live-auth:" + (string?)m["nonce"]) });
                            Out(Hello()); break;
                        case "broadcasting": lock (pending) { on = true; foreach (var p in pending) outbox.Writer.TryWrite(p); pending.Clear(); } Say("Live"); break;
                        case "viewers": Viewers = (int)(Js.Num(m["now"]) ?? Js.Num(m["count"]) ?? Viewers); Changed?.Invoke(); break;
                        case "error": Say("The hub says: " + (string?)m["error"] + " (trying again)"); _ = Task.Delay(3000).ContinueWith(_ => Out(Hello())); break;
                    }
                }
            }
            catch { }
            lock (pending) on = false;
            if (!closed) { Say("Reconnecting…"); await Task.Delay(2000); }
        }
    }
    JsonObject Hello() => new() { ["t"] = "broadcast", ["stream"] = Stream, ["chain"] = Chain, ["mime"] = Mime, ["rtc"] = false, ["dcSlots"] = 0 };
    void Out(JsonObject m) => outbox.Writer.TryWrite(m.ToJsonString());

    /// <summary>One chunk from the recorder: a signed segment for the hub (and from there your computers and viewers).</summary>
    public void Chunk(byte[] bytes)
    {
        if (closed || bytes.Length == 0) return;
        var h = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var g = new JsonObject { ["stream"] = Stream, ["n"] = n, ["o"] = o, ["h"] = h };
        g["sig"] = key.Sign($"opentube-live:{Stream}:{n}:{o}:{h}"); g["d"] = Convert.ToBase64String(bytes);
        n++; o += bytes.Length; Bytes = o;
        var m = new JsonObject { ["t"] = "seg", ["seg"] = g }.ToJsonString();
        lock (pending) { if (on) outbox.Writer.TryWrite(m); else { pending.Add(m); if (pending.Count > 60) pending.RemoveAt(0); } }
        Changed?.Invoke();
    }
    public async Task Stop()
    {
        if (closed) return; closed = true;
        Out(new JsonObject { ["t"] = "leave", ["stream"] = Stream });
        try { await Task.Run(() => s.Engine.Append(Chain, new JsonObject { ["type"] = "liveend", ["stream"] = Stream, ["outcome"] = "ended" })); } catch { }
        Say("Ended");
        _ = Task.Delay(1500).ContinueWith(_ => outbox.Writer.TryComplete());
    }
    public async ValueTask DisposeAsync() => await Stop();
}
