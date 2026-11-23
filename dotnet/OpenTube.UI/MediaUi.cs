using OpenTube.Client;

namespace OpenTube.UI;

/// <summary>Sharing your media's bytes over BitTorrent from this device (the player's torrent client).</summary>
public static class MediaUi
{
    public static Func<byte[], string, Task<string>> Seeder(IVideoPlayer player) => async (bytes, name) =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "opentube-media", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..16]);
        Directory.CreateDirectory(dir);
        var safe = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' ? c : '_'));
        var path = Path.Combine(dir, safe.Length > 0 ? safe : "file");
        if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes);
        return (await player.Share(path, Path.GetFileName(path))).InfoHash;
    };
    static bool reseeded;
    /// <summary>Once per run: keep sharing what you've attached.</summary>
    public static void ReseedOnce(ClientSession s, IVideoPlayer player)
    {
        if (reseeded) return; reseeded = true;
        _ = Task.Run(async () => { await Task.Delay(10000); try { await s.Media.Reseed(Seeder(player)); } catch { } });
    }
}
