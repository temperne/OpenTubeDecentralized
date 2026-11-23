using SIPSorcery.Net;
using OpenTube.Core.BitTorrent;

namespace OpenTube.Net;

/// <summary>WebRTC data channels for BitTorrent with browsers, in C# (SIPSorcery): for the node and the phone app.</summary>
public sealed class SipRtc : IRtc
{
    static readonly List<RTCIceServer> Ice = [new() { urls = "stun:stun.l.google.com:19302" }, new() { urls = "stun:global.stun.twilio.com:3478" }];

    sealed class Offered(RTCPeerConnection pc, string sdp, Task<Stream?> channel) : IRtcOffer
    {
        public string Sdp => sdp; public Task<Stream?> Channel => channel;
        public void Accept(string answer) { if (pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answer }) != SetDescriptionResultEnum.OK) Close(); }
        public void Close() { try { pc.close(); } catch { } }
    }
    /// <param name="given">A channel the other side made (it arrives open: SIPSorcery may never raise onopen for it).</param>
    static Task<Stream?> WhenOpen(RTCPeerConnection pc, Task<RTCDataChannel> ch, int timeoutMs, bool given = false)
    {
        var tcs = new TaskCompletionSource<Stream?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = ch.ContinueWith(c =>
        {
            if (!c.IsCompletedSuccessfully) { tcs.TrySetResult(null); return; }
            var d = c.Result; var stream = new DataChannelStream(d, pc);
            if (given || d.readyState == RTCDataChannelState.open) tcs.TrySetResult(stream); else d.onopen += () => tcs.TrySetResult(stream);
        });
        pc.onconnectionstatechange += s => { if (s is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed) tcs.TrySetResult(null); };
        _ = Task.Delay(timeoutMs).ContinueWith(_ => { if (tcs.TrySetResult(null)) try { pc.close(); } catch { } });
        return tcs.Task;
    }
    public async Task<IRtcOffer> Offer()
    {
        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = Ice });
        var ch = await pc.createDataChannel(Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20)), new RTCDataChannelInit { ordered = true });
        var gathered = new TaskCompletionSource();
        pc.onicegatheringstatechange += st => { if (st == RTCIceGatheringState.complete) gathered.TrySetResult(); };
        await pc.setLocalDescription(pc.createOffer(null));
        await Task.WhenAny(gathered.Task, Task.Delay(1000));
        return new Offered(pc, pc.createOffer(null).sdp, WhenOpen(pc, Task.FromResult(ch), 60000));
    }
    public async Task<(string Sdp, Task<Stream?> Channel)> Answer(string offerSdp)
    {
        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = Ice });
        var got = new TaskCompletionSource<RTCDataChannel>();
        pc.ondatachannel += ch => got.TrySetResult(ch);
        var gathered = new TaskCompletionSource();
        pc.onicegatheringstatechange += s => { if (s == RTCIceGatheringState.complete) gathered.TrySetResult(); };
        if (pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = offerSdp }) != SetDescriptionResultEnum.OK) { pc.close(); throw new InvalidDataException("bad offer"); }
        await pc.setLocalDescription(pc.createAnswer(null));
        await Task.WhenAny(gathered.Task, Task.Delay(2000));
        return (pc.createAnswer(null).sdp, WhenOpen(pc, got.Task, 30000, given: true));
    }
}

/// <summary>A WebRTC data channel as a byte stream (BitTorrent's wire runs over it as over TCP).</summary>
public sealed class DataChannelStream : Stream
{
    readonly RTCDataChannel ch; readonly RTCPeerConnection pc;
    readonly System.Threading.Channels.Channel<byte[]> inbox = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
    byte[]? cur; int curAt; bool closed;
    const int Part = 16384;
    public DataChannelStream(RTCDataChannel ch, RTCPeerConnection pc)
    {
        this.ch = ch; this.pc = pc;
        ch.onmessage += (_, _, data) => inbox.Writer.TryWrite(data);
        ch.onclose += () => { closed = true; inbox.Writer.TryComplete(); };
        pc.onconnectionstatechange += s => { if (s is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed or RTCPeerConnectionState.disconnected) { closed = true; inbox.Writer.TryComplete(); } };
    }
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
        for (int off = 0; off < buffer.Length; off += Part)
        {
            if (closed || ch.readyState != RTCDataChannelState.open) throw new IOException("data channel closed");
            for (int i = 0; ch.bufferedAmount > 1 << 20 && i < 400; i++) await Task.Delay(25, ct);
            var part = buffer.Slice(off, Math.Min(Part, buffer.Length - off)).ToArray();
            ch.send(part, 0, part.Length);
        }
    }
    public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => ReadAsync(b.AsMemory(o, c), ct).AsTask();
    public override Task WriteAsync(byte[] b, int o, int c, CancellationToken ct) => WriteAsync(b.AsMemory(o, c), ct).AsTask();
    public override int Read(byte[] b, int o, int c) => ReadAsync(b, o, c, default).GetAwaiter().GetResult();
    public override void Write(byte[] b, int o, int c) => WriteAsync(b, o, c, default).GetAwaiter().GetResult();
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override void Flush() { }
    public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
    protected override void Dispose(bool d) { if (d) { closed = true; inbox.Writer.TryComplete(); try { ch.close(); } catch { } try { pc.close(); } catch { } } base.Dispose(d); }
}
