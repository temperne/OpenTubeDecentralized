using System.Text.Json.Nodes;
using OpenTube.Client;
using OpenTube.Core;

namespace OpenTube.Uno.Services;

/// <summary>
/// The app's services, shared by every page on every platform: the network session (OpenTube.Client: the chains,
/// keys, the coin and gossip, all C#), and where videos play from.
/// </summary>
public static class Svc
{
    /// <summary>The site whose node the app links to first (then the entry nodes it lists). In the browser: the page's own site.</summary>
    public const string DefaultSite = "https://opentubedecentralized.jasonajensen.workers.dev";
    public static ClientSession S { get; private set; } = null!;
    public static IVideoSource Video { get; private set; } = null!;
    public static string Site { get; private set; } = DefaultSite;
    static bool started;

    public static void Init()
    {
        if (started) return; started = true;
        Site = Environment.GetEnvironmentVariable("OPENTUBE_SITE") ?? PlatformSite() ?? DefaultSite;
        var data = DataDir();
        S = new ClientSession(new FileStore(Path.Combine(data, "chain")), Site) { Log = m => Console.WriteLine("[opentube] " + m) };
#if __WASM__
        Video = new BrowserVideoSource();
#else
        Video = new NativeVideoSource(S, Path.Combine(data, "videos"));
#endif
        _ = Task.Run(async () => { try { await S.Start(); } catch (Exception e) { Console.WriteLine("[opentube] start: " + e.Message); } });
    }

    static string DataDir()
    {
        var root = Environment.GetEnvironmentVariable("OPENTUBE_DATA");
        if (root == null)
        {
            try { root = Windows.Storage.ApplicationData.Current.LocalFolder.Path; } catch { }
            root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTube");
        }
        Directory.CreateDirectory(root); return root;
    }

    static string? PlatformSite()
    {
#if __WASM__
        try
        {
            var loc = System.Runtime.InteropServices.JavaScript.JSHost.GlobalThis.GetPropertyAsJSObject("location");
            var q = loc?.GetPropertyAsString("search") ?? "";   // ?site=https://… : another site's network (testing a local copy)
            var m = System.Text.RegularExpressions.Regex.Match(q, @"[?&]site=([^&]+)");
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : loc?.GetPropertyAsString("origin");
        }
        catch { }
#endif
        return null;
    }

    // ---- small helpers for the pages
    public static string Ago(double ms)
    {
        var s = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ms) / 1000;
        return s < 60 ? "just now" : s < 3600 ? $"{(int)(s / 60)} min ago" : s < 86400 ? $"{(int)(s / 3600)} h ago" : s < 86400 * 30 ? $"{(int)(s / 86400)} days ago" : DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToLocalTime().ToString("MMM d, yyyy");
    }
    public static string Views(double n) => n == 1 ? "1 view" : (n >= 1e6 ? $"{n / 1e6:0.#}M" : n >= 1e4 ? $"{Math.Round(n / 1e3)}K" : n.ToString("N0")) + " views";
    public static string Bytes(double n) => n < 1e6 ? $"{n / 1e3:0} KB" : n < 1e9 ? $"{n / 1e6:0.0} MB" : $"{n / 1e9:0.00} GB";

    /// <summary>A video's picture: the thumbnail on its block (a data: URI), as an image the controls can show.</summary>
    public static async Task<Microsoft.UI.Xaml.Media.Imaging.BitmapImage?> Thumb(string? dataUri)
    {
        if (dataUri is not { Length: > 30 } || !dataUri.StartsWith("data:image/")) return null;
        try
        {
            var bytes = Convert.FromBase64String(dataUri[(dataUri.IndexOf(',') + 1)..]);
            var img = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using var ms = new MemoryStream(bytes);
            await img.SetSourceAsync(ms.AsRandomAccessStream());
            return img;
        }
        catch { return null; }
    }
}

/// <summary>Where a video plays from: an address the player control can open.</summary>
public interface IVideoSource
{
    /// <summary>Start getting a video (BitTorrent) and return an address to play it from, as it arrives.</summary>
    Task<Uri?> Play(string infoHash, string file, double size, Action<string> status);
}
