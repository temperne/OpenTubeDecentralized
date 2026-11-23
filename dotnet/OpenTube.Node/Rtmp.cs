using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace OpenTube.Node;

/// <summary>Where an OBS stream goes: the MP4 header once, then fragments (each a moof + mdat, starting at a keyframe when possible).</summary>
public interface IStreamSink
{
    void Init(byte[] header, string mime);
    void Fragment(byte[] fragment, bool key);
    void End();
    /// <summary>Cut fragments at least this often (ms), even between keyframes.</summary>
    int FragMs => 2000;
}

/// <summary>
/// OBS "Start Streaming" receiver (port of rtmp.mjs): a small RTMP server that turns the incoming H.264 + AAC stream
/// into fragmented MP4, the format browsers play while it arrives. onPublish gets the stream key and returns a sink, or
/// a reason the stream is refused (OBS shows it). In OBS: Settings → Stream → Service "Custom…", Server
/// rtmp://this-computer/live, the stream key from the site; video H.264 (x264 or any), audio AAC, keyframes every 1–2 s.
/// </summary>
public sealed class RtmpServer
{
    readonly Func<string, object> onPublish;   // IStreamSink, or a string: why not
    readonly Action<string> log;
    TcpListener? listener;
    public int Port { get; }
    public int Live; public long Bytes; public string? LastStage; public double? KeyInterval; public string? Error;

    public RtmpServer(int port, Func<string, object> onPublish, Action<string> log) { Port = port; this.onPublish = onPublish; this.log = s => log("[obs] " + s); }

