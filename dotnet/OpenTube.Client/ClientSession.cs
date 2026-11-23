using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>
/// Where a client keeps its identity and its copy of the chains. In the browser it's the same IndexedDB the JavaScript
/// page used ("opentube-chain": kv, and blocks as {hash, chainId, block}), so a browser keeps its key and chains; on a
/// phone, files.
/// </summary>
public interface IClientStore
{
    Task<string?> Get(string key);
    Task Set(string key, string value);
    /// <summary>Every saved block: (chain id, block).</summary>
    Task<List<(string Chain, JsonObject Block)>> LoadBlocks();
    void SaveBlock(string chain, JsonObject block);
    /// <summary>Bytes kept on this device (your media library's files); null removes them.</summary>
    Task SetBytes(string key, byte[]? data, string mime = "application/octet-stream") => throw new NotSupportedException("this device can’t keep files");
    Task<byte[]?> GetBytes(string key) => Task.FromResult<byte[]?>(null);
}

/// <summary>
/// The network for an app (the website in WebAssembly, the phone app): the C# engine (the network's rules, chains, the
/// coin, gossip) with the person's identity, linked to OpenTube computers over WebSockets (the site's own node if it
/// has one, the entry nodes from seeds.json and /entries.json). The UI reads the engine and redraws on Changed.
/// </summary>
public sealed class ClientSession : IAsyncDisposable
{
    public Engine Engine { get; private set; } = null!;
    public Identity Me { get; private set; } = null!;
    public string Site { get; }
    public string MainId => Engine.MainId;
    public event Action? Changed;
    public Action<string>? Log;
    readonly IClientStore store;
    readonly ConcurrentDictionary<string, Link> links = new();
    readonly CancellationTokenSource stop = new();
    sealed record Link(string Url, System.Threading.Channels.Channel<string> Out) { public bool Open; }
    public int Peers => links.Values.Count(l => l.Open);
    public IEnumerable<string> LinkedUrls => links.Values.Where(l => l.Open).Select(l => l.Url);
    public bool Ready { get; private set; }
    /// <summary>Notifications (shared with the JavaScript page in the browser; system notifications on the phone).</summary>
    public Notifications Notes { get; private set; } = null!;
    /// <summary>Private messages.</summary>
    public Mail Mail { get; private set; } = null!;
    /// <summary>Posts and friends.</summary>
    public Social Social { get; private set; } = null!;
    public Media Media { get; private set; } = null!;
    /// <summary>Publishing videos, and help from your computers.</summary>
    public Publish Publish { get; private set; } = null!;
    /// <summary>The market, the exchange, organizations.</summary>
    public Trade Trade { get; private set; } = null!;
    /// <summary>Your keys: backup, import, binding, retiring and replacing them.</summary>
    public Keys Keys { get; private set; } = null!;
    /// <summary>The key for one of your keys (your identity or a channel's), or null.</summary>
    public Identity? KeyOf(string pub) => KeyFor(pub);
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public ClientSession(IClientStore store, string site) { this.store = store; Site = site.TrimEnd('/'); }

    public async Task Start()
    {
        var saved = await store.Get("identity");
        Me = saved != null ? JsonSerializer.Deserialize<Identity>(saved)! : Identity.New();
        if (saved == null) await store.Set("identity", JsonSerializer.Serialize(Me));
        // every channel/list has its own key pair (the JavaScript page's "channelKeys": pub -> {pub, priv, chain, name, exported})
        try { if (await store.Get("channelKeys") is { } ck && JsonNode.Parse(ck) is JsonObject o) channelKeys = o; } catch { }
        Engine = new Engine(Network.MainGenesis(), Me, (json, to) => Send(json, to), () => links.Where(l => l.Value.Open).Select(l => l.Key).ToList(), KeyFor) { MineMode = "own", RelayWatch = false, Log = m => Log?.Invoke(m) };
        // this browser's (or phone's) own copy of the chains: checked when they arrived, so loaded as trusted
        try
        {
            var rows = await store.LoadBlocks(); var byChain = rows.GroupBy(r => r.Chain).ToDictionary(g => g.Key, g => g.Select(r => (JsonNode?)r.Block).ToList());
            Engine.Insert(Engine.MainId, byChain.GetValueOrDefault(Engine.MainId) ?? [], null, persist: false, trusted: true);
            foreach (var (c, blocks) in byChain) if (c != Engine.MainId && Engine.OpenChain(c) != null) Engine.Insert(c, blocks, null, persist: false, trusted: true);
            Log?.Invoke($"loaded {rows.Count} saved blocks of {byChain.Count} chains");
        }
        catch (Exception e) { Log?.Invoke("couldn’t load saved chains: " + e.Message); }
        Engine.Persist = (c, b) => { try { store.SaveBlock(c, (JsonObject)b.DeepClone()); } catch { } Changed?.Invoke(); };
        Notes = new Notifications(this, store); await Notes.Load(); Notes.Start();
        Mail = new Mail(this, store); Mail.Start();
        Publish = new Publish(this); Trade = new Trade(this, store);
        try { if (await store.Get("retiredIdentities") is { } ri && JsonNode.Parse(ri) is JsonArray ra) foreach (var r in ra.OfType<JsonObject>()) if ((string?)r["pub"] is { } rp && (string?)r["priv"] is { } rk) Retired[rp] = new Identity(rp, rk); } catch { }
        Keys = new Keys(this, store); Keys.Start();
        Social = new Social(this, store); Media = new Media(store); _ = Task.Run(async () => { await Task.Delay(5000); try { await Social.Refresh(); } catch { } });
        Ready = true; Changed?.Invoke();
        online = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        _ = Task.Run(() => FindNodes(online));
    }

