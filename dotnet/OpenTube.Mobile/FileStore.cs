using System.Text.Json.Nodes;
using OpenTube.Client;

namespace OpenTube.Mobile;

/// <summary>The app's copy of the chains and its keys, in its private files: kv/{key} and blocks.jsonl (one block per line).</summary>
public sealed class FileStore : IClientStore
{
    readonly string dir, blocks; readonly object gate = new();
    public FileStore(string dir) { this.dir = dir; blocks = Path.Combine(dir, "blocks.jsonl"); Directory.CreateDirectory(Path.Combine(dir, "kv")); }
    string KvPath(string key) => Path.Combine(dir, "kv", string.Concat(key.Select(c => char.IsLetterOrDigit(c) ? c : '_')));
    public Task<string?> Get(string key) => Task.FromResult(File.Exists(KvPath(key)) ? File.ReadAllText(KvPath(key)) : null);
    public Task Set(string key, string value) { var p = KvPath(key); File.WriteAllText(p + ".tmp", value); File.Move(p + ".tmp", p, true); return Task.CompletedTask; }
    public Task<List<(string Chain, JsonObject Block)>> LoadBlocks()
    {
        var list = new List<(string, JsonObject)>();
        lock (gate)
            if (File.Exists(blocks))
                foreach (var line in File.ReadLines(blocks))
                    try { if (JsonNode.Parse(line) is JsonObject r && (string?)r["c"] is { } c && r["b"] is JsonObject b) list.Add((c, (JsonObject)b.DeepClone())); } catch { }   // a line cut short by a crash
        return Task.FromResult(list);
    }
    string BlobPath(string key) => Path.Combine(dir, "files", string.Concat(key.Select(c => char.IsLetterOrDigit(c) ? c : '_')));
    public Task SetBytes(string key, byte[]? data, string mime = "application/octet-stream")
    {
        Directory.CreateDirectory(Path.Combine(dir, "files")); var p = BlobPath(key);
        if (data == null) { if (File.Exists(p)) File.Delete(p); } else File.WriteAllBytes(p, data);
        return Task.CompletedTask;
    }
    public Task<byte[]?> GetBytes(string key) => Task.FromResult(File.Exists(BlobPath(key)) ? File.ReadAllBytes(BlobPath(key)) : null);
    public void SaveBlock(string chain, JsonObject block)
    {
        var line = new JsonObject { ["c"] = chain, ["b"] = block.DeepClone() }.ToJsonString() + "\n";
        lock (gate) File.AppendAllText(blocks, line);
    }
}