    public void Start()
    {
        try
        {   // IPv4 and IPv6 together (OBS tries "localhost" as ::1 first on some systems); IPv4 only where IPv6 is off
            try { listener = new TcpListener(IPAddress.IPv6Any, Port); listener.Server.DualMode = true; listener.Start(); }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported or SocketError.AddressNotAvailable) { listener = new TcpListener(IPAddress.Any, Port); listener.Start(); }
        }
        catch (Exception e) { Error = $"couldn’t listen on port {Port}: {e.Message}"; log(Error); return; }
        log($"OBS receiver on rtmp://this-computer:{Port}/live");
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c; try { c = await listener.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(() => new Session(this, c).Run());
            }
        });
    }
    public void Stop() { try { listener?.Stop(); } catch { } }

    sealed class Session(RtmpServer srv, TcpClient client)
    {
        readonly NetworkStream net = client.GetStream();
        readonly string who = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4().ToString() ?? "?";
        int inChunk = 128; const int OutChunk = 4096;
        IStreamSink? sink; bool published;
        sealed class ChunkStream { public uint Ts, Delta, Sid; public int Len; public byte Type; public MemoryStream Body = new(); public bool Ext; }
        readonly Dictionary<int, ChunkStream> streams = [];
        (byte[] AvcC, int W, int H, string Codec)? video; (byte[] Asc, int Ot, int Rate, int Ch)? audio; bool warnedV, warnedA, warnedKey;
        readonly List<(long Dts, int Cto, bool Key, byte[] Data, long Dur)> vq = [], aq = [];
        uint seq = 1; bool sentInit; long? lastVDur; long? fragStart; long? lastKey;

        public async Task Run()
        {
            client.NoDelay = true;
            srv.log($"connection from {who}"); srv.LastStage = "connected";
            try
            {
                // handshake: C0 + C1 → S0 + S1 + S2, then C2
                var c01 = await Read(1537, TimeSpan.FromSeconds(10));
                var s1 = new byte[1536]; BinaryPrimitives.WriteUInt32BigEndian(s1, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()); RandomNumberGenerator.Fill(s1.AsSpan(8));
                await net.WriteAsync((byte[])[3, .. s1, .. c01[1..]]);
                await Read(1536, TimeSpan.FromSeconds(10)); srv.LastStage = "handshake done";
                await Chunks();
            }
            catch (TimeoutException) { srv.log($"{who}: no RTMP handshake within 10 s (is this OBS, with Service “Custom…” and a server starting rtmp://?)"); }
            catch (Exception e) when (published) { srv.log($"{who}: OBS connection closed while streaming ({e.Message})"); }
            catch { }
            finally { End(); client.Dispose(); }
        }
        async Task<byte[]> Read(int n, TimeSpan? limit = null)
        {
            var b = new byte[n]; using var cts = limit is { } l ? new CancellationTokenSource(l) : new CancellationTokenSource();
            try { await net.ReadExactlyAsync(b, cts.Token); } catch (OperationCanceledException) { throw new TimeoutException(); }
            return b;
        }
        async Task Chunks()
        {
            while (true)
            {
                var b0 = (await Read(1))[0]; int fmt = b0 >> 6, csid = b0 & 63;
                if (csid == 0) csid = 64 + (await Read(1))[0];
                else if (csid == 1) { var x = await Read(2); csid = 64 + x[0] + x[1] * 256; }
                var hl = new[] { 11, 7, 3, 0 }[fmt]; var h = hl > 0 ? await Read(hl) : [];
                var st = streams.TryGetValue(csid, out var s0) ? s0 : streams[csid] = new ChunkStream();
                uint tsField = 0;
                if (fmt <= 2) tsField = (uint)(h[0] << 16 | h[1] << 8 | h[2]);
                if (fmt <= 1) { st.Len = h[3] << 16 | h[4] << 8 | h[5]; st.Type = h[6]; }
                if (fmt == 0) st.Sid = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(7));
                var ext = fmt <= 2 ? tsField == 0xffffff : st.Ext; var tsVal = tsField;
                if (ext) tsVal = BinaryPrimitives.ReadUInt32BigEndian(await Read(4));
                if (st.Body.Length == 0)
                {   // a new message: its timestamp
                    if (fmt == 0) st.Ts = tsVal; else if (fmt is 1 or 2) { st.Delta = tsVal; st.Ts += tsVal; } else st.Ts += st.Delta;
                    st.Ext = ext;
                }
                var take = Math.Min(inChunk, st.Len - (int)st.Body.Length);
                if (take > 0) st.Body.Write(await Read(take));
                if (st.Body.Length >= st.Len) { var body = st.Body.ToArray(); st.Body = new MemoryStream(); await OnMessage(st.Type, st.Sid, st.Ts, body); }
            }
        }
        async Task Write(int csid, byte type, uint sid, byte[] payload, uint ts = 0)
        {
            var o = new MemoryStream();
            for (int off = 0; off < payload.Length || off == 0; off += OutChunk)
            {
                if (off == 0)
                {
                    var hd = new byte[12]; hd[0] = (byte)(csid & 63); var t = Math.Min(ts, 0xffffff); hd[1] = (byte)(t >> 16); hd[2] = (byte)(t >> 8); hd[3] = (byte)t;
                    hd[4] = (byte)(payload.Length >> 16); hd[5] = (byte)(payload.Length >> 8); hd[6] = (byte)payload.Length; hd[7] = type; BinaryPrimitives.WriteUInt32LittleEndian(hd.AsSpan(8), sid); o.Write(hd);
                }
                else o.WriteByte((byte)(0xc0 | (csid & 63)));
                o.Write(payload, off, Math.Min(OutChunk, payload.Length - off));
                if (payload.Length == 0) break;
            }
            await net.WriteAsync(o.ToArray());
        }
        Task Control(byte type, uint n) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, n); return Write(2, type, 0, b); }
        Task Command(uint sid, params object?[] vals) => Write(3, 20, sid, vals.SelectMany(Amf.Write).ToArray());
        Task Status(uint sid, string level, string code, string desc) => Command(sid, "onStatus", 0.0, null, new Dictionary<string, object?> { ["level"] = level, ["code"] = code, ["description"] = desc });

        async Task OnMessage(byte type, uint sid, uint ts, byte[] body)
        {
            if (type == 1) inChunk = (int)(BinaryPrimitives.ReadUInt32BigEndian(body) & 0x7fffffff);
            else if (type is 20 or 17) { try { await OnCommand(sid, Amf.ReadAll(type == 17 ? body[1..] : body)); } catch (Exception e) { srv.log("rtmp command error " + e.Message); } }
            else if (type is 8 or 9) { try { Media(type, ts, body); } catch (Exception e) { srv.log("rtmp media error " + e.Message); } }
        }
        async Task OnCommand(uint sid, List<object?> vals)
        {
            var name = vals.ElementAtOrDefault(0) as string; var txn = vals.ElementAtOrDefault(1);
            if (name == "connect")
            {
                var app = (vals.ElementAtOrDefault(2) as Dictionary<string, object?>)?.GetValueOrDefault("app") as string ?? "?";
                srv.log($"{who}: OBS connected (app “{app}”)"); srv.LastStage = "connected to app " + app;
                await Control(5, 2500000); await Write(2, 6, 0, [0, 0x26, 0x25, 0xa0, 2]); await Control(1, OutChunk);
                await Command(0, "_result", txn, new Dictionary<string, object?> { ["fmsVer"] = "FMS/3,0,1,123", ["capabilities"] = 31.0 },
                    new Dictionary<string, object?> { ["level"] = "status", ["code"] = "NetConnection.Connect.Success", ["description"] = "Connection succeeded.", ["objectEncoding"] = 0.0 });
            }
            else if (name == "createStream") await Command(0, "_result", txn, null, 1.0);
            else if (name is "releaseStream" or "FCPublish") await Command(0, "_result", txn, null, null);
            else if (name == "publish")
            {
                var key = vals.ElementAtOrDefault(3) as string ?? "";
                srv.log($"{who}: publish with stream key {(key.Length > 0 ? key[..Math.Min(4, key.Length)] + "…" : "(empty: put the key in OBS’s Stream Key box)")}"); srv.LastStage = "publish";
                var r = srv.onPublish(key);
                if (r is not IStreamSink s) { var why = r as string ?? "refused"; srv.LastStage = "refused: " + why; await Status(sid, "error", "NetStream.Publish.BadName", why); srv.log("OBS stream refused: " + why); await Task.Delay(500); client.Close(); return; }
                sink = s; published = true; Interlocked.Increment(ref srv.Live);
                await Status(sid, "status", "NetStream.Publish.Start", "Publishing to OpenTube"); srv.LastStage = "streaming"; srv.log("OBS is streaming");
            }
            else if (name is "FCUnpublish" or "deleteStream") { if (published) srv.log($"{who}: OBS stopped streaming ({name})"); End(); }
        }

        void Media(byte type, uint ts, byte[] body)
        {
            if (!published || body.Length == 0) return;
            if (type == 9)
            {
                if ((body[0] & 0x0f) != 7 || (body[0] & 0x80) != 0) { if (!warnedV) { srv.log("OBS stream: only H.264 video works here; pick x264 or another H.264 encoder"); warnedV = true; } return; }
                int pt = body[1], cto = (body[2] << 24 | body[3] << 16 | body[4] << 8) >> 8; bool key = (body[0] >> 4) == 1;
                if (pt == 0) { var avcC = body[5..]; var (w, hh) = Mp4.SpsSize(avcC) ?? (1280, 720); video = (avcC, w, hh, "avc1." + Convert.ToHexStringLower(avcC.AsSpan(1, 3))); return; }
                if (pt != 1 || video == null) return;
                if (key)
                {   // keyframes: YouTube and quick joining need one every few seconds
                    if (lastKey != null && ts > lastKey) { var gap = (ts - lastKey.Value) / 1000.0; srv.KeyInterval = Math.Round(gap * 10) / 10;
                        if (gap > 4.5 && !warnedKey) { warnedKey = true; srv.log($"OBS sends a keyframe only every {srv.KeyInterval} s. Set OBS → Settings → Output → Keyframe Interval to 2 (YouTube needs 4 s or less, and viewers join faster)."); } }
                    lastKey = ts;
                }
                if (key && fragStart != null && ts - fragStart >= 500) Flush(false, ts);   // start fragments at keyframes
                fragStart ??= ts;
                vq.Add((ts, cto, key, body[5..], 0));
            }
            else
            {
                if ((body[0] >> 4) != 10) { if (!warnedA) { srv.log("OBS stream: audio must be AAC"); warnedA = true; } return; }
                if (body[1] == 0) { var asc = body[2..]; var (ot, rate, ch) = Mp4.AacConfig(asc); audio = (asc, ot, rate, ch); return; }
                if (audio == null) return;
                aq.Add(((long)Math.Round(ts * (double)audio.Value.Rate / 1000), 0, false, body[2..], 0));
            }
            if (fragStart != null && ts - fragStart >= (sink?.FragMs ?? 2000)) Flush();   // long keyframe intervals: cut anyway
        }
        string Mime() => $"video/mp4; codecs=\"{string.Join(",", new[] { video?.Codec, audio is { } a ? "mp4a.40." + a.Ot : null }.Where(x => x != null))}\"";
        /// <summary>nextKey: the dts of a keyframe about to start the next fragment (so every frame before it goes out now).</summary>
        void Flush(bool final = false, long? nextKey = null)
        {
            if (sink == null || (video == null && audio == null)) return;
            if (!sentInit) { sink.Init(Mp4.InitSegment(video is { } v ? (v.AvcC, v.W, v.H) : null, audio is { } a ? (a.Asc, a.Rate, a.Ch) : null), Mime()); sentInit = true; }
            // otherwise keep the last sample of each track: its duration comes from the next one
            var vn = final || nextKey != null ? vq.Count : Math.Max(0, vq.Count - 1); var an = final ? aq.Count : Math.Max(0, aq.Count - 1);
            var vs = vq.GetRange(0, vn); vq.RemoveRange(0, vn); var @as = aq.GetRange(0, an); aq.RemoveRange(0, an);
            for (int i = 0; i < vs.Count; i++) { long? next = i + 1 < vs.Count ? vs[i + 1].Dts : nextKey ?? (vq.Count > 0 ? vq[0].Dts : null); vs[i] = vs[i] with { Dur = Math.Max(1, (next ?? vs[i].Dts + (lastVDur ?? 33)) - vs[i].Dts) }; }
            for (int i = 0; i < @as.Count; i++) { long? next = i + 1 < @as.Count ? @as[i + 1].Dts : aq.Count > 0 ? aq[0].Dts : null; @as[i] = @as[i] with { Dur = Math.Max(1, (next ?? @as[i].Dts + 1024) - @as[i].Dts) }; }
            if (vs.Count > 0) lastVDur = vs[^1].Dur;
            if (vs.Count == 0 && @as.Count == 0) return;
            var frag = Mp4.Fragment(seq++, vs.Select(s => (s.Dts, s.Dur, s.Cto, s.Key, s.Data)).ToList(), @as.Select(s => (s.Dts, s.Dur, 0, false, s.Data)).ToList());
            Interlocked.Add(ref srv.Bytes, frag.Length);
            sink.Fragment(frag, vs.Count > 0 && vs[0].Key);
            fragStart = null;
        }
        void End()
        {
            if (!published) return;
            published = false; Interlocked.Decrement(ref srv.Live);
            try { Flush(true); } catch { }
            sink?.End(); sink = null;
            srv.log($"OBS stream ended ({srv.Bytes / 1e6:0.0} MB)");
        }
    }
}

