using System.Text.Json.Nodes;
using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Core.BitTorrent;
using OpenTube.Net;
using OpenTube.UI;

namespace OpenTube.Mobile;

/// <summary>
/// Plays a video with the app's own BitTorrent client (all C#): it finds peers on the network's WebSocket trackers
/// (browsers and nodes, over WebRTC), the DHT, and the TCP peers they name, downloads in order from where the viewer
/// is, and the page's video element streams it from 127.0.0.1 (<see cref="TorrentHttp"/>). Videos are kept in the
/// app's cache and shared back while the app runs.
/// </summary>
public sealed class NativeVideoPlayer : IVideoPlayer
{
    readonly ClientSession s; readonly string dir;
    readonly BtClient bt = new();
    readonly WebTorrentPeers web;
    readonly TorrentHttp http;
    Dht? dht;
    public NativeVideoPlayer(ClientSession s, string dir)
    {
        this.s = s; this.dir = dir; Directory.CreateDirectory(dir);
        web = new WebTorrentPeers(bt, new SipRtc(), m => System.Diagnostics.Debug.WriteLine("[bt] " + m));
        http = new TorrentHttp(ih => bt.Torrents.GetValueOrDefault(ih), stream =>
        {
            if (s.Watching(stream) is not { } w) return null;
            var r = w.Subscribe(); return (w.Mp4 ? "video/mp4" : "video/webm", r, () => w.Unsubscribe(r));
        });
        bt.Listen(0);
        _ = Task.Run(async () => { try { var d = new Dht(); d.Start(0); await d.Bootstrap(); bt.UseDht(d); dht = d; } catch { } });
    }

    List<string> Trackers()
    {
        var list = new List<string> { s.Site.Replace("https://", "wss://").Replace("http://", "ws://") + "/tracker" };
        list.AddRange(s.LinkedUrls.Select(u => u.EndsWith("/ot") ? u[..^3] + "/tracker" : u));
        foreach (var t in JsonNode.Parse(Network.MainGenesisJson)?["payload"]?["trackers"]?.AsArray() ?? []) if ((string?)t is { } u) list.Add(u);
        return list.Distinct().ToList();
    }



    public IReadOnlyList<Transfer> Transfers() => bt.Torrents.Values.Select(t => new Transfer(t.InfoHashHex, t.Meta?.Name ?? "", t.Have.Length == 0 ? 0 : (double)t.HaveCount / t.Have.Length, bt.PeersOf(t.InfoHashHex), t.Complete, t.Meta?.Length ?? 0)).ToList();
    public IReadOnlyList<string> TrackerList() => Trackers();
    public async Task<byte[]?> Fetch(string infoHash, string file, double size, TimeSpan timeout)
    {
        var name = string.Concat(Path.GetFileName(file).Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')); if (name.Length == 0) name = "ad.mp4";
        var t = bt.Torrents.GetValueOrDefault(infoHash) ?? bt.Add(TorrentData.Fetch(infoHash, Path.Combine(dir, infoHash, name)));
        web.Announce(t, Trackers());
        var until = DateTime.UtcNow + timeout;
        while (!t.Complete && DateTime.UtcNow < until) await Task.Delay(250);
        if (!t.Complete || t.Meta is not { } m) return null;
        var outp = new byte[m.Length]; long at = 0;
        for (int p = 0; p < t.Have.Length; p++)
            for (int off = 0; off < m.SizeOf(p); off += 128 * 1024) { var b = t.ReadBlock(p, off, Math.Min(128 * 1024, m.SizeOf(p) - off)); if (b == null) return null; b.CopyTo(outp, at); at += b.Length; }
        return outp;
    }

    public Task<TorrentMeta> Share(string path, string name)
    {
        var t = bt.Add(TorrentData.Seed(path, name)); web.Announce(t, Trackers());
        return Task.FromResult(t.Meta!);
    }
    public Task<Playback> PlayLive(LiveWatch live) => Task.FromResult(new Playback($"http://127.0.0.1:{http.Port}/live/{live.Stream}", live.Status));

    public Task<Playback> Play(string elementId, string infoHash, string file, double size, Action<string> status)
    {
        var name = string.Concat(Path.GetFileName(file).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var t = bt.Torrents.GetValueOrDefault(infoHash) ?? bt.Add(TorrentData.Fetch(infoHash, Path.Combine(dir, infoHash, name.Length > 0 ? name : "video.mp4")));
        web.Announce(t, Trackers());
        _ = Task.Run(async () =>
        {
            while (!t.Complete)
            {
                await Task.Delay(2000);
                status(t.Meta == null ? $"Finding peers… ({bt.PeersOf(infoHash)} connected)" : $"{100.0 * t.HaveCount / Math.Max(1, t.Have.Length):0}% · {bt.PeersOf(infoHash)} peer(s)");
            }
            status($"Downloaded · sharing it back to {bt.PeersOf(infoHash)} peer(s)");
        });
        return Task.FromResult(new Playback(http.UrlFor(t), "Finding peers for this video…"));
    }
}
