using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace OpenTube.Core.BitTorrent;

/// <summary>
/// One torrent's data on disk: which pieces we have, reading blocks for peers, and assembling pieces from peers
/// (each checked against its SHA-1 before it counts). Without metadata yet (a magnet), it collects the info
/// dictionary from peers (ut_metadata) and checks it against the infohash.
/// </summary>
public sealed class TorrentData
{
    public const int Block = 16384;
    public readonly byte[] InfoHash; public TorrentMeta? Meta { get; private set; }
    public readonly string Path; public bool[] Have = [];
    public event Action<TorrentData>? Done, MetaReady;
    public event Action<int>? PieceDone;
    public long Uploaded, Downloaded;
    readonly object gate = new();
    readonly Dictionary<int, Assembly> building = [];
    byte[]? metaBuf; bool[]? metaGot;
    sealed class Assembly { public required byte[] Data; public required bool[] Got; public readonly Dictionary<int, object> Asked = []; }

    TorrentData(byte[] infoHash, string path) { InfoHash = infoHash; Path = path; }
    /// <summary>A complete file we seed (its torrent made as WebTorrent makes it).</summary>
    public static TorrentData Seed(string path, string? name = null)
    {
        var m = TorrentMeta.Create(path, name);
        var t = new TorrentData(m.InfoHashBytes, path); t.SetMeta(m, true); return t;
    }
    /// <summary>A torrent to download into <paramref name="path"/> (pieces already there and correct count).</summary>
    public static TorrentData Fetch(string infoHashHex, string path, TorrentMeta? meta = null)
    {
        var t = new TorrentData(Convert.FromHexString(infoHashHex), path);
        if (meta != null) t.SetMeta(meta, false);
        return t;
    }
    public string InfoHashHex => Convert.ToHexStringLower(InfoHash);
    public bool Complete => Meta != null && Have.All(x => x);
    public int HaveCount => Have.Count(x => x);

