using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>
/// Your media library (as the JavaScript page's "My media", and the same records): pictures and videos kept on this
/// device, private until you attach them to something. Attached to something public (a post, a group, a listing) a file
/// is shared as an ordinary torrent; attached to a friends-only post it's encrypted first with a fresh key (AES-GCM),
/// only that copy is shared, and the key travels inside the sealed post. A reference on the chain:
/// { ih, name, mime, size, thumb?, key?, iv? }.
/// </summary>
public sealed class Media
{
    readonly IClientStore store;
    public event Action? Changed;
    public Media(IClientStore store) { this.store = store; }

    public sealed record Item(JsonObject O)
    {
        public string Id => (string?)O["id"] ?? ""; public string Name => (string?)O["name"] ?? "file"; public string Mime => (string?)O["mime"] ?? "application/octet-stream";
        public double Size => Js.Num(O["size"]) ?? 0; public double Ts => Js.Num(O["ts"]) ?? 0; public string? Thumb => (string?)O["thumb"];
        public JsonArray Shared => O["shared"] as JsonArray ?? [];
    }
    public async Task<List<Item>> List() => ((await store.Get("mediaLib") is { } j ? JsonNode.Parse(j) as JsonArray : null) ?? []).OfType<JsonObject>().Select(o => new Item((JsonObject)o.DeepClone())).ToList();
    async Task Save(List<Item> lib) { await store.Set("mediaLib", new JsonArray(lib.Select(i => (JsonNode?)i.O.DeepClone()).ToArray()).ToJsonString()); Changed?.Invoke(); }

    public async Task<Item> Add(string name, string mime, byte[] data, string? thumb)
    {
        var o = new JsonObject { ["id"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), ["name"] = name.Length > 0 ? name[..Math.Min(200, name.Length)] : "file", ["mime"] = (mime.Length > 0 ? mime : "application/octet-stream")[..Math.Min(80, Math.Max(1, mime.Length))],
            ["size"] = data.Length, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["thumb"] = thumb ?? "", ["store"] = "browser", ["shared"] = new JsonArray() };
        var item = new Item(o);
        await store.SetBytes("mediablob:" + item.Id, data, item.Mime);
        var lib = await List(); lib.Insert(0, item); await Save(lib);
        return item;
    }
    public Task<byte[]?> Bytes(Item item) => store.GetBytes("mediablob:" + item.Id);
    public async Task Remove(Item item)
    {
        await store.SetBytes("mediablob:" + item.Id, null);
        var lib = await List(); lib.RemoveAll(x => x.Id == item.Id); await Save(lib);
    }

    static byte[] Gcm(bool encrypt, byte[] key, byte[] iv, byte[] data)
    {
        var c = new GcmBlockCipher(new AesEngine()); c.Init(encrypt, new AeadParameters(new KeyParameter(key), 128, iv));
        var o = new byte[c.GetOutputSize(data.Length)]; var n = c.ProcessBytes(data, 0, data.Length, o, 0); c.DoFinal(o, n); return o;
    }
    public static byte[] Seal(byte[] data, string key, string iv) => Gcm(true, B64u.Decode(key), B64u.Decode(iv), data);
    public static byte[] Open(byte[] data, string key, string iv) => Gcm(false, B64u.Decode(key), B64u.Decode(iv), data);

    /// <summary>Share an item: <paramref name="seed"/>(bytes, file name) shares them over BitTorrent and gives the infohash.
    /// Private: a fresh key for each share (each audience gets its own encrypted copy).</summary>
    public async Task<JsonObject> Share(Item item, bool priv, Func<byte[], string, Task<string>> seed)
    {
        var data = await Bytes(item) ?? throw new InvalidOperationException($"“{item.Name}” isn’t on this device any more");
        JsonObject sh;
        if (priv)
        {
            var key = B64u.Encode(RandomNumberGenerator.GetBytes(32)); var iv = B64u.Encode(RandomNumberGenerator.GetBytes(12));
            sh = new JsonObject { ["ih"] = await seed(Seal(data, key, iv), item.Id + ".sealed"), ["key"] = key, ["iv"] = iv, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        }
        else sh = item.Shared.OfType<JsonObject>().FirstOrDefault(x => x["key"] == null) is { } old ? (JsonObject)old.DeepClone() : new JsonObject { ["ih"] = await seed(data, item.Name), ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        if (!priv && item.Shared.OfType<JsonObject>().Any(x => x["key"] == null)) await seed(data, item.Name);   // (shares it again from this device: same infohash)
        var lib = await List(); var mine = lib.FirstOrDefault(x => x.Id == item.Id);
        if (mine != null && !mine.Shared.OfType<JsonObject>().Any(x => (string?)x["ih"] == (string?)sh["ih"])) { (mine.O["shared"] as JsonArray ?? (JsonArray)(mine.O["shared"] = new JsonArray())).Add(sh.DeepClone()); await Save(lib); }
        var r = new JsonObject { ["ih"] = sh["ih"]!.DeepClone(), ["name"] = item.Name, ["mime"] = item.Mime, ["size"] = item.Size };
        if (item.Thumb is { Length: > 0 and <= 16000 } th) r["thumb"] = th;
        if (priv) { r["key"] = sh["key"]!.DeepClone(); r["iv"] = sh["iv"]!.DeepClone(); }
        return r;
    }
    /// <summary>Keep sharing what you've attached, from your own copy (after a restart).</summary>
    public async Task Reseed(Func<byte[], string, Task<string>> seed)
    {
        foreach (var item in await List())
            foreach (var sh in item.Shared.OfType<JsonObject>())
                try
                {
                    var data = await Bytes(item); if (data == null) break;
                    await seed((string?)sh["key"] is { } k ? Seal(data, k, (string)sh["iv"]!) : data, sh["key"] != null ? item.Id + ".sealed" : item.Name);
                }
                catch { }
    }
    /// <summary>Your own copy of a shared file, if it's in your library.</summary>
    public async Task<byte[]?> OwnCopy(string ih)
    {
        var item = (await List()).FirstOrDefault(x => x.Shared.OfType<JsonObject>().Any(s => (string?)s["ih"] == ih));
        return item == null ? null : await Bytes(item);
    }
    public static bool RefOk(JsonNode? m) => m is JsonObject o && Js.IsHex(o["ih"], 40) && (string?)o["name"] is { Length: <= 200 } && (string?)o["mime"] is { Length: <= 80 } && Js.Num(o["size"]) > 0;
}
