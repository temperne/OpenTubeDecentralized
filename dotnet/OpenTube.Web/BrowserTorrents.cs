using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.JSInterop;
using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Core.BitTorrent;
using OpenTube.UI;

namespace OpenTube.Web;

/// <summary>
/// Videos in the browser with the C# BitTorrent client (OpenTube.Core): it finds peers on the network's WebSocket
/// trackers, connects to them over the browser's WebRTC (<see cref="BrowserRtc"/>, the one binding it needs), checks
/// and keeps the pieces, and answers the page's video element through the service worker (byte ranges, in order from
/// where the viewer is). Everything but WebRTC and the service worker's relay is C#.
/// </summary>
public sealed class BrowserTorrents : IVideoPlayer
{
    readonly IJSRuntime js; readonly ClientSession s;
    readonly BtClient bt = new();
    readonly WebTorrentPeers web;
    readonly DotNetObjectReference<BrowserTorrents> self;
    Task<bool>? init;
    public BrowserTorrents(IJSRuntime js, ClientSession s)
    {
        this.js = js; this.s = s; self = DotNetObjectReference.Create(this);
        web = new WebTorrentPeers(bt, new BrowserRtc(js), m => Console.WriteLine("[bt] " + m));
    }

    List<string> Trackers()
    {
        var list = new List<string> { s.Site.Replace("https://", "wss://").Replace("http://", "ws://") + "/tracker" };
        list.AddRange(s.LinkedUrls.Select(u => u.EndsWith("/ot") ? u[..^3] + "/tracker" : u));
        foreach (var t in JsonNode.Parse(Network.MainGenesisJson)?["payload"]?["trackers"]?.AsArray() ?? []) if ((string?)t is { } u) list.Add(u);
        return list.Distinct().ToList();
    }

    public async Task<Playback> Play(string elementId, string infoHash, string file, double size, Action<string> status)
    {
        init ??= js.InvokeAsync<bool>("otStream.init", self).AsTask();
        if (!await init) return new Playback(null, "This browser can't stream videos (no service workers).");
        var name = string.Concat(Path.GetFileName(file).Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
        if (name.Length == 0) name = "video.mp4";
        var t = bt.Torrents.GetValueOrDefault(infoHash) ?? bt.Add(TorrentData.Fetch(infoHash, Path.Combine(Path.GetTempPath(), "opentube", infoHash, name)));
        web.Announce(t, Trackers());
        _ = Task.Run(async () =>
        {
            while (!t.Complete)
            {
                await Task.Delay(2000);
                status(t.Meta == null ? $"Finding peers… ({bt.PeersOf(infoHash)} connected)" : $"{100.0 * t.HaveCount / Math.Max(1, t.Have.Length):0}% · {bt.PeersOf(infoHash)} peer(s) · C# BitTorrent");
            }
            status($"Downloaded · sharing it with {bt.PeersOf(infoHash)} peer(s) · C# BitTorrent");
        });
        if (!await js.InvokeAsync<bool>("otStream.ready")) return new Playback(null, "Reload the page once to let it stream videos (its service worker just started).");
        return new Playback($"/webtorrent/{infoHash}/{name}", "Finding peers for this video…");
    }

    // ---- what the service worker asks (through the binding in opentube.js)
    [JSInvokable] public async Task<object?> Info(string infoHash)
    {
        if (!bt.Torrents.TryGetValue(infoHash, out var t)) return null;
        for (int i = 0; t.Meta == null && i < 600; i++) await Task.Delay(100);
        if (t.Meta is not { } m) return null;
        var type = Path.GetExtension(m.Name).ToLowerInvariant() switch { ".webm" => "video/webm", ".mkv" => "video/x-matroska", ".mov" => "video/quicktime", ".mp3" => "audio/mpeg", _ => "video/mp4" };
        return new { length = m.Length, type };
    }
    [JSInvokable] public async Task<byte[]?> Read(string infoHash, double position, int max)
    {
        if (!bt.Torrents.TryGetValue(infoHash, out var t) || t.Meta is not { } m) return null;
        var pos = (long)position; if (pos < 0 || pos >= m.Length) return null;
        int piece = (int)(pos / m.PieceLength), off = (int)(pos - (long)piece * m.PieceLength);
        if (!await t.WhenHave(piece, TimeSpan.FromMinutes(2))) return null;
        return t.ReadBlock(piece, off, Math.Min(Math.Min(max, 128 * 1024), m.SizeOf(piece) - off));
    }
}

/// <summary>WebRTC data channels through the browser (binding: otRtc in opentube.js); bytes go across as Uint8Array.</summary>
public sealed class BrowserRtc : IRtc
{
    readonly IJSRuntime js; readonly DotNetObjectReference<BrowserRtc> self; bool inited;
    readonly ConcurrentDictionary<int, Chan> chans = new();
    public BrowserRtc(IJSRuntime js) { this.js = js; self = DotNetObjectReference.Create(this); }
    sealed record Desc(int Id, string Sdp);

    sealed class Chan(BrowserRtc rtc, int id)
    {
        public readonly TaskCompletionSource<Stream?> Open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly JsChannelStream Stream = new(rtc, id);
    }
    sealed class Offered(BrowserRtc rtc, Desc d, Chan c) : IRtcOffer
    {
        public string Sdp => d.Sdp; public Task<Stream?> Channel => c.Open.Task;
        public void Accept(string answer) => _ = rtc.js.InvokeVoidAsync("otRtc.accept", d.Id, answer).AsTask();
        public void Close() => rtc.Close(d.Id);
    }
    async Task Init() { if (!inited) { inited = true; await js.InvokeVoidAsync("otRtc.init", self); } }
    Chan Track(int id, int timeoutMs)
    {
        var c = chans.GetOrAdd(id, i => new Chan(this, i));
        _ = Task.Delay(timeoutMs).ContinueWith(_ => { if (c.Open.TrySetResult(null)) Close(id); });
        return c;
    }
    public async Task<IRtcOffer> Offer()
    {
        await Init(); var d = await js.InvokeAsync<Desc>("otRtc.offer");
        return new Offered(this, d, Track(d.Id, 60000));
    }
    public async Task<(string Sdp, Task<Stream?> Channel)> Answer(string offerSdp)
    {
        await Init(); var d = await js.InvokeAsync<Desc>("otRtc.answer", offerSdp);
        return (d.Sdp, Track(d.Id, 30000).Open.Task);
    }
    internal void Close(int id) { if (chans.TryRemove(id, out var c)) { c.Open.TrySetResult(null); c.Stream.Ended(); } _ = js.InvokeVoidAsync("otRtc.close", id).AsTask(); }
    internal ValueTask<double> Send(int id, byte[] part) => js.InvokeAsync<double>("otRtc.send", id, part);

    [JSInvokable] public void RtcOpen(int id) { if (chans.TryGetValue(id, out var c)) c.Open.TrySetResult(c.Stream); }
    [JSInvokable] public void RtcData(int id, byte[] data) { if (chans.TryGetValue(id, out var c)) c.Stream.Got(data); }
    [JSInvokable] public void RtcClosed(int id) => Close(id);

    /// <summary>A browser data channel as a byte stream (the BitTorrent wire runs over it).</summary>
    internal sealed class JsChannelStream(BrowserRtc rtc, int id) : Stream
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
                var buffered = await rtc.Send(id, buffer.Slice(off, Math.Min(16384, buffer.Length - off)).ToArray());
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
        protected override void Dispose(bool d) { if (d && !closed) rtc.Close(id); base.Dispose(d); }
    }
}