    void SetMeta(TorrentMeta m, bool all)
    {
        Meta = m; Have = new bool[m.PieceCount];
        if (all) Array.Fill(Have, true);
        else if (File.Exists(Path))
        {   // resume: keep the pieces already there
            using var f = File.OpenRead(Path); var buf = new byte[m.PieceLength];
            for (int i = 0; i < m.PieceCount; i++)
            {
                long at = (long)i * m.PieceLength; int n = m.SizeOf(i); if (at + n > f.Length) break;
                f.Position = at; f.ReadExactly(buf, 0, n);
                Have[i] = SHA1.HashData(buf.AsSpan(0, n)).AsSpan().SequenceEqual(m.PieceHash(i));
            }
        }
        if (!all) { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!); using var f = new FileStream(Path, FileMode.OpenOrCreate); f.SetLength(m.Length); }
    }

    public byte[]? ReadBlock(int piece, int offset, int length)
    {
        if (Meta == null || piece < 0 || piece >= Have.Length || !Have[piece] || length <= 0 || length > 128 * 1024 || offset < 0 || offset + length > Meta.SizeOf(piece)) return null;
        var b = new byte[length];
        lock (gate) { using var f = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); f.Position = (long)piece * Meta.PieceLength + offset; f.ReadExactly(b); }
        Interlocked.Add(ref Uploaded, length); return b;
    }

    /// <summary>The next block to ask this peer for (one of the pieces it has that we don't), or null.</summary>
    public (int piece, int offset, int length)? NextRequest(object peer, bool[] peerHas)
    {
        if (Meta == null) return null;
        lock (gate)
        {
            foreach (var (p, a) in building)   // finish pieces under way first
                if (p < peerHas.Length && peerHas[p] && Free(a) is int bi) { a.Asked[bi] = peer; return (p, bi * Block, Math.Min(Block, a.Data.Length - bi * Block)); }
            for (int p = 0; p < Have.Length; p++)
            {
                if (Have[p] || building.ContainsKey(p) || p >= peerHas.Length || !peerHas[p]) continue;
                int size = Meta.SizeOf(p); var asm = new Assembly { Data = new byte[size], Got = new bool[(size + Block - 1) / Block] };
                building[p] = asm; asm.Asked[0] = peer; return (p, 0, Math.Min(Block, size));
            }
            // endgame: everything is asked for; ask this peer too for blocks still missing
            foreach (var (p, a) in building)
                if (p < peerHas.Length && peerHas[p]) for (int bi = 0; bi < a.Got.Length; bi++) if (!a.Got[bi] && a.Asked.TryGetValue(bi, out var who) && who != peer) { a.Asked[bi] = peer; return (p, bi * Block, Math.Min(Block, a.Data.Length - bi * Block)); }
            return null;
        }
        static int? Free(Assembly a) { for (int i = 0; i < a.Got.Length; i++) if (!a.Got[i] && !a.Asked.ContainsKey(i)) return i; return null; }
    }
    /// <summary>A peer went away or choked us: its outstanding blocks may be asked of others.</summary>
    public void Release(object peer)
    {
        lock (gate) foreach (var a in building.Values) foreach (var k in a.Asked.Where(x => x.Value == peer).Select(x => x.Key).ToList()) if (!a.Got[k]) a.Asked.Remove(k);
    }
    /// <summary>A block arrived. True when it completed a piece that checked out.</summary>
    public bool WriteBlock(int piece, int offset, ReadOnlySpan<byte> data)
    {
        if (Meta == null) return false;
        bool done = false, all = false;
        lock (gate)
        {
            if (!building.TryGetValue(piece, out var a) || offset % Block != 0 || offset + data.Length > a.Data.Length) return false;
            int bi = offset / Block; if (a.Got[bi]) return false;
            data.CopyTo(a.Data.AsSpan(offset)); a.Got[bi] = true; Downloaded += data.Length;
            if (!a.Got.All(x => x)) return false;
            building.Remove(piece);
            if (!SHA1.HashData(a.Data).AsSpan().SequenceEqual(Meta.PieceHash(piece))) return false;   // bad data: asked again later
            using (var f = new FileStream(Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)) { f.Position = (long)piece * Meta.PieceLength; f.Write(a.Data); }
            Have[piece] = true; done = true; all = Have.All(x => x);
        }
        PieceDone?.Invoke(piece);
        if (all) Done?.Invoke(this);
        return done;
    }

    // ── metadata (BEP 9) ──
    public int MetaPieces(int size) => (size + Block - 1) / Block;
    public int? NextMetaPiece(int size)
    {
        lock (gate)
        {
            if (Meta != null || size <= 0 || size > 8 * 1024 * 1024) return null;
            if (metaBuf == null || metaBuf.Length != size) { metaBuf = new byte[size]; metaGot = new bool[MetaPieces(size)]; }
            for (int i = 0; i < metaGot!.Length; i++) if (!metaGot[i]) return i;
            return null;
        }
    }
    public void MetaPiece(int piece, int total, ReadOnlySpan<byte> data)
    {
        TorrentMeta? ready = null;
        lock (gate)
        {
            if (Meta != null || metaBuf == null || metaBuf.Length != total || piece < 0 || piece >= metaGot!.Length || piece * Block + data.Length > total) return;
            data.CopyTo(metaBuf.AsSpan(piece * Block)); metaGot[piece] = true;
            if (!metaGot.All(x => x)) return;
            if (SHA1.HashData(metaBuf).AsSpan().SequenceEqual(InfoHash) && TorrentMeta.FromInfoBytes(metaBuf) is { } m) { SetMeta(m, false); ready = m; }
            metaBuf = null; metaGot = null;
        }
        if (ready != null) { MetaReady?.Invoke(this); if (Complete) Done?.Invoke(this); }
    }
}

/// <summary>
/// One BitTorrent peer connection (BEP 3) over any byte stream: TCP between computers, or a WebRTC data channel to a
/// browser running WebTorrent. Speaks the extension protocol (BEP 10) with ut_metadata (BEP 9), so a peer with only the
/// magnet link gets the torrent from us. Seeds what we have, downloads what we don't.
/// </summary>
public sealed class PeerSession : IAsyncDisposable
{
    static readonly byte[] Proto = [19, .. Encoding.ASCII.GetBytes("BitTorrent protocol")];   // (not "\x13B…": C# reads \x13B as one character)
    const byte UtMetadataId = 1;   // our id for ut_metadata in the extension handshake
    readonly Stream s; readonly byte[] peerId; readonly SemaphoreSlim writeLock = new(1, 1);
    readonly CancellationTokenSource cts = new();
    public TorrentData? Torrent { get; private set; }
    public byte[]? RemoteId { get; private set; }
    public string Label = "";
    bool[] peerHas = []; bool peerChokingUs = true, weInterested, peerSupportsExt; int peerUtMetadata, peerMetaSize;
    int outstanding;
    const int Pipeline = 16;
    public Action<string>? Log;

    public PeerSession(Stream stream, byte[] myPeerId) { s = stream; peerId = myPeerId; }

