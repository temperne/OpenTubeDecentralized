using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenTube.Client;
using OpenTube.Core;

namespace OpenTube.Node;

/// <summary>
/// An authoritative DNS server for a domain whose name servers point at this node (--dns=opentuber.com). It answers:
/// the domain itself, www, ns1/ns2 and every wildcard namespace (alice.c.DOMAIN, bob.u.DOMAIN, …) with this node's
/// address; each subdomain the zone's owner approved on the network with the address it names (A/AAAA for an IP,
/// CNAME for a host name; none given: this node); TXT for Let's Encrypt's dns-01 checks and for _opentuber.DOMAIN (the
/// zone chain, so apps know which registry the domain uses). Other names: refused (it is not a resolver).
/// </summary>
public sealed class DnsServer(OpenTubeNode node, string domain, string? zone, IPAddress[] self, int port, Action<string> log)
{
    public readonly string Domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
    /// <summary>_acme-challenge names → the values Let's Encrypt looks for (several at once for a wildcard and its base).</summary>
    public static readonly ConcurrentDictionary<string, ConcurrentBag<string>> Txt = new(StringComparer.OrdinalIgnoreCase);
    const ushort A = 1, NS = 2, CNAME = 5, SOA = 6, TXT = 16, AAAA = 28, ANY = 255;
    UdpClient? udp; TcpListener? tcp; readonly CancellationTokenSource stop = new();
    public string? ZoneId => Zones.ZoneOf(node.Engine, Domain, zone);

