#if __WASM__
using System.Runtime.InteropServices.JavaScript;
using Uno.UI.NativeElementHosting;

namespace OpenTube.Uno.Controls;

/// <summary>
/// The player in the browser: the browser's own video element, hosted by Uno in this control's spot (Uno places and
/// clips it with the layout). The browser gives it its controls, full screen and picture-in-picture.
/// </summary>
public sealed partial class WebVideo : ContentControl
{
    readonly BrowserHtmlElement el = BrowserHtmlElement.CreateHtmlElement("video");
    string? src; bool ready;
    [JSImport("globalThis.otVideo.setup")] private static partial void Setup(string id);
    [JSImport("globalThis.otVideo.play")] private static partial void PlaySrc(string id, string src);
    [JSImport("globalThis.otVideo.stop")] private static partial void Stop(string id);
    [JSImport("globalThis.otVideo.state")] private static partial string State(string id);
    public WebVideo()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        Content = el;
        Loaded += (_, _) => { Setup(el.ElementId); ready = true; if (src != null) PlaySrc(el.ElementId, src); };
        Unloaded += (_, _) => { ready = false; Stop(el.ElementId); };
    }
    public void Play(Uri uri) { src = uri.ToString(); if (ready) PlaySrc(el.ElementId, src); }
    /// <summary>The player's state, position and duration (seconds).</summary>
    public (string State, double Position, double Duration) Status()
    {
        if (!ready) return ("None", 0, 0);
        var p = State(el.ElementId).Split(' ');
        return (p[0], double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture));
    }
}
#endif
