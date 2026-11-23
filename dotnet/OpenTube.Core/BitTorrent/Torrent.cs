using System.Security.Cryptography;
using System.Text;

namespace OpenTube.Core.BitTorrent;

/// <summary>Bencode, the encoding of torrents and of BitTorrent extension messages. Strings are byte arrays.</summary>
public static class Bencode
{
    public static byte[] Encode(object? v) { var ms = new MemoryStream(); Write(ms, v); return ms.ToArray(); }
    static void Write(Stream s, object? v)
    {
        switch (v)
        {
            case byte[] b: W(s, $"{b.Length}:"); s.Write(b); break;
            case string str: Write(s, Encoding.UTF8.GetBytes(str)); break;
            case int or long or uint: W(s, $"i{Convert.ToInt64(v)}e"); break;
            case IDictionary<string, object?> d:
                s.WriteByte((byte)'d');
                foreach (var k in d.Keys.OrderBy(k => Encoding.UTF8.GetBytes(k), ByteOrder.Instance)) { Write(s, k); Write(s, d[k]); }
                s.WriteByte((byte)'e'); break;
            case System.Collections.IEnumerable list:
                s.WriteByte((byte)'l'); foreach (var x in list) Write(s, x); s.WriteByte((byte)'e'); break;
            default: throw new ArgumentException("can't bencode " + v?.GetType().Name);
        }
        static void W(Stream s, string t) => s.Write(Encoding.ASCII.GetBytes(t));
    }
    sealed class ByteOrder : IComparer<byte[]> { public static readonly ByteOrder Instance = new(); public int Compare(byte[]? a, byte[]? b) => a!.AsSpan().SequenceCompareTo(b); }

    /// <summary>Decode one value; <paramref name="end"/> is where it ended (extension messages carry raw bytes after the dictionary).</summary>
    public static object Decode(ReadOnlySpan<byte> b, out int end) { int p = 0; var v = Read(b, ref p); end = p; return v; }
    public static object Decode(ReadOnlySpan<byte> b) => Decode(b, out _);
    static object Read(ReadOnlySpan<byte> b, ref int p)
    {
        switch (b[p])
        {
            case (byte)'i': { int e = b[p..].IndexOf((byte)'e') + p; var n = long.Parse(Encoding.ASCII.GetString(b[(p + 1)..e])); p = e + 1; return n; }
            case (byte)'l': { p++; var l = new List<object>(); while (b[p] != (byte)'e') l.Add(Read(b, ref p)); p++; return l; }
            case (byte)'d':
            {
                p++; var d = new Dictionary<string, object?>();
                while (b[p] != (byte)'e') { var k = Encoding.UTF8.GetString((byte[])Read(b, ref p)); d[k] = Read(b, ref p); }
                p++; return d;
            }
            default:
            {
                int c = b[p..].IndexOf((byte)':') + p; int n = int.Parse(Encoding.ASCII.GetString(b[p..c])); var s = b.Slice(c + 1, n).ToArray(); p = c + 1 + n; return s;
            }
        }
    }
    /// <summary>The exact bytes of one top-level dictionary entry's value (for a torrent's info: its hash is over these bytes).</summary>
    public static byte[]? RawValue(ReadOnlySpan<byte> b, string key)
    {
        if (b[0] != (byte)'d') return null; int p = 1;
        while (b[p] != (byte)'e')
        {
            var k = Encoding.UTF8.GetString((byte[])Read(b, ref p)); int start = p; Read(b, ref p);
            if (k == key) return b[start..p].ToArray();
        }
        return null;
    }
}

/// <summary>
/// A single-file torrent made exactly as WebTorrent (create-torrent) makes it, so the same file gives the same infohash:
/// info = { name, piece length, pieces (SHA-1 of each piece), length }; piece length from the file size (16 KiB to 4 MiB).
/// </summary>
public sealed class TorrentMeta
{
    public required string Name; public required long Length; public required int PieceLength; public required byte[] InfoBytes;
    public byte[] InfoHashBytes => SHA1.HashData(InfoBytes);
    public string InfoHash => Convert.ToHexStringLower(InfoHashBytes);
    public int PieceCount => (int)((Length + PieceLength - 1) / PieceLength);
    public required byte[] Pieces;   // 20 bytes per piece
    public ReadOnlySpan<byte> PieceHash(int i) => Pieces.AsSpan(i * 20, 20);
    public int SizeOf(int piece) => (int)Math.Min(PieceLength, Length - (long)piece * PieceLength);

    /// <summary>piece-length: max(16 KiB, 2^round(log2(size / 1 KiB))), at most 4 MiB.</summary>
    public static int PieceLengthFor(long size) =>
        Math.Min(Math.Max(16384, 1 << (int)(Math.Log2(size < 1024 ? 1 : size / 1024.0) + 0.5)), 4 * 1024 * 1024);

    public static TorrentMeta Create(string path, string? name = null)
    {
        var len = new FileInfo(path).Length; var pl = PieceLengthFor(len);
        var pieces = new MemoryStream(); var buf = new byte[pl];
        using (var f = File.OpenRead(path))
            for (long done = 0; done < len;) { int n = (int)Math.Min(pl, len - done); f.ReadExactly(buf, 0, n); pieces.Write(SHA1.HashData(buf.AsSpan(0, n))); done += n; }
        return FromInfo(name ?? Path.GetFileName(path), len, pl, pieces.ToArray());
    }
    static TorrentMeta FromInfo(string name, long len, int pl, byte[] pieces) => new()
    {
        Name = name, Length = len, PieceLength = pl, Pieces = pieces,
        InfoBytes = Bencode.Encode(new Dictionary<string, object?> { ["name"] = name, ["piece length"] = pl, ["pieces"] = pieces, ["length"] = len }),
    };
    /// <summary>From the info dictionary's bytes (as received from a peer with ut_metadata, or from a .torrent file).</summary>
    public static TorrentMeta? FromInfoBytes(byte[] info)
    {
        try
        {
            var d = (Dictionary<string, object?>)Bencode.Decode(info);
            if (d.ContainsKey("files")) return null;   // (single-file torrents only: the network's videos)
            return new TorrentMeta { Name = Encoding.UTF8.GetString((byte[])d["name"]!), Length = (long)d["length"]!, PieceLength = (int)(long)d["piece length"]!, Pieces = (byte[])d["pieces"]!, InfoBytes = info };
        }
        catch { return null; }
    }
    public string Magnet(IEnumerable<string>? trackers = null) =>
        $"magnet:?xt=urn:btih:{InfoHash}&dn={Uri.EscapeDataString(Name)}" + string.Concat((trackers ?? []).Select(t => "&tr=" + Uri.EscapeDataString(t)));
}
