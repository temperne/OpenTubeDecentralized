using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Node;

/// <summary>
/// Relaying (as the JavaScript node's liveRelays): when this computer's owner, or someone it's shared with, is live from
/// a browser on a channel they own or joined (or asks with the "relay" command), the node watches the stream as a
/// member on its hub (it gets every segment) and serves viewers itself over WebRTC data channels (the segment tree),
/// so neither the streamer's browser nor the hub has to. It keeps a recording of streams on channels its people own,
/// or when asked to. At most 6 streams at a time.
/// </summary>
public sealed class LiveRelays(OpenTubeNode node)
{
    sealed record Asked(string Chain, double At, bool Record, string? Title);
    readonly Dictionary<string, Asked> asked = [];
    readonly Dictionary<string, Relay> relaying = [];
    readonly object gate = new();
    public int Count { get { lock (gate) return relaying.Count; } }
    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>"I just went live: help serve it" (from the owner's or a sharing person's browser).</summary>
    public void Ask(string stream, string chain, bool record, string? title)
    {
        lock (gate)
        {
            if (record && relaying.Remove(stream, out var r)) r.Close();   // already relaying it: again, recording this time
            asked[stream] = new Asked(chain, Now(), record, title is { Length: > 100 } ? title[..100] : title);
        }
        _ = Task.Delay(500).ContinueWith(_ => Tick());
    }
    public async Task Run(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested) { try { Tick(); } catch (Exception e) { node.Log("[live] relays: " + e.Message); } await Task.Delay(10000, stop).ContinueWith(_ => { }); }
        lock (gate) { foreach (var r in relaying.Values) r.Close(); relaying.Clear(); }
    }

    IEnumerable<string> People() => new[] { node.OwnerUser }.Concat(((JsonObject?)node.Obs?.Store["users"])?.Select(kv => kv.Key) ?? []).Where(x => x != null).Select(x => x!);
    void Tick()
    {
        if (node.OwnerUser == null || Environment.GetEnvironmentVariable("LIVE_RELAY") == "0") return;
        var people = People().ToList();
        var want = node.Engine.Read(e =>
        {
            var w = new Dictionary<string, string>();
            var chans = people.SelectMany(u => e.Memberships.TryGetValue(u, out var m) ? (m["own"] as JsonArray ?? []).Concat(m["joined"] as JsonArray ?? []).Select(x => (string?)x) : []).Where(x => x != null).Distinct();
            foreach (var c in chans)
            {
                if (!e.Store.Chains.TryGetValue(c!, out var ch)) continue;
                foreach (var lb in Derived.Of(ch).LiveNow)
                    if ((string?)lb["author"] != node.NodeKey && (string?)lb["payload"]?["relay"] != node.NodeKey && (string?)lb["payload"]?["stream"] is { } s) w[s] = c!;
            }
            return w;
        });
        lock (gate)
        {
            foreach (var (s, a) in asked.ToList())
            {
                var ended = node.Engine.Read(e => e.Store.Chains.TryGetValue(a.Chain, out var ch) && Derived.Of(ch).LiveEnds.ContainsKey(s));
                if (Now() - a.At > 24 * 3600000 || ended) asked.Remove(s); else want[s] = a.Chain;
            }
            foreach (var (s, r) in relaying.ToList()) if (!want.ContainsKey(s) || r.Done) { r.Close(); relaying.Remove(s); }
            foreach (var (s, chain) in want)
            {
                if (relaying.ContainsKey(s) || relaying.Count >= 6) continue;
                var (title, record) = RecordWanted(s, chain, people);
                relaying[s] = new Relay(node, s, chain, HubUrlFor(chain, s), title, record);
            }
        }
    }
    /// <summary>Keep a recording here: when asked, and for browser streams on channels this computer's people own.</summary>
    (string Title, bool Record) RecordWanted(string stream, string chain, List<string> people)
    {
        asked.TryGetValue(stream, out var a);
        return node.Engine.Read(e =>
        {
            if (!e.Store.Chains.TryGetValue(chain, out var ch)) return (a?.Title ?? "Live", a?.Record == true);
            var d = Derived.Of(ch); var lb = d.Streams.FirstOrDefault(b => (string?)b["payload"]?["stream"] == stream);
            var title = a?.Title ?? (string?)lb?["payload"]?["title"] ?? "Live";
            if (title.Length == 0) title = "Live";
            var fromBrowser = lb == null || ((string?)lb["author"] == ch.Owner && lb["payload"]?["relay"] == null);
            var owned = people.Any(u => e.Memberships.TryGetValue(u, out var m) && (m["own"] as JsonArray ?? []).Any(x => (string?)x == chain) || ch.Owner == u || e.Store.Ledger.Resolve(ch.Owner ?? "") == e.Store.Ledger.Resolve(u));
            return (title, a?.Record == true || owned && fromBrowser && lb?["payload"]?["group"] == null);
        });
    }
    /// <summary>The hub a stream lives on: named in its live block (its hub node), else an entry node's or the site's.</summary>
    string HubUrlFor(string chain, string stream)
    {
        string Own() => $"ws://127.0.0.1:{node.Options.Port}/live";
        return node.Engine.Read(e =>
        {
            var lb = e.Store.Chains.TryGetValue(chain, out var ch) ? Derived.Of(ch).Streams.FirstOrDefault(b => (string?)b["payload"]?["stream"] == stream) : null;
            var hub = (string?)lb?["payload"]?["hub"];
            if (hub == node.NodeKey) return Own();
            if (hub != null && e.Nodes.TryGetValue(hub, out var n))
            {
                var addrs = (n["addrs"] as JsonArray ?? []).Select(x => (string?)x).Where(x => x != null).ToList();
                var a = addrs.FirstOrDefault(x => x!.StartsWith("https:")) ?? addrs.FirstOrDefault(x => !x!.Contains("localhost") && !x.Contains("127.")) ?? addrs.FirstOrDefault();
                if (a != null) return a.TrimEnd('/').Replace("https://", "wss://").Replace("http://", "ws://") + "/live";
            }
            if (node.Options.Site != null) return node.Options.Site.TrimEnd('/').Replace("https://", "wss://").Replace("http://", "ws://") + "/live";
            return Own();
        });
    }

    /// <summary>One stream being relayed: a member's socket on its hub, the children it feeds, and the recording.</summary>
    sealed class Relay
    {
        readonly OpenTubeNode node; readonly string stream, chain, url, title; readonly LiveKids kids; readonly CancellationTokenSource cts = new();
        readonly System.Threading.Channels.Channel<string> outbox = System.Threading.Channels.Channel.CreateUnbounded<string>();
        FileStream? rec; string? recPath; long got; public bool Done;
        public Relay(OpenTubeNode node, string stream, string chain, string url, string title, bool record)
        {
            this.node = node; this.stream = stream; this.chain = chain; this.url = url; this.title = title;
            kids = new LiveKids(stream, m => outbox.Writer.TryWrite(m.ToJsonString()), node.Log);
            if (record)
            {
                var dir = Path.Combine(node.Options.DataDir, "recordings"); Directory.CreateDirectory(dir);
                recPath = Path.Combine(dir, $"{string.Concat(title.Where(c => char.IsLetterOrDigit(c) || " .()-_".Contains(c))).Trim() switch { "" => "Live", var t => t }} {DateTime.Now:yyyy-MM-dd-HH-mm}.webm");
                try { rec = new FileStream(recPath, FileMode.Create, FileAccess.Write, FileShare.Read); node.Log($"[live] recording “{title}” to {recPath}"); } catch (Exception e) { node.Log("[live] can’t record: " + e.Message); }
            }
            _ = Task.Run(Run);
        }
        void Send(JsonObject m) => outbox.Writer.TryWrite(m.ToJsonString());
        async Task Run()
        {
            var alive = Task.Run(async () => { while (!cts.IsCancellationRequested) { await Task.Delay(15000, cts.Token).ContinueWith(_ => { }); Send(new JsonObject { ["t"] = "alive", ["stream"] = stream }); } });
            while (!cts.IsCancellationRequested)
            {
                using var ws = new ClientWebSocket();
                try
                {
                    await ws.ConnectAsync(new Uri(url), cts.Token);
                    var writer = Task.Run(async () => { await foreach (var m in outbox.Reader.ReadAllAsync(cts.Token)) { if (ws.State != WebSocketState.Open) break; await ws.SendAsync(Encoding.UTF8.GetBytes(m), WebSocketMessageType.Text, true, cts.Token); } });
                    var buf = new byte[1 << 16]; using var ms = new MemoryStream();
                    while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
                    {
                        var r = await ws.ReceiveAsync(buf, cts.Token);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buf, 0, r.Count); if (!r.EndOfMessage) continue;
                        JsonNode? m = null; try { m = JsonNode.Parse(ms.ToArray()); } catch { } ms.SetLength(0);
                        if (m is JsonObject o) Handle(o);
                    }
                }
                catch { }
                kids.Close();
                if (!cts.IsCancellationRequested) await Task.Delay(2000).ContinueWith(_ => { });
            }
        }
        void Handle(JsonObject m)
        {
            switch ((string?)m["t"])
            {
                case "hi":
                    Send(new JsonObject { ["t"] = "auth", ["key"] = node.NodeKey, ["sig"] = node.Identity!.Sign("opentube-live-auth:" + (string?)m["nonce"]) });
                    Send(new JsonObject { ["t"] = "watch", ["stream"] = stream, ["segOnly"] = true, ["media"] = false, ["dcSlots"] = 16, ["slots"] = 0, ["relay"] = true });
                    break;
                case "seg" when m["seg"] is JsonObject g && (string?)g["stream"] == stream:
                    if (!Verified(g)) return;
                    kids.Seg(g); Record(g);
                    if (got++ == 0) node.Log($"[live] relaying “{title}” on {Name()} to viewers over WebRTC");
                    break;
                case "child" when Js.IsTrue(m["dc"]) && (string?)m["peer"] is { } p: kids.Child(p); break;
                case "unchild" when (string?)m["peer"] is { } p: kids.Unchild(p); break;
                case "signal" when (string?)m["from"] is { } p: kids.Signal(p, m["data"]); break;
                case "ended" or "nostream": Done = true; Close(); break;
            }
        }
        /// <summary>Signed by the channel, as every viewer checks: only real segments are passed on and recorded.</summary>
        bool Verified(JsonObject g)
        {
            if ((string?)g["d"] is not { } d) return false;
            byte[] bytes; try { bytes = Convert.FromBase64String(d); } catch { return false; }
            if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != (string?)g["h"]) return false;
            var key = node.Engine.Read(e => e.Store.Chains.TryGetValue(chain, out var ch) ? Derived.Of(ch).Streams.FirstOrDefault(b => (string?)b["payload"]?["stream"] == stream)?["author"]?.ToString() : null);
            return key != null && Rules.VerifySig(key, $"opentube-live:{stream}:{(long)(Js.Num(g["n"]) ?? -1)}:{(long)(Js.Num(g["o"]) ?? -1)}:{(string?)g["h"]}", g["sig"]);
        }
        void Record(JsonObject g)
        {
            if (rec == null || Js.Num(g["o"]) is not double o) return;
            try { var b = Convert.FromBase64String((string)g["d"]!); rec.Position = (long)o; rec.Write(b); } catch { }
        }
        string Name() => node.Engine.Read(e => e.Store.Chains.TryGetValue(chain, out var c) ? (string?)Derived.Of(c).Profile["name"] : null) ?? chain[..8];
        public void Close()
        {
            if (cts.IsCancellationRequested) return;
            Send(new JsonObject { ["t"] = "leave", ["stream"] = stream });
            _ = Task.Delay(500).ContinueWith(_ => { cts.Cancel(); kids.Close(); });
            if (rec != null) { var size = rec.Length; rec.Dispose(); rec = null; node.Log($"[live] recording saved: {recPath} ({size / 1048576.0:0.0} MB)"); }
        }
    }
}
