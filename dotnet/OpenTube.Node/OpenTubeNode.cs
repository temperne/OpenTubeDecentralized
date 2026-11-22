using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Jint;
using OpenTube.Core;

namespace OpenTube.Node;

/// <summary>
/// An OpenTube node: keeps the chains (the network's rules run in <see cref="JsCore"/>), talks to other nodes and
/// browsers over WebSockets (/ot), is a BitTorrent tracker for browsers (/tracker) and a live hub (/live). The website,
/// HTTP and tunnel live in <see cref="NodeServer"/>; the UIs only read <see cref="Status"/> and <see cref="Logs"/>.
/// </summary>
public sealed class OpenTubeNode : IAsyncDisposable
{
    public NodeOptions Options { get; }
    public JsCore Core { get; } = new();
    public string SiteDir { get; }
    public string Html { get; private set; } = "";
    public string Build { get; private set; } = "";
    public string? NodeKey { get; private set; }
    public string? NetworkId { get; private set; }
    /// <summary>The public https address (a Cloudflare tunnel, or one given), once there is one.</summary>
    public string? PublicUrl { get; set; }
    public DateTime Started { get; } = DateTime.UtcNow;

    readonly ConcurrentDictionary<string, Peer> peers = new();
    readonly ConcurrentQueue<string> logs = new();
    readonly CancellationTokenSource stop = new();
    StreamWriter? blockLog;
    int nextId;
    JsonObject status = new();

    public event Action<string>? Logged;
    /// <summary>Also print the log to the console (off while the terminal UI owns the screen).</summary>
    public bool Echo { get; set; } = true;
    /// <summary>Connect to another node: an IP address ("1.2.3.4" or "1.2.3.4:8080"), an https address, or ws://…/ot.</summary>
    public void AddPeer(string address) { var url = NodeOptions.NormalizePeer(address); if (!Options.Peers.Contains(url)) Options.Peers.Add(url); _ = DialAsync(url); }
    public IReadOnlyCollection<string> Logs => logs;
    public JsonObject Status => status;
    public IEnumerable<(string Id, string Label, string Role)> PeerList => peers.Values.Select(p => (p.Id, p.Label, p.Role));

    sealed record Peer(string Id, string Role, string Label, Channel<string> Out);

    public OpenTubeNode(NodeOptions options)
    {
        Options = options;
        SiteDir = options.SiteDir ?? FindSite() ?? throw new DirectoryNotFoundException("Can't find the website (index.html). Pass --site-dir=<folder with index.html>.");
        Core.Log = Log;
    }

    public void Log(string line)
    {
        line = DateTime.Now.ToString("HH:mm:ss") + " " + line;
        logs.Enqueue(line); while (logs.Count > 500) logs.TryDequeue(out _);
        if (Echo) Console.WriteLine(line);
        Logged?.Invoke(line);
    }

