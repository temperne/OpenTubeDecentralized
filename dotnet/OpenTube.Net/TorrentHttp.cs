using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenTube.Core.BitTorrent;

namespace OpenTube.Net;

/// <summary>
/// Serves torrents being downloaded to a local video player over HTTP (127.0.0.1, byte ranges): a request for bytes
/// not here yet waits for their pieces, and moves the download to where the player is (seeking). The phone app's
/// web view plays http://127.0.0.1:port/{infohash} in an ordinary video element.
/// </summary>
public sealed class TorrentHttp : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource cts = new();
    readonly Func<string, TorrentData?> find;
    public int Port { get; }
    public TorrentHttp(Func<string, TorrentData?> find)
    {
        this.find = find; listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () => { while (!cts.IsCancellationRequested) { try { var c = await listener.AcceptTcpClientAsync(cts.Token); _ = Serve(c); } catch { return; } } });
    }
    public string UrlFor(TorrentData t) => $"http://127.0.0.1:{Port}/{t.InfoHashHex}";

    async Task Serve(TcpClient c)
    {
        using var _ = c; var s = c.GetStream();
        try
        {
            while (!cts.IsCancellationRequested)   // keep-alive: a player makes several range requests
            {
                var head = await ReadHead(s); if (head == null) return;
                var lines = head.Split("\r\n"); var parts = lines[0].Split(' ');
                if (parts.Length < 2) return;
                var headers = lines.Skip(1).Select(l => l.Split(':', 2)).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0].Trim().ToLowerInvariant(), kv => kv[1].Trim());
                var t = find(parts[1].TrimStart('/').Split('?')[0]);
                if (t == null) { await Status(s, "404 Not Found"); return; }
                for (int i = 0; t.Meta == null && i < 600; i++) await Task.Delay(100, cts.Token);   // the torrent's info comes from the first peer
                if (t.Meta is not { } m) { await Status(s, "504 Gateway Timeout"); return; }
                long from = 0, to = m.Length - 1; bool ranged = false;
                if (headers.TryGetValue("range", out var r) && r.StartsWith("bytes="))
                {
                    var se = r[6..].Split(',')[0].Split('-'); ranged = true;
                    if (se[0].Length > 0) { from = long.Parse(se[0]); if (se[1].Length > 0) to = Math.Min(long.Parse(se[1]), m.Length - 1); }
                    else from = Math.Max(0, m.Length - long.Parse(se[1]));
                    if (from >= m.Length) { await Status(s, "416 Range Not Satisfiable", $"Content-Range: bytes */{m.Length}\r\n"); continue; }
                }
                var type = Path.GetExtension(m.Name).ToLowerInvariant() switch { ".webm" => "video/webm", ".mkv" => "video/x-matroska", ".mov" => "video/quicktime", ".mp3" => "audio/mpeg", _ => "video/mp4" };
                var hdr = $"HTTP/1.1 {(ranged ? "206 Partial Content" : "200 OK")}\r\nContent-Type: {type}\r\nAccept-Ranges: bytes\r\nContent-Length: {to - from + 1}\r\n" +
                          (ranged ? $"Content-Range: bytes {from}-{to}/{m.Length}\r\n" : "") + "Access-Control-Allow-Origin: *\r\nConnection: keep-alive\r\n\r\n";
                await s.WriteAsync(Encoding.ASCII.GetBytes(hdr), cts.Token);
                if (parts[0] == "HEAD") continue;
                t.FirstWanted = (int)(from / m.PieceLength);
                for (long at = from; at <= to;)
                {
                    int piece = (int)(at / m.PieceLength), off = (int)(at - (long)piece * m.PieceLength);
                    await WaitFor(t, piece);
                    int n = (int)Math.Min(Math.Min(m.SizeOf(piece) - off, 64 * 1024), to - at + 1);
                    var b = t.ReadBlock(piece, off, n) ?? throw new IOException("can't read piece " + piece);
                    await s.WriteAsync(b, cts.Token); at += n;
                }
            }
        }
        catch { }   // the player closed the connection (seeking, leaving)
    }
    static async Task WaitFor(TorrentData t, int piece)
    {
        if (t.Have[piece]) return;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(int p) { if (p == piece) tcs.TrySetResult(); }
        t.PieceDone += On;
        try { if (!t.Have[piece]) { if (t.FirstWanted > piece || piece - t.FirstWanted > 4) t.FirstWanted = piece; await tcs.Task.WaitAsync(TimeSpan.FromMinutes(5)); } }
        finally { t.PieceDone -= On; }
    }
    static async Task<string?> ReadHead(NetworkStream s)
    {
        var buf = new List<byte>(); var one = new byte[1];
        while (buf.Count < 16384)
        {
            if (await s.ReadAsync(one) == 0) return null;
            buf.Add(one[0]);
            if (buf.Count >= 4 && buf[^1] == '\n' && buf[^2] == '\r' && buf[^3] == '\n' && buf[^4] == '\r') return Encoding.ASCII.GetString(buf.ToArray(), 0, buf.Count - 4);
        }
        return null;
    }
    static Task Status(NetworkStream s, string status, string extra = "") => s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n{extra}Content-Length: 0\r\n\r\n")).AsTask();
    public void Dispose() { cts.Cancel(); listener.Stop(); }
}
