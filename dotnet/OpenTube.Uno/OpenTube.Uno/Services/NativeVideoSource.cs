#if !__WASM__
using OpenTube.Client;
using OpenTube.Core.BitTorrent;
using OpenTube.Net;

namespace OpenTube.Uno.Services;

/// <summary>
/// Videos on Android and desktop: the app's own BitTorrent client (all C#) finds peers on the network's WebSocket
/// trackers (browsers and nodes, over WebRTC), the DHT and TCP, downloads in order from where the viewer is, and the
/// player control streams it from a small server on this device (127.0.0.1). Kept in the app's cache and shared back.
/// </summary>
public sealed class NativeVideoSource : IVideoSource
{
    readonly ClientSession s; readonly string dir;
    readonly BtClient bt = new();
    readonly WebTorrentPeers web;
    readonly TorrentHttp http;
    public NativeVideoSource(ClientSession s, string dir)
    {
        this.s = s; this.dir = dir; Directory.CreateDirectory(dir);
        web = new WebTorrentPeers(bt, new SipRtc(), m => Console.WriteLine("[bt] " + m));
        http = new TorrentHttp(ih => bt.Torrents.GetValueOrDefault(ih), stream =>
        {
            if (s.Watching(stream) is not { } w) return null;
            var r = w.Subscribe(); return (w.Mp4 ? "video/mp4" : "video/webm", r, () => w.Unsubscribe(r));
        });
        bt.Listen(0);
        _ = Task.Run(async () => { try { var d = new Dht(); d.Start(0); await d.Bootstrap(); bt.UseDht(d); } catch { } });
    }
    public Task<Uri?> Play(string infoHash, string file, double size, Action<string> status)
    {
        var name = string.Concat(Path.GetFileName(file).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var t = bt.Torrents.GetValueOrDefault(infoHash) ?? bt.Add(TorrentData.Fetch(infoHash, Path.Combine(dir, infoHash, name.Length > 0 ? name : "video.mp4")));
        web.Announce(t, s.TrackerUrls());
        _ = Task.Run(async () =>
        {
            while (!t.Complete)
            {
                await Task.Delay(1500);
                status(t.Meta == null ? $"Finding peers… ({bt.PeersOf(infoHash)} connected)" : $"{100.0 * t.HaveCount / Math.Max(1, t.Have.Length):0}% · {bt.PeersOf(infoHash)} peer(s)");
            }
            status($"Downloaded · sharing it with {bt.PeersOf(infoHash)} peer(s)");
        });
        status("Finding peers for this video…");
        return Task.FromResult<Uri?>(new Uri(http.UrlFor(t)));
    }
}
#endif
