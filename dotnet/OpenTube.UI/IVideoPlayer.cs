namespace OpenTube.UI;

/// <summary>
/// Plays a video from the network into an element: in the browser, BitTorrent over WebRTC (thin bindings to the
/// browser's WebRTC and its video element); on a phone, the C# BitTorrent client streaming to the page's own video
/// element from 127.0.0.1 (no JavaScript). Returns the video's address when the page should play it itself, and a status line.
/// </summary>
public interface IVideoPlayer
{
    Task<Playback> Play(string elementId, string infoHash, string file, double size, Action<string> status);
    /// <summary>A live stream (<see cref="OpenTube.Client.LiveWatch"/>) as an address the page's video element plays.</summary>
    Task<Playback> PlayLive(OpenTube.Client.LiveWatch live);
    /// <summary>Share a file from this device over BitTorrent (as a seed); its torrent (the infohash WebTorrent would give it).</summary>
    Task<OpenTube.Core.BitTorrent.TorrentMeta> Share(string path, string name);
    /// <summary>A whole (small) file from the network, e.g. an ad, once every piece is here; null if it doesn't arrive in time.</summary>
    Task<byte[]?> Fetch(string infoHash, string file, double size, TimeSpan timeout);
    /// <summary>What this device's BitTorrent client is moving right now (for the network page).</summary>
    IReadOnlyList<Transfer> Transfers() => [];
    /// <summary>The trackers (meet places) this device announces to.</summary>
    IReadOnlyList<string> TrackerList() => [];
}
public sealed record Playback(string? Src, string? Status);
public sealed record Transfer(string InfoHash, string Name, double Progress, int Peers, bool Complete, long Size);