/// <summary>AMF0, the encoding of RTMP commands.</summary>
static class Amf
{
    public static List<object?> ReadAll(byte[] b) { var o = new List<object?>(); int p = 0; while (p < b.Length) o.Add(Read(b, ref p)); return o; }
    static object? Read(byte[] b, ref int p)
    {
        var t = b[p++];
        switch (t)
        {
            case 0: { var v = BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(p)); p += 8; return v; }
            case 1: return b[p++] != 0;
            case 2: { int n = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p)); var s = Encoding.UTF8.GetString(b, p + 2, n); p += 2 + n; return s; }
            case 12: { int n = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)); var s = Encoding.UTF8.GetString(b, p + 4, n); p += 4 + n; return s; }
            case 3: case 8:
            {
                if (t == 8) p += 4;
                var o = new Dictionary<string, object?>();
                while (true)
                {
                    int n = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p)); p += 2;
                    if (n == 0 && b[p] == 9) { p++; return o; }
                    var k = Encoding.UTF8.GetString(b, p, n); p += n; o[k] = Read(b, ref p);
                }
            }
            case 10: { int n = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p)); p += 4; var a = new List<object?>(); for (int i = 0; i < n; i++) a.Add(Read(b, ref p)); return a; }
            case 11: { var v = BinaryPrimitives.ReadDoubleBigEndian(b.AsSpan(p)); p += 10; return v; }
            case 5: case 6: return null;
            default: throw new InvalidDataException("AMF0 type " + t);
        }
    }
    public static byte[] Write(object? v) => v switch
    {
        null => [5],
        double d => [0, .. BE(d)],
        int i => [0, .. BE(i)],
        bool b => [1, (byte)(b ? 1 : 0)],
        string s => [2, (byte)(Encoding.UTF8.GetByteCount(s) >> 8), (byte)Encoding.UTF8.GetByteCount(s), .. Encoding.UTF8.GetBytes(s)],
        Dictionary<string, object?> o => [3, .. o.SelectMany(kv => { var k = Encoding.UTF8.GetBytes(kv.Key); return (byte[])[(byte)(k.Length >> 8), (byte)k.Length, .. k, .. Write(kv.Value)]; }), 0, 0, 9],
        _ => [5],
    };
    static byte[] BE(double d) { var b = new byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, d); return b; }
}

