using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace OpenTube.Core.BitTorrent;

/// <summary>
/// The BitTorrent DHT (BEP 5, "Mainline"): finding peers for a torrent without a tracker. KRPC (bencoded dictionaries)
/// over UDP; a routing table of k-buckets by XOR distance from this node's ID; the four queries (ping, find_node,
/// get_peers, announce_peer) answered for others, with write tokens; and the iterative lookup that walks toward an
/// infohash to collect peers and then announces this client's port to the closest nodes. IPv4 (compact 6/26-byte forms).
/// </summary>
public sealed class Dht : IDisposable
{
    public const int K = 8, Alpha = 3;
    public static readonly string[] DefaultBootstrap = ["router.bittorrent.com:6881", "dht.transmissionbt.com:6881", "router.utorrent.com:6881", "dht.libtorrent.org:25401"];

    public byte[] Id { get; }
    public int Port { get; private set; }
    public Action<string>? Log;
    UdpClient? udp;
    readonly CancellationTokenSource cts = new();
    readonly ConcurrentDictionary<ushort, TaskCompletionSource<Dictionary<string, object?>>> pending = new();
    int nextTid = RandomNumberGenerator.GetInt32(65536);
    // the routing table: bucket i holds nodes whose distance has its first set bit at i (0 = farthest half)
    readonly List<Node>[] buckets = Enumerable.Range(0, 161).Select(_ => new List<Node>()).ToArray();
    // peers others announced here: infohash hex -> endpoint -> when
    readonly ConcurrentDictionary<string, ConcurrentDictionary<IPEndPoint, DateTime>> store = new();
    byte[] secret = RandomNumberGenerator.GetBytes(16), oldSecret = RandomNumberGenerator.GetBytes(16);
    DateTime secretAt = DateTime.UtcNow;

    public sealed class Node(byte[] id, IPEndPoint ep)
    {
        public byte[] Id = id; public IPEndPoint Ep = ep; public DateTime Seen = DateTime.UtcNow; public int Fails;
        public override string ToString() => $"{Convert.ToHexStringLower(Id)[..8]}@{Ep}";
    }

    public Dht(byte[]? id = null) => Id = id ?? RandomNumberGenerator.GetBytes(20);

    /// <summary>How many nodes the routing table holds.</summary>
    public int NodeCount { get { lock (buckets) return buckets.Sum(b => b.Count); } }
    public List<Node> AllNodes() { lock (buckets) return buckets.SelectMany(b => b).ToList(); }

