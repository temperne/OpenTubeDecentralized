#if __WASM__
using System.Collections.Concurrent;
using System.Runtime.InteropServices.JavaScript;
using OpenTube.Client;
using OpenTube.Core.BitTorrent;

namespace OpenTube.Uno.Services;

/// <summary>
/// Videos in the browser with the C# BitTorrent client (OpenTube.Core): it finds peers on the network's WebSocket
/// trackers, connects to them over the browser's WebRTC (<see cref="BrowserRtc"/>), checks and keeps the pieces, and
/// answers the player through the site's service worker (byte ranges, in order from where the viewer is), as the
/// website does. Only WebRTC and the service worker's relay are JavaScript (opentube-bridge.js).
/// </summary>
public sealed class BrowserVideoSource : IVideoSource
{
    internal static BrowserVideoSource? Current;
    readonly ClientSession s;
    internal readonly BtClient Bt = new();
    readonly WebTorrentPeers web;
    Task<bool>? init;
    public BrowserVideoSource(ClientSession s)
    {
        this.s = s; Current = this;
        web = new WebTorrentPeers(Bt, BrowserRtc.Instance, m => Console.WriteLine("[bt] " + m));
    }
    public async Task<Uri?> PlayLive(LiveWatch live)
    {
        init ??= BrowserBridge.StreamInit();
        if (!await init || !await BrowserBridge.StreamReady()) return null;
        var origin = JSHost.GlobalThis.GetPropertyAsJSObject("location")!.GetPropertyAsString("origin");
        return new Uri($"{origin}/webtorrent/live/{live.Stream}/live.{(live.Mp4 ? "mp4" : "webm")}");
    }
    public Task<TorrentMeta> Share(string path, string name)
    {
        var t = Bt.Add(TorrentData.Seed(path, name)); web.Announce(t, s.TrackerUrls());
        return Task.FromResult(t.Meta!);
    }
    public long Uploaded(string infoHash) => Bt.Torrents.TryGetValue(infoHash, out var t) ? Interlocked.Read(ref t.Uploaded) : 0;
    /// <summary>A still from the video, through the same stream the player uses (the service worker): a frame a second in.</summary>
    public async Task<string?> Thumbnail(string path, string infoHash, string name)
    {
        init ??= BrowserBridge.StreamInit();
        if (!await init || !await BrowserBridge.StreamReady()) return null;
        try { var r = await BrowserBridge.ThumbJs($"/webtorrent/{infoHash}/{name}"); return string.IsNullOrEmpty(r) ? null : r; } catch { return null; }
    }
    public async Task<Uri?> Play(string infoHash, string file, double size, Action<string> status)
    {
        init ??= BrowserBridge.StreamInit();
        if (!await init) { status("This browser can’t stream videos (no service workers)."); return null; }
        var name = string.Concat(Path.GetFileName(file).Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
        if (name.Length == 0) name = "video.mp4";
        var t = Bt.Torrents.GetValueOrDefault(infoHash) ?? Bt.Add(TorrentData.Fetch(infoHash, Path.Combine(Path.GetTempPath(), "opentube", infoHash, name)));
        web.Announce(t, s.TrackerUrls());
        _ = Task.Run(async () =>
        {
            while (!t.Complete)
            {
                await Task.Delay(1500);
                status(t.Meta == null ? $"Finding peers… ({Bt.PeersOf(infoHash)} connected)" : $"{100.0 * t.HaveCount / Math.Max(1, t.Have.Length):0}% · {Bt.PeersOf(infoHash)} peer(s)");
            }
            status($"Downloaded · sharing it with {Bt.PeersOf(infoHash)} peer(s)");
        });
        if (!await BrowserBridge.StreamReady()) { status("Reload the page once to let it stream videos (its service worker just started)."); return null; }
        status("Finding peers for this video…");
        var origin = JSHost.GlobalThis.GetPropertyAsJSObject("location")!.GetPropertyAsString("origin");
        return new Uri($"{origin}/webtorrent/{infoHash}/{name}");
    }
}

/// <summary>The browser's side of videos: calls out to opentube-bridge.js, and what it (and the service worker) asks of C#.</summary>
public static partial class BrowserBridge
{
    [JSImport("globalThis.otStream.init")] internal static partial Task<bool> StreamInit();
    [JSImport("globalThis.otStream.ready")] internal static partial Task<bool> StreamReady();
    [JSImport("globalThis.otRtc.offer")] internal static partial Task<string> RtcOfferJs();
    [JSImport("globalThis.otRtc.answer")] internal static partial Task<string> RtcAnswerJs(string sdp);
    [JSImport("globalThis.otRtc.accept")] internal static partial void RtcAcceptJs(int id, string sdp);
    [JSImport("globalThis.otRtc.send")] internal static partial double RtcSendJs(int id, [JSMarshalAs<JSType.MemoryView>] Span<byte> bytes);
    [JSImport("globalThis.otRtc.close")] internal static partial void RtcCloseJs(int id);
    [JSImport("globalThis.otThumb")] internal static partial Task<string> ThumbJs(string src);

