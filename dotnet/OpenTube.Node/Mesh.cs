using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using SIPSorcery.Net;

namespace OpenTube.Node;

/// <summary>
/// The peer mesh: this node joins the network's WebRTC room the way every browser does (Trystero 0.26, torrent
/// strategy), so browsers connect to it directly, introduced by any WebTorrent tracker (the network's public ones, the
/// site's, entry nodes', this node's own). Same wire as Trystero:
///   signalling: tracker "announce" messages for info_hash = the first 20 characters of SHA-1(SHA-1(room topic)) (each
///     SHA-1 written as its bytes in base 36); offers and answers carry the SDP encrypted with AES-GCM (key: SHA-256 of ":appId:roomId",
///     a 16-byte IV, written "i,v,…$base64"); ICE isn't trickled (the SDP has the candidates);
///   the data channel "data", binary frames: presence [2, present, len16, room token] and room data [1, len16, room
///     token, action frame]; the room token is hex SHA-256 of "Trystero:appId:roomId";
///   action frames (v2): inline [2, 0|format&lt;&lt;4, type(32), metaLen16, meta, payload] up to 16 KB, or an offer
///     [2, 1|format&lt;&lt;4, type(32), metaLen16, id32, size(float64)] answered with accepted [2, 3, id32], then chunks
///     [2, 2, id32, offset(float64), bytes]; format 0 text, 1 JSON, 2 bytes;
///   joining: each side sends "@_hsready" (no room password); "@_ping" is answered with "@_pong"; "@_leave" ends it.
/// This node answers the offers browsers announce. The network's messages go in the action "ot", as JSON.
/// </summary>
public sealed class Mesh : IAsyncDisposable
{
    const string Charset = "0123456789AaBbCcDdEeFfGgHhIiJjKkLlMmNnOoPpQqRrSsTtUuVvWwXxYyZz";
    const int MaxActionFrame = 16384, ChunkHeader = 14, ChunkSize = MaxActionFrame - ChunkHeader, MaxReceive = 16 << 20, MaxPeers = 40;
    static readonly List<RTCIceServer> IceServers = [new() { urls = "stun:stun.l.google.com:19302" }, new() { urls = "stun:stun1.l.google.com:19302" }, new() { urls = "stun:stun2.l.google.com:19302" }, new() { urls = "stun:stun.cloudflare.com:3478" }];

    public readonly string SelfId = string.Concat(Enumerable.Range(0, 20).Select(_ => Charset[RandomNumberGenerator.GetInt32(62)]));
    readonly string appId, roomId, infoHash, roomToken; readonly byte[] key;
    readonly Action<string> log;
    readonly Action<string, string, Func<string, Task>> onJoin; readonly Action<string, string> onMessage; readonly Action<string> onLeave;
    readonly ConcurrentDictionary<string, Tracker> trackers = [];
    readonly ConcurrentDictionary<string, MeshPeer> peerMap = [];
    readonly ConcurrentDictionary<string, long> handled = [];
    readonly CancellationTokenSource stop = new();
    static readonly bool Debug = Environment.GetEnvironmentVariable("MESH_DEBUG") == "1";
    public string InfoHash => infoHash;
    public int PeerCount => peerMap.Values.Count(p => p.Joined);
    public IEnumerable<string> TrackerUrls => trackers.Keys;
    public int TrackersUp => trackers.Values.Count(t => t.Up);

