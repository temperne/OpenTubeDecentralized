using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using OpenTube.Core.BitTorrent;
using SIPSorcery.Net;

namespace OpenTube.Net;

/// <summary>
/// BitTorrent with browsers: browsers speak BitTorrent only over WebRTC, finding each other on WebSocket trackers
/// (WebTorrent's protocol, bittorrent-tracker 11). This announces our torrents on those trackers and answers the offers
/// browsers send through them; each data channel then carries the ordinary BitTorrent wire to <see cref="BtClient"/>.
///   announce: {action, info_hash, peer_id (both 20 bytes as a binary string), numwant, uploaded, downloaded, left, event?, offers}
///   an offer forwarded to us: {action, info_hash, peer_id, offer {type, sdp}, offer_id} → we answer
///   {action, info_hash, peer_id, to_peer_id, answer {type, sdp}, offer_id}; the SDP carries its candidates (no trickle).
/// </summary>
public sealed class WebTorrentPeers(BtClient client, Action<string> log) : IAsyncDisposable
{
    readonly BtClient bt = client;
    static readonly List<RTCIceServer> Ice = [new() { urls = "stun:stun.l.google.com:19302" }, new() { urls = "stun:global.stun.twilio.com:3478" }];
    readonly ConcurrentDictionary<string, Tracker> trackers = [];
    readonly ConcurrentDictionary<string, int> peersOf = [];
    readonly CancellationTokenSource stop = new();
    const int MaxPeersPerTorrent = 30;
    static string Bin(byte[] b) => Encoding.Latin1.GetString(b);
    static byte[] Bytes(string s) => Encoding.Latin1.GetBytes(s);
    public int TrackersUp => trackers.Values.Count(t => t.Up);

    /// <summary>Announce this torrent on these trackers (WebSocket ones; others are ignored here).</summary>
    public void Announce(TorrentData t, IEnumerable<string> urls)
    {
        foreach (var u in urls.Where(u => u.StartsWith("ws://") || u.StartsWith("wss://")).Distinct())
            trackers.GetOrAdd(u, x => { var tr = new Tracker(this, x); tr.Start(); return tr; }).Add(t);
    }
    public void Forget(string infoHash) { foreach (var t in trackers.Values) t.Remove(infoHash); }
    /// <summary>Trackers no longer in the list stop being used (a meet place offline for 5 minutes).</summary>
    public void Drop(IEnumerable<string> urls) { foreach (var u in urls) if (trackers.TryRemove(u, out var t)) t.Close(); }

