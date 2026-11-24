using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>
/// Watching a live stream in C#: it joins the stream's hub (the node its live block names, else the site's), takes
/// the stream's signed segments (each checked against the channel's key, as every viewer does), and turns them into
/// one continuous video: the header, then from a point a decoder can start (an MP4 keyframe, a WebM cluster). The
/// page's own video element plays it as an ordinary stream (the browser through its service worker, the phone from
/// 127.0.0.1): no Media Source Extensions, no JavaScript.
/// </summary>
public sealed class LiveWatch : IAsyncDisposable
{
    public string Chain { get; }
    public string Stream { get; }
    public string Mime { get; }
    public bool Mp4 => Mime.StartsWith("video/mp4");
    public string Status { get; private set; } = "Joining the stream…";
    public bool Ended { get; private set; }
    public long Bytes { get; private set; }
    /// <summary>The hub's latest count: {viewers, peak, now: [keys], seen: [{k, first, secs}], seenCount, watchSecs, quality}.</summary>
    public JsonObject? Viewers { get; private set; }
    public event Action? Changed;
    readonly ClientSession s; readonly string author;
    readonly CancellationTokenSource cts = new();
    readonly object gate = new();
    byte[]? init; long next = -1;
    readonly List<byte[]> gop = [];   // since the newest starting point: what a new viewer gets after the header
    sealed class Viewer { public readonly Channel<byte[]> C = Channel.CreateUnbounded<byte[]>(); public bool Started; }
    readonly List<Viewer> readers = [];
    ClientWebSocket? ws;

    LiveWatch(ClientSession s, string chain, JsonObject liveBlock)
    {
        this.s = s; Chain = chain; author = (string)liveBlock["author"]!;
        Stream = (string)liveBlock["payload"]!["stream"]!; Mime = (string?)liveBlock["payload"]?["mime"] ?? "video/webm";
    }
    public static LiveWatch Start(ClientSession s, string chain, JsonObject liveBlock)
    {
        var w = new LiveWatch(s, chain, liveBlock); _ = Task.Run(() => w.Run(((string?)liveBlock["payload"]?["hub"])));
        return w;
    }
    void Say(string st) { Status = st; Changed?.Invoke(); }

    /// <summary>The addresses to find the stream on, in order: each address of the node its live block names that this
    /// device may open (from an https page: only https; a computer lists several: public, home network, localhost),
    /// the one that worked last time first; then the site's own hub.</summary>
    List<string> HubUrls(string? hubNode)
    {
        var https = s.Site.StartsWith("https://");
        var addrs = hubNode == null ? [] : s.Engine.Read(e => e.Nodes.TryGetValue(hubNode, out var n) ? (n["addrs"] as JsonArray ?? []).Select(x => (string?)x).OfType<string>().ToList() : []);
        var list = addrs.Where(a => !https || a.StartsWith("https://")).OrderBy(a => a.StartsWith("https://") ? 0 : a.Contains("localhost") || a.Contains("127.") ? 2 : 1)
            .Select(a => a.TrimEnd('/').Replace("https://", "wss://").Replace("http://", "ws://") + "/live").ToList();
        list.AddRange(s.LinkedUrls.Where(u => u != s.SiteSocket).Select(u => u.Replace("/ot", "/live")));   // the entry nodes this device is linked to
        list.Add(s.Site.TrimEnd('/').Replace("https://", "wss://").Replace("http://", "ws://") + "/live");
        list = list.Distinct().ToList();
        if (good != null && list.Remove(good)) list.Insert(0, good);
        return list;
    }
    string? good; bool nextHub;