    /// <summary>Run the connection. Outgoing: we know the torrent. Incoming: <paramref name="lookup"/> finds it by infohash.</summary>
    public async Task Run(TorrentData? torrent, Func<byte[], TorrentData?>? lookup = null)
    {
        var ct = cts.Token;
        try
        {
            if (torrent != null) { Torrent = torrent; await SendHandshake(); }
            var h = new byte[68]; await s.ReadExactlyAsync(h, ct);
            if (!h.AsSpan(0, 20).SequenceEqual(Proto)) throw new InvalidDataException("not BitTorrent");
            peerSupportsExt = (h[25] & 0x10) != 0;
            var ih = h[28..48]; RemoteId = h[48..68];
            if (Torrent == null) { Torrent = lookup?.Invoke(ih) ?? throw new InvalidDataException("unknown torrent"); await SendHandshake(); }
            else if (!ih.AsSpan().SequenceEqual(Torrent.InfoHash)) throw new InvalidDataException("wrong torrent");
            var t = Torrent;
            if (peerSupportsExt) await SendExtHandshake();
            if (t.Meta != null && t.HaveCount > 0) await SendBitfield();
            t.PieceDone += OnPieceDone;
            t.MetaReady += OnMetaReady;
            try { await ReadLoop(ct); }
            finally { t.PieceDone -= OnPieceDone; t.MetaReady -= OnMetaReady; t.Release(this); }
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException or OperationCanceledException or ObjectDisposedException) { Log?.Invoke($"peer {Label}: {e.Message}"); }
    }