    // ---- WebRTC events
    [JSExport] public static void RtcOpen(int id) => BrowserRtc.Instance.Opened(id);
    [JSExport] public static void RtcData(int id, byte[] data) => BrowserRtc.Instance.Data(id, data);
    [JSExport] public static void RtcClosed(int id) => BrowserRtc.Instance.Close(id);

    // ---- the service worker's requests: "length type" of a video, its bytes from a position
    [JSExport] public static async Task<string?> Info(string infoHash)
    {
        if (BrowserVideoSource.Current?.Bt.Torrents.TryGetValue(infoHash, out var t) != true) return null;
        for (int i = 0; t!.Meta == null && i < 600; i++) await Task.Delay(100);
        if (t.Meta is not { } m) return null;
        var type = Path.GetExtension(m.Name).ToLowerInvariant() switch { ".webm" => "video/webm", ".mkv" => "video/x-matroska", ".mov" => "video/quicktime", ".mp3" => "audio/mpeg", _ => "video/mp4" };
        return $"{m.Length} {type}";
    }
    // bytes go back to JavaScript in two steps (an async call can't return them): a ticket (-1: none), then Take(ticket)
    static readonly ConcurrentDictionary<int, byte[]> tickets = new(); static int nextTicket;
    static int Ticket(byte[]? b) { if (b == null) return -1; var t = Interlocked.Increment(ref nextTicket); tickets[t] = b; return t; }
    [JSExport] public static byte[] Take(int ticket) => tickets.TryRemove(ticket, out var b) ? b : [];
    [JSExport] public static async Task<int> Read(string infoHash, double position, int max) => Ticket(await ReadBytes(infoHash, position, max));
    [JSExport] public static async Task<int> LiveRead(int id) => Ticket(await LiveBytes(id));
    static async Task<byte[]?> ReadBytes(string infoHash, double position, int max)
    {
        if (BrowserVideoSource.Current?.Bt.Torrents.TryGetValue(infoHash, out var t) != true || t!.Meta is not { } m) return null;
        var pos = (long)position; if (pos < 0 || pos >= m.Length) return null;
        int piece = (int)(pos / m.PieceLength), off = (int)(pos - (long)piece * m.PieceLength);
        if (!await t.WhenHave(piece, TimeSpan.FromSeconds(4))) return [];   // still coming: an empty chunk keeps the pull alive
        return t.ReadBlock(piece, off, Math.Min(Math.Min(max, 128 * 1024), m.SizeOf(piece) - off));
    }

