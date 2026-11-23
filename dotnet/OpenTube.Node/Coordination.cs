using System.Net.Sockets;
using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Node;

/// <summary>
/// Coordination between computers (as the JavaScript node):
///  • meet places: entry nodes' own trackers (where browsers and nodes find each other); one is used while it answers
///    (its /entries.json, checked every minute, or a fresh signed status), dropped after 5 minutes of silence;
///  • the scratch pad: each entry node sends the other entry nodes what's live on its hub every 15 s, signed; entries
///    expire after a minute, so any entry node's /status shows the network's live streams;
///  • "can the internet reach that computer?": another node asks this one to connect to its OBS and web ports (only
///    addresses and ports a node announced for itself, at most 10 a minute); and this node's own OBS test asks others.
/// </summary>
public sealed class Coordination(OpenTubeNode node)
{
    const double MeetFresh = 5 * 60000;
    readonly Dictionary<string, double> meetSeen = [];
    readonly Dictionary<string, (double At, JsonArray Items)> pad = [];
    readonly List<double> reachTimes = [];
    readonly object gate = new();
    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(6) };

    public async Task Run(CancellationToken stop)
    {
        await Task.Delay(5000, stop).ContinueWith(_ => { });
        int n = 0;
        while (!stop.IsCancellationRequested)
        {
            if (n % 4 == 0) try { await CheckMeetPlaces(); } catch { }
            try { await SendPad(); } catch { }
            n++;
            await Task.Delay(15000, stop).ContinueWith(_ => { });
        }
    }

    // ---- meet places
    public List<string> MeetCandidates()
    {
        var own = node.PublicUrl?.TrimEnd('/'); var now = Now(); var outp = new List<string>();
        void Add(string? u) { u = u?.TrimEnd('/'); if (u != null && u.StartsWith("https://") && u != own && !outp.Contains(u)) outp.Add(u); }
        foreach (var x in node.Engine.Read(e => e.Nodes.Values.Select(v => (JsonObject)v.DeepClone()).ToList()))
        {
            if ((string?)x["node"] == node.NodeKey || x["roles"] is not JsonArray r || !r.Any(v => (string?)v == "entry") || now - (Js.Num(x["at"]) ?? 0) > 24 * 3600000) continue;
            var u = (x["addrs"] as JsonArray ?? []).Select(a => (string?)a).FirstOrDefault(a => a?.StartsWith("https:") == true); Add(u);
            if (u != null && now - (Js.Num(x["at"]) ?? 0) < MeetFresh) lock (gate) meetSeen[u.TrimEnd('/')] = Math.Max(meetSeen.GetValueOrDefault(u.TrimEnd('/')), Js.Num(x["at"]) ?? 0);
        }
        try { foreach (var e in JsonNode.Parse(File.ReadAllText(Path.Combine(node.SiteDir, "seeds.json")))?["entry"]?.AsArray() ?? []) Add(e is JsonValue v && v.TryGetValue<string>(out var s) ? s : (string?)e?["url"]); } catch { }
        return outp;
    }
    async Task CheckMeetPlaces()
    {
        await Task.WhenAll(MeetCandidates().Take(40).Select(async u =>
        {
            try { using var r = await http.GetAsync(u + "/entries.json"); if (r.IsSuccessStatusCode) lock (gate) meetSeen[u] = Now(); } catch { }
        }));
        lock (gate) foreach (var (u, t) in meetSeen.ToList()) if (Now() - t > 24 * 3600000) meetSeen.Remove(u);
    }
    bool Alive(string u) { lock (gate) return Now() - meetSeen.GetValueOrDefault(u) < MeetFresh; }
    /// <summary>This node's own tracker, and every entry node's that answers.</summary>
    public List<string> MeetPlaces()
    {
        var outp = new List<string> { $"ws://127.0.0.1:{node.Options.Port}/tracker" };
        foreach (var u in MeetCandidates()) if (Alive(u)) outp.Add(u.Replace("https://", "wss://") + "/tracker");
        return outp.Distinct().Take(16).ToList();
    }
    public List<string> MeetOffline() => MeetCandidates().Where(u => !Alive(u)).ToList();

    // ---- the scratch pad
    static string PadPreimage(JsonObject b) => "opentube-pad:" + Blocks.Canon(new JsonObject { ["from"] = b["from"]?.DeepClone(), ["at"] = b["at"]?.DeepClone(), ["items"] = b["items"]?.DeepClone() });
    public JsonObject PadTake(JsonObject b)
    {
        var no = new JsonObject { ["ok"] = false };
        if (Js.Str(b["from"]) is not { } from || b["items"] is not JsonArray items || items.Count > 200 || Math.Abs(Now() - (Js.Num(b["at"]) ?? 0)) > 60000) return no;
        var isEntry = node.Engine.Read(e => e.Nodes.TryGetValue(from, out var n) && n["roles"] is JsonArray r && r.Any(v => (string?)v == "entry"));
        if (!isEntry || !Rules.VerifySig(from, PadPreimage(b), b["sig"])) return no;
        lock (gate) pad[from] = (Js.Num(b["at"]) ?? 0, new JsonArray(items.Where(x => Js.IsHex(x?["stream"], 64)).Take(200).Select(x => x!.DeepClone()).ToArray()));
        return new JsonObject { ["ok"] = true };
    }
    /// <summary>Live streams on other entry nodes' hubs (each with its hub's key).</summary>
    public List<JsonObject> PadStreams()
    {
        var now = Now(); var outp = new List<JsonObject>();
        lock (gate)
            foreach (var (k, x) in pad.ToList())
            {
                if (now - x.At > 60000) { pad.Remove(k); continue; }
                foreach (var it in x.Items) { var o = (JsonObject)it!.DeepClone(); o["hub"] = k; outp.Add(o); }
            }
        return outp;
    }
    async Task SendPad()
    {
        if (node.PublicUrl == null) return;   // entry nodes only
        var b = new JsonObject { ["kind"] = "pad", ["from"] = node.NodeKey, ["at"] = Now(), ["items"] = new JsonArray(node.Hub.Live().Take(100).Select(x => x?.DeepClone()).ToArray()) };
        b["sig"] = node.Identity!.Sign(PadPreimage(b));
        var targets = node.Engine.Read(e => e.Nodes.Values.Where(n => (string?)n["node"] != node.NodeKey && n["roles"] is JsonArray r && r.Any(v => (string?)v == "entry") && Now() - (Js.Num(n["at"]) ?? 0) < 120000).Select(n => (string)n["node"]!).ToList());
        foreach (var k in targets) _ = node.Engine.Route(k, b.DeepClone(), 10000);
        await Task.CompletedTask;
    }

    // ---- reachability
    public static async Task<JsonObject> RtmpProbe(string host, int port)
    {
        var t0 = DateTime.UtcNow; JsonObject R(bool ok, string? error = null) { var o = new JsonObject { ["ok"] = ok, ["ms"] = (int)(DateTime.UtcNow - t0).TotalMilliseconds, ["host"] = host, ["port"] = port }; if (error != null) o["error"] = error; return o; }
        using var c = new TcpClient();
        try
        {
            using var cts = new CancellationTokenSource(6000);
            await c.ConnectAsync(host, port, cts.Token);
            var s = c.GetStream(); var c1 = new byte[1537]; c1[0] = 3; await s.WriteAsync(c1, cts.Token);
            var b = new byte[1]; var n = await s.ReadAsync(b, cts.Token);
            return n == 1 && b[0] == 3 ? R(true) : R(false, "something else answers on that port");
        }
        catch (OperationCanceledException) { return R(false, "no answer: the port is blocked by a firewall, or the address isn’t this computer"); }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused) { return R(false, "refused: nothing listens there (is OpenTube running with OBS on?)"); }
        catch (Exception e) { return R(false, e.Message); }
    }
    /// <summary>Another node asks: connect to its OBS and web ports from here (it can't test that itself).</summary>
    public async Task<JsonObject> ReachProbe(JsonObject b)
    {
        lock (gate) { var now = Now(); reachTimes.RemoveAll(t => now - t > 60000); if (reachTimes.Count >= 10) return new JsonObject { ["ok"] = false, ["error"] = "busy" }; reachTimes.Add(now); }
        var host = Js.Str(b["host"]) ?? ""; int rtmp = (int)(Js.Num(b["rtmp"]) ?? 0), httpPort = (int)(Js.Num(b["http"]) ?? 0);
        if (!System.Text.RegularExpressions.Regex.IsMatch(host, @"^\d{1,3}(\.\d{1,3}){3}$")) return new JsonObject { ["ok"] = false, ["error"] = "bad address" };
        var mine = (await Internet.PublicIPv4())?.ToString();
        if (host == mine) return new JsonObject { ["ok"] = false, ["error"] = "same network", ["same"] = true };
        var announced = node.Engine.Read(e => e.Nodes.Values.Where(n => (string?)n["info"]?["publicIp"] == host || (n["addrs"] as JsonArray ?? []).Any(a => Uri.TryCreate((string?)a, UriKind.Absolute, out var u) && u.Host == host)).Select(n => (JsonObject)n.DeepClone()).ToList());
        if (announced.Count == 0) return new JsonObject { ["ok"] = false, ["error"] = "no node announced that address" };
        var o = new JsonObject { ["by"] = node.NodeKey, ["name"] = node.Options.Label ?? Environment.MachineName, ["from"] = mine };
        if (rtmp > 0 && announced.Any(n => Js.Num(n["info"]?["obs"]?["port"]) == rtmp)) o["rtmp"] = await RtmpProbe(host, rtmp);
        if (httpPort > 0 && announced.Any(n => (n["addrs"] as JsonArray ?? []).Any(a => Uri.TryCreate((string?)a, UriKind.Absolute, out var u) && (u.Host == host || (string?)n["info"]?["publicIp"] == host) && u.Port == httpPort)))
        {
            var t0 = DateTime.UtcNow;
            try { using var r = await http.GetAsync($"http://{host}:{httpPort}/status"); o["http"] = new JsonObject { ["ok"] = r.IsSuccessStatusCode, ["ms"] = (int)(DateTime.UtcNow - t0).TotalMilliseconds, ["port"] = httpPort }; }
            catch (Exception e) { o["http"] = new JsonObject { ["ok"] = false, ["port"] = httpPort, ["ms"] = (int)(DateTime.UtcNow - t0).TotalMilliseconds, ["error"] = e is TaskCanceledException ? "no answer (blocked by a firewall?)" : e.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused ? "refused" : e.Message }; }
        }
        return o;
    }
    /// <summary>This node's OBS test: its own RTMP greeting from its public address, and up to 3 other online nodes asked to connect from outside.</summary>
    public async Task<JsonObject> ObsTest()
    {
        var pub = (await Internet.PublicIPv4())?.ToString(); var port = node.Rtmp?.Port ?? 0; var httpPort = node.Options.Port;
        var others = pub == null ? [] : node.Engine.Read(e => e.Nodes.Values.Where(n => (string?)n["node"] != node.NodeKey && Now() - (Js.Num(n["at"]) ?? 0) < 120000 && !(n["addrs"] as JsonArray ?? []).Any(x => ((string?)x ?? "").Contains("//" + pub + ":") || ((string?)x ?? "").Contains("//" + pub + "/"))).Select(n => (string)n["node"]!).ToList())
            .OrderBy(_ => Random.Shared.Next()).Take(4).ToList();
        var answers = await Task.WhenAll(others.Select(k => node.Engine.Route(k, new JsonObject { ["kind"] = "reach", ["host"] = pub, ["rtmp"] = port, ["http"] = httpPort, ["nonce"] = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)) }, 20000)));
        var outside = answers.OfType<JsonObject>().Where(r => !Js.IsTrue(r["same"]) && (r["rtmp"] != null || r["http"] != null)).Take(3).Select(r => (JsonNode?)r.DeepClone()).ToArray();
        return new JsonObject { ["test"] = pub != null ? await RtmpProbe(pub, port) : new JsonObject { ["ok"] = false, ["error"] = "couldn’t find this computer’s public address" }, ["outside"] = new JsonArray(outside), ["asked"] = others.Count, ["publicIp"] = pub, ["httpPort"] = httpPort };
    }
}
