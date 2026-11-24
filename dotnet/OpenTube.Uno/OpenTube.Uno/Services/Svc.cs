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
    public const string DefaultSite = OpenTube.Core.Network.SiteUrl;
    public static ClientSession S { get; private set; } = null!;
    public static IVideoSource Video { get; private set; } = null!;
    public static string Site { get; private set; } = DefaultSite;
    static bool started;

    /// <summary>What went wrong starting up, if anything (shown on Home instead of closing the app).</summary>
    public static string? StartError { get; private set; }
    /// <summary>The crash from the last run, if the app closed on an error (shown once, then cleared).</summary>
    public static string? LastCrash { get; private set; }
    static string dataDir = "";
    /// <summary>The app's own data folder (chains, keys, videos).</summary>
    public static string DataFolder => dataDir;

    public static void Init()
    {
        if (started) return; started = true;
        try { dataDir = DataDir(); } catch (Exception e) { dataDir = Path.GetTempPath(); StartError = "storage: " + e.Message; }
        var crashFile = Path.Combine(dataDir, "last-crash.txt");
        try { if (File.Exists(crashFile)) { LastCrash = File.ReadAllText(crashFile); File.Delete(crashFile); } } catch { }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { try { File.WriteAllText(crashFile, DateTime.Now + "\n" + e.ExceptionObject); } catch { } };
        TaskScheduler.UnobservedTaskException += (_, e) => { e.SetObserved(); Console.WriteLine("[opentube] background error: " + e.Exception.GetBaseException().Message); };
        try
        {
            Site = Environment.GetEnvironmentVariable("OPENTUBE_SITE") ?? PlatformSite() ?? Reachable(DefaultSite) ?? OpenTube.Core.Network.FallbackSite;
            S = new ClientSession(new FileStore(Path.Combine(dataDir, "chain")), Site) { Log = m => Console.WriteLine("[opentube] " + m) };
            _ = Task.Run(async () => { try { await S.Start(); } catch (Exception e) { StartError = "network: " + e.Message; Console.WriteLine("[opentube] start: " + e); } });
        }
        catch (Exception e) { StartError = "network: " + e.Message; Console.WriteLine("[opentube] start: " + e); }
        TestHooks.Start();
        Video = new LazyVideoSource(() =>
        {
#if __WASM__
            return new BrowserVideoSource(S);
#else
            return new NativeVideoSource(S, Path.Combine(dataDir, "videos"));
#endif
        });
    }
    /// <summary>The site if it answers within a few seconds (the home domain before its DNS is live doesn't).</summary>
    static string? Reachable(string site)
    {
#if __WASM__
        return site;
#else
        try { using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(4) }; return h.GetAsync(site + "/status").GetAwaiter().GetResult().IsSuccessStatusCode ? site : null; } catch { return null; }
#endif
    }
    /// <summary>Note an error a page caught, for the crash report.</summary>
    public static void Report(Exception e) { Console.WriteLine("[opentube] error: " + e); try { File.AppendAllText(Path.Combine(dataDir, "errors.txt"), DateTime.Now + " " + e + "\n"); } catch { } }

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

    /// <summary>(tests and screenshots) what to open first: OPENTUBE_START, or ?start= in the browser.</summary>
    public static string? StartOption => Environment.GetEnvironmentVariable("OPENTUBE_START") ?? Query("start");
    public static string? QueryOption(string name) => Query(name);
    static string? Query(string name)
    {
#if __WASM__
        try { var q = System.Runtime.InteropServices.JavaScript.JSHost.GlobalThis.GetPropertyAsJSObject("location")?.GetPropertyAsString("search") ?? ""; var m = System.Text.RegularExpressions.Regex.Match(q, "[?&]" + name + "=([^&]+)"); if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value); } catch { }
#endif
        return null;
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

/// <summary>The video source, made when the first video plays (its sockets and the DHT start then, and a failure
/// there is reported on the watch page instead of stopping the app).</summary>
sealed class LazyVideoSource(Func<IVideoSource> make) : IVideoSource
{
    IVideoSource? inner; string? error;
    public async Task<Uri?> Play(string infoHash, string file, double size, Action<string> status)
    {
        if (inner == null && error == null) try { inner = make(); } catch (Exception e) { error = e.Message; Svc.Report(e); }
        if (inner == null) { status("Videos can’t play on this device: " + error); return null; }
        return await inner.Play(infoHash, file, size, status);
    }
    IVideoSource Inner() { if (inner == null && error == null) try { inner = make(); } catch (Exception e) { error = e.Message; Svc.Report(e); } return inner ?? throw new InvalidOperationException("Videos can’t be shared from this device: " + error); }
    public Task<OpenTube.Core.BitTorrent.TorrentMeta> Share(string path, string name) => Inner().Share(path, name);
    public Task<Uri?> PlayLive(OpenTube.Client.LiveWatch live) => Inner().PlayLive(live);
    public long Uploaded(string infoHash) => inner?.Uploaded(infoHash) ?? 0;
    public Task<string?> Thumbnail(string path, string infoHash, string name) => Inner().Thumbnail(path, infoHash, name);
}

/// <summary>Where a video plays from: an address the player control can open.</summary>
public interface IVideoSource
{
    /// <summary>Start getting a video (BitTorrent) and return an address to play it from, as it arrives.</summary>
    Task<Uri?> Play(string infoHash, string file, double size, Action<string> status);
    /// <summary>A live stream being watched (LiveWatch), as an address the player opens.</summary>
    Task<Uri?> PlayLive(OpenTube.Client.LiveWatch live) => Task.FromResult<Uri?>(null);
    /// <summary>Share a file from this device over BitTorrent (as a seed); its torrent.</summary>
    Task<OpenTube.Core.BitTorrent.TorrentMeta> Share(string path, string name);
    /// <summary>What this device is moving (uploads to your computer: bytes sent).</summary>
    long Uploaded(string infoHash) => 0;
    /// <summary>A still from a video this device shares, as a small JPEG data: URI (null when this platform can't).</summary>
    Task<string?> Thumbnail(string path, string infoHash, string name) => Task.FromResult<string?>(null);
}
