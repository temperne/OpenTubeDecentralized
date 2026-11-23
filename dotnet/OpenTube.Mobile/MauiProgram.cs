using Microsoft.Extensions.Logging;
using OpenTube.Client;
using OpenTube.UI;

namespace OpenTube.Mobile;

/// <summary>
/// OpenTube on a phone: the same pages as the website (OpenTube.UI) in a native web view, with everything else native
/// C#: the network's engine (OpenTube.Client over OpenTube.Core), the chains and keys in the app's own files, and
/// videos over BitTorrent (WebRTC with browsers and nodes, TCP, the DHT) streamed to the page's video element.
/// </summary>
public static class MauiProgram
{
    /// <summary>The site whose node the app links to first (then the entry nodes it lists).</summary>
    public const string Site = "https://opentubedecentralized.jasonajensen.workers.dev";

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
        var data = FileSystem.AppDataDirectory;
        builder.Services.AddSingleton<IClientStore>(new FileStore(Path.Combine(data, "chain")));
        builder.Services.AddSingleton(sp => new ClientSession(sp.GetRequiredService<IClientStore>(), Site) { Log = m => System.Diagnostics.Debug.WriteLine("[opentube] " + m) });
        builder.Services.AddSingleton<IVideoPlayer>(sp => new NativeVideoPlayer(sp.GetRequiredService<ClientSession>(), Path.Combine(FileSystem.CacheDirectory, "videos")));
        builder.Services.AddSingleton(new UiPlatform("C# · " + DeviceInfo.Platform));
        builder.Services.AddSingleton<IDeviceFiles, DeviceFiles>();
        builder.Services.AddSingleton(sp => new PushLink(sp.GetRequiredService<ClientSession>(), sp.GetRequiredService<IClientStore>(), $"{DeviceInfo.Manufacturer} {DeviceInfo.Model}".Trim()));
#if ANDROID
        builder.Services.AddSingleton<IPushSetup>(sp => new AndroidPushSetup(sp.GetRequiredService<PushLink>()));
#endif
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif
        var app = builder.Build();
        var session = app.Services.GetRequiredService<ClientSession>();
        _ = Task.Run(async () =>
        {
            await session.Start();
            await app.Services.GetRequiredService<PushLink>().Init();
#if ANDROID
            app.Services.GetRequiredService<IPushSetup>();   // (listens for the push address)
            await AndroidNotifier.Attach(session);
#endif
        });
        return app;
    }
}