    async Task Run(string? hubNode)
    {
        int tries = 0, attempt = 0;
        while (!cts.IsCancellationRequested && !Ended)
        {
            var urls = HubUrls(hubNode); var url = urls[attempt++ % urls.Count];
            ws = new ClientWebSocket();
            try
            {
                using (var to = CancellationTokenSource.CreateLinkedTokenSource(cts.Token)) { to.CancelAfter(6000); await ws.ConnectAsync(new Uri(url), to.Token); }
                var thisUrl = url;
                Say("Waiting for the stream’s video…");
                using var alive = new Timer(_ => _ = Send(new JsonObject { ["t"] = "alive", ["stream"] = Stream }), null, 15000, 15000);
                var buf = new byte[1 << 16]; using var ms = new MemoryStream();
                nextHub = false;
                while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested && !Ended && !nextHub)
                {
                    var r = await ws.ReceiveAsync(buf, cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buf, 0, r.Count); if (!r.EndOfMessage) continue;
                    JsonNode? m = null; try { m = JsonNode.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length)); } catch { }
                    ms.SetLength(0);
                    if (m is not JsonObject o) continue;
                    switch ((string?)o["t"])
                    {
                        case "hi":
                            await Send(new JsonObject { ["t"] = "auth", ["key"] = s.Me.pub, ["sig"] = s.Me.Sign("opentube-live-auth:" + (string?)o["nonce"]) });
                            await Send(new JsonObject { ["t"] = "watch", ["stream"] = Stream, ["segOnly"] = true, ["media"] = false, ["slots"] = 0, ["dcSlots"] = 0 });
                            break;
                        case "seg" when o["seg"] is JsonObject g && (string?)g["stream"] == Stream: good = thisUrl; attempt = 0; Seg(g); break;
                        case "nostream" when attempt < urls.Count && good != thisUrl:   // not on this hub: try the next one
                            nextHub = true; break;
                        case "nostream":
                            if (++tries >= 10) { End("This stream isn’t live right now."); break; }
                            Say("The stream hasn’t started yet (or is over): checking again…");
                            _ = Task.Delay(3000).ContinueWith(_ => Send(new JsonObject { ["t"] = "watch", ["stream"] = Stream, ["segOnly"] = true, ["media"] = false, ["slots"] = 0, ["dcSlots"] = 0 }));
                            break;
                        case "ended" when (string?)o["stream"] == Stream: End("The stream has ended."); break;
                        case "viewers" when (string?)o["stream"] == Stream: Viewers = o; Changed?.Invoke(); break;
                    }
                }
            }
            catch (Exception) when (!cts.IsCancellationRequested) { Say("Reconnecting to the stream…"); }
            catch { }
            try { ws.Abort(); } catch { }
            if (!cts.IsCancellationRequested && !Ended) await Task.Delay(attempt > 0 && attempt < HubUrls(hubNode).Count ? 200 : 2000).ContinueWith(_ => { });
        }
    }
    async Task Send(JsonObject m) { var w = ws; if (w?.State == WebSocketState.Open) try { await w.SendAsync(Encoding.UTF8.GetBytes(m.ToJsonString()), WebSocketMessageType.Text, true, cts.Token); } catch { } }

    /// <summary>One signed segment: {stream, n, o, h, sig, d (base64), k (starts with a keyframe)}.</summary>
    void Seg(JsonObject g)
    {
        if ((string?)g["d"] is not { } d || Js.Num(g["n"]) is not double nd) return;
        byte[] b; try { b = Convert.FromBase64String(d); } catch { return; }
        var h = Convert.ToHexStringLower(SHA256.HashData(b));
        if (h != (string?)g["h"]) { s.Log?.Invoke($"[live] seg {nd}: hash differs"); return; }
        if (!Rules.VerifySig(author, $"opentube-live:{Stream}:{(long)nd}:{(long)(Js.Num(g["o"]) ?? -1)}:{h}", g["sig"])) { s.Log?.Invoke($"[live] seg {nd}: signature not by {author[..8]}"); return; }   // only the channel's own video
        long n = (long)nd; bool key = Js.Truthy(g["k"]);   // (k: 1)
        lock (gate)
        {
            if (next >= 0 && n < next) return;   // seen already
            if (n == 0)
            {   // the header: MP4's is the whole segment; WebM's runs up to its first cluster
                if (Mp4) { init = b; return; }
                var c0 = ClusterAt(b); init = c0 > 0 ? b[..c0] : b;
                if (c0 < 0) return;
            }
            if (init == null) return;
            int start = Mp4 ? (key ? 0 : -1) : ClusterAt(b);   // where a decoder can start in this segment
            if (next < 0)
            {
                if (start < 0) return;   // wait for one
                Say("Playing live");
            }
            next = n + 1; Bytes += b.Length;
            var from = start >= 0 ? (start == 0 ? b : b[start..]) : null;
            if (from != null) { gop.Clear(); gop.Add(from); } else gop.Add(b);
            foreach (var r in readers)
                if (r.Started) r.C.Writer.TryWrite(b);
                else if (from != null) { r.C.Writer.TryWrite(init); r.C.Writer.TryWrite(from); r.Started = true; }   // joined before a start point: header + this one
        }
        Changed?.Invoke();
    }
    /// <summary>Where a WebM cluster starts in these bytes (the ID 1F 43 B6 75, then its timecode element E7), or -1.</summary>
    static int ClusterAt(byte[] u)
    {
        for (int i = 0; i + 6 < u.Length; i++)
        {
            if (u[i] != 0x1f || u[i + 1] != 0x43 || u[i + 2] != 0xb6 || u[i + 3] != 0x75) continue;
            var first = u[i + 4]; if (first == 0) continue;
            var len = System.Numerics.BitOperations.LeadingZeroCount((uint)first) - 23;
            if (i + 4 + len < u.Length && u[i + 4 + len] == 0xe7) return i;
        }
        return -1;
    }

    /// <summary>A viewer: the header, the newest group from its start, then everything as it comes; completes when the stream ends.</summary>
    public ChannelReader<byte[]> Subscribe()
    {
        var v = new Viewer();
        lock (gate)
        {
            if (init != null && gop.Count > 0 && next >= 0) { v.C.Writer.TryWrite(init); foreach (var b in gop) v.C.Writer.TryWrite(b); v.Started = true; }
            if (Ended) v.C.Writer.TryComplete(); else readers.Add(v);
        }
        return v.C.Reader;
    }
    public void Unsubscribe(ChannelReader<byte[]> r) { lock (gate) readers.RemoveAll(v => v.C.Reader == r); }
    void End(string why) { lock (gate) { Ended = true; foreach (var r in readers) r.C.Writer.TryComplete(); readers.Clear(); } Say(why); }

    public ValueTask DisposeAsync() { _ = Send(new JsonObject { ["t"] = "leave", ["stream"] = Stream }); cts.Cancel(); End("Left the stream."); return ValueTask.CompletedTask; }
}
