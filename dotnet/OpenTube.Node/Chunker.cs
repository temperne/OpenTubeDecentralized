using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Node;

/// <summary>
/// Live chunks (core: liveChunkPreimage): about 10 consecutive segments of a stream become one small torrent this node
/// seeds (with hosting on), announced with the list of segments inside, so viewers who missed segments (members
/// saving the stream) fetch them over BitTorrent from everyone who has the chunk and check each segment's signature.
/// </summary>
public sealed class Chunker(OpenTubeNode node, string stream, Action<JsonObject> announce)
{
    List<JsonObject> cur = []; int announced; Task busy = Task.CompletedTask; readonly object gate = new();
    public void Seg(JsonObject g)
    {
        if ((string?)g["d"] == null) return;
        lock (gate)
        {
            if (cur.Count > 0 && Js.Num(g["n"]) != Js.Num(cur[^1]["n"]) + 1) Flush();
            cur.Add(g); if (cur.Count >= 10) Flush();
        }
    }
    public Task End() { lock (gate) { Flush(); return busy; } }
    void Flush()
    {
        var segs = cur; cur = [];
        if (segs.Count == 0 || node.Hosting is not { } host) return;
        busy = busy.ContinueWith(_ =>
        {
            try
            {
                var buf = segs.SelectMany(g => Convert.FromBase64String((string)g["d"]!)).ToArray();
                long from = (long)Js.Num(segs[0]["n"])!, to = (long)Js.Num(segs[^1]["n"])!, o = (long)Js.Num(segs[0]["o"])!;
                var ih = host.SeedChunk(buf, $"live-{stream[..8]}-{from}-{to}.bin");
                var c = new JsonObject
                {
                    ["stream"] = stream, ["from"] = from, ["to"] = to, ["o"] = o, ["len"] = buf.Length, ["ih"] = ih, ["by"] = node.NodeKey,
                    ["segs"] = new JsonArray(segs.Select(g => { var x = new JsonObject { ["n"] = g["n"]!.DeepClone(), ["o"] = g["o"]!.DeepClone(), ["h"] = g["h"]!.DeepClone(), ["sig"] = g["sig"]!.DeepClone() }; if (g["k"] != null) x["k"] = 1; return (JsonNode?)x; }).ToArray()),
                };
                c["sig"] = node.Identity!.Sign($"opentube-live-chunk:{stream}:{from}:{to}:{o}:{buf.Length}:{ih}");
                announce(c);
                if (announced++ == 0) node.Log($"[live] sharing the stream over BitTorrent too, in chunks of ~10 segments (first: {ih})");
            }
            catch (Exception e) { node.Log("[live] chunk: " + e.Message); }
        });
    }
}