    sealed class Tracker(WebTorrentPeers w, string url)
    {
        readonly ConcurrentDictionary<string, TorrentData> torrents = [];
        readonly ConcurrentDictionary<string, bool> completedSent = [];
        ClientWebSocket? ws; readonly SemaphoreSlim sendLock = new(1, 1);
        readonly CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(w.stop.Token);
        int intervalMs = 120000; TaskCompletionSource kick = new();
        public bool Up => ws?.State == WebSocketState.Open;
        public void Add(TorrentData t) { if (torrents.TryAdd(t.InfoHashHex, t)) { kick.TrySetResult(); t.Done += _ => kick.TrySetResult(); } }
        public void Remove(string ih) { if (torrents.TryRemove(ih, out var t)) _ = AnnounceOne(t, "stopped"); }
        public void Start() => _ = Task.Run(Run);
        public void Close() { cts.Cancel(); try { ws?.Abort(); } catch { } }
        async Task Run()
        {
            int retry = 3000;
            while (!cts.IsCancellationRequested)
            {
                ws = new ClientWebSocket();
                try
                {
                    using (var to = CancellationTokenSource.CreateLinkedTokenSource(cts.Token)) { to.CancelAfter(15000); await ws.ConnectAsync(new Uri(url), to.Token); }
                    retry = 3000;
                    var announcer = Task.Run(async () =>
                    {
                        bool first = true;
                        while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
                        {
                            foreach (var t in torrents.Values) await AnnounceOne(t, first ? "started" : null);
                            first = false;
                            kick = new TaskCompletionSource();
                            await Task.WhenAny(Task.Delay(intervalMs, cts.Token), kick.Task).ContinueWith(_ => { });
                        }
                    });
                    var buf = new byte[1 << 16]; using var msg = new MemoryStream();
                    while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
                    {
                        var r = await ws.ReceiveAsync(buf, cts.Token);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        msg.Write(buf, 0, r.Count); if (msg.Length > 1 << 20) break;
                        if (!r.EndOfMessage) continue;
                        JsonObject? d = null; try { d = JsonNode.Parse(Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length)) as JsonObject; } catch { }
                        msg.SetLength(0);
                        if (d != null) Handle(d);
                    }
                }
                catch { }
                try { ws.Abort(); } catch { }
                if (cts.IsCancellationRequested) return;
                try { await Task.Delay(Random.Shared.Next(retry), cts.Token); } catch { return; }
                retry = Math.Min(retry * 2, 60000);
            }
        }
        async Task AnnounceOne(TorrentData t, string? ev)
        {
            var left = t.Meta == null ? (long?)null : Math.Max(0, t.Meta.Length - (long)t.HaveCount * t.Meta.PieceLength);
            if (t.Complete && completedSent.TryAdd(t.InfoHashHex, true) && ev == null) ev = "completed";
            var o = new JsonObject
            {
                ["action"] = "announce", ["info_hash"] = Bin(t.InfoHash), ["peer_id"] = Bin(w.bt.PeerId), ["numwant"] = 5,
                ["uploaded"] = t.Uploaded, ["downloaded"] = t.Downloaded, ["left"] = t.Complete ? 0 : left,
            };
            if (ev != null) o["event"] = ev;
            // our own offers (as WebTorrent makes them): the tracker hands them to peers already there, who answer
            o["offers"] = ev is "stopped" or "completed" ? new JsonArray() : await w.MakeOffers(this, t, t.Complete ? 2 : 4);
            await Send(o);
        }
        public async Task Send(JsonObject o)
        {
            var s = ws; if (s?.State != WebSocketState.Open) return;
            await sendLock.WaitAsync();
            try { await s.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()), WebSocketMessageType.Text, true, cts.Token); } catch { }
            finally { sendLock.Release(); }
        }
        void Handle(JsonObject d)
        {
            if (OpenTube.Core.Js.Num(d["interval"]) is double iv && iv >= 10) intervalMs = (int)Math.Min(iv * 1000, 600000);
            if ((string?)d["info_hash"] is not { } ihb || ihb.Length != 20) return;
            var ih = Convert.ToHexStringLower(Bytes(ihb));
            if (!torrents.TryGetValue(ih, out var t)) return;
            if ((string?)d["peer_id"] is not { } pid || pid == Bin(w.bt.PeerId)) return;
            if (d["offer"] is JsonObject offer && (string?)offer["sdp"] is { } sdp && d["offer_id"] is JsonNode oid)
                _ = w.Answer(this, t, pid, oid.DeepClone(), sdp);
            else if (d["answer"] is JsonObject ans && (string?)ans["sdp"] is { } asdp && (string?)d["offer_id"] is { } aid && w.offers.TryRemove(aid, out var pend))
            {   // a peer answered one of our offers
                if (pend.Pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = asdp }) != SetDescriptionResultEnum.OK) try { pend.Pc.close(); } catch { }
            }
        }
    }

    sealed record Pending(RTCPeerConnection Pc, DateTime At);
    readonly ConcurrentDictionary<string, Pending> offers = [];   // offer_id (binary string) -> our connection waiting for an answer
    async Task<JsonArray> MakeOffers(Tracker tr, TorrentData t, int n)
    {
        var list = new JsonArray();
        foreach (var (k, p) in offers) if (DateTime.UtcNow - p.At > TimeSpan.FromSeconds(50)) { offers.TryRemove(k, out _); try { p.Pc.close(); } catch { } }
        if (peersOf.GetValueOrDefault(t.InfoHashHex) >= MaxPeersPerTorrent || offers.Count > 200) return list;
        for (int i = 0; i < n; i++)
        {
            try
            {
                var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = Ice });
                var ch = await pc.createDataChannel(Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20)), new RTCDataChannelInit { ordered = true });
                var stream = new DataChannelStream(ch, pc); var started = false;
                ch.onopen += () =>
                {
                    if (started) return; started = true;
                    peersOf.AddOrUpdate(t.InfoHashHex, 1, (_, x) => x + 1);
                    _ = Task.Run(async () => { try { await bt.AddPeer(stream, t, "webrtc (our offer)"); } finally { peersOf.AddOrUpdate(t.InfoHashHex, 0, (_, x) => Math.Max(0, x - 1)); try { pc.close(); } catch { } } });
                };
                var gathered = new TaskCompletionSource();
                pc.onicegatheringstatechange += st => { if (st == RTCIceGatheringState.complete) gathered.TrySetResult(); };
                await pc.setLocalDescription(pc.createOffer(null));
                await Task.WhenAny(gathered.Task, Task.Delay(1000));
                var offer = pc.createOffer(null);
                var id = Bin(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20));
                offers[id] = new Pending(pc, DateTime.UtcNow);
                list.Add(new JsonObject { ["offer"] = new JsonObject { ["type"] = "offer", ["sdp"] = offer.sdp }, ["offer_id"] = id });
            }
            catch (Exception e) { log("[host] WebRTC offer failed: " + e.Message); break; }
        }
        return list;
    }

    async Task Answer(Tracker tr, TorrentData t, string remotePeerId, JsonNode offerId, string sdp)
    {
        if (peersOf.GetValueOrDefault(t.InfoHashHex) >= MaxPeersPerTorrent) return;
        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = Ice });
        var label = "webrtc " + Convert.ToHexStringLower(Bytes(remotePeerId))[..12];
        var opened = false;
        pc.ondatachannel += ch =>
        {
            if (opened) return; opened = true;
            var stream = new DataChannelStream(ch, pc);
            peersOf.AddOrUpdate(t.InfoHashHex, 1, (_, n) => n + 1);
            _ = Task.Run(async () => { try { await bt.AddPeer(stream, t, label); } finally { peersOf.AddOrUpdate(t.InfoHashHex, 0, (_, n) => Math.Max(0, n - 1)); try { pc.close(); } catch { } } });
        };
        try
        {
            var gathered = new TaskCompletionSource();
            pc.onicegatheringstatechange += s => { if (s == RTCIceGatheringState.complete) gathered.TrySetResult(); };
            if (pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp }) != SetDescriptionResultEnum.OK) { pc.close(); return; }
            await pc.setLocalDescription(pc.createAnswer(null));
            await Task.WhenAny(gathered.Task, Task.Delay(2000));
            var answer = pc.createAnswer(null);
            await tr.Send(new JsonObject
            {
                ["action"] = "announce", ["info_hash"] = Bin(t.InfoHash), ["peer_id"] = Bin(bt.PeerId), ["to_peer_id"] = remotePeerId,
                ["answer"] = new JsonObject { ["type"] = "answer", ["sdp"] = answer.sdp }, ["offer_id"] = offerId,
            });
            _ = Task.Delay(30000).ContinueWith(_ => { if (!opened) try { pc.close(); } catch { } });
        }
        catch (Exception e) { log("[host] WebRTC answer failed: " + e.Message); try { pc.close(); } catch { } }
    }

    public ValueTask DisposeAsync() { stop.Cancel(); foreach (var t in trackers.Values) t.Close(); return ValueTask.CompletedTask; }
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