    /// <summary>Open the UDP port (0 picks one) and start answering.</summary>
    public void Start(int port = 0)
    {
        udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        if (OperatingSystem.IsWindows()) try { udp.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, [0, 0, 0, 0], null); } catch { }
        Port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        _ = Task.Run(Receive);
        _ = Task.Run(Maintain);
    }

    // ---- the wire
    async Task Receive()
    {
        while (!cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp!.ReceiveAsync(cts.Token); } catch (OperationCanceledException) { return; } catch (SocketException) { continue; } catch (ObjectDisposedException) { return; }
            try { Handle(r.Buffer, r.RemoteEndPoint); } catch (Exception e) { Log?.Invoke($"[dht] bad message from {r.RemoteEndPoint}: {e.Message}"); }
        }
    }
    void SendRaw(Dictionary<string, object?> m, IPEndPoint to) { try { udp?.Send(Bencode.Encode(m), to); } catch { } }
    static IPEndPoint V4(IPEndPoint e) => e.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(e.Address.MapToIPv4(), e.Port) : e;

    void Handle(byte[] data, IPEndPoint from)
    {
        from = V4(from);
        if (Bencode.Decode(data) is not Dictionary<string, object?> m || m.GetValueOrDefault("y") is not byte[] y) return;
        var type = Encoding.ASCII.GetString(y);
        if (type is "r" or "e")
        {
            if (m.GetValueOrDefault("t") is byte[] { Length: 2 } t && pending.TryRemove((ushort)(t[0] << 8 | t[1]), out var tcs))
            {
                if (type == "r" && m.GetValueOrDefault("r") is Dictionary<string, object?> r) { if (r.GetValueOrDefault("id") is byte[] { Length: 20 } rid) Saw(rid, from); tcs.TrySetResult(r); }
                else tcs.TrySetException(new IOException("error " + (m.GetValueOrDefault("e") is List<object> e && e.Count > 1 && e[1] is byte[] msg ? Encoding.UTF8.GetString(msg) : "")));
            }
            return;
        }
        if (type != "q" || m.GetValueOrDefault("q") is not byte[] qb || m.GetValueOrDefault("a") is not Dictionary<string, object?> a || a.GetValueOrDefault("id") is not byte[] { Length: 20 } qid) return;
        var tid = m.GetValueOrDefault("t") as byte[] ?? [];
        var q = Encoding.ASCII.GetString(qb);
        var reply = new Dictionary<string, object?> { ["id"] = Id };
        void Error(int code, string msg) => SendRaw(new() { ["t"] = tid, ["y"] = "e", ["e"] = new List<object> { code, msg } }, from);
        switch (q)
        {
            case "ping": break;
            case "find_node":
                if (a.GetValueOrDefault("target") is not byte[] { Length: 20 } target) { Error(203, "no target"); return; }
                reply["nodes"] = Compact(Closest(target, K)); break;
            case "get_peers":
            {
                if (a.GetValueOrDefault("info_hash") is not byte[] { Length: 20 } ih) { Error(203, "no info_hash"); return; }
                reply["token"] = Token(from.Address, secret);
                var peers = store.TryGetValue(Convert.ToHexStringLower(ih), out var s) ? s.Where(p => DateTime.UtcNow - p.Value < TimeSpan.FromMinutes(30)).Select(p => p.Key).Take(50).ToList() : [];
                if (peers.Count > 0) reply["values"] = peers.Select(p => (object)CompactPeer(p)).ToList();
                else reply["nodes"] = Compact(Closest(ih, K));
                break;
            }
            case "announce_peer":
            {
                if (a.GetValueOrDefault("info_hash") is not byte[] { Length: 20 } ih || a.GetValueOrDefault("token") is not byte[] tok) { Error(203, "missing arguments"); return; }
                if (!tok.AsSpan().SequenceEqual(Token(from.Address, secret)) && !tok.AsSpan().SequenceEqual(Token(from.Address, oldSecret))) { Error(203, "bad token"); return; }
                var port = a.GetValueOrDefault("implied_port") is long ip && ip != 0 ? from.Port : a.GetValueOrDefault("port") is long p ? (int)p : 0;
                if (port is <= 0 or > 65535) { Error(203, "bad port"); return; }
                var peersOf = store.GetOrAdd(Convert.ToHexStringLower(ih), _ => new());
                if (peersOf.Count < 2000) peersOf[new IPEndPoint(from.Address, port)] = DateTime.UtcNow;
                break;
            }
            default: Error(204, "method unknown"); return;
        }
        SendRaw(new() { ["t"] = tid, ["y"] = "r", ["r"] = reply }, from);
        if (a.GetValueOrDefault("ro") is not long ro || ro == 0) Saw(qid, from);   // read-only nodes (BEP 43) stay out of the table
    }

    /// <summary>Send a query and wait for its answer (null: no answer in time).</summary>
    public async Task<Dictionary<string, object?>?> Query(IPEndPoint to, string q, Dictionary<string, object?> args, int timeoutMs = 2500)
    {
        var tid = (ushort)Interlocked.Increment(ref nextTid);
        var tcs = new TaskCompletionSource<Dictionary<string, object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[tid] = tcs;
        args["id"] = Id;
        SendRaw(new() { ["t"] = new[] { (byte)(tid >> 8), (byte)tid }, ["y"] = "q", ["q"] = q, ["a"] = args }, to);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, cts.Token).ContinueWith(_ => { }));
        pending.TryRemove(tid, out _);
        if (done == tcs.Task && tcs.Task.IsCompletedSuccessfully) return tcs.Task.Result;
        Failed(to); return null;
    }

    // ---- the routing table
    static int Bucket(byte[] a, byte[] b) { for (int i = 0; i < 20; i++) { var x = a[i] ^ b[i]; if (x != 0) return i * 8 + System.Numerics.BitOperations.LeadingZeroCount((uint)x) - 24; } return 160; }
    static int CompareDistance(byte[] target, byte[] a, byte[] b) { for (int i = 0; i < 20; i++) { int da = a[i] ^ target[i], db = b[i] ^ target[i]; if (da != db) return da.CompareTo(db); } return 0; }
    void Saw(byte[] id, IPEndPoint ep)
    {
        if (id.AsSpan().SequenceEqual(Id) || ep.Port == 0) return;
        lock (buckets)
        {
            var b = buckets[Bucket(Id, id)];
            var n = b.FindIndex(x => x.Id.AsSpan().SequenceEqual(id));
            if (n >= 0) { var node = b[n]; node.Ep = ep; node.Seen = DateTime.UtcNow; node.Fails = 0; b.RemoveAt(n); b.Add(node); return; }
            if (b.Count < K) { b.Add(new Node(id, ep)); return; }
            var bad = b.FindIndex(x => x.Fails >= 2); if (bad >= 0) { b.RemoveAt(bad); b.Add(new Node(id, ep)); return; }
            var stale = b[0]; if (DateTime.UtcNow - stale.Seen > TimeSpan.FromMinutes(15)) _ = Query(stale.Ep, "ping", []);   // a full bucket: check its oldest (it's dropped after two failures)
        }
    }
    void Failed(IPEndPoint ep)
    {
        lock (buckets) foreach (var b in buckets) { var n = b.FindIndex(x => x.Ep.Equals(ep)); if (n >= 0) { if (++b[n].Fails >= 3) b.RemoveAt(n); return; } }
    }
    public List<Node> Closest(byte[] target, int count)
    {
        lock (buckets) return buckets.SelectMany(b => b).Where(n => n.Fails < 2).OrderBy(n => n, Comparer<Node>.Create((x, y) => CompareDistance(target, x.Id, y.Id))).Take(count).ToList();
    }

    // ---- compact forms
    static byte[] CompactPeer(IPEndPoint ep) { var b = new byte[6]; ep.Address.MapToIPv4().GetAddressBytes().CopyTo(b, 0); b[4] = (byte)(ep.Port >> 8); b[5] = (byte)ep.Port; return b; }
    static byte[] Compact(List<Node> nodes) { var b = new byte[nodes.Count * 26]; for (int i = 0; i < nodes.Count; i++) { nodes[i].Id.CopyTo(b, i * 26); CompactPeer(nodes[i].Ep).CopyTo(b, i * 26 + 20); } return b; }
    static IPEndPoint PeerFrom(ReadOnlySpan<byte> b) => new(new IPAddress(b[..4]), b[4] << 8 | b[5]);
    static IEnumerable<Node> NodesFrom(object? v)
    {
        if (v is not byte[] b) yield break;
        for (int i = 0; i + 26 <= b.Length; i += 26) { var ep = PeerFrom(b.AsSpan(i + 20, 6)); if (ep.Port != 0) yield return new Node(b[i..(i + 20)], ep); }
    }
    byte[] Token(IPAddress ip, byte[] s) => SHA1.HashData([.. s, .. ip.MapToIPv4().GetAddressBytes()])[..8];

    // ---- joining and lookups
    /// <summary>Join the network through known nodes ("host:port"), then look up this node's own ID to fill the table.</summary>
    public async Task Bootstrap(IEnumerable<string>? hosts = null)
    {
        var eps = new List<IPEndPoint>();
        foreach (var h in hosts ?? DefaultBootstrap)
        {
            var i = h.LastIndexOf(':'); if (i < 0 || !int.TryParse(h[(i + 1)..], out var port)) continue;
            try { foreach (var a in (await Dns.GetHostAddressesAsync(h[..i], cts.Token)).Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(2)) eps.Add(new IPEndPoint(a, port)); }
            catch (Exception e) { Log?.Invoke($"[dht] {h}: {e.Message}"); }
        }
        await Task.WhenAll(eps.Select(ep => Query(ep, "find_node", new() { ["target"] = Id })));
        await Lookup(Id, false);
        Log?.Invoke($"[dht] joined: {NodeCount} nodes known");
    }

    sealed record Found(List<IPEndPoint> Peers, List<(Node Node, byte[] Token)> Closest);
    /// <summary>The iterative lookup: ask the closest nodes known, then the closer ones they name, until no one closer answers.</summary>
    async Task<Found> Lookup(byte[] target, bool peers, Action<IPEndPoint>? onPeer = null)
    {
        var asked = new HashSet<string>(); var seenPeers = new HashSet<IPEndPoint>(); var got = new List<IPEndPoint>();
        var answered = new List<(Node Node, byte[] Token)>();
        var candidates = Closest(target, K * 2);
        var cmp = Comparer<Node>.Create((x, y) => CompareDistance(target, x.Id, y.Id));
        for (int round = 0; round < 12 && !cts.IsCancellationRequested; round++)
        {
            // the closest not yet asked: Alpha of them, or all of the best K when nothing got closer last round
            var next = candidates.Where(n => !asked.Contains(n.Ep.ToString())).OrderBy(n => n, cmp).Take(round == 0 ? K : Alpha * 2).ToList();
            if (next.Count == 0) break;
            foreach (var n in next) asked.Add(n.Ep.ToString());
            var results = await Task.WhenAll(next.Select(async n => (n, r: await Query(n.Ep, peers ? "get_peers" : "find_node", peers ? new() { ["info_hash"] = target } : new() { ["target"] = target }))));
            foreach (var (n, r) in results)
            {
                if (r == null) continue;
                if (r.GetValueOrDefault("id") is byte[] { Length: 20 } rid) n.Id = rid;
                if (peers && r.GetValueOrDefault("token") is byte[] tok) answered.Add((n, tok));
                if (r.GetValueOrDefault("values") is List<object> vals)
                    foreach (var v in vals) if (v is byte[] { Length: 6 } pb) { var ep = PeerFrom(pb); if (ep.Port != 0 && seenPeers.Add(ep)) { got.Add(ep); onPeer?.Invoke(ep); } }
                foreach (var nn in NodesFrom(r.GetValueOrDefault("nodes"))) if (!nn.Id.AsSpan().SequenceEqual(Id) && candidates.All(c => !c.Ep.Equals(nn.Ep))) candidates.Add(nn);
            }
            // stop once the K closest candidates have all been asked
            if (candidates.OrderBy(n => n, cmp).Take(K).All(n => asked.Contains(n.Ep.ToString()))) break;
        }
        answered.Sort((a, b) => CompareDistance(target, a.Node.Id, b.Node.Id));
        return new Found(got, answered.Take(K).ToList());
    }

    /// <summary>Peers for a torrent (each also reported to <paramref name="onPeer"/> as it's found).</summary>
    public async Task<List<IPEndPoint>> GetPeers(byte[] infoHash, Action<IPEndPoint>? onPeer = null) => (await Lookup(infoHash, true, onPeer)).Peers;

    /// <summary>Find peers and tell the closest nodes this client has the torrent on <paramref name="port"/> (0: the UDP port's number, "implied").</summary>
    public async Task<List<IPEndPoint>> Announce(byte[] infoHash, int port, Action<IPEndPoint>? onPeer = null)
    {
        var f = await Lookup(infoHash, true, onPeer);
        var acks = await Task.WhenAll(f.Closest.Select(c => Query(c.Node.Ep, "announce_peer", new()
        {
            ["info_hash"] = infoHash, ["port"] = port == 0 ? Port : port, ["implied_port"] = port == 0 ? 1 : 0, ["token"] = c.Token
        })));
        Log?.Invoke($"[dht] {Convert.ToHexStringLower(infoHash)[..8]}: {f.Peers.Count} peers, announced to {acks.Count(a => a != null)} of {f.Closest.Count} nodes");
        return f.Peers;
    }

    /// <summary>Keep the table fresh (refresh quiet buckets) and rotate the token secret every 5 minutes.</summary>
    async Task Maintain()
    {
        while (!cts.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cts.Token).ContinueWith(_ => { });
            if (cts.IsCancellationRequested) return;
            if (DateTime.UtcNow - secretAt > TimeSpan.FromMinutes(5)) { oldSecret = secret; secret = RandomNumberGenerator.GetBytes(16); secretAt = DateTime.UtcNow; }
            foreach (var s in store.Values) foreach (var p in s.Where(p => DateTime.UtcNow - p.Value > TimeSpan.FromMinutes(30)).ToList()) s.TryRemove(p.Key, out _);
            try
            {
                List<Node> quiet; lock (buckets) quiet = buckets.Where(b => b.Count > 0 && b.Max(n => n.Seen) < DateTime.UtcNow.AddMinutes(-15)).Select(b => b[^1]).Take(3).ToList();
                foreach (var n in quiet) { var t = (byte[])n.Id.Clone(); t[19] ^= 1; await Lookup(t, false); }
                if (NodeCount < K) await Bootstrap(saved.Count > 0 ? saved : null);
            }
            catch (Exception e) { Log?.Invoke("[dht] refresh: " + e.Message); }
        }
    }

    // ---- remembering nodes between runs
    List<string> saved = [];
    /// <summary>The nodes worth contacting next time ("ip:port" lines).</summary>
    public void Save(string file) { try { File.WriteAllLines(file, AllNodes().OrderBy(n => n.Fails).ThenByDescending(n => n.Seen).Take(200).Select(n => n.Ep.ToString())); } catch { } }
    /// <summary>Nodes from an earlier run, used to join (with the well-known routers).</summary>
    public List<string> Load(string file) { try { saved = File.Exists(file) ? File.ReadAllLines(file).Where(l => l.Contains(':')).ToList() : []; } catch { saved = []; } return [.. saved, .. DefaultBootstrap]; }

    public void Dispose() { cts.Cancel(); udp?.Dispose(); }
}
