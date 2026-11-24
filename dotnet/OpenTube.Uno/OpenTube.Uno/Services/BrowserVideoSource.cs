#if __WASM__
namespace OpenTube.Uno.Services;

/// <summary>Videos in the browser: BitTorrent over the browser's WebRTC, streamed to the player through the site's
/// service worker (as the website does). Wired up after the proof's Android/desktop player.</summary>
public sealed class BrowserVideoSource : IVideoSource
{
    public Task<Uri?> Play(string infoHash, string file, double size, Action<string> status)
    {
        status("Playing in the browser is the next step of the conversion.");
        return Task.FromResult<Uri?>(null);
    }
}
#endif