/// <summary>Fragmented MP4 (ISO BMFF) boxes: the header (ftyp + moov) and fragments (moof + mdat).</summary>
static class Mp4
{
    static byte[] U32(uint n) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, n); return b; }
    static byte[] U16(int n) => [(byte)(n >> 8), (byte)n];
    static byte[] U64(long n) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, (ulong)Math.Max(0, n)); return b; }
    static byte[] I32(int n) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, n); return b; }
    static byte[] Z(int n) => new byte[n];
    static byte[] Box(string type, params byte[][] parts) { var body = parts.SelectMany(x => x).ToArray(); return [.. U32((uint)body.Length + 8), .. Encoding.Latin1.GetBytes(type), .. body]; }
    static byte[] Full(string type, int version, int flags, params byte[][] parts) => Box(type, [[(byte)version, (byte)(flags >> 16), (byte)(flags >> 8), (byte)flags], .. parts]);
    static readonly byte[] Matrix = [.. U32(0x10000), .. U32(0), .. U32(0), .. U32(0), .. U32(0x10000), .. U32(0), .. U32(0), .. U32(0), .. U32(0x40000000)];
    static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    /// <summary>Width and height from the first SPS in an AVCDecoderConfigurationRecord.</summary>
    public static (int, int)? SpsSize(byte[] avcC)
    {
        try
        {
            int len = avcC[6] << 8 | avcC[7]; var nal = avcC.AsSpan(8, len);
            var rbsp = new List<byte>(); for (int i = 0; i < nal.Length; i++) { if (i > 1 && nal[i] == 3 && nal[i - 1] == 0 && nal[i - 2] == 0) continue; rbsp.Add(nal[i]); }
            int bit = 8;
            int Rd(int n) { int v = 0; for (int i = 0; i < n; i++) { v = (v << 1) | ((rbsp[bit >> 3] >> (7 - (bit & 7))) & 1); bit++; } return v; }
            int Ue() { int z = 0; while (Rd(1) == 0 && z < 32) z++; return (1 << z) - 1 + Rd(z); }
            int Se() { int k = Ue(); return (k & 1) != 0 ? (k + 1) / 2 : -k / 2; }
            int profile = Rd(8); Rd(16); Ue();
            int chroma = 1;
            if (new[] { 100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135 }.Contains(profile))
            {
                chroma = Ue(); if (chroma == 3) Rd(1); Ue(); Ue(); Rd(1);
                if (Rd(1) != 0) for (int i = 0; i < (chroma != 3 ? 8 : 12); i++) if (Rd(1) != 0) { int last = 8, next = 8; for (int j = 0; j < (i < 6 ? 16 : 64); j++) { if (next != 0) next = (last + Se() + 256) % 256; last = next != 0 ? next : last; } }
            }
            Ue(); int poc = Ue();
            if (poc == 0) Ue(); else if (poc == 1) { Rd(1); Se(); Se(); int n = Ue(); for (int i = 0; i < n; i++) Se(); }
            Ue(); Rd(1);
            int wMbs = Ue() + 1, hMap = Ue() + 1, frameMbsOnly = Rd(1);
            if (frameMbsOnly == 0) Rd(1);
            Rd(1);
            int cl = 0, cr = 0, ct = 0, cb = 0;
            if (Rd(1) != 0) { cl = Ue(); cr = Ue(); ct = Ue(); cb = Ue(); }
            int cx = chroma == 0 ? 1 : 2, cy = (chroma == 1 ? 2 : 1) * (2 - frameMbsOnly);
            return (wMbs * 16 - (cl + cr) * cx, (2 - frameMbsOnly) * hMap * 16 - (ct + cb) * cy);
        }
        catch { return null; }
    }
    public static (int Ot, int Rate, int Ch) AacConfig(byte[] asc)
    {
        int ot = asc[0] >> 3, fi = ((asc[0] & 7) << 1) | (asc[1] >> 7), ch = (asc[1] >> 3) & 15;
        int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        return (ot, fi < rates.Length ? rates[fi] : 44100, ch == 0 ? 2 : ch);
    }
    public static byte[] InitSegment((byte[] AvcC, int W, int H)? v, (byte[] Asc, int Rate, int Ch)? a)
    {
        var traks = new List<byte[]>(); var trex = new List<byte[]>();
        byte[] Stbl(byte[] entry) => Box("stbl", Full("stsd", 0, 0, U32(1), entry), Full("stts", 0, 0, U32(0)), Full("stsc", 0, 0, U32(0)), Full("stsz", 0, 0, U32(0), U32(0)), Full("stco", 0, 0, U32(0)));
        byte[] Dinf() => Box("dinf", Full("dref", 0, 0, U32(1), Full("url ", 0, 1)));
        if (v is { } vv)
        {
            var avc1 = Box("avc1", Z(6), U16(1), Z(16), U16(vv.W), U16(vv.H), U32(0x480000), U32(0x480000), U32(0), U16(1), Z(32), U16(0x18), U16(0xffff), Box("avcC", vv.AvcC));
            traks.Add(Box("trak", Full("tkhd", 0, 3, U32(0), U32(0), U32(1), U32(0), U32(0), Z(8), U16(0), U16(0), U16(0), U16(0), Matrix, U32((uint)vv.W << 16), U32((uint)vv.H << 16)),
                Box("mdia", Full("mdhd", 0, 0, U32(0), U32(0), U32(1000), U32(0), U16(0x55c4), U16(0)), Full("hdlr", 0, 0, U32(0), B("vide"), Z(12), B("VideoHandler\0")),
                    Box("minf", Full("vmhd", 0, 1, Z(8)), Dinf(), Stbl(avc1)))));
            trex.Add(Full("trex", 0, 0, U32(1), U32(1), U32(0), U32(0), U32(0)));
        }
        if (a is { } aa)
        {
            byte[] dsi = [5, (byte)aa.Asc.Length, .. aa.Asc];
            byte[] dcd = [4, (byte)(13 + dsi.Length), 0x40, 0x15, 0, 0, 0, .. U32(0), .. U32(0), .. dsi];
            byte[] es = [3, (byte)(3 + dcd.Length + 3), 0, 2, 0, .. dcd, 6, 1, 2];
            var mp4a = Box("mp4a", Z(6), U16(1), Z(8), U16(aa.Ch), U16(16), U16(0), U16(0), U32((uint)aa.Rate << 16), Full("esds", 0, 0, es));
            traks.Add(Box("trak", Full("tkhd", 0, 3, U32(0), U32(0), U32(2), U32(0), U32(0), Z(8), U16(0), U16(1), U16(0x100), U16(0), Matrix, U32(0), U32(0)),
                Box("mdia", Full("mdhd", 0, 0, U32(0), U32(0), U32((uint)aa.Rate), U32(0), U16(0x55c4), U16(0)), Full("hdlr", 0, 0, U32(0), B("soun"), Z(12), B("SoundHandler\0")),
                    Box("minf", Full("smhd", 0, 0, U32(0)), Dinf(), Stbl(mp4a)))));
            trex.Add(Full("trex", 0, 0, U32(2), U32(1), U32(0), U32(0), U32(0)));
        }
        var moov = Box("moov", [Full("mvhd", 0, 0, U32(0), U32(0), U32(1000), U32(0), U32(0x10000), U16(0x100), Z(10), Matrix, Z(24), U32(3)), .. traks, Box("mvex", [.. trex])]);
        return [.. Box("ftyp", B("isom"), U32(0x200), B("isomiso6avc1mp41")), .. moov];
    }
    /// <summary>One fragment: video samples (track 1, ms) and audio samples (track 2, sample-rate units).</summary>
    public static byte[] Fragment(uint seq, List<(long Dts, long Dur, int Cto, bool Key, byte[] Data)> vs, List<(long Dts, long Dur, int Cto, bool Key, byte[] Data)> @as)
    {
        var trafs = new List<(uint Track, List<(long Dts, long Dur, int Cto, bool Key, byte[] Data)> S, bool Video)>();
        if (vs.Count > 0) trafs.Add((1, vs, true));
        if (@as.Count > 0) trafs.Add((2, @as, false));
        byte[] Trun((uint Track, List<(long Dts, long Dur, int Cto, bool Key, byte[] Data)> S, bool Video) t, int off)
        {
            var flags = 0x1 | 0x100 | 0x200 | (t.Video ? 0x400 | 0x800 : 0);
            var per = t.S.SelectMany(s => t.Video ? (byte[])[.. U32((uint)s.Dur), .. U32((uint)s.Data.Length), .. U32(s.Key ? 0x02000000u : 0x01010000u), .. I32(s.Cto)] : [.. U32((uint)s.Dur), .. U32((uint)s.Data.Length)]).ToArray();
            return Full("trun", 1, flags, U32((uint)t.S.Count), I32(off), per);
        }
        byte[] Build(int[] offsets) => Box("moof", [Full("mfhd", 0, 0, U32(seq)), .. trafs.Select((t, i) => Box("traf", Full("tfhd", 0, 0x020000, U32(t.Track)), Full("tfdt", 1, 0, U64(t.S[0].Dts)), Trun(t, offsets[i])))]);
        var size = Build(trafs.Select(_ => 0).ToArray()).Length;
        int off = size + 8; var offs = new List<int>();
        foreach (var t in trafs) { offs.Add(off); off += t.S.Sum(s => s.Data.Length); }
        var mdat = trafs.SelectMany(t => t.S.SelectMany(s => s.Data)).ToArray();
        return [.. Build(offs.ToArray()), .. U32((uint)mdat.Length + 8), .. B("mdat"), .. mdat];
    }
}
