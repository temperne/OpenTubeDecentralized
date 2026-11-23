namespace OpenTube.UI;

/// <summary>
/// Plays a video from the network into an element: in the browser, BitTorrent over WebRTC (thin bindings to the
/// browser's WebRTC and its video element); on a phone, the C# BitTorrent client streaming to the page's own video
/// element from 127.0.0.1 (no JavaScript). Returns the video's address when the page should play it itself, and a status line.
/// </summary>
public interface IVideoPlayer
{
    Task<Playback> Play(string elementId, string infoHash, string file, double size, Action<string> status);
}
public sealed record Playback(string? Src, string? Status);
