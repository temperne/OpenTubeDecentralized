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
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public ClientSession(IClientStore store, string site) { this.store = store; Site = site.TrimEnd('/'); }

    public async Task Start()
    {
        var saved = await store.Get("identity");
        Me = saved != null ? JsonSerializer.Deserialize<Identity>(saved)! : Identity.New();
        if (saved == null) await store.Set("identity", JsonSerializer.Serialize(Me));
        // every channel/list has its own key pair (the JavaScript page's "channelKeys": pub -> {pub, priv, chain, name, exported})
        try { if (await store.Get("channelKeys") is { } ck && JsonNode.Parse(ck) is JsonObject o) channelKeys = o; } catch { }
        Engine = new Engine(Network.MainGenesis(), Me, (json, to) => Send(json, to), () => links.Where(l => l.Value.Open).Select(l => l.Key).ToList(), KeyFor) { MineMode = "own", Log = m => Log?.Invoke(m) };
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
        Ready = true; Changed?.Invoke();
        _ = Task.Run(FindNodes);
    }

    void Send(string json, string? to)
    {
        if (to != null) { if (links.TryGetValue(to, out var l) && l.Open) l.Out.Writer.TryWrite(json); return; }
        foreach (var l in links.Values) if (l.Open) l.Out.Writer.TryWrite(json);
    }

    /// <summary>The site's own node (when it has one) and the entry nodes it lists; checked again every minute.</summary>
    async Task FindNodes()
    {
        while (!stop.IsCancellationRequested)
        {
            var urls = new List<string> { Site.Replace("https://", "wss://").Replace("http://", "ws://") + "/ot" };
            foreach (var f in new[] { "/seeds.json", "/entries.json" })
                try
                {
                    var j = JsonNode.Parse(await http.GetStringAsync(Site + f));
                    foreach (var e in j?["entry"]?.AsArray() ?? []) if ((e is JsonValue v && v.TryGetValue<string>(out var u) ? u : (string?)e?["url"]) is { } eu && eu.StartsWith("https://")) urls.Add(eu.TrimEnd('/').Replace("https://", "wss://") + "/ot");
                }
                catch { }
            foreach (var u in urls.Distinct().Take(5)) if (!links.ContainsKey(u)) { var l = new Link(u, System.Threading.Channels.Channel.CreateUnbounded<string>()); links[u] = l; _ = Task.Run(() => Dial(l)); }
            await Task.Delay(60000, stop.Token).ContinueWith(_ => { });
        }
    }
    async Task Dial(Link l)
    {
        var tries = 0;
        while (!stop.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                await ws.ConnectAsync(new Uri(l.Url), stop.Token);
                l.Open = true; tries = 0; Log?.Invoke("linked to " + l.Url);
                Engine.PeerJoined(l.Url); Changed?.Invoke();
                var writer = Task.Run(async () => { await foreach (var m in l.Out.Reader.ReadAllAsync(stop.Token)) { if (ws.State != WebSocketState.Open) break; await ws.SendAsync(Encoding.UTF8.GetBytes(m), WebSocketMessageType.Text, true, stop.Token); } });
                var buf = new byte[1 << 16]; using var ms = new MemoryStream();
                while (ws.State == WebSocketState.Open && !stop.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buf, stop.Token);
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
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(300, 3 * Math.Pow(2, tries++))), stop.Token).ContinueWith(_ => { });
        }
    }

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
    JsonObject channelKeys = new();
    Identity? KeyFor(string pub) { lock (channelKeys) return pub == Me.pub ? Me : channelKeys[pub] is JsonObject k ? new Identity((string)k["pub"]!, (string)k["priv"]!) : null; }
    /// <summary>Your key, or one of your channels' keys.</summary>
    public bool IsMine(string? pub) { if (pub == null) return false; lock (channelKeys) return pub == Me.pub || channelKeys.ContainsKey(pub); }
    async Task AddChannelKey(Identity key, string name, string? chain)
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
