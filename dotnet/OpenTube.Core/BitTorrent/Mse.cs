using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace OpenTube.Core.BitTorrent;

/// <summary>
/// BitTorrent protocol encryption (Message Stream Encryption), which WebTorrent in Node.js uses on TCP by default:
/// a Diffie-Hellman exchange (768-bit prime), then RC4 both ways, keyed by the shared secret and the infohash.
/// Browsers' WebRTC connections don't use it (they're encrypted already).
/// </summary>
public static class Mse
{
    static readonly BigInteger P = BigInteger.Parse("0FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A63A36210000000000090563", System.Globalization.NumberStyles.HexNumber);
    static readonly byte[] Vc = new byte[8];
    const int Plain = 1, Rc4Method = 2;

    static byte[] H(string tag, params byte[][] parts) { var all = new List<byte>(Encoding.ASCII.GetBytes(tag)); foreach (var p in parts) all.AddRange(p); return SHA1.HashData(all.ToArray()); }
    static byte[] Be96(BigInteger v) { var b = v.ToByteArray(isUnsigned: true, isBigEndian: true); var r = new byte[96]; b.CopyTo(r, 96 - b.Length); return r; }
    static (BigInteger x, byte[] y) KeyPair() { var x = new BigInteger(RandomNumberGenerator.GetBytes(20), isUnsigned: true, isBigEndian: true); return (x, Be96(BigInteger.ModPow(2, x, P))); }
    static byte[] Secret(BigInteger x, byte[] other) => Be96(BigInteger.ModPow(new BigInteger(other, isUnsigned: true, isBigEndian: true), x, P));
    static byte[] Pad() => RandomNumberGenerator.GetBytes(RandomNumberGenerator.GetInt32(0, 64));
    static byte[] U16(int n) => [(byte)(n >> 8), (byte)n];
    static byte[] U32(int n) => [(byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n];

    /// <summary>The first bytes of a plain BitTorrent handshake; anything else on an incoming connection is encrypted.</summary>
    public static bool LooksPlain(ReadOnlySpan<byte> first20) => first20.Length >= 20 && first20[0] == 19 && first20[1..20].SequenceEqual("BitTorrent protocol"u8);

    /// <summary>We connect (the initiator). Returns the stream to speak BitTorrent over.</summary>
    public static async Task<Stream> Connect(Stream s, byte[] infoHash, CancellationToken ct)
    {
        var (x, ya) = KeyPair();
        await s.WriteAsync((byte[])[.. ya, .. Pad()], ct);
        var yb = new byte[96]; await s.ReadExactlyAsync(yb, ct);
        var S = Secret(x, yb);
        var enc = new Rc4(H("keyA", S, infoHash)); var dec = new Rc4(H("keyB", S, infoHash));
        var req23 = H("req2", infoHash); var r3 = H("req3", S); for (int i = 0; i < 20; i++) req23[i] ^= r3[i];
        byte[] tail = [.. Vc, .. U32(Plain | Rc4Method), .. U16(0), .. U16(0)];   // no PadC, no initial payload
        enc.Apply(tail);
        await s.WriteAsync((byte[])[.. H("req1", S), .. req23, .. tail], ct); await s.FlushAsync(ct);
        // find the other side's encrypted VC (after its pad, up to 512 bytes)
        var want = new Rc4(H("keyB", S, infoHash)); var vcEnc = (byte[])Vc.Clone(); want.Apply(vcEnc);
        var win = new List<byte>(); var one = new byte[1];
        while (true)
        {
            await s.ReadExactlyAsync(one, ct); win.Add(one[0]);
            if (win.Count >= 8 && win.Skip(win.Count - 8).SequenceEqual(vcEnc)) break;
            if (win.Count > 512 + 8) throw new InvalidDataException("encryption: no answer");
        }
        dec.Apply(new byte[8]);   // (the VC just read, keeping the stream in step)
        var sel = new byte[6]; await s.ReadExactlyAsync(sel, ct); dec.Apply(sel);
        int method = sel[3] & 3, padD = sel[4] << 8 | sel[5];
        if (padD > 512) throw new InvalidDataException("encryption: bad pad");
        var pd = new byte[padD]; await s.ReadExactlyAsync(pd, ct); dec.Apply(pd);
        return method == Rc4Method ? new Rc4Stream(s, enc, dec, []) : method == Plain ? s : throw new InvalidDataException("encryption: no method");
    }

    /// <summary>
    /// Someone connected to us with encryption (<paramref name="first"/> is what we read before telling). <paramref name="find"/>
    /// returns the infohash for the torrent whose HASH('req2', infohash) is given.
    /// </summary>
    public static async Task<Stream> Accept(Stream s, byte[] first, Func<byte[], byte[]?> find, CancellationToken ct)
    {
        var ya = new byte[96]; first.CopyTo(ya, 0); await s.ReadExactlyAsync(ya.AsMemory(first.Length), ct);
        var (x, yb) = KeyPair();
        await s.WriteAsync((byte[])[.. yb, .. Pad()], ct); await s.FlushAsync(ct);
        var S = Secret(x, ya); var req1 = H("req1", S);
        var win = new List<byte>(); var one = new byte[1];
        while (true)
        {
            await s.ReadExactlyAsync(one, ct); win.Add(one[0]);
            if (win.Count >= 20 && win.Skip(win.Count - 20).SequenceEqual(req1)) break;
            if (win.Count > 512 + 20) throw new InvalidDataException("encryption: no request");
        }
        var x23 = new byte[20]; await s.ReadExactlyAsync(x23, ct);
        var r3 = H("req3", S); for (int i = 0; i < 20; i++) x23[i] ^= r3[i];
        var ih = find(x23) ?? throw new InvalidDataException("unknown torrent");
        var dec = new Rc4(H("keyA", S, ih)); var enc = new Rc4(H("keyB", S, ih));
        var head = new byte[14]; await s.ReadExactlyAsync(head, ct); dec.Apply(head);
        if (!head.AsSpan(0, 8).SequenceEqual(Vc)) throw new InvalidDataException("encryption: bad VC");
        int provide = head[11], padC = head[12] << 8 | head[13];
        if (padC > 512) throw new InvalidDataException("encryption: bad pad");
        var pc = new byte[padC + 2]; await s.ReadExactlyAsync(pc, ct); dec.Apply(pc);
        int iaLen = pc[padC] << 8 | pc[padC + 1];
        var ia = new byte[iaLen]; await s.ReadExactlyAsync(ia, ct); dec.Apply(ia);
        int method = (provide & Rc4Method) != 0 ? Rc4Method : (provide & Plain) != 0 ? Plain : throw new InvalidDataException("encryption: no method");
        byte[] answer = [.. Vc, .. U32(method), .. U16(0)]; enc.Apply(answer);
        await s.WriteAsync(answer, ct); await s.FlushAsync(ct);
        return method == Rc4Method ? new Rc4Stream(s, enc, dec, ia) : new PrefixStream(s, ia);
    }

    /// <summary>For incoming connections: the infohash whose HASH('req2', infohash) this is.</summary>
    public static Func<byte[], byte[]?> Finder(IEnumerable<byte[]> infoHashes) => h => infoHashes.FirstOrDefault(ih => H("req2", ih).AsSpan().SequenceEqual(h));

    sealed class Rc4
    {
        readonly byte[] st = new byte[256]; int i, j;
        public Rc4(byte[] key)
        {
            for (int k = 0; k < 256; k++) st[k] = (byte)k;
            for (int k = 0, jj = 0; k < 256; k++) { jj = (jj + st[k] + key[k % key.Length]) & 255; (st[k], st[jj]) = (st[jj], st[k]); }
            Apply(new byte[1024]);   // RC4-drop1024
        }
        public void Apply(Span<byte> b)
        {
            for (int k = 0; k < b.Length; k++) { i = (i + 1) & 255; j = (j + st[i]) & 255; (st[i], st[j]) = (st[j], st[i]); b[k] ^= st[(st[i] + st[j]) & 255]; }
        }
    }
    sealed class Rc4Stream(Stream inner, Rc4 enc, Rc4 dec, byte[] prefix) : Stream
    {
        int pos;   // into prefix (already decrypted)
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (pos < prefix.Length) { int n = Math.Min(buffer.Length, prefix.Length - pos); prefix.AsMemory(pos, n).CopyTo(buffer); pos += n; return n; }
            int r = await inner.ReadAsync(buffer, ct); dec.Apply(buffer.Span[..r]); return r;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { var b = buffer.ToArray(); lock (enc) enc.Apply(b); await inner.WriteAsync(b, ct); }
        public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => ReadAsync(b.AsMemory(o, c), ct).AsTask();
        public override Task WriteAsync(byte[] b, int o, int c, CancellationToken ct) => WriteAsync(b.AsMemory(o, c), ct).AsTask();
        public override int Read(byte[] b, int o, int c) => ReadAsync(b, o, c, default).GetAwaiter().GetResult();
        public override void Write(byte[] b, int o, int c) => WriteAsync(b, o, c, default).GetAwaiter().GetResult();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override void Flush() => inner.Flush();
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
        protected override void Dispose(bool d) { if (d) inner.Dispose(); base.Dispose(d); }
    }
}

/// <summary>A stream that first gives back bytes already read from it.</summary>
public sealed class PrefixStream(Stream inner, byte[] prefix) : Stream
{
    int pos;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (pos < prefix.Length) { int n = Math.Min(buffer.Length, prefix.Length - pos); prefix.AsMemory(pos, n).CopyTo(buffer); pos += n; return n; }
        return await inner.ReadAsync(buffer, ct);
    }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
    public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => ReadAsync(b.AsMemory(o, c), ct).AsTask();
    public override Task WriteAsync(byte[] b, int o, int c, CancellationToken ct) => inner.WriteAsync(b, o, c, ct);
    public override int Read(byte[] b, int o, int c) => ReadAsync(b, o, c, default).GetAwaiter().GetResult();
    public override void Write(byte[] b, int o, int c) => inner.Write(b, o, c);
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override void Flush() => inner.Flush();
    public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long o, SeekOrigin so) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
    protected override void Dispose(bool d) { if (d) inner.Dispose(); base.Dispose(d); }
}