    async Task SendHandshake()
    {
        var h = new byte[68]; Proto.CopyTo(h, 0); h[25] |= 0x10; Torrent!.InfoHash.CopyTo(h, 28); peerId.CopyTo(h, 48);
        await Write(h);
    }
    async Task SendExtHandshake()
    {
        var d = new Dictionary<string, object?> { ["m"] = new Dictionary<string, object?> { ["ut_metadata"] = (int)UtMetadataId } };
        if (Torrent!.Meta != null) d["metadata_size"] = Torrent.Meta.InfoBytes.Length;
        await SendExt(0, Bencode.Encode(d));
    }
    async Task SendBitfield()
    {
        var have = Torrent!.Have; var bf = new byte[(have.Length + 7) / 8];
        for (int i = 0; i < have.Length; i++) if (have[i]) bf[i >> 3] |= (byte)(0x80 >> (i & 7));
        await Msg(5, bf);
    }
    void OnPieceDone(int piece) { var b = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, piece); _ = Msg(4, b).ContinueWith(_ => { }); }
    void OnMetaReady(TorrentData t) { if (peerHas.Length != t.Meta!.PieceCount) peerHas = Resize(peerHas, t.Meta.PieceCount); _ = Want(); }
    static bool[] Resize(bool[] a, int n) { var r = new bool[n]; Array.Copy(a, r, Math.Min(a.Length, n)); return r; }

    async Task ReadLoop(CancellationToken ct)
    {
        var len = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            await s.ReadExactlyAsync(len, ct); int n = BinaryPrimitives.ReadInt32BigEndian(len);
            if (n == 0) continue;   // keep-alive
            if (n < 0 || n > 1 << 18) throw new InvalidDataException("message too long");
            var m = new byte[n]; await s.ReadExactlyAsync(m, ct);
            await Handle(m[0], m.AsMemory(1));
        }
    }

    async Task Handle(byte id, ReadOnlyMemory<byte> p)
    {
        var t = Torrent!;
        switch (id)
        {
            case 0: peerChokingUs = true; t.Release(this); outstanding = 0; break;
            case 1: peerChokingUs = false; await Fill(); break;
            case 2: await Msg(1, []); break;          // interested: we unchoke everyone (seeding is the point)
            case 3: break;
            case 4:
            {
                int i = BinaryPrimitives.ReadInt32BigEndian(p.Span);
                if (i >= 0) { if (i >= peerHas.Length) peerHas = Resize(peerHas, Math.Max(i + 1, t.Meta?.PieceCount ?? 0)); peerHas[i] = true; }
                await Want(); break;
            }
            case 5:
            {
                int count = t.Meta?.PieceCount ?? p.Length * 8; peerHas = new bool[count];
                for (int i = 0; i < count && (i >> 3) < p.Length; i++) peerHas[i] = (p.Span[i >> 3] & (0x80 >> (i & 7))) != 0;
                await Want(); break;
            }
            case 6:
            {
                int pi = BinaryPrimitives.ReadInt32BigEndian(p.Span), off = BinaryPrimitives.ReadInt32BigEndian(p.Span[4..]), l = BinaryPrimitives.ReadInt32BigEndian(p.Span[8..]);
                var b = t.ReadBlock(pi, off, l); if (b == null) break;
                var msg = new byte[8 + b.Length]; p.Span[..8].CopyTo(msg); b.CopyTo(msg, 8);
                await Msg(7, msg); break;
            }
            case 7:
            {
                int pi = BinaryPrimitives.ReadInt32BigEndian(p.Span), off = BinaryPrimitives.ReadInt32BigEndian(p.Span[4..]);
                outstanding = Math.Max(0, outstanding - 1);
                t.WriteBlock(pi, off, p.Span[8..]);
                if (t.Complete) { if (weInterested) { weInterested = false; await Msg(3, []); } }
                else await Fill();
                break;
            }
            case 20: await HandleExt(p.Span[0], p[1..]); break;
        }
    }

    async Task HandleExt(byte ext, ReadOnlyMemory<byte> p)
    {
        var t = Torrent!;
        if (ext == 0)
        {
            if (Bencode.Decode(p.Span) is not Dictionary<string, object?> d) return;
            if (d.GetValueOrDefault("m") is Dictionary<string, object?> m && m.GetValueOrDefault("ut_metadata") is long um) peerUtMetadata = (int)um;
            if (d.GetValueOrDefault("metadata_size") is long ms) peerMetaSize = (int)ms;
            await AskMeta();
            return;
        }
        if (ext != UtMetadataId) return;
        var msg = (Dictionary<string, object?>)Bencode.Decode(p.Span, out int end);
        long type = msg.GetValueOrDefault("msg_type") as long? ?? -1, piece = msg.GetValueOrDefault("piece") as long? ?? -1;
        if (type == 0)
        {   // a request for a piece of the info dictionary
            if (peerUtMetadata == 0) return;
            var info = t.Meta?.InfoBytes;
            if (info == null || piece < 0 || piece * TorrentData.Block >= info.Length) { await SendExt(peerUtMetadata, Bencode.Encode(new Dictionary<string, object?> { ["msg_type"] = 2, ["piece"] = piece })); return; }
            var head = Bencode.Encode(new Dictionary<string, object?> { ["msg_type"] = 1, ["piece"] = piece, ["total_size"] = info.Length });
            var chunk = info.AsSpan((int)piece * TorrentData.Block, Math.Min(TorrentData.Block, info.Length - (int)piece * TorrentData.Block));
            await SendExt(peerUtMetadata, [.. head, .. chunk]);
        }
        else if (type == 1 && msg.GetValueOrDefault("total_size") is long total)
        {
            t.MetaPiece((int)piece, (int)total, p.Span[end..]);
            await AskMeta();
        }
    }
    async Task AskMeta()
    {
        var t = Torrent!;
        if (t.Meta != null || peerUtMetadata == 0 || peerMetaSize == 0) return;
        if (t.NextMetaPiece(peerMetaSize) is int i) await SendExt(peerUtMetadata, Bencode.Encode(new Dictionary<string, object?> { ["msg_type"] = 0, ["piece"] = i }));
    }

    async Task Want()
    {
        var t = Torrent!; if (t.Meta == null || t.Complete) return;
        bool any = false; for (int i = 0; i < t.Have.Length && i < peerHas.Length; i++) if (peerHas[i] && !t.Have[i]) { any = true; break; }
        if (any && !weInterested) { weInterested = true; await Msg(2, []); }
        if (any) await Fill();
    }
    async Task Fill()
    {
        var t = Torrent!;
        while (!peerChokingUs && weInterested && outstanding < Pipeline && t.NextRequest(this, peerHas) is var (pi, off, l))
        {
            var b = new byte[12]; BinaryPrimitives.WriteInt32BigEndian(b, pi); BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), off); BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(8), l);
            outstanding++; await Msg(6, b);
        }
    }

    Task SendExt(int ext, byte[] payload) { var b = new byte[1 + payload.Length]; b[0] = (byte)ext; payload.CopyTo(b, 1); return Msg(20, b); }
    Task Msg(byte id, byte[] payload)
    {
        var b = new byte[5 + payload.Length]; BinaryPrimitives.WriteInt32BigEndian(b, 1 + payload.Length); b[4] = id; payload.CopyTo(b, 5);
        return Write(b);
    }
    async Task Write(byte[] b)
    {
        await writeLock.WaitAsync();
        try { await s.WriteAsync(b, cts.Token); await s.FlushAsync(cts.Token); }
        catch (Exception) { cts.Cancel(); }
        finally { writeLock.Release(); }
    }
    public async ValueTask DisposeAsync() { cts.Cancel(); await s.DisposeAsync(); }
}
