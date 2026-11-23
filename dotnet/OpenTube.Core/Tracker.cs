using System.Text.Json.Nodes;
using static OpenTube.Core.Js;

namespace OpenTube.Core;

/// <summary>
/// createTracker: a WebTorrent tracker over WebSockets. Browsers announce the videos they hold with WebRTC offers; the
/// tracker passes offers and answers between peers in the same swarm (it never carries video), and answers scrapes.
/// </summary>
public sealed class Tracker
{
    sealed class Sock(Action<string> send) { public Action<string> Send = send; public Dictionary<string, string> Joined = []; }
    readonly Dictionary<string, Dictionary<string, string>> swarms = [];   // info_hash -> peer_id -> socket id
    readonly Dictionary<string, Sock> sockets = [];
    readonly object gate = new();
    void Out(string sid, JsonObject m) { if (sockets.TryGetValue(sid, out var s)) try { s.Send(m.ToJsonString()); } catch { } }

    void Leave(string sid, string ih)
    {
        if (!sockets.TryGetValue(sid, out var s) || !s.Joined.TryGetValue(ih, out var pid)) return;
        if (swarms.TryGetValue(ih, out var sw) && sw.TryGetValue(pid, out var at) && at == sid) sw.Remove(pid);
        if (sw != null && sw.Count == 0) swarms.Remove(ih);
        s.Joined.Remove(ih);
    }
    public void Attach(string sid, Action<string> send) { lock (gate) sockets[sid] = new Sock(send); }
    public void Detach(string sid) { lock (gate) { if (!sockets.TryGetValue(sid, out var s)) return; foreach (var ih in s.Joined.Keys.ToList()) Leave(sid, ih); sockets.Remove(sid); } }
    public JsonObject Stats() { lock (gate) return new JsonObject { ["swarms"] = swarms.Count, ["peers"] = sockets.Count }; }

    public void Message(string sid, JsonNode? node)
    {
        lock (gate)
        {
            if (!sockets.TryGetValue(sid, out var s) || node is not JsonObject m) return;
            var action = Str(Get(m, "action"));
            if (action == "scrape")
            {
                var files = new JsonObject();
                var list = Get(m, "info_hash") is JsonArray a ? a.ToList() : Has(m, "info_hash") && Truthy(Get(m, "info_hash")) ? [Get(m, "info_hash")] : [];
                foreach (var ih in list.Take(50)) if (Str(ih) is { } h) files[h] = new JsonObject { ["complete"] = swarms.TryGetValue(h, out var sw0) ? sw0.Count : 0, ["incomplete"] = 0, ["downloaded"] = 0 };
                Out(sid, new JsonObject { ["action"] = "scrape", ["files"] = files }); return;
            }
            if (action != "announce" || Str(Get(m, "info_hash")) is not { } ihash || Str(Get(m, "peer_id")) is not { } pid || ihash.Length > 40 || pid.Length > 40) return;
            if (Truthy(Get(m, "answer")))
            {
                if (swarms.TryGetValue(ihash, out var sw1) && Str(Get(m, "to_peer_id")) is { } tp && sw1.TryGetValue(tp, out var to))
                    Out(to, new JsonObject { ["action"] = "announce", ["answer"] = Get(m, "answer")!.DeepClone(), ["offer_id"] = Get(m, "offer_id")?.DeepClone(), ["peer_id"] = pid, ["info_hash"] = ihash });
                return;
            }
            if (Str(Get(m, "event")) == "stopped") { Leave(sid, ihash); Out(sid, new JsonObject { ["action"] = "announce", ["interval"] = 120, ["info_hash"] = ihash, ["complete"] = 0, ["incomplete"] = 0 }); return; }
            if (!s.Joined.ContainsKey(ihash) && s.Joined.Count >= 500) return;
            var sw = swarms.TryGetValue(ihash, out var x) ? x : swarms[ihash] = [];
            sw[pid] = sid; s.Joined[ihash] = pid;
            Out(sid, new JsonObject { ["action"] = "announce", ["interval"] = 120, ["info_hash"] = ihash, ["complete"] = sw.Count, ["incomplete"] = 0 });
            if (Get(m, "offers") is JsonArray offers && offers.Count > 0)
            {
                var others = sw.Where(kv => kv.Key != pid && sockets.ContainsKey(kv.Value)).ToList();
                for (int i = others.Count - 1; i > 0; i--) { int j = Random.Shared.Next(i + 1); (others[i], others[j]) = (others[j], others[i]); }
                var n = Math.Min(offers.Count, 20);
                for (int i = 0; i < Math.Min(n, others.Count); i++)
                {
                    var o = offers[i];
                    if (Truthy(o) && Truthy(Get(o, "offer"))) Out(others[i].Value, new JsonObject { ["action"] = "announce", ["offer"] = Get(o, "offer")!.DeepClone(), ["offer_id"] = Get(o, "offer_id")?.DeepClone(), ["peer_id"] = pid, ["info_hash"] = ihash });
                }
            }
        }
    }
}
