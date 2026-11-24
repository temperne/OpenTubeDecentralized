using System.Text;
using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>
/// Your computers watch for you while this device sleeps: it gives one or two of them (yours first, else ones shared
/// with you) its push address and keys, your subscriptions and notification settings, and your public keys; they send
/// each notification as an encrypted Web Push. Re-registered when your subscriptions or settings change, and every few
/// hours (so a computer that restarted, or a new push address, is picked up).
/// </summary>
public sealed class PushLink
{
    readonly ClientSession s; readonly IClientStore store; readonly string label;
    public string? Endpoint { get; private set; }
    public byte[] Pub { get; private set; } = [];
    byte[] priv = [], auth = [];
    public string Device { get; private set; } = "";
    public List<string> Nodes { get; private set; } = [];
    public string Status { get; private set; } = "Not set up";
    public event Action? Changed;
    CancellationTokenSource? debounce;

    public PushLink(ClientSession s, IClientStore store, string label) { this.s = s; this.store = store; this.label = label; }

    /// <summary>This device's push keys (made once) and its push address, if it has one.</summary>
    public async Task Init()
    {
        var saved = await store.Get("pushDevice") is { } j ? JsonNode.Parse(j) as JsonObject : null;
        if (saved?["priv"] == null)
        {
            var (p, k) = WebPush.NewKeys();
            saved = new JsonObject { ["device"] = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)), ["pub"] = B64u.Encode(p), ["priv"] = B64u.Encode(k), ["auth"] = B64u.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)) };
            await store.Set("pushDevice", saved.ToJsonString());
        }
        Device = (string)saved["device"]!; Pub = B64u.Decode((string)saved["pub"]!); priv = B64u.Decode((string)saved["priv"]!); auth = B64u.Decode((string)saved["auth"]!);
        Endpoint = (string?)saved["endpoint"];
        s.Notes.Changed += () => { debounce?.Cancel(); debounce = new(); var t = debounce.Token; _ = Task.Delay(5000, t).ContinueWith(async x => { if (!x.IsCanceled) await Sync(); }); };
        _ = Task.Run(async () => { await Task.Delay(20000); while (true) { await Sync(); await Task.Delay(TimeSpan.FromHours(6)); } });
    }
    /// <summary>A new push address for this device (from its push service or UnifiedPush distributor).</summary>
    public async Task SetEndpoint(string? endpoint)
    {
        Endpoint = endpoint;
        var saved = await store.Get("pushDevice") is { } j ? JsonNode.Parse(j) as JsonObject ?? [] : [];
        saved["endpoint"] = endpoint; await store.Set("pushDevice", saved.ToJsonString());
        if (endpoint == null) { Say("No push address"); return; }
        await Sync();
    }
    void Say(string st) { Status = st; Changed?.Invoke(); }

    /// <summary>Register with up to two of your computers that are online and can push.</summary>
    public async Task Sync()
    {
        if (Endpoint == null || Pub.Length == 0) return;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nodes = s.MyNodes().Where(n => now - n.At < 10 * 60000 && s.Engine.Read(e => e.Nodes.TryGetValue(n.Node, out var x) && Js.IsTrue(x["info"]?["push"]))).Take(2).ToList();
        if (nodes.Count == 0) { Say("No computer of yours is online to watch for you. Run the OpenTuber command on a computer (Devices on the site), or ask someone to share theirs."); return; }
        var args = new JsonObject
        {
            ["device"] = Device, ["endpoint"] = Endpoint, ["p256dh"] = B64u.Encode(Pub), ["auth"] = B64u.Encode(auth), ["label"] = label,
            ["mine"] = new JsonArray(s.MyKeys().Select(k => (JsonNode?)k).ToArray()), ["prefs"] = s.Notes.Export(),
        };
        var ok = new List<string>(); string? err = null;
        foreach (var (node, _, _) in nodes)
            try { await s.NodeCtl(node, "push.register", (JsonObject)args.DeepClone()); ok.Add(node); }
            catch (Exception e) { err = e.Message; }
        Nodes = ok;
        Say(ok.Count > 0 ? $"On: {ok.Count} computer{(ok.Count == 1 ? "" : "s")} of yours watch{(ok.Count == 1 ? "es" : "")} for you while this device sleeps" : "Couldn’t reach your computers: " + err);
    }

    /// <summary>A push that arrived for this device, decrypted (null if it isn't one of ours).</summary>
    public static Notifications.Note? Read(byte[] body, byte[] pub, byte[] priv, byte[] auth)
    {
        try
        {
            var o = JsonNode.Parse(Encoding.UTF8.GetString(WebPush.Decrypt(body, pub, priv, auth))) as JsonObject;
            if (o == null || !Js.NumIs(o["v"], 1)) return null;
            return new Notifications.Note((string?)o["id"] ?? "", (string?)o["type"] ?? "post", (string?)o["text"] ?? "", (string?)o["href"] ?? "", null, Js.Num(o["ts"]) ?? 0, false);
        }
        catch { return null; }
    }
    public Notifications.Note? Read(byte[] body) => Read(body, Pub, priv, auth);
}