    // Power: in the background (a phone's screen off) the app keeps no connections; your node watches for you and
    // wakes the phone with a push when something's worth it. Back on screen, it reconnects and catches up.
    CancellationTokenSource online = new();
    public bool Paused { get; private set; }
    public void Pause()
    {
        if (Paused) return; Paused = true; online.Cancel();
        foreach (var l in links.Values) l.Out.Writer.TryComplete();
        links.Clear(); Changed?.Invoke(); Log?.Invoke("paused: no connections while in the background");
    }
    public void Resume()
    {
        if (!Paused) return; Paused = false;
        online = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        _ = Task.Run(() => FindNodes(online)); Log?.Invoke("resumed");
    }

    void Send(string json, string? to)
    {
        if (to != null) { if (links.TryGetValue(to, out var l) && l.Open) l.Out.Writer.TryWrite(json); return; }
        foreach (var l in links.Values) if (l.Open) l.Out.Writer.TryWrite(json);
    }

    /// <summary>The site's own node (when it has one) and the entry nodes it lists; checked again every minute.</summary>
    async Task FindNodes(CancellationTokenSource on)
    {
        while (!on.IsCancellationRequested)
        {
            var urls = new List<string> { Site.Replace("https://", "wss://").Replace("http://", "ws://") + "/ot" };
            foreach (var f in new[] { "/seeds.json", "/entries.json" })
                try
                {
                    var j = JsonNode.Parse(await http.GetStringAsync(Site + f));
                    foreach (var e in j?["entry"]?.AsArray() ?? []) if ((e is JsonValue v && v.TryGetValue<string>(out var u) ? u : (string?)e?["url"]) is { } eu && eu.StartsWith("https://")) urls.Add(eu.TrimEnd('/').Replace("https://", "wss://") + "/ot");
                }
                catch { }
            if (on.IsCancellationRequested) return;
            foreach (var u in urls.Distinct().Take(5)) if (!links.ContainsKey(u)) { var l = new Link(u, System.Threading.Channels.Channel.CreateUnbounded<string>()); links[u] = l; _ = Task.Run(() => Dial(l, on.Token)); }
            await Task.Delay(60000, on.Token).ContinueWith(_ => { });
        }
    }
    async Task Dial(Link l, CancellationToken on)
    {
        var tries = 0;
        while (!on.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri(l.Url), on);
                l.Open = true; tries = 0; Log?.Invoke("linked to " + l.Url);
                Engine.PeerJoined(l.Url); Changed?.Invoke();
                var writer = Task.Run(async () => { await foreach (var m in l.Out.Reader.ReadAllAsync(on)) { if (ws.State != WebSocketState.Open) break; await ws.SendAsync(Encoding.UTF8.GetBytes(m), WebSocketMessageType.Text, true, on); } });
                var buf = new byte[1 << 16]; using var ms = new MemoryStream();
                while (ws.State == WebSocketState.Open && !on.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buf, on);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buf, 0, r.Count); if (ms.Length > 16 << 20) break;
                    if (!r.EndOfMessage) continue;
                    JsonObject? m = null; try { m = JsonNode.Parse(ms.GetBuffer().AsSpan(0, (int)ms.Length)) as JsonObject; } catch { }
                    ms.SetLength(0);
                    if (m != null) { Engine.Handle(m, l.Url); Changed?.Invoke(); }
                }
            }
            catch { }
            if (l.Open) { l.Open = false; Changed?.Invoke(); }
            if (on.IsCancellationRequested) { try { ws.Abort(); } catch { } return; }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(300, 3 * Math.Pow(2, tries++))), on).ContinueWith(_ => { });
        }
    }

    // ---- your computers: the nodes you own, and those shared with you (their owner added you on Devices)
    /// <summary>Your nodes (yours first), newest check-in first: (node key, permissions, last seen).</summary>
    public List<(string Node, string Perms, double At)> MyNodes() => Engine.Read(e => e.Nodes.Values.Select(n =>
    {
        var user = (string?)n["grant"]?["user"];
        var perms = user == Me.pub ? "ohm" : (string?)n["info"]?["users"]?[Me.pub] ?? "";
        return ((string)n["node"]!, perms, Js.Num(n["at"]) ?? 0);
    }).Where(x => x.Item2.Length > 0).OrderByDescending(x => x.Item2.Contains('o')).ThenByDescending(x => x.Item3).ToList());
    /// <summary>A signed command to one of your computers (as the site's Devices page sends them), answered over the network.</summary>
    public async Task<JsonObject> NodeCtl(string node, string cmd, JsonObject? args = null)
    {
        var c = new JsonObject { ["kind"] = "nodectl", ["v"] = 1, ["node"] = node, ["user"] = Me.pub, ["nonce"] = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)), ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["cmd"] = cmd, ["args"] = args ?? new JsonObject() };
        c["sig"] = Me.Sign("opentube-nodectl:" + Blocks.Canon(c));
        var r = await Engine.Route(node, c) as JsonObject ?? throw new TimeoutException("that computer didn’t answer");
        if (!Js.IsTrue(r["ok"])) throw new InvalidOperationException((string?)r["error"] ?? "refused");
        return r["data"] as JsonObject ?? [];
    }
    /// <summary>A person's name: their channel's (by their identity or a key bound to it), else the start of their key.</summary>
    public string NameOf(string key) => Engine.Read(e =>
    {
        var who = e.Store.Ledger.Resolve(key);
        var page = e.Store.Chains.Values.FirstOrDefault(c => c != e.Store.Main && c.Owner != null && (c.Owner == who || e.Store.Ledger.Resolve(c.Owner) == who) && (string?)c.Genesis["payload"]?["kind"] == "channel");
        if (page != null) return (string?)Derived.Of(page).Profile["name"] ?? who[..8];
        var reg = e.Store.Directory.Values.Where(d => (string?)d.Genesis["author"] is { } a && (a == who || e.Store.Ledger.Resolve(a) == who) && (string?)d.Genesis["payload"]?["kind"] == "channel").Select(d => d.Genesis).FirstOrDefault();
        return (string?)reg?["payload"]?["name"] ?? who[..Math.Min(8, who.Length)];
    });
    /// <summary>All your keys (your identity and your channels' keys): public, so a node can tell what's yours.</summary>
    public List<string> MyKeys() { lock (channelKeys) return [Me.pub, .. channelKeys.Select(kv => kv.Key)]; }

    // ---- what pages show
    public JsonObject MainBest => Engine.Read(e => (JsonObject)e.Store.MainBest.DeepClone());
    /// <summary>Registered channels and lists (from the main chain): id, kind, name, about, avatar, owner.</summary>
    public List<ChainInfo> Directory() => Engine.Read(e => e.Store.Directory.Select(kv => ChainInfo.Of(kv.Key, kv.Value.Genesis, e.Store.Chains.TryGetValue(kv.Key, out var c) ? Derived.Of(c) : null)).ToList());
    /// <summary>A chain's current state (opening it and asking peers for its blocks if it isn't here yet).</summary>
    public Derived? Chain(string id)
    {
        var have = Engine.Read(e => e.Store.Chains.TryGetValue(id, out var c) && c.Blocks.Count > 0 ? Derived.Of(c) : null);
        if (have == null && Engine.OpenChain(id) is { } ch) Engine.RequestChain(ch);
        return have;
    }
    public JsonObject? Genesis(string id) => Engine.Read(e => e.Store.Directory.TryGetValue(id, out var d) ? (JsonObject)d.Genesis.DeepClone() : e.Store.Chains.TryGetValue(id, out var c) ? (JsonObject)c.Genesis.DeepClone() : null);
    /// <summary>Register a channel or list: its genesis block goes to the main chain's pool, and this device mines its own registration.</summary>
    readonly Dictionary<string, LiveWatch> watching = [];
    /// <summary>Watch a live stream (one connection per stream, however many pages show it).</summary>
    public LiveWatch Watch(string chain, JsonObject liveBlock)
    {
        var stream = (string)liveBlock["payload"]!["stream"]!;
        lock (watching)
        {
            if (watching.TryGetValue(stream, out var w) && !w.Ended) return w;
            return watching[stream] = LiveWatch.Start(this, chain, liveBlock);
        }
    }
    public LiveWatch? Watching(string stream) { lock (watching) return watching.GetValueOrDefault(stream); }
    /// <summary>Live streams now, newest first: (chain, channel name, live block).</summary>
    public List<(string Chain, string Name, JsonObject Block)> LiveNow() => Engine.Read(e => e.Store.Chains.Values.Where(c => c != e.Store.Main)
        .SelectMany(c => { var d = Derived.Of(c); return d.LiveNow.Select(b => (c.Id, (string?)d.Profile["name"] ?? c.Id[..8], (JsonObject)b.DeepClone())); })
        .OrderByDescending(x => Js.Num(x.Item3["ts"])).ToList());

    JsonObject channelKeys = new();
    /// <summary>Your older personal keys (retired: they still sign for what they owned until it's handed over).</summary>
    internal readonly Dictionary<string, Identity> Retired = [];
    Identity? KeyFor(string pub) { lock (channelKeys) return pub == Me.pub ? Me : channelKeys[pub] is JsonObject k ? new Identity((string)k["pub"]!, (string)k["priv"]!) : Retired.GetValueOrDefault(pub); }
    /// <summary>Your channel and list keys as kept (pub -> {pub, priv, chain, name, exported}).</summary>
    internal JsonObject ChannelKeys { get { lock (channelKeys) return (JsonObject)channelKeys.DeepClone(); } }
    internal async Task SaveChannelKeys(JsonObject keys) { lock (channelKeys) channelKeys = (JsonObject)keys.DeepClone(); await store.Set("channelKeys", keys.ToJsonString()); }
    /// <summary>Your key, or one of your channels' keys.</summary>
    public bool IsMine(string? pub) { if (pub == null) return false; lock (channelKeys) return pub == Me.pub || channelKeys.ContainsKey(pub) || Retired.ContainsKey(pub); }
    internal async Task AddChannelKey(Identity key, string name, string? chain)
    {
        string json;
        lock (channelKeys)
        {
            var k = channelKeys[key.pub] as JsonObject ?? new JsonObject { ["pub"] = key.pub, ["priv"] = key.priv };
            k["name"] = name; if (chain != null) k["chain"] = chain; channelKeys[key.pub] = k; json = channelKeys.ToJsonString();
        }
        await store.Set("channelKeys", json);
    }
    /// <summary>A new channel, list or group with a fresh key pair of its own (kept with the others in this browser).</summary>
    /// <summary>Register a chain with your own key (your friends page): its genesis goes to the main chain's pool.</summary>
    public Task<JsonObject> CreateGenesis(JsonObject payload) { var g = Engine.CreateGenesis(payload, Me); _ = Task.Run(Engine.MaybeMine); return Task.FromResult(g); }
    public async Task<JsonObject> CreateChannel(JsonObject payload)
    {
        var key = Identity.New(); var name = (string?)payload["name"] ?? "";
        await AddChannelKey(key, name, null);
        var g = Engine.CreateGenesis(payload, key);
        await AddChannelKey(key, name, (string)g["hash"]!);
        _ = Task.Run(Engine.MaybeMine); return g;
    }
    public double Balance(string key) => Engine.Read(e => e.Store.Ledger.Balance(key));

    public async ValueTask DisposeAsync() { stop.Cancel(); await Task.CompletedTask; }
}

/// <summary>A registered channel or list, for listing.</summary>
public sealed record ChainInfo(string Id, string Kind, string Name, string About, string? Avatar, string? Owner, int Videos)
{
    public static ChainInfo Of(string id, JsonObject genesis, Derived? d)
    {
        var p = d?.Profile ?? (JsonObject)genesis["payload"]!;
        return new ChainInfo(id, (string?)genesis["payload"]?["kind"] ?? "channel", (string?)p["name"] ?? id[..8], (string?)p["about"] ?? "", (string?)p["avatar"], d?.Owner ?? (string?)genesis["author"], d?.Videos.Count ?? 0);
    }
}
