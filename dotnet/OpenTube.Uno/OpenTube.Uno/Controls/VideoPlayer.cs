namespace OpenTube.Uno.Controls;

/// <summary>
/// The video player every screen uses: on Android and desktop the platform's player (Uno's MediaPlayerElement, with
/// full screen); in the browser the browser's own video element over this spot (its controls and full screen).
/// </summary>
public sealed partial class VideoPlayer : Grid
{
#if __WASM__
    readonly WebVideo web = new();
#else
    readonly MediaPlayerElement mpe = new() { AreTransportControlsEnabled = true, AutoPlay = true, Stretch = Stretch.Uniform };
#endif
    public VideoPlayer()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
#if __WASM__
        Children.Add(web);
#else
        Children.Add(mpe);
#endif
    }
    public void Play(Uri uri)
    {
#if __WASM__
        web.Play(uri);
#else
        mpe.Source = Windows.Media.Core.MediaSource.CreateFromUri(uri);
#endif
    }
    /// <summary>State ("Playing", "Paused", …), position and duration in seconds.</summary>
    public (string State, double Position, double Duration) Status()
    {
#if __WASM__
        return web.Status();
#else
        try { var ps = mpe.MediaPlayer?.PlaybackSession; return ps == null ? ("None", 0, 0) : (ps.PlaybackState.ToString(), ps.Position.TotalSeconds, ps.NaturalDuration.TotalSeconds); } catch { return ("None", 0, 0); }
#endif
    }
    public void Pause()
    {
#if !__WASM__
        try { mpe.MediaPlayer?.Pause(); } catch { }
#endif
    }
}
