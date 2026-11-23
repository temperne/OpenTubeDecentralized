using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenTube.Core.BitTorrent;

/// <summary>
/// The torrents this computer seeds or downloads, and its peers: TCP between computers (incoming on a port, outgoing to
/// addresses), plus any other byte stream handed to <see cref="AddPeer"/> (a WebRTC data channel to a browser).
/// </summary>
public sealed class BtClient : IAsyncDisposable
{
    /// <summary>Peer id in WebTorrent's style (-WW0300-…), so browsers treat us like any WebTorrent peer.</summary>
    public readonly byte[] PeerId = [.. Encoding.ASCII.GetBytes("-OT0100-"), .. System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)];
    public readonly ConcurrentDictionary<string, TorrentData> Torrents = [];
    readonly ConcurrentDictionary<PeerSession, TorrentData?> peers = [];
    TcpListener? listener; readonly CancellationTokenSource cts = new();
    public int Port { get; private set; }
    public Action<string>? Log;
    public int PeerCount => peers.Count;
    public int PeersOf(string infoHash) => peers.Count(p => p.Key.Torrent?.InfoHashHex == infoHash);

    public TorrentData Add(TorrentData t) => Torrents.GetOrAdd(t.InfoHashHex, t);
    public void Remove(string infoHash) { if (Torrents.TryRemove(infoHash, out _)) foreach (var p in peers.Keys.Where(p => p.Torrent?.InfoHashHex == infoHash)) _ = p.DisposeAsync(); }

    /// <summary>Accept peers on a TCP port (0 picks one).</summary>
    public void Listen(int port = 0)
    {
        try { listener = new TcpListener(IPAddress.IPv6Any, port); listener.Server.DualMode = true; listener.Start(); }
        catch (SocketException) { listener = new TcpListener(IPAddress.Any, port); listener.Start(); }
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient c; try { c = await listener.AcceptTcpClientAsync(cts.Token); } catch { return; }
                c.NoDelay = true; _ = Incoming(c);
            }
        });
    }
    /// <summary>Plain or encrypted (as WebTorrent in Node.js connects by default): told apart by the first 20 bytes.</summary>
    async Task Incoming(TcpClient c)
    {
        var label = c.Client.RemoteEndPoint?.ToString() ?? ""; Stream s = c.GetStream();
        try
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(cts.Token); t.CancelAfter(TimeSpan.FromSeconds(30));
            var first = new byte[20]; await s.ReadExactlyAsync(first, t.Token);
            s = Mse.LooksPlain(first) ? new PrefixStream(s, first) : await Mse.Accept(s, first, Mse.Finder(Torrents.Values.Select(x => x.InfoHash).ToList()), t.Token);
        }
        catch (Exception e) { Log?.Invoke($"peer {label}: {e.Message}"); c.Dispose(); return; }
        await AddPeer(s, null, label + (s is PrefixStream ? "" : " (encrypted)"));
    }
    /// <summary>Encrypt outgoing TCP connections (falling back to plain if the other side won't).</summary>
    public bool Encrypt = true;
    /// <summary>Connect to a peer at host:port for a torrent.</summary>
    public async Task Connect(TorrentData t, string host, int port)
    {
        for (int attempt = Encrypt ? 0 : 1; attempt < 2; attempt++)
        {
            var c = new TcpClient { NoDelay = true }; Stream s;
            try
            {
                using var to = CancellationTokenSource.CreateLinkedTokenSource(cts.Token); to.CancelAfter(TimeSpan.FromSeconds(20));
                await c.ConnectAsync(host, port, to.Token); s = c.GetStream();
                if (attempt == 0) s = await Mse.Connect(s, t.InfoHash, to.Token);
            }
            catch (Exception e) { c.Dispose(); Log?.Invoke($"peer {host}:{port}{(attempt == 0 ? " (encrypted)" : "")}: {e.Message}"); continue; }
            await AddPeer(s, t, $"{host}:{port}{(attempt == 0 ? " (encrypted)" : "")}");
            return;
        }
    }
    /// <summary>A peer over any stream. <paramref name="t"/> null: the peer says which torrent in its handshake.</summary>
    public async Task AddPeer(Stream stream, TorrentData? t, string label)
    {
        var p = new PeerSession(stream, PeerId) { Label = label, Log = Log };
        peers[p] = t;
        try { await p.Run(t, ih => Torrents.GetValueOrDefault(Convert.ToHexStringLower(ih))); }
        finally { peers.TryRemove(p, out _); await p.DisposeAsync(); }
    }
    public async ValueTask DisposeAsync()
    {
        cts.Cancel(); listener?.Stop();
        foreach (var p in peers.Keys) await p.DisposeAsync();
    }
}