    // ---- live streams: one reader per request, pulled a chunk at a time ("id type")
    static readonly ConcurrentDictionary<int, (LiveWatch W, System.Threading.Channels.ChannelReader<byte[]> R)> live = new();
    static int nextLive;
    [JSExport] public static string? LiveOpen(string stream)
    {
        if (Svc.S.Watching(stream) is not { } w) return null;
        var id = Interlocked.Increment(ref nextLive); live[id] = (w, w.Subscribe());
        return $"{id} {(w.Mp4 ? "video/mp4" : "video/webm")}";
    }
    static async Task<byte[]?> LiveBytes(int id)
    {
        if (!live.TryGetValue(id, out var x)) return null;
        using var to = new CancellationTokenSource(4000);
        try
        {
            if (!await x.R.WaitToReadAsync(to.Token)) { LiveClose(id); return null; }
            using var ms = new MemoryStream();
            while (ms.Length < 1 << 20 && x.R.TryRead(out var b)) ms.Write(b);
            return ms.ToArray();
        }
        catch (OperationCanceledException) { return []; }
    }
    [JSExport] public static void LiveClose(int id) { if (live.TryRemove(id, out var x)) x.W.Unsubscribe(x.R); }
}

/// <summary>WebRTC data channels through the browser, for the C# BitTorrent client.</summary>
public sealed class BrowserRtc : IRtc
{
    public static readonly BrowserRtc Instance = new();
    readonly ConcurrentDictionary<int, Chan> chans = new();
    sealed class Chan(int id)
    {
        public readonly TaskCompletionSource<Stream?> Open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly JsChannelStream Stream = new(id);
    }
    sealed class Offered(int id, string sdp, Chan c) : IRtcOffer
    {
        public string Sdp => sdp; public Task<Stream?> Channel => c.Open.Task;
        public void Accept(string answer) => BrowserBridge.RtcAcceptJs(id, answer);
        public void Close() => Instance.Close(id);
    }
    static (int Id, string Sdp) Split(string d) { var sp = d.IndexOf(' '); return (int.Parse(d[..sp]), d[(sp + 1)..]); }
    Chan Track(int id, int timeoutMs)
    {
        var c = chans.GetOrAdd(id, i => new Chan(i));
        _ = Task.Delay(timeoutMs).ContinueWith(_ => { if (c.Open.TrySetResult(null)) Close(id); });
        return c;
    }
    public async Task<IRtcOffer> Offer() { var (id, sdp) = Split(await BrowserBridge.RtcOfferJs()); return new Offered(id, sdp, Track(id, 60000)); }
    public async Task<(string Sdp, Task<Stream?> Channel)> Answer(string offerSdp) { var (id, sdp) = Split(await BrowserBridge.RtcAnswerJs(offerSdp)); return (sdp, Track(id, 30000).Open.Task); }
    internal void Opened(int id) { if (chans.TryGetValue(id, out var c)) c.Open.TrySetResult(c.Stream); }
    internal void Data(int id, byte[] data) { if (chans.TryGetValue(id, out var c)) c.Stream.Got(data); }
    internal void Close(int id) { if (chans.TryRemove(id, out var c)) { c.Open.TrySetResult(null); c.Stream.Ended(); } try { BrowserBridge.RtcCloseJs(id); } catch { } }

    /// <summary>A browser data channel as a byte stream (the BitTorrent wire runs over it).</summary>
    internal sealed class JsChannelStream(int id) : Stream
    {
        readonly System.Threading.Channels.Channel<byte[]> inbox = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        byte[]? cur; int curAt; bool closed;
        public void Got(byte[] b) => inbox.Writer.TryWrite(b);
        public void Ended() { closed = true; inbox.Writer.TryComplete(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (cur == null || curAt >= cur.Length)
            {
                if (!await inbox.Reader.WaitToReadAsync(ct)) return 0;
                if (inbox.Reader.TryRead(out var next)) { cur = next; curAt = 0; }
            }
            int n = Math.Min(buffer.Length, cur.Length - curAt); cur.AsMemory(curAt, n).CopyTo(buffer); curAt += n; return n;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            for (int off = 0; off < buffer.Length; off += 16384)
            {
                if (closed) throw new IOException("data channel closed");
                var part = buffer.Slice(off, Math.Min(16384, buffer.Length - off)).ToArray();
                var buffered = BrowserBridge.RtcSendJs(id, part);
                if (buffered < 0) { Ended(); throw new IOException("data channel closed"); }
                if (buffered > 1 << 20) await Task.Delay(50, ct);
            }
        }
        public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => ReadAsync(b.AsMemory(o, c), ct).AsTask();
        public override Task WriteAsync(byte[] b, int o, int c, CancellationToken ct) => WriteAsync(b.AsMemory(o, c), ct).AsTask();
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException("async only in the browser");
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException("async only in the browser");
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override void Flush() { }
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
        protected override void Dispose(bool d) { if (d && !closed) Instance.Close(id); base.Dispose(d); }
    }
}
#endif
