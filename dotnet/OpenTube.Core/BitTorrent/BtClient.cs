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
                c.NoDelay = true; _ = AddPeer(c.GetStream(), null, c.Client.RemoteEndPoint?.ToString() ?? "");
            }
        });
    }
    /// <summary>Connect to a peer at host:port for a torrent.</summary>
    public async Task Connect(TorrentData t, string host, int port)
    {
        var c = new TcpClient { NoDelay = true };
        try { await c.ConnectAsync(host, port, cts.Token); } catch (Exception e) { c.Dispose(); Log?.Invoke($"peer {host}:{port}: {e.Message}"); return; }
        _ = AddPeer(c.GetStream(), t, $"{host}:{port}");
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
