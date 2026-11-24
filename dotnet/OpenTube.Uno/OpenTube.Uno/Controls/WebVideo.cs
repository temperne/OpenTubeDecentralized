#if __WASM__
using System.Runtime.InteropServices.JavaScript;

namespace OpenTube.Uno.Controls;

/// <summary>
/// The player in the browser: the browser's own video element, kept over this control's spot (it follows the layout
/// as the page moves). The browser gives it its controls, full screen and picture-in-picture.
/// </summary>
public sealed partial class WebVideo : Border
{
    static int nextId;
    readonly int id = Interlocked.Increment(ref nextId);
    string? src; bool shown;
    [JSImport("globalThis.otVideo.show")] private static partial void Show(int id, string src);
    [JSImport("globalThis.otVideo.place")] private static partial void Place(int id, double x, double y, double w, double h, bool visible);
    [JSImport("globalThis.otVideo.remove")] private static partial void Remove(int id);
    [JSImport("globalThis.otVideo.state")] private static partial string State(int id);
    readonly DispatcherTimer follow = new() { Interval = TimeSpan.FromMilliseconds(250) };
    public WebVideo()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
        LayoutUpdated += (_, _) => Follow();
        follow.Tick += (_, _) => Follow();
        Loaded += (_, _) => { follow.Start(); if (src != null) { Show(id, src); shown = true; } Follow(); };
        Unloaded += (_, _) => { follow.Stop(); if (shown) { Remove(id); shown = false; } };
    }
    public void Play(Uri uri) { src = uri.ToString(); if (IsLoaded) { Show(id, src); shown = true; Follow(); } }
    /// <summary>The player's state, position and duration (seconds).</summary>
    public (string State, double Position, double Duration) Status()
    {
        if (!shown) return ("None", 0, 0);
        var p = State(id).Split(' ');
        return (p[0], double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture));
    }
    void Follow()
    {
        if (!shown || XamlRoot == null) return;
        try
        {
            var at = TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            Place(id, at.X, at.Y, ActualWidth, ActualHeight, Visibility == Visibility.Visible && ActualWidth > 0);
        }
        catch { }
    }
}
#endif
