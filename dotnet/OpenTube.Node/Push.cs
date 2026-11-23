using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTube.Client;
using OpenTube.Core;

namespace OpenTube.Node;

/// <summary>
/// Notifications for your phones and browsers while they sleep: a device registers here (yours, or a computer shared
/// with you) with its push address and what it wants to hear about (its subscriptions and notification settings, the
/// same the app uses). This node, connected anyway, runs the same notification rules on everything that arrives and
/// sends each one as a Web Push (encrypted for that device, signed by this node): no polling, the device sleeps
/// until there's something. Android gets them through a UnifiedPush distributor (e.g. ntfy), browsers through their
/// push service. Kept in push.json.
/// </summary>
public sealed class NodePush
{
    readonly OpenTubeNode node; readonly string file;
    readonly byte[] vapidPub, vapidPriv;
    readonly object gate = new();
    readonly Dictionary<string, Device> devices = [];   // device id -> registration
    public string VapidKey => B64u.Encode(vapidPub);
    public int Count { get { lock (gate) return devices.Count; } }

    sealed class Device
    {
        public required string Id, User, Endpoint, Label; public required byte[] P256dh, Auth;
        public required List<string> Mine; public required JsonObject Prefs; public double At;
        public Notifications? Notes; public readonly Queue<double> Sent = new();
        public JsonObject Save() => new()
        {
            ["id"] = Id, ["user"] = User, ["endpoint"] = Endpoint, ["label"] = Label, ["p256dh"] = B64u.Encode(P256dh), ["auth"] = B64u.Encode(Auth),
            ["mine"] = new JsonArray(Mine.Select(m => (JsonNode?)m).ToArray()), ["prefs"] = Prefs.DeepClone(), ["at"] = At,
        };
    }
    /// <summary>A store in memory, holding the prefs the device sent (the notification rules read their settings from it).</summary>
    sealed class MemStore(JsonObject prefs) : IClientStore
    {
        public Task<string?> Get(string key) => Task.FromResult(key switch
        {
            "settings" => new JsonObject { ["notif"] = prefs["notif"]?.DeepClone() ?? new JsonObject() }.ToJsonString(),
            "subs" => prefs["subs"]?.ToJsonString(),
            _ => null,
        });
        public Task Set(string key, string value) => Task.CompletedTask;
        public Task<List<(string Chain, JsonObject Block)>> LoadBlocks() => Task.FromResult(new List<(string, JsonObject)>());
        public void SaveBlock(string chain, JsonObject block) { }
    }

    public NodePush(OpenTubeNode node)
    {
        this.node = node; file = Path.Combine(node.Options.DataDir, "push.json");
        JsonObject? saved = null; try { if (File.Exists(file)) saved = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; } catch { }
        if ((string?)saved?["vapid"]?["pub"] is { } vp && (string?)saved?["vapid"]?["priv"] is { } vk) { vapidPub = B64u.Decode(vp); vapidPriv = B64u.Decode(vk); }
        else (vapidPub, vapidPriv) = WebPush.NewKeys();
        foreach (var d in (saved?["devices"] as JsonArray ?? []).OfType<JsonObject>())
            try
            {
                var dev = new Device
                {
                    Id = (string)d["id"]!, User = (string)d["user"]!, Endpoint = (string)d["endpoint"]!, Label = (string?)d["label"] ?? "", P256dh = B64u.Decode((string)d["p256dh"]!), Auth = B64u.Decode((string)d["auth"]!),
                    Mine = (d["mine"] as JsonArray ?? []).Select(x => (string?)x).OfType<string>().ToList(), Prefs = d["prefs"] as JsonObject ?? [], At = Js.Num(d["at"]) ?? 0,
                };
                devices[dev.Id] = dev;
            }
            catch { }
        Save();
    }

