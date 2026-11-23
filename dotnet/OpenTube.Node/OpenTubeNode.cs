using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
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
    public Tracker Tracker { get; } = new();
    public LiveHub Hub { get; } = new();
    public string Version { get; private set; } = "";
    public string SiteDir { get; }
    public string Html { get; private set; } = "";
    public string Build { get; private set; } = "";
    public string? NodeKey { get; private set; }
    public string? NetworkId { get; private set; }
    /// <summary>The public https address (a Cloudflare tunnel, or one given), once there is one.</summary>
    public string? PublicUrl { get; set; }
    /// <summary>How this node is reachable (set by the web server): shown on the site and answering public.set.</summary>
    public Func<JsonObject>? ReachInfo { get; set; }
    public Func<string, Task>? SetPublic { get; set; }
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
    /// <summary>The network's engine, in C#: chains, peers' messages, mining, memberships, node statuses, rankings.</summary>
    public Engine Engine { get; private set; } = null!;
    public Identity? Identity { get; private set; }
    /// <summary>The person this computer belongs to (from the grant), if any.</summary>
    public string? OwnerUser { get; private set; }
    public ObsLive? Obs { get; private set; }
    public RtmpServer? Rtmp { get; private set; }
    public NodeControl? Control { get; private set; }
    Identity? rankerKey; JsonObject? rankerGenesis;

    /// <summary>Load this node's key and its saved blocks, then connect to peers.</summary>
    public async Task StartAsync()
    {
        Directory.CreateDirectory(Options.DataDir);
        Html = File.ReadAllText(Path.Combine(SiteDir, "index.html"));
        Build = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Html)))[..12];
        Version = Network.VersionOf(Html);
        var id = Identity.LoadOrCreate(Path.Combine(Options.DataDir, "key.json"));
        NodeKey = id.pub; Identity = id;
        if (Options.Grant != null) try { OwnerUser = (string?)JsonNode.Parse(Encoding.UTF8.GetString(B64u.Decode(Options.Grant)))?["user"]; } catch { }
        var mainGenesis = Network.MainGenesis();

        // this node's ranking list key ("Trending on …"), in the JavaScript node's ranker.json format
        var rankerFile = Path.Combine(Options.DataDir, "ranker.json");
        var ranker = File.Exists(rankerFile) ? JsonNode.Parse(File.ReadAllText(rankerFile))!.AsObject() : new JsonObject { ["key"] = JsonSerializer.SerializeToNode(Identity.New()) };
        if (!File.Exists(rankerFile)) File.WriteAllText(rankerFile, ranker.ToJsonString());
        rankerKey = ranker["key"].Deserialize<Identity>(); rankerGenesis = ranker["genesis"] as JsonObject;

        Engine = new Engine(mainGenesis, id, (json, to) => Send(json, to ?? ""), () => peers.Values.Where(p => p.Role is "node-in" or "node-out").Select(p => p.Id).ToList(),
            k => k == rankerKey?.pub ? rankerKey : null) { MineMode = Options.Mine, Log = Log };
        Engine.Persist = (c, b) => { lock (this) blockLog?.WriteLine(new JsonObject { ["c"] = c, ["b"] = b.DeepClone() }.ToJsonString()); };
        Engine.OnMembership = _ => SaveSoon(Path.Combine(Options.DataDir, "memberships.json"), Engine.Read(e => new JsonArray(e.Memberships.Values.Select(m => (JsonNode?)m.DeepClone()).ToArray()).ToJsonString()));
        NetworkId = Engine.MainId;

        // this computer's own copy of the chains: nothing is downloaded again (trusted: verified when they arrived)
        var file = Path.Combine(Options.DataDir, "blocks.jsonl");
        if (File.Exists(file))
        {
            var t0 = DateTime.UtcNow; int n = 0; var byChain = new Dictionary<string, List<JsonNode?>>();
            foreach (var line in File.ReadLines(file))
            {
                if (line.Length == 0) continue;
                try { var o = JsonNode.Parse(line)!; var c = (string)o["c"]!; (byChain.TryGetValue(c, out var l) ? l : byChain[c] = []).Add(o["b"]!.DeepClone()); n++; } catch { }
            }
            Engine.Insert(Engine.MainId, byChain.GetValueOrDefault(Engine.MainId) ?? [], null, persist: false, trusted: true);
            foreach (var (c, blocks) in byChain) if (c != Engine.MainId && Engine.OpenChain(c) != null) Engine.Insert(c, blocks, null, persist: false, trusted: true);
            Log($"loaded {n} saved blocks of {byChain.Count} chains in {(DateTime.UtcNow - t0).TotalSeconds:0.0} s (main at #{Engine.Read(e => e.Store.MainBest["height"])}); only newer ones come from peers");
        }
        var memFile = Path.Combine(Options.DataDir, "memberships.json");
        if (File.Exists(memFile)) try { foreach (var m in JsonNode.Parse(File.ReadAllText(memFile))!.AsArray()) Engine.AcceptMembership(m); } catch { }
        blockLog = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };

        // the ranking list: registered once (mined like any registration), republished every 5 minutes
        if (Options.Ranks && rankerKey != null)
        {
            var name = Options.RankName ?? $"Trending on {Environment.MachineName}"; name = name[..Math.Min(60, name.Length)];
            if (rankerGenesis == null)
            {
                rankerGenesis = Engine.CreateGenesis(new JsonObject { ["type"] = "genesis", ["kind"] = "list", ["name"] = name, ["about"] = "Videos ranked by votes and freshness, computed by this home node.", ["ranking"] = true }, rankerKey);
                ranker["genesis"] = rankerGenesis.DeepClone(); File.WriteAllText(rankerFile, ranker.ToJsonString()); Log($"created ranking list \"{name}\"");
            }
            else if (!Engine.Read(e => e.Store.Directory.ContainsKey((string)rankerGenesis["hash"]!))) { Engine.Read(e => e.Store.AddToMempool(rankerGenesis.DeepClone())); Engine.OpenChain((string)rankerGenesis["hash"]!); _ = Task.Run(Engine.MaybeMine); }
            _ = Task.Run(RankLoop);
        }

        // the live hub needs to know channels' owners, linked computers and members (from the engine)
        Hub.OwnerOf = c => Engine.Read(x => x.Store.Directory.TryGetValue(c, out var d) ? (string?)d.Genesis["author"] : x.Store.Chains.TryGetValue(c, out var ch) ? ch.Owner : null);
        Hub.SignerOf = (c, st) => Engine.Read(x =>
        {
            if (!x.Store.Chains.TryGetValue(c, out var ch)) return null;
            var d = Derived.Of(ch); var b = d.Streams.FirstOrDefault(s => (string?)s["payload"]!["stream"] == st);
            return b == null ? null : (string?)b["payload"]!["relay"] ?? ((string?)b["author"] != d.Owner ? (string?)b["author"] : null);
        });
        Hub.IsJoined = (k, c) => Engine.Read(x => x.Memberships.TryGetValue(k, out var m) && m["joined"] is JsonArray j && j.Any(v => (string?)v == c));
        Hub.UserOf = k => Engine.Read(x => x.Nodes.TryGetValue(k, out var nd) ? (string?)nd["grant"]?["user"] : null);
        Log($"OpenTube {Version} on .NET {Environment.Version} (all C#) · build {Build} · network {NetworkId[..16]}… · node {NodeKey[..8]}… · website from {SiteDir}");

        // OBS: each person who may use this computer has a stream key (the owner's is made now and shown here)
        if (Options.ObsPort > 0)
        {
            Obs = new ObsLive(this);
            if (OwnerUser != null) { var p = Obs.ProfileOf(OwnerUser); Log($"[obs] stream key for the owner: {(string?)p["key"]}"); }
            Obs.Save();
            Rtmp = new RtmpServer(Options.ObsPort, Obs.OnPublish, Log); Rtmp.Start();
        }

        // control from the site (Devices): signed commands routed to this node, answered with signed replies
        if (Options.ObsPort <= 0) Obs = new ObsLive(this);   // (sharing settings live in obs.json too)
        Control = new NodeControl(this);
        Engine.OnRoute = (body, replyTo) => _ = Task.Run(async () =>
        {
            try { var r = await Control.OnRoute(JsonNode.Parse(body)); if (r != null) Engine.SendReply(replyTo, r); }
            catch (Exception e) { Log("[control] " + e.Message); }
        });

        foreach (var p in Options.Peers) _ = DialAsync(p);
        _ = Task.Run(FindEntryNodes);
        if (Options.Mesh) _ = Task.Run(JoinMesh);
        Relays = new LiveRelays(this); _ = Task.Run(() => Relays.Run(stop.Token));
        if (Options.Host && OwnerUser != null)
            try { Hosting = new Host(this, Options.VideoDir, Options.CapGB); _ = Task.Run(Hosting.Run); }
            catch (Exception e) { Log("video hosting unavailable: " + e.Message); }
        Coord = new Coordination(this); _ = Task.Run(() => Coord.Run(stop.Token));
        Update = new Updater(this); _ = Task.Run(() => Update.Run(stop.Token));
        Earn = new Earnings(this) { PendingTabs = Hosting != null ? Hosting.Tabs.Pending : null }; _ = Task.Run(() => Earn.Run(stop.Token));
        if (Hosting != null) _ = Task.Run(async () =>
        {
            var every = int.TryParse(Environment.GetEnvironmentVariable("PROOF_EVERY_MS"), out var ms) ? ms : 2 * 60000;
            while (!stop.IsCancellationRequested) { await Task.Delay(every, stop.Token).ContinueWith(_ => { }); try { await Earn.ChallengeRound(); } catch (Exception e) { Log("[proof] " + e.Message); } }
        });
        _ = Task.Run(StatusLoop);
        _ = Task.Run(async () => { while (!stop.IsCancellationRequested) { await Task.Delay(60000, stop.Token).ContinueWith(_ => { }); Engine.Gossip(); } });
        _ = Task.Run(Engine.MaybeMine);
        if (Options.Grant != null) _ = Task.Run(AnnounceLoop);
    }

    async Task RankLoop()
    {
        await Task.Delay(5000, stop.Token).ContinueWith(_ => { });
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var id = (string)rankerGenesis!["hash"]!;
                if (Engine.Read(e => e.Store.Directory.ContainsKey(id))) Engine.PublishRanking(id, Engine.Rank());
                else _ = Task.Run(Engine.MaybeMine);
            }
            catch (Exception e) { Log("ranking failed: " + e.Message); }
            await Task.Delay(300000, stop.Token).ContinueWith(_ => { });
        }
    }

    /// <summary>
    /// Ways into the network without naming a peer, asked every minute: the entry nodes the network itself announces
    /// (signed node statuses with an https address, fresh in the last 10 minutes), the site's /entries.json (the Cloudflare
    /// site this node came from: --site, remembered for later starts), and seeds.json. Keeps up to 4 linked; stops dialing
    /// ones nobody lists any more. While none answers, the site's own node is the way in (dropped once an entry answers).
    /// </summary>
    async Task FindEntryNodes()
    {
        var siteFile = Path.Combine(Options.DataDir, "site.txt");
        if (Options.Site != null) { try { File.WriteAllText(siteFile, Options.Site); } catch { } }
        else if (File.Exists(siteFile)) { Options.Site = File.ReadAllText(siteFile).Trim().TrimEnd('/'); Log($"site: {Options.Site} (remembered)"); }
        if (Options.Site == null) Log("no --site given and none remembered: entry nodes come only from seeds.json and the network");
        var dials = new Dictionary<string, CancellationTokenSource>();
        List<string> fromSite = []; DateTime siteAsked = DateTime.MinValue; string? siteNode = null; CancellationTokenSource? siteDial = null;
        bool Linked(string url) => peers.ContainsKey("node:" + url);
        while (!stop.IsCancellationRequested)
        {
            var found = new List<string>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var n in Engine.Read(e => e.Nodes.Values.Select(x => (JsonObject)x.DeepClone()).ToList()))   // entry nodes the network announces
                if ((string?)n["node"] != NodeKey && n["roles"] is JsonArray r && r.Any(x => (string?)x == "entry") && now - (Js.Num(n["at"]) ?? 0) < 10 * 60000)
                    if ((n["addrs"] as JsonArray ?? []).Select(x => (string?)x).FirstOrDefault(x => x?.StartsWith("https://", StringComparison.Ordinal) == true) is { } u) found.Add(u);
            bool anyLinked = dials.Keys.Any(Linked);
            if (Options.Site != null && (DateTime.UtcNow - siteAsked > TimeSpan.FromMinutes(anyLinked ? 10 : 1)))
            {
                siteAsked = DateTime.UtcNow;
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    var j = JsonNode.Parse(await http.GetStringAsync(Options.Site + "/entries.json"));
                    fromSite = (j?["entry"]?.AsArray() ?? []).Select(e => (string?)e?["url"]).Where(u => u != null).Select(u => u!).ToList();
                }
                catch (Exception e) { Log($"couldn't ask {Options.Site} for entry nodes: {e.Message}"); }
            }
            found.AddRange(fromSite);
            try
            {
                var seeds = Path.Combine(SiteDir, "seeds.json");
                if (File.Exists(seeds)) foreach (var e in JsonNode.Parse(File.ReadAllText(seeds))?["entry"]?.AsArray() ?? []) if ((e is JsonValue v && v.TryGetValue<string>(out var u) ? u : (string?)e?["url"]) is { } su) found.Add(su);
            }
            catch { }
            var own = PublicUrl?.TrimEnd('/');
            var want = found.Select(x => x.TrimEnd('/')).Where(x => x.StartsWith("https://", StringComparison.Ordinal) && x != own).Distinct().Select(NodeOptions.NormalizePeer).ToList();
            foreach (var (url, cts) in dials.ToList())   // nobody lists it any more and it isn't answering: let it go
                if (!want.Contains(url) && !Linked(url)) { cts.Cancel(); dials.Remove(url); Log($"let go of {url} (no longer listed)"); }
            var linkedCount = dials.Keys.Count(Linked);
            foreach (var url in want)
            {
                if (dials.ContainsKey(url) || Options.Peers.Contains(url)) continue;
                if (dials.Count >= 4 + dials.Keys.Count(k => !Linked(k)) && linkedCount >= 4) break;
                var cts = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); dials[url] = cts;
                Log($"connecting to entry node {url.Replace("wss://", "https://").Replace("/ot", "")}"); _ = DialAsync(url, cts.Token);
            }
            // the site's own node: only while no entry node answers
            anyLinked = dials.Keys.Any(Linked);
            siteNode ??= Options.Site != null ? NodeOptions.NormalizePeer(Options.Site) : null;
            if (siteNode != null)
            {
                if (!anyLinked && siteDial == null) { siteDial = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); Log($"no entry node answers yet: connecting to the site's node {siteNode}"); _ = DialAsync(siteNode, siteDial.Token); }
                else if (anyLinked && siteDial != null && !Options.Peers.Contains(siteNode)) { siteDial.Cancel(); siteDial = null; Log($"let go of {siteNode} (an entry node answers)"); }
            }
            await Task.Delay(TimeSpan.FromSeconds(anyLinked ? 60 : 15), stop.Token).ContinueWith(_ => { });
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

    async Task DialAsync(string url, CancellationToken? until = null)
    {
        var ct = until ?? stop.Token; var tries = 0;
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri(url), ct);
                tries = 0; Log($"linked to {url}");
                using var reg = ct.Register(() => { try { ws.Abort(); } catch { } });
                await RunSocket(ws, "node:" + url, "node-out", url);
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { if (tries == 0) Log($"can't reach {url}: {e.Message}"); }
            catch when (ct.IsCancellationRequested) { return; }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(600, 10 * Math.Pow(2, tries++))), ct).ContinueWith(_ => { });
        }
    }

    async Task RunSocket(WebSocket ws, string id, string role, string label)
    {
        var peer = new Peer(id, role, label, Channel.CreateBounded<string>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest }));
        peers[id] = peer;
        if (role == "tracker") Tracker.Attach(id, text => Send(text, id));
        else if (role == "live") Hub.Attach(id, text => Send(text, id));
        else { Engine.PeerJoined(id); if (role == "node-in") Send(new JsonObject { ["t"] = "nodehi", ["node"] = NodeKey }.ToJsonString(), id); }
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
                JsonObject? m = null; try { m = JsonNode.Parse(text) as JsonObject; } catch { }
                if (m == null) continue;
                if (role == "tracker") Tracker.Message(id, m); else if (role == "live") Hub.Message(id, m); else Engine.Handle(m, id);
            }
        }
        catch { }
        finally
        {
            peers.TryRemove(id, out _); peer.Out.Writer.TryComplete();
            if (role == "tracker") Tracker.Detach(id); else if (role == "live") Hub.Detach(id);
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
                var s = Engine.Read(e => new JsonObject
                {
                    ["version"] = Version, ["network"] = e.MainId, ["node"] = NodeKey, ["height"] = e.Store.MainBest["height"]?.DeepClone(), ["tip"] = e.Store.MainBest["hash"]?.DeepClone(),
                    ["registered"] = e.Store.Directory.Count, ["pending"] = e.Store.Mempool.Count, ["chains"] = e.Store.Chains.Count, ["nodes"] = e.Nodes.Count, ["mining"] = e.Mining,
                    ["ranker"] = (string?)rankerGenesis?["hash"], ["memberships"] = e.Memberships.Count, ["rejected"] = new JsonObject(e.Store.Rejected.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
                });
                if (Rtmp != null) s["obs"] = new JsonObject { ["linked"] = new JsonArray((Obs?.LinkedChannels() ?? []).Select(c => (JsonNode?)c).ToArray()), ["port"] = Rtmp.Port, ["live"] = Rtmp.Live > 0, ["bytes"] = Rtmp.Bytes, ["stage"] = Rtmp.LastStage, ["keyInterval"] = Rtmp.KeyInterval, ["error"] = Rtmp.Error };
                s["tracker"] = Tracker.Stats(); s["live"] = Hub.Stats();
                var own = Hub.Live(); foreach (var x in own) if (x is JsonObject xo) xo["hub"] = PublicUrl != null ? NodeKey : null;
                foreach (var x in Coord?.PadStreams() ?? []) if (!own.Any(y => (string?)y?["stream"] == (string?)x["stream"])) own.Add(x);
                s["liveStreams"] = new JsonArray(own.Take(100).Select(x => x?.DeepClone()).ToArray());
                if (Coord != null) { s["meet"] = new JsonArray(Coord.MeetPlaces().Select(x => (JsonNode?)x).ToArray()); s["meetOffline"] = new JsonArray(Coord.MeetOffline().Select(x => (JsonNode?)x).ToArray()); }
                s["peers"] = peers.Values.Count(p => p.Role is "node-in" or "node-out");
                s["trackerClients"] = peers.Values.Count(p => p.Role == "tracker");
                s["liveClients"] = peers.Values.Count(p => p.Role == "live");
                s["build"] = Build; s["public"] = PublicUrl; s["impl"] = $".NET {Environment.Version}"; s["uptime"] = (long)(DateTime.UtcNow - Started).TotalSeconds; s["now"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                status = s;
            }
            catch (Exception e) { Log("[status] " + e.Message); }
            await Task.Delay(2000, stop.Token).ContinueWith(_ => { });
        }
    }

    /// <summary>The entry nodes seen in the last 5 minutes (signed statuses), this one first when it has a public address.</summary>
    public Task<string?> EntriesJsonAsync() => Task.FromResult<string?>(Engine.Read(e =>
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fresh = e.Nodes.Values.Where(n => n["roles"] is JsonArray r && r.Any(x => (string?)x == "entry") && now - (double)n["at"]! < 5 * 60000)
            .Select(n => new JsonObject { ["node"] = n["node"]?.DeepClone(), ["url"] = (n["addrs"] as JsonArray)?.Select(a => (string?)a).FirstOrDefault(a => a != null && a.StartsWith("https://")), ["at"] = n["at"]?.DeepClone() })
            .Where(x => x["url"] != null).ToList();
        if (PublicUrl != null && !fresh.Any(x => (string?)x["node"] == NodeKey)) fresh.Insert(0, new JsonObject { ["node"] = NodeKey, ["url"] = PublicUrl, ["at"] = now });
        return new JsonObject { ["network"] = e.MainId, ["at"] = now, ["entry"] = new JsonArray(fresh.Take(50).Select(x => (JsonNode?)x).ToArray()) }.ToJsonString();
    }));

    // ---- "your node": it announces itself to its owner's Devices page every 30 s (needs the owner's grant)
    async Task AnnounceLoop()
    {
        string grantJson;
        try { grantJson = Encoding.UTF8.GetString(B64u.Decode(Options.Grant!)); JsonNode.Parse(grantJson); }
        catch { Log("the grant isn't valid; not announcing this node"); return; }
        await Task.Delay(4000);
        while (!stop.IsCancellationRequested)
        {
            var roles = new List<string> { "node" }; if (Rtmp != null) roles.Add("obs"); if (Hosting != null) roles.Add("host"); if (PublicUrl != null) roles.Add("entry");
            var addrs = new List<string>(); if (PublicUrl != null) addrs.Add(PublicUrl);
            addrs.AddRange(System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a.Address))
                .Select(a => $"http://{a.Address}:{Options.Port}"));
            addrs.Add($"http://localhost:{Options.Port}");
            var info = new JsonObject
            {
                ["version"] = new JsonObject { ["name"] = Version, ["build"] = Build, ["impl"] = "dotnet" }, ["height"] = Engine.Read(e => e.Store.MainBest["height"]?.DeepClone()), ["chains"] = Engine.Read(e => e.Store.Chains.Count),
                ["name"] = (Options.Label ?? Environment.MachineName)[..Math.Min(60, (Options.Label ?? Environment.MachineName).Length)],
                ["os"] = $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}",
                ["node"] = $".NET {Environment.Version}", ["peers"] = peers.Values.Count(p => p.Role is "node-in" or "node-out"), ["public"] = PublicUrl,
                ["reach"] = ReachInfo?.Invoke(), ["tunnel"] = ReachInfo?.Invoke() is { } ri ? ((string?)ri["tunnelUrl"] != null ? "on" : (string?)ri["tunnelError"]) : null,
                // who this computer is shared with: their permissions as letters (o: OBS, h: hosting, m: managing; g: a guest)
                ["users"] = new JsonObject(((Obs?.Store["users"] as JsonObject) ?? []).Where(kv => !(Js.Num(kv.Value?["until"]) < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())).Take(20)
                    .Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, string.Concat((kv.Value?["can"] as JsonArray ?? []).Select(x => ((string?)x ?? " ")[0])) + (kv.Value?["guestOf"] != null ? "g" : "")))),
                ["obs"] = Rtmp == null ? null : new JsonObject { ["port"] = Rtmp.Port, ["listening"] = Rtmp.Error == null, ["live"] = Rtmp.Live > 0, ["error"] = Rtmp.Error },
                ["host"] = Hosting?.Status() is { } hs ? new JsonObject { ["hosting"] = hs["hosting"]?.DeepClone(), ["hostedBytes"] = hs["hostedBytes"]?.DeepClone(), ["channels"] = hs["channels"]?.DeepClone(), ["videoDir"] = hs["videoDir"]?.DeepClone(), ["downloading"] = hs["downloading"]?.DeepClone() } : null,
            };
            try { Engine.PublishNodeInfo(new JsonObject { ["grant"] = JsonNode.Parse(grantJson), ["roles"] = JsonSerializer.SerializeToNode(roles.Take(8).ToList()), ["addrs"] = JsonSerializer.SerializeToNode(addrs.Take(8).ToList()), ["info"] = info }); }
            catch (Exception e) { Log("announce failed: " + e.Message); }
            var wake = new TaskCompletionSource(); announceWake = wake;
            await Task.WhenAny(Task.Delay(30000, stop.Token), wake.Task).ContinueWith(_ => { });
        }
    }
    TaskCompletionSource? announceWake;
    /// <summary>Tell the owner's Devices page now (after a change), not at the next half-minute.</summary>
    public void AnnounceSoon() => _ = Task.Delay(1500).ContinueWith(_ => announceWake?.TrySetResult());

    // ---- the peer mesh: the network's WebRTC room, as every browser joins it (see Mesh)
    public Mesh? MeshRoom { get; private set; }
    public LiveRelays? Relays { get; private set; }
    public Host? Hosting { get; private set; }
    public Earnings? Earn { get; private set; }
    public Updater? Update { get; private set; }
    public Coordination? Coord { get; private set; }
    async Task JoinMesh()
    {
        await Task.Delay(1500, stop.Token).ContinueWith(_ => { });
        var main = Network.MainId;
        MeshRoom = new Mesh("opentube-" + main[..16], "opentube/" + main[..32], Log,
            onJoin: (pid, label, send) =>
            {
                var id = "rtc:" + pid;
                var peer = new Peer(id, "node-in", label, Channel.CreateBounded<string>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest }));
                peers[id] = peer;
                _ = Task.Run(async () => { await foreach (var text in peer.Out.Reader.ReadAllAsync()) { try { await send(text); } catch { } } });
                Log($"+ peer {pid[..8]} over WebRTC ({peers.Count(p => p.Value.Role is "node-in" or "node-out")} connected)");
                Engine.PeerJoined(id); Send(new JsonObject { ["t"] = "nodehi", ["node"] = NodeKey }.ToJsonString(), id);
            },
            onMessage: (pid, text) => { try { if (JsonNode.Parse(text) is JsonObject m) Engine.Handle(m, "rtc:" + pid); } catch { } },
            onLeave: pid => { if (peers.TryRemove("rtc:" + pid, out var p)) { p.Out.Writer.TryComplete(); Log($"- peer {pid[..8]} ({peers.Count(x => x.Value.Role is "node-in" or "node-out")} connected)"); } });
        var logged = false;
        while (!stop.IsCancellationRequested)
        {
            var urls = MeshTrackerUrls();
            MeshRoom.SetTrackers(urls);
            if (!logged) { logged = true; Log($"peer mesh: joining the network's room via {urls.Count} tracker(s)"); }
            await Task.Delay(60000, stop.Token).ContinueWith(_ => { });
        }
    }
    /// <summary>The trackers the room is found through: entry nodes' own (fresh), the network's public ones, the site's, and this node's own.</summary>
    public List<string> MeshTrackerUrls()
    {
        if (Options.MeshTrackers != null) return Options.MeshTrackers;
        var urls = Coord?.MeetPlaces().Where(u => !u.StartsWith("ws://127.0.0.1")).ToList() ?? [];   // entry nodes' trackers that answer
        foreach (var t in Network.MainGenesis()["payload"]?["trackers"]?.AsArray() ?? []) if ((string?)t is { } tu) urls.Add(tu);
        if (Options.Site != null) urls.Add(Options.Site.TrimEnd('/').Replace("https://", "wss://").Replace("http://", "ws://") + "/tracker");
        urls.Add($"ws://127.0.0.1:{Options.Port}/tracker");
        return urls.Distinct().ToList();
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); Rtmp?.Stop(); if (MeshRoom != null) await MeshRoom.DisposeAsync(); if (Hosting != null) await Hosting.DisposeAsync();
        foreach (var p in peers.Values) p.Out.Writer.TryComplete();
        await Task.Delay(100);
        lock (this) { blockLog?.Dispose(); blockLog = null; }
    }

    // The node's side of the rules, in the same script language as the rules themselves (it mirrors server.mjs).
}
