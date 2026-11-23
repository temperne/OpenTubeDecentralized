namespace OpenTube.UI;

/// <summary>
/// Plays a video from the network into an element: in the browser, BitTorrent over WebRTC (thin bindings to the
/// browser's WebRTC and its video element); on a phone, the C# client and the system player. Returns a status line.
/// </summary>
public interface IVideoPlayer
{
    Task<string?> Play(string elementId, string infoHash, string file, double size, Action<string> status);
}