    public void Start()
    {
        // both IPv6 and IPv4 where the computer has IPv6; IPv4 alone where it doesn't
        try { udp = new UdpClient(AddressFamily.InterNetworkV6); udp.Client.DualMode = true; udp.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port)); }
        catch (SocketException) { udp?.Dispose(); udp = new UdpClient(new IPEndPoint(IPAddress.Any, port)); }
        try { tcp = new TcpListener(IPAddress.IPv6Any, port); tcp.Server.DualMode = true; tcp.Start(); }
        catch (SocketException) { tcp = new TcpListener(IPAddress.Any, port); tcp.Start(); }
        log($"[dns] answering for {Domain} on port {port} (this node: {string.Join(", ", self.Select(a => a.ToString()))}; zone {ZoneId?[..12] ?? "not found yet"})");
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
                try { var r = await udp.ReceiveAsync(stop.Token); var res = Answer(r.Buffer, 512); if (res != null) await udp.SendAsync(res, r.RemoteEndPoint, stop.Token); }
                catch (OperationCanceledException) { break; } catch { }
        });
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient c; try { c = await tcp.AcceptTcpClientAsync(stop.Token); } catch { break; }
                _ = Task.Run(async () =>
                {
                    using (c) try
                    {
                        var s = c.GetStream(); var len = new byte[2]; await s.ReadExactlyAsync(len, stop.Token);
                        var q = new byte[len[0] << 8 | len[1]]; await s.ReadExactlyAsync(q, stop.Token);
                        var res = Answer(q, 65535); if (res == null) return;
                        await s.WriteAsync(new[] { (byte)(res.Length >> 8), (byte)res.Length }, stop.Token); await s.WriteAsync(res, stop.Token);
                    }
                    catch { }
                });
            }
        });
    }
    public void Stop() { stop.Cancel(); udp?.Dispose(); tcp?.Stop(); }

    sealed record Rr(string Name, ushort Type, byte[] Data, uint Ttl = 300);
    /// <summary>The answer to one DNS message (null to ignore it).</summary>
    public byte[]? Answer(byte[] q, int max)
    {
        if (q.Length < 12 || (q[2] & 0x80) != 0 || q[4] != 0 || q[5] != 1) return null;   // one question, a query
        var at = 12; var name = ReadName(q, ref at); if (name == null || at + 4 > q.Length) return null;
        var type = (ushort)(q[at] << 8 | q[at + 1]); var qend = at + 4;
        var (rcode, answers, authority) = Resolve(name.ToLowerInvariant(), type);
        var o = new List<byte>(); o.AddRange(q[..2]);
        o.Add((byte)(0x84 | (q[2] & 0x01))); o.Add((byte)rcode);   // response, authoritative, keeps "recursion desired"
        o.AddRange([0, 1, (byte)(answers.Count >> 8), (byte)answers.Count, (byte)(authority.Count >> 8), (byte)authority.Count, 0, 0]);
        o.AddRange(q[12..qend]);
        foreach (var r in answers.Concat(authority)) { o.AddRange(Name(r.Name)); o.AddRange([(byte)(r.Type >> 8), (byte)r.Type, 0, 1]); o.AddRange(BitConverter.GetBytes(r.Ttl).Reverse()); o.AddRange([(byte)(r.Data.Length >> 8), (byte)r.Data.Length]); o.AddRange(r.Data); }
        if (o.Count > max) { o = o.Take(qend).ToList(); o[2] |= 0x02; o[6] = o[7] = o[8] = o[9] = 0; }   // truncated: ask again over TCP
        return o.ToArray();
    }
    (int Rcode, List<Rr> Answers, List<Rr> Authority) Resolve(string name, ushort type)
    {
        var ans = new List<Rr>(); var soa = new List<Rr> { new(Domain, SOA, Soa(), 300) };
        if (name != Domain && !name.EndsWith("." + Domain)) return (5, ans, []);   // refused: not ours
        var sub = name == Domain ? "" : name[..^(Domain.Length + 1)]; var labels = sub.Length == 0 ? [] : sub.Split('.');
        void Self() { foreach (var ip in self) if (ip.AddressFamily == AddressFamily.InterNetwork ? type is A or ANY : type is AAAA or ANY) ans.Add(new(name, ip.AddressFamily == AddressFamily.InterNetwork ? A : AAAA, ip.GetAddressBytes())); }
        // TXT: certificate checks, and which zone chain this domain uses
        if (labels.Length > 0 && labels[0] == "_acme-challenge")
        {
            if (type is TXT or ANY && Txt.TryGetValue(name, out var vals)) foreach (var v in vals.Distinct()) ans.Add(new(name, TXT, TxtData(v), 60));
            return (0, ans, ans.Count == 0 ? soa : []);
        }
        if (sub == "_opentuber") { if (type is TXT or ANY && ZoneId is { } z) ans.Add(new(name, TXT, TxtData("zone=" + z))); return (0, ans, ans.Count == 0 ? soa : []); }
        if (sub.Length == 0)
        {
            if (type is NS or ANY) { ans.Add(new(name, NS, Name("ns1." + Domain))); ans.Add(new(name, NS, Name("ns2." + Domain))); }
            if (type is SOA or ANY) ans.Add(soa[0]);
            Self(); return (0, ans, ans.Count == 0 ? soa : []);
        }
        // this node: www, the name servers, the wildcard letters and every name under them
        if (labels.Length == 1 && labels[0] is "www" or "ns1" or "ns2" || Zones.Wildcards.Any(w => w.Letter == labels[^1]) && labels.Length <= 2) { Self(); return (0, ans, ans.Count == 0 ? soa : []); }
        // an approved subdomain
        if (labels.Length == 1 && ZoneId is { } zone && Zones.Records(node.Engine, zone).TryGetValue(labels[0], out var rec))
        {
            if (rec.Target.Length == 0) Self();
            else if (IPAddress.TryParse(rec.Target, out var ip)) { if (ip.AddressFamily == AddressFamily.InterNetwork ? type is A or ANY : type is AAAA or ANY) ans.Add(new(name, ip.AddressFamily == AddressFamily.InterNetwork ? A : AAAA, ip.GetAddressBytes())); }
            else ans.Add(new(name, CNAME, Name(rec.Target)));
            return (0, ans, ans.Count == 0 ? soa : []);
        }
        return (3, ans, soa);   // no such name
    }
    byte[] Soa()
    {
        var o = new List<byte>(); o.AddRange(Name("ns1." + Domain)); o.AddRange(Name("hostmaster." + Domain));
        var serial = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60);
        foreach (var v in new uint[] { serial, 3600, 600, 604800, 60 }) o.AddRange(BitConverter.GetBytes(v).Reverse());
        return o.ToArray();
    }
    static byte[] TxtData(string s) { var b = Encoding.UTF8.GetBytes(s); var o = new List<byte>(); for (int i = 0; i < b.Length; i += 255) { var n = Math.Min(255, b.Length - i); o.Add((byte)n); o.AddRange(b.AsSpan(i, n).ToArray()); } return o.ToArray(); }
    static byte[] Name(string n)
    {
        var o = new List<byte>();
        foreach (var l in n.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries)) { var b = Encoding.ASCII.GetBytes(l); o.Add((byte)b.Length); o.AddRange(b); }
        o.Add(0); return o.ToArray();
    }
    static string? ReadName(byte[] m, ref int at)
    {
        var parts = new List<string>(); int jumps = 0, back = -1;
        while (at < m.Length)
        {
            var len = m[at];
            if (len == 0) { at++; break; }
            if ((len & 0xC0) == 0xC0) { if (at + 1 >= m.Length || ++jumps > 10) return null; if (back < 0) back = at + 2; at = (len & 0x3F) << 8 | m[at + 1]; continue; }
            if (at + 1 + len > m.Length) return null;
            parts.Add(Encoding.ASCII.GetString(m, at + 1, len)); at += 1 + len;
        }
        if (back >= 0) at = back;
        return string.Join('.', parts);
    }
}