    /// <summary>Start watching for every registered device (once the node's chains are loaded: nothing old is sent).</summary>
    public void Start()
    {
        lock (gate) foreach (var d in devices.Values) _ = Attach(d);
        _ = Task.Run(async () => { while (true) { await Task.Delay(TimeSpan.FromMinutes(10)); Warm(); } });
    }
    async Task Attach(Device d)
    {
        d.Notes?.Stop();
        // the device's settings, but only what happens from now on (a restart doesn't send old things again)
        var prefs = (JsonObject)d.Prefs.DeepClone(); if (prefs["notif"] is JsonObject n) n["since"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3600000 - 120000;
        var notes = new Notifications(node.Engine, new MemStore(prefs), d.User, k => k == d.User || d.Mine.Contains(k));
        await notes.Load(); notes.Import(prefs);
        notes.Arrived += x => _ = Send(d, x);
        notes.Start(); d.Notes = notes;
        Warm(d);
    }
    /// <summary>The device's subscriptions stay loaded here (asked for from peers when they aren't).</summary>
    void Warm(Device? only = null)
    {
        List<Device> ds; lock (gate) ds = only != null ? [only] : [.. devices.Values];
        foreach (var d in ds) foreach (var ch in d.Notes?.Subscribed ?? [])
            if (Js.IsHex(ch, 64) && node.Engine.OpenChain(ch) is { } c) node.Engine.RequestChain(c);
    }

    async Task Send(Device d, Notifications.Note n)
    {
        lock (d.Sent) { var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); while (d.Sent.Count > 0 && now - d.Sent.Peek() > 60000) d.Sent.Dequeue(); if (d.Sent.Count >= 30) return; d.Sent.Enqueue(now); }   // at most 30 a minute
        var msg = Encoding.UTF8.GetBytes(new JsonObject { ["v"] = 1, ["id"] = n.Id, ["type"] = n.Type, ["text"] = n.Text.Length > 600 ? n.Text[..600] : n.Text, ["href"] = n.Href, ["ts"] = n.Ts, ["node"] = node.NodeKey }.ToJsonString());
        try
        {
            var status = await WebPush.Send(d.Endpoint, d.P256dh, d.Auth, msg, vapidPub, vapidPriv, node.Options.Site ?? "https://opentube.invalid", urgency: n.Type is "live" or "message" ? "high" : "normal");
            if (status is 404 or 410) { node.Log($"[push] {d.Label}’s push address is gone: forgetting it"); Remove(d.Id); }
            else if (status >= 400) node.Log($"[push] {d.Label}: the push service said {status}");
        }
        catch (Exception e) { node.Log($"[push] {d.Label}: {e.Message}"); }
    }

    /// <summary>push.register: a device's push address, its keys, its subscriptions and settings, and its owner's keys.</summary>
    public async Task<JsonObject> Register(string user, JsonObject a)
    {
        string Need(string k, int max) => Js.Str(a[k]) is { Length: > 0 } v && v.Length <= max ? v : throw new ArgumentException("missing " + k);
        var id = Need("device", 80); var endpoint = Need("endpoint", 1000);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var eu) || !(eu.Scheme == "https" || eu.Scheme == "http" && eu.IsLoopback)) throw new ArgumentException("the push address must be https");
        var p256 = B64u.Decode(Need("p256dh", 200)); var auth = B64u.Decode(Need("auth", 60));
        if (p256.Length != 65 || auth.Length != 16) throw new ArgumentException("bad push keys");
        var prefs = a["prefs"] as JsonObject ?? []; if (prefs.ToJsonString().Length > 200_000) throw new ArgumentException("too many settings");
        var mine = (a["mine"] as JsonArray ?? []).Select(x => Js.Str(x)).Where(x => x is { Length: 43 }).Take(500).Cast<string>().ToList();
        var d = new Device { Id = user[..8] + ":" + id, User = user, Endpoint = endpoint, Label = (Js.Str(a["label"]) ?? "a device")[..Math.Min(60, (Js.Str(a["label"]) ?? "a device").Length)], P256dh = p256, Auth = auth, Mine = mine, Prefs = (JsonObject)prefs.DeepClone(), At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        lock (gate) { if (devices.TryGetValue(d.Id, out var old)) old.Notes?.Stop(); devices[d.Id] = d; if (devices.Count(x => x.Value.User == user) > 20) throw new ArgumentException("too many devices"); }
        await Attach(d); Save();
        node.Log($"[push] notifications for {d.Label} ({(d.Notes?.Subscribed.Count ?? 0)} subscriptions) go out from this computer");
        return new JsonObject { ["registered"] = d.Id, ["key"] = VapidKey, ["subscriptions"] = d.Notes?.Subscribed.Count ?? 0 };
    }
    public bool Remove(string id) { bool had; lock (gate) { if ((had = devices.Remove(id, out var d))) d!.Notes?.Stop(); } if (had) Save(); return had; }
    public bool Unregister(string user, string device) => Remove(user[..8] + ":" + device);
    void Save()
    {
        JsonObject o; lock (gate) o = new JsonObject { ["vapid"] = new JsonObject { ["pub"] = B64u.Encode(vapidPub), ["priv"] = B64u.Encode(vapidPriv) }, ["devices"] = new JsonArray(devices.Values.Select(d => (JsonNode)d.Save()).ToArray()) };
        try { File.WriteAllText(file, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    }
}