    static string? FindSite()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "index.html")) && File.Exists(Path.Combine(d.FullName, "sw.js"))) return d.FullName;
                if (File.Exists(Path.Combine(d.FullName, "site", "index.html"))) return Path.Combine(d.FullName, "site");
            }
        return null;
    }

    /// <summary>Load the rules, this node's key and its saved blocks, then connect to peers.</summary>
    public async Task StartAsync()
    {
        Directory.CreateDirectory(Options.DataDir);
        Html = File.ReadAllText(Path.Combine(SiteDir, "index.html"));
        Build = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Html)))[..12];
        Core.LoadFromPage(Html);
        var id = Identity.LoadOrCreate(Path.Combine(Options.DataDir, "key.json"));
        NodeKey = id.pub;

        // the bridge from the rules to this node: sending to peers, which peers there are, saving blocks
        Core.Post(e => e.SetValue("__n", e.Intrinsics.Object.Construct(Jint.Runtime.Arguments.Empty)));
        Core.Post(e =>
        {
            var n = e.GetValue("__n").AsObject();
            n.Set("send", Jint.Native.JsValue.FromObject(e, new Action<string, string>((json, to) => Send(json, to))));
            n.Set("sock", Jint.Native.JsValue.FromObject(e, new Action<string, string>((to, text) => Send(text, to))));
            n.Set("peers", Jint.Native.JsValue.FromObject(e, new Func<string>(() => JsonSerializer.Serialize(peers.Values.Where(p => p.Role is "node-in" or "node-out").Select(p => p.Id)))));
            n.Set("persist", Jint.Native.JsValue.FromObject(e, new Action<string>(line => { lock (this) blockLog?.WriteLine(line); })));
            n.Set("saveMem", Jint.Native.JsValue.FromObject(e, new Action<string>(json => SaveSoon(Path.Combine(Options.DataDir, "memberships.json"), json))));
            n.Set("saveRanker", Jint.Native.JsValue.FromObject(e, new Action<string>(json => File.WriteAllText(Path.Combine(Options.DataDir, "ranker.json"), json))));
        });
        Core.Post(e => e.Execute(Boot));
        var rankerFile = Path.Combine(Options.DataDir, "ranker.json");
        var ranker = File.Exists(rankerFile) ? File.ReadAllText(rankerFile) : JsonSerializer.Serialize(new { key = Identity.New() });
        if (!File.Exists(rankerFile)) File.WriteAllText(rankerFile, ranker);
        var bootOpts = new { mine = Options.Mine, ranks = Options.Ranks, rankName = (Options.RankName ?? $"Trending on {Environment.MachineName}")[..Math.Min(60, (Options.RankName ?? $"Trending on {Environment.MachineName}").Length)] };
        var r = JsonNode.Parse((await Core.RunAsync($"__boot({JsonSerializer.Serialize(JsonSerializer.Serialize(id))}, {JsonSerializer.Serialize(JsonSerializer.Serialize(bootOpts))}, {JsonSerializer.Serialize(ranker)})"))!)!;
        NetworkId = (string?)r["main"];

        // this computer's own copy of the chains: nothing is downloaded again
        var file = Path.Combine(Options.DataDir, "blocks.jsonl");
        if (File.Exists(file))
        {
            var t0 = DateTime.UtcNow;
            var rr = JsonNode.Parse((await Core.RunAsync($"__replay({JsonSerializer.Serialize(File.ReadAllText(file))})"))!)!;
            Log($"loaded {rr["n"]} saved blocks of {rr["chains"]} chains in {(DateTime.UtcNow - t0).TotalSeconds:0.0} s (main at #{rr["height"]}); only newer ones come from peers");
        }
        var memFile = Path.Combine(Options.DataDir, "memberships.json");
        if (File.Exists(memFile)) await Core.RunAsync($"__loadMem({JsonSerializer.Serialize(File.ReadAllText(memFile))})");
        await Core.RunAsync("__rankStart()");
        blockLog = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        Log($"OpenTube {Core.Version} on .NET {Environment.Version} · build {Build} · network {NetworkId?[..16]}… · node {NodeKey[..8]}… · website from {SiteDir}");

        foreach (var p in Options.Peers) _ = DialAsync(p);
        _ = Task.Run(FindEntryNodes);
        _ = Task.Run(StatusLoop);
        if (Options.Grant != null) _ = Task.Run(AnnounceLoop);
    }

    /// <summary>
    /// Ways into the network without naming a peer: the entry nodes in seeds.json and, with --site, the site's
    /// /entries.json (asked again every 10 minutes). Like the JavaScript node, it keeps a few of them connected.
    /// </summary>
    async Task FindEntryNodes()
    {
        var dialed = new HashSet<string>(Options.Peers);
        while (!stop.IsCancellationRequested)
        {
            var found = new List<string>();
            try
            {
                var seeds = Path.Combine(SiteDir, "seeds.json");
                if (File.Exists(seeds)) foreach (var e in JsonNode.Parse(File.ReadAllText(seeds))?["entry"]?.AsArray() ?? []) if (e is JsonValue v && v.TryGetValue<string>(out var u)) found.Add(u);
            }
            catch { }
            if (Options.Site != null)
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var j = JsonNode.Parse(await http.GetStringAsync(Options.Site + "/entries.json"));
                    foreach (var e in j?["entry"]?.AsArray() ?? []) if ((string?)e?["url"] is { } u) found.Add(u);
                }
                catch (Exception e) { Log($"couldn't ask {Options.Site} for entry nodes: {e.Message}"); }
            var own = PublicUrl?.TrimEnd('/');
            foreach (var u in found.Select(x => x.TrimEnd('/')).Where(x => x.StartsWith("https://", StringComparison.Ordinal) && x != own).Distinct().Take(4))
            {
                var url = NodeOptions.NormalizePeer(u);
                if (dialed.Add(url)) { Log($"connecting to entry node {u}"); _ = DialAsync(url); }
            }
            await Task.Delay(TimeSpan.FromMinutes(10), stop.Token).ContinueWith(_ => { });
        }
    }

    readonly ConcurrentDictionary<string, string> toSave = new();
    void SaveSoon(string file, string json)
    {
        if (toSave.TryAdd(file, json)) _ = Task.Delay(2000).ContinueWith(_ => { if (toSave.TryRemove(file, out var j)) File.WriteAllText(file, j); });
        else toSave[file] = json;
    }

    // ---- peers
    void Send(string text, string to)
    {
        if (string.IsNullOrEmpty(to)) { foreach (var p in peers.Values) if (p.Role is "node-in" or "node-out") p.Out.Writer.TryWrite(text); }
        else if (peers.TryGetValue(to, out var p)) p.Out.Writer.TryWrite(text);
    }

    /// <summary>A WebSocket that connected to us: a browser or node (/ot), a tracker client (/tracker) or a live viewer (/live).</summary>
    public async Task AcceptAsync(WebSocket ws, string role, string label)
    {
        var id = role == "node-in" ? "ws" + Interlocked.Increment(ref nextId) : role + Interlocked.Increment(ref nextId);
        await RunSocket(ws, id, role, label);
    }

    async Task DialAsync(string url)
    {
        var tries = 0;
        while (!stop.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri(url), stop.Token);
                tries = 0; Log($"linked to {url}");
                await RunSocket(ws, "node:" + url, "node-out", url);
            }
            catch (Exception e) when (!stop.IsCancellationRequested) { if (tries == 0) Log($"can't reach {url}: {e.Message}"); }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(600, 10 * Math.Pow(2, tries++))), stop.Token).ContinueWith(_ => { });
        }
    }

    async Task RunSocket(WebSocket ws, string id, string role, string label)
    {
        var peer = new Peer(id, role, label, Channel.CreateBounded<string>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest }));
        peers[id] = peer;
        var jsRole = role is "node-in" or "node-out" ? role : role;
        Core.Call("__open", jsRole, id);
        if (role == "node-in") Log($"+ browser {id} ({peers.Count(p => p.Value.Role is "node-in" or "node-out")} connected)");
        var writer = Task.Run(async () =>
        {
            await foreach (var text in peer.Out.Reader.ReadAllAsync())
            {
                if (ws.State != WebSocketState.Open) break;
                try { await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, stop.Token); } catch { break; }
            }
        });
        var buf = new byte[64 * 1024]; using var msg = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buf, stop.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                msg.Write(buf, 0, r.Count);
                if (msg.Length > 16 << 20) break;   // too big: drop the peer
                if (!r.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length); msg.SetLength(0);
                Core.Call("__in", role, id, text);
            }
        }
        catch { }
        finally
        {
            peers.TryRemove(id, out _); peer.Out.Writer.TryComplete();
            Core.Call("__close", role, id);
            if (role == "node-in") Log($"- browser {id} ({peers.Count(p => p.Value.Role is "node-in" or "node-out")} connected)");
            if (role == "node-out") Log($"lost {label}; reconnecting");
            try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
            await writer;
        }
    }

    // ---- status for the UIs and /status
    async Task StatusLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var s = JsonNode.Parse((await Core.RunAsync("Promise.resolve(__status())"))!)!.AsObject();
                s["peers"] = peers.Values.Count(p => p.Role is "node-in" or "node-out");
                s["trackerClients"] = peers.Values.Count(p => p.Role == "tracker");
                s["liveClients"] = peers.Values.Count(p => p.Role == "live");
                s["build"] = Build; s["public"] = PublicUrl; s["impl"] = $".NET {Environment.Version}"; s["uptime"] = (long)(DateTime.UtcNow - Started).TotalSeconds;
                status = s;
            }
            catch (Exception e) { Log("[status] " + e.Message); }
            await Task.Delay(2000, stop.Token).ContinueWith(_ => { });
        }
    }

    public Task<string?> EntriesJsonAsync() => Core.RunAsync($"Promise.resolve(__entries({JsonSerializer.Serialize(PublicUrl)}))");

    // ---- "your node": it announces itself to its owner's Devices page every 30 s (needs the owner's grant)
    async Task AnnounceLoop()
    {
        string grantJson;
        try { grantJson = Encoding.UTF8.GetString(B64u.Decode(Options.Grant!)); JsonNode.Parse(grantJson); }
        catch { Log("the grant isn't valid; not announcing this node"); return; }
        await Task.Delay(4000);
        while (!stop.IsCancellationRequested)
        {
            var roles = new List<string> { "node" }; if (PublicUrl != null) roles.Add("entry");
            var addrs = new List<string>(); if (PublicUrl != null) addrs.Add(PublicUrl);
            addrs.AddRange(System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a.Address))
                .Select(a => $"http://{a.Address}:{Options.Port}"));
            addrs.Add($"http://localhost:{Options.Port}");
            var info = new JsonObject
            {
                ["version"] = new JsonObject { ["name"] = Core.Version, ["build"] = Build, ["impl"] = "dotnet" },
                ["name"] = (Options.Label ?? Environment.MachineName)[..Math.Min(60, (Options.Label ?? Environment.MachineName).Length)],
                ["os"] = $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}",
                ["node"] = $".NET {Environment.Version}", ["peers"] = peers.Values.Count(p => p.Role is "node-in" or "node-out"),
            };
            try { await Core.RunAsync($"__announce({grantJson}, {JsonSerializer.Serialize(roles.Take(8))}, {JsonSerializer.Serialize(addrs.Take(8))}, {info.ToJsonString()})"); }
            catch (Exception e) { Log("announce failed: " + e.Message); }
            await Task.Delay(30000, stop.Token).ContinueWith(_ => { });
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        foreach (var p in peers.Values) p.Out.Writer.TryComplete();
        await Task.Delay(100);
        Core.Dispose();
        lock (this) { blockLog?.Dispose(); blockLog = null; }
    }

    // The node's side of the rules, in the same script language as the rules themselves (it mirrors server.mjs).
    const string Boot = """
      globalThis.__boot = async (identJson, optsJson, rankerJson) => {
        const core = OpenTubeCore, o = JSON.parse(optsJson), ranker = JSON.parse(rankerJson);
        const signer = await core.signer(JSON.parse(identJson)), rankerSigner = await core.signer(ranker.key);
        let rankTimer = null; const soonRank = () => { if (!rankTimer) rankTimer = setTimeout(() => { rankTimer = null; publishRanking(); }, 20000); };
        const send = (m, to) => __n.send(JSON.stringify(m), to == null ? '' : String(to));
        const engine = await core.createEngine({
          signer, send, peers: () => JSON.parse(__n.peers()),
          keyFor: (pub) => pub === rankerSigner.pub ? rankerSigner : null,
          onMembership: () => __n.saveMem(JSON.stringify([...engine.memberships.values()])),
          mine: o.mine === 'off' ? null : __mine,
          shouldMine: o.mine === 'all' ? () => true : (g) => g.author === signer.pub,
          trackAll: true, relayWatch: true,
          persist: (c, b) => __n.persist(JSON.stringify({ c, b })),
          onBlock: (chain, b) => {
            if (chain === engine.main && !globalThis.__replaying) __h.log(`main #${b.height} ${b.hash.slice(0, 12)} (${b.payload.entries.length} registration(s))`);
            else if (['video', 'vote', 'hide'].includes(b.payload.type) && !globalThis.__replaying) soonRank();
          }
        });
        // this node's own ranking list ("Trending on …"), like every node: created once, republished every 5 minutes
        async function publishRanking() {
          const id = ranker.genesis?.hash; if (!id) return;
          if (!engine.directory.has(id)) { engine.maybeMine(); return; }   // still being registered
          try { await engine.publishRanking(id, engine.rank()); } catch (e) { __h.log('ranking failed: ' + e.message); }
        }
        globalThis.__rankStart = async () => {
          if (!o.ranks) return;
          if (!ranker.genesis) {
            ranker.genesis = await engine.createGenesis({ type: 'genesis', kind: 'list', name: o.rankName, about: 'Videos ranked by votes and freshness, computed by this home node.', ranking: true }, rankerSigner);
            __n.saveRanker(JSON.stringify(ranker)); __h.log(`created ranking list "${o.rankName}"`);
          } else if (!engine.directory.has(ranker.genesis.hash)) { await engine.addToMempool(ranker.genesis); engine.openChain(ranker.genesis.hash); engine.maybeMine(); }
          setTimeout(publishRanking, 5000); setInterval(publishRanking, 300000);
        };
        globalThis.__loadMem = async (text) => { for (const m of JSON.parse(text)) await engine.acceptMembership(m); };
        const tracker = core.createTracker();
        const live = core.createLiveHub({
          ownerOf: (c) => engine.directory.get(c)?.genesis.author || engine.chains.get(c)?.owner || null,
          signerOf: (c, s) => { if (!engine.chains.has(c)) return null; const b = engine.derive(c)?.streams.find(x => x.payload.stream === s); return b ? (b.payload.relay || (b.author !== engine.derive(c).owner ? b.author : null)) : null; },
          isJoined: (k, c) => !!engine.memberships.get(k)?.joined?.includes(c),
          userOf: (k) => engine.nodes.get(k)?.grant?.user || null,
          ingest: { ticket: () => '' }
        });
        globalThis.__in = (role, id, text) => {
          let m; try { m = JSON.parse(text); } catch { return; }
          if (role === 'tracker') tracker.message(id, m);
          else if (role === 'live') Promise.resolve(live.message(id, m)).catch(() => {});
          else Promise.resolve(engine.handle(m, id)).catch(() => {});
        };
        globalThis.__open = (role, id) => {
          const out = (t) => __n.sock(id, t);
          if (role === 'tracker') tracker.attach(id, out);
          else if (role === 'live') live.attach(id, out);
          else { engine.peerJoined(id); if (role === 'node-in') send({ t: 'nodehi', node: signer.pub }, id); }
        };
        globalThis.__close = (role, id) => { if (role === 'tracker') tracker.detach(id); else if (role === 'live') live.detach(id); };
        globalThis.__replay = async (text) => {
          globalThis.__replaying = true; let n = 0; const byChain = new Map();
          for (const line of text.split('\n')) { if (!line) continue; try { const { c, b } = JSON.parse(line); (byChain.get(c) || byChain.set(c, []).get(c)).push(b); n++; } catch {} }
          await engine.insert(engine.MAIN_ID, byChain.get(engine.MAIN_ID) || [], null, false, true);
          for (const [c, blocks] of byChain) if (c !== engine.MAIN_ID && engine.openChain(c)) await engine.insert(c, blocks, null, false, true);
          globalThis.__replaying = false;
          return { n, chains: byChain.size, height: engine.main.best.height };
        };
        globalThis.__status = () => ({ version: core.VERSION, network: engine.MAIN_ID, node: signer.pub, height: engine.main.best.height, tip: engine.main.best.hash,
          registered: engine.directory.size, pending: engine.mempool.size, ranker: ranker.genesis?.hash || null, memberships: engine.memberships.size, chains: engine.chains.size, nodes: engine.nodes.size, mining: !!engine.miner,
          tracker: tracker.stats?.() || null, live: live.stats?.() || null, now: Date.now() });
        globalThis.__entries = (own) => {
          const fresh = [...engine.nodes.values()].filter(n => n.roles?.includes('entry') && Date.now() - n.at < 5 * 60000)
            .map(n => ({ node: n.node, url: (n.addrs || []).find(a => /^https:\/\//.test(a)) || null, at: n.at })).filter(x => x.url);
          if (own && !fresh.some(x => x.node === signer.pub)) fresh.unshift({ node: signer.pub, url: own, at: Date.now() });
          return { network: engine.MAIN_ID, at: Date.now(), entry: fresh.slice(0, 50) };
        };
        globalThis.__announce = (grant, roles, addrs, info) => engine.publishNodeInfo({ grant, roles, addrs, info }).then(() => true);
        setInterval(() => engine.gossip(), 60000);
        globalThis.__engine = engine;
        return { pub: signer.pub, main: engine.MAIN_ID };
      };
    """;
}