    /// <param name="onJoin">(peer id, label, send text): a browser joined; <paramref name="onMessage"/>: (peer id, text) it sent; <paramref name="onLeave"/>: it left.</param>
    public Mesh(string appId, string roomId, Action<string> log, Action<string, string, Func<string, Task>> onJoin, Action<string, string> onMessage, Action<string> onLeave)
    {
        this.appId = appId; this.roomId = roomId; this.log = log; this.onJoin = onJoin; this.onMessage = onMessage; this.onLeave = onLeave;
        var rootTopic = $"Trystero@{appId}@{roomId}";
        infoHash = Sha1Base36(Sha1Base36(rootTopic))[..20];   // the strategy's topic is already the SHA-1 string; the tracker gets its SHA-1 again
        key = SHA256.HashData(Encoding.UTF8.GetBytes($":{appId}:{roomId}"));   // (no room password)
        roomToken = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"Trystero:{appId}:{roomId}")));
    }
    public static string Sha1Base36(string s) => string.Concat(SHA1.HashData(Encoding.UTF8.GetBytes(s)).Select(b => ToBase36(b)));
    static string ToBase36(int v) { if (v == 0) return "0"; var sb = new StringBuilder(); while (v > 0) { sb.Insert(0, "0123456789abcdefghijklmnopqrstuvwxyz"[v % 36]); v /= 36; } return sb.ToString(); }

    // ── the SDP's encryption (as Trystero's crypto.ts) ──
    public string Encrypt(string plain)
    {
        var iv = RandomNumberGenerator.GetBytes(16);
        var gcm = new GcmBlockCipher(new AesEngine()); gcm.Init(true, new AeadParameters(new KeyParameter(key), 128, iv));
        var input = Encoding.UTF8.GetBytes(plain); var output = new byte[gcm.GetOutputSize(input.Length)];
        int n = gcm.ProcessBytes(input, 0, input.Length, output, 0); gcm.DoFinal(output, n);
        return string.Join(',', iv) + "$" + Convert.ToBase64String(output);
    }
    public string Decrypt(string raw)
    {
        var parts = raw.Split('$'); var iv = parts[0].Split(',').Select(byte.Parse).ToArray(); var c = Convert.FromBase64String(parts.Length > 1 ? parts[1] : "");
        var gcm = new GcmBlockCipher(new AesEngine()); gcm.Init(false, new AeadParameters(new KeyParameter(key), 128, iv));
        var output = new byte[gcm.GetOutputSize(c.Length)]; int n = gcm.ProcessBytes(c, 0, c.Length, output, 0); n += gcm.DoFinal(output, n);
        return Encoding.UTF8.GetString(output, 0, n);
    }

    /// <summary>Use these trackers (added ones connect, missing ones are let go).</summary>
    public void SetTrackers(IEnumerable<string> urls)
    {
        var want = urls.Distinct().Take(24).ToHashSet();
        foreach (var (u, t) in trackers) if (!want.Contains(u)) { trackers.TryRemove(u, out _); t.Close(); }
        foreach (var u in want) if (!trackers.ContainsKey(u)) { var t = new Tracker(this, u); trackers[u] = t; t.Start(); }
    }

    // ── a WebTorrent tracker: announce, and answer the offers it forwards ──
    sealed class Tracker(Mesh m, string url)
    {
        ClientWebSocket? ws; readonly CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(m.stop.Token);
        readonly SemaphoreSlim sendLock = new(1, 1);
        public bool Up => ws?.State == WebSocketState.Open;
        int announceMs = 10000;
        public void Start() => _ = Task.Run(Run);
        public void Close() { cts.Cancel(); try { ws?.Abort(); } catch { } }
        async Task Run()
        {
            var retry = 3333;
            while (!cts.IsCancellationRequested)
            {
                ws = new ClientWebSocket();
                try
                {
                    using (var to = CancellationTokenSource.CreateLinkedTokenSource(cts.Token)) { to.CancelAfter(15000); await ws.ConnectAsync(new Uri(url), to.Token); }
                    retry = 3333;
                    var announcer = Task.Run(async () =>
                    {
                        int[] warm = [0, 233, 533, 1333];
                        for (int i = 0; ws.State == WebSocketState.Open && !cts.IsCancellationRequested; i++)
                        {
                            await Task.Delay(i < warm.Length ? warm[i] : announceMs, cts.Token);
                            await Send(new JsonObject { ["action"] = "announce", ["info_hash"] = m.infoHash, ["peer_id"] = m.SelfId, ["numwant"] = 3, ["offers"] = new JsonArray() });
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
                try { await Task.Delay(RandomNumberGenerator.GetInt32(retry), cts.Token); } catch { return; }
                retry = Math.Min(retry * 2, 60000);
            }
        }
        public async Task Send(JsonObject o)
        {
            var w = ws; if (w?.State != WebSocketState.Open) return;
            await sendLock.WaitAsync();
            try { await w.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()), WebSocketMessageType.Text, true, cts.Token); } catch { }
            finally { sendLock.Release(); }
        }
        void Handle(JsonObject d)
        {
            if (Debug) m.log("[mesh] tracker → " + d.ToJsonString()[..Math.Min(200, d.ToJsonString().Length)]);
            if (Core.Js.Num(d["interval"]) is double iv) announceMs = (int)Math.Clamp(iv * 1000, 10000, 120000);
            if ((string?)d["info_hash"] != m.infoHash) return;
            var peerId = (string?)d["peer_id"]; var offerId = (string?)d["offer_id"];
            if (peerId == null || offerId == null || peerId == m.SelfId) return;
            if (d["offer"]?["sdp"] is JsonValue os && os.TryGetValue<string>(out var enc))
            {
                var k = $"offer:{offerId}:{peerId}"; var now = Environment.TickCount64;
                if (m.handled.TryGetValue(k, out var at) && now - at < 4000) return;
                m.handled[k] = now; if (m.handled.Count > 500) foreach (var (kk, t) in m.handled) if (now - t > 24000) m.handled.TryRemove(kk, out _);
                _ = m.Answer(this, peerId, offerId, enc);
            }
        }
    }

    async Task Answer(Tracker t, string peerId, string offerId, string encOffer)
    {
        if (peerMap.TryGetValue(peerId, out var have) && !have.Dead) return;   // already connected (or connecting)
        if (peerMap.Count >= MaxPeers) return;
        string sdp; try { sdp = Decrypt(encOffer); } catch { return; }   // not our room's key
        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = IceServers });
        var p = new MeshPeer(this, peerId, pc); peerMap[peerId] = p;
        try
        {
            pc.ondatachannel += ch => p.Attach(ch);
            pc.onconnectionstatechange += st =>
            {
                if (st is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed) p.Close();
                else if (st == RTCPeerConnectionState.disconnected) _ = Task.Delay(5000).ContinueWith(_ => { if (pc.connectionState == RTCPeerConnectionState.disconnected) p.Close(); });
            };
            var gathered = new TaskCompletionSource();
            pc.onicegatheringstatechange += s => { if (s == RTCIceGatheringState.complete) gathered.TrySetResult(); };
            var r = pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdp });
            if (r != SetDescriptionResultEnum.OK) { p.Close(); return; }
            var answer = pc.createAnswer(null);
            await pc.setLocalDescription(answer);
            await Task.WhenAny(gathered.Task, Task.Delay(3000));
            var full = pc.createAnswer(null);   // now with every candidate gathered (no trickling)
            await t.Send(new JsonObject { ["action"] = "announce", ["info_hash"] = infoHash, ["peer_id"] = SelfId, ["answer"] = new JsonObject { ["type"] = "answer", ["sdp"] = Encrypt(full.sdp) }, ["offer_id"] = offerId, ["to_peer_id"] = peerId });
            _ = Task.Delay(30000).ContinueWith(_ => { if (!p.Joined) p.Close(); });   // never connected
        }
        catch (Exception e) { log("[mesh] answer failed: " + e.Message); p.Close(); }
    }

    // ── one browser over the data channel ──
    sealed class MeshPeer(Mesh m, string id, RTCPeerConnection pc)
    {
        public readonly string Id = id; RTCDataChannel? ch; public bool Joined, Dead; bool sentReady;
        readonly SemaphoreSlim sendLock = new(1, 1);
        readonly ConcurrentDictionary<uint, (byte[] Buf, int Got, string Type, int Format)> inbound = [];
        readonly ConcurrentDictionary<uint, TaskCompletionSource<bool>> accepts = [];
        uint nextId;
        public void Attach(RTCDataChannel c)
        {
            ch = c;
            c.onmessage += (_, _, data) => { try { Frame(data); } catch { Close(); } };
            c.onclose += Close;
            if (c.readyState == RTCDataChannelState.open) Opened(); else c.onopen += Opened;
        }
        void Opened()
        {
            if (sentReady) return; sentReady = true;
            var t = Encoding.UTF8.GetBytes(m.roomToken); var pres = new byte[4 + t.Length]; pres[0] = 2; pres[1] = 1; pres[2] = (byte)(t.Length >> 8); pres[3] = (byte)t.Length; t.CopyTo(pres, 4);
            Raw(pres);
            _ = SendAction("@_hsready", 0, []);
        }
        void Raw(byte[] b) { try { if (ch?.readyState == RTCDataChannelState.open) ch.send(b, 0, b.Length); } catch { Close(); } }
        byte[] Room(ReadOnlySpan<byte> action)
        {
            var t = Encoding.UTF8.GetBytes(m.roomToken); var f = new byte[3 + t.Length + action.Length];
            f[0] = 1; f[1] = (byte)(t.Length >> 8); f[2] = (byte)t.Length; t.CopyTo(f, 3); action.CopyTo(f.AsSpan(3 + t.Length)); return f;
        }
        void Frame(byte[] data)
        {
            if (data.Length < 3 || data.Length > 16515) { Close(); return; }
            if (data[0] == 2)
            {   // presence
                int tl = data[2] << 8 | data[3]; var tok = Encoding.UTF8.GetString(data, 4, Math.Min(tl, data.Length - 4));
                if (tok == m.roomToken && data[1] == 0) Close();
                return;
            }
            if (data[0] != 1) { Close(); return; }
            int len = data[1] << 8 | data[2]; if (3 + len > data.Length) { Close(); return; }
            if (Encoding.UTF8.GetString(data, 3, len) != m.roomToken) return;   // another room on the same connection
            Action(data.AsSpan(3 + len));
        }
        void Action(ReadOnlySpan<byte> b)
        {
            if (b.Length < 2 || b[0] != 2) { Close(); return; }
            int kind = b[1] & 15, format = b[1] >> 4;
            if (kind >= 2)
            {
                uint aid = BinaryPrimitives.ReadUInt32BigEndian(b[2..]);
                if (kind == 3) { if (accepts.TryRemove(aid, out var tcs)) tcs.TrySetResult(true); }
                else if (kind is 4 or 5) { if (accepts.TryRemove(aid, out var tcs)) tcs.TrySetResult(false); inbound.TryRemove(aid, out _); }
                else if (kind == 2 && inbound.TryGetValue(aid, out var st))
                {
                    long off = (long)BinaryPrimitives.ReadDoubleBigEndian(b[6..]); var chunk = b[ChunkHeader..];
                    if (off != st.Got || st.Got + chunk.Length > st.Buf.Length) { Close(); return; }
                    chunk.CopyTo(st.Buf.AsSpan(st.Got)); st.Got += chunk.Length; inbound[aid] = st;
                    if (st.Got == st.Buf.Length) { inbound.TryRemove(aid, out _); Deliver(st.Type, st.Format, st.Buf); }
                }
                return;
            }
            int headerSize = kind == 0 ? 36 : 48; if (b.Length < headerSize) { Close(); return; }
            var type = Encoding.UTF8.GetString(b[2..34]).Replace("\0", "");
            int metaLen = BinaryPrimitives.ReadUInt16BigEndian(b[34..]); int payloadAt = headerSize + metaLen; if (payloadAt > b.Length) { Close(); return; }
            if (kind == 0) { Deliver(type, format, b[payloadAt..].ToArray()); return; }
            uint id = BinaryPrimitives.ReadUInt32BigEndian(b[36..]); var size = BinaryPrimitives.ReadDoubleBigEndian(b[40..]);
            if (size <= 0 || size > MaxReceive || size != Math.Floor(size)) { Control(4, id, "too large"); return; }
            inbound[id] = (new byte[(int)size], 0, type, format);
            Control(3, id);
        }
        void Control(byte kind, uint id, string reason = "")
        {
            var r = Encoding.UTF8.GetBytes(reason); var b = new byte[6 + Math.Min(200, r.Length)]; b[0] = 2; b[1] = kind; BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(2), id); r.AsSpan(0, b.Length - 6).CopyTo(b.AsSpan(6));
            Raw(Room(b));
        }
        void Deliver(string type, int format, byte[] payload)
        {
            switch (type)
            {
                case "@_hsready": if (!Joined) { Joined = true; m.onJoin(Id, "webrtc " + Id[..6], text => SendAction("ot", 1, Encoding.UTF8.GetBytes(text))); } break;
                case "@_ping": _ = SendAction("@_pong", 0, []); break;
                case "@_leave": Close(); break;
                case "ot":
                    if (!Joined || format != 1) break;
                    var text = Encoding.UTF8.GetString(payload);
                    if (Debug && text.StartsWith("{\"t\":\"meshecho\"")) { m.log($"[mesh] echo of {payload.Length} bytes"); _ = SendAction("ot", 1, payload); break; }   // (tests only: MESH_DEBUG=1)
                    m.onMessage(Id, text); break;
            }
        }
        /// <summary>One action to this peer: inline when it fits a frame, else offered and sent in chunks once accepted.</summary>
        public async Task SendAction(string type, int format, byte[] data)
        {
            if (Dead) return;
            await sendLock.WaitAsync();
            try
            {
                var typeBytes = Encoding.UTF8.GetBytes(type);
                if (36 + data.Length <= MaxActionFrame)
                {
                    var f = new byte[36 + data.Length]; f[0] = 2; f[1] = (byte)(0 | format << 4); typeBytes.CopyTo(f, 2); data.CopyTo(f, 36);
                    await Drain(); Raw(Room(f)); return;
                }
                var id = nextId++; var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); accepts[id] = tcs;
                var o = new byte[48]; o[0] = 2; o[1] = (byte)(1 | format << 4); typeBytes.CopyTo(o, 2);
                BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(36), id); BinaryPrimitives.WriteDoubleBigEndian(o.AsSpan(40), data.Length);
                Raw(Room(o));
                if (await Task.WhenAny(tcs.Task, Task.Delay(120000)) != tcs.Task || !tcs.Task.Result) { accepts.TryRemove(id, out _); return; }
                for (int off = 0; off < data.Length && !Dead; off += ChunkSize)
                {
                    int n = Math.Min(ChunkSize, data.Length - off); var c = new byte[ChunkHeader + n]; c[0] = 2; c[1] = 2;
                    BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(2), id); BinaryPrimitives.WriteDoubleBigEndian(c.AsSpan(6), off); data.AsSpan(off, n).CopyTo(c.AsSpan(ChunkHeader));
                    await Drain(); Raw(Room(c));
                }
            }
            finally { sendLock.Release(); }
        }
        async Task Drain() { for (int i = 0; i < 200 && ch != null && ch.bufferedAmount > 1 << 20; i++) await Task.Delay(50); }
        public void Close()
        {
            if (Dead) return; Dead = true;
            m.peerMap.TryRemove(new KeyValuePair<string, MeshPeer>(Id, this));
            foreach (var t in accepts.Values) t.TrySetResult(false);
            try { pc.close(); } catch { }
            if (Joined) m.onLeave(Id);
        }
    }

    public ValueTask DisposeAsync()
    {
        stop.Cancel();
        foreach (var t in trackers.Values) t.Close();
        foreach (var p in peerMap.Values) p.Close();
        return ValueTask.CompletedTask;
    }
}
