using Android.Webkit;
using WebView = Android.Webkit.WebView;

namespace OpenTube.Mobile;

/// <summary>
/// The web view asking for the camera and microphone (going live): Android asks the person, then the page gets them.
/// Wraps the web view's own client (which, among other things, opens the file picker for uploads) and passes everything
/// else on to it.
/// </summary>
public sealed class CameraChromeClient(WebChromeClient inner) : WebChromeClient
{
    public override void OnPermissionRequest(PermissionRequest? request)
    {
        if (request == null) return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            var res = request.GetResources() ?? [];
            bool ok = true;
            if (res.Contains(PermissionRequest.ResourceVideoCapture)) ok &= await Permissions.RequestAsync<Permissions.Camera>() == PermissionStatus.Granted;
            if (res.Contains(PermissionRequest.ResourceAudioCapture)) ok &= await Permissions.RequestAsync<Permissions.Microphone>() == PermissionStatus.Granted;
            if (ok) request.Grant(res); else request.Deny();
        });
    }
    // ---- full screen video: the player's ⛶ button hands its view to the app, which shows it over everything (bars hidden,
    // free to turn sideways), and takes it back when the person leaves full screen
    Android.Views.View? full; ICustomViewCallback? fullDone; Android.Content.PM.ScreenOrientation oldOrientation;
    public override void OnShowCustomView(Android.Views.View? view, ICustomViewCallback? callback)
    {
        if (view == null) return;
        if (full != null) { callback?.OnCustomViewHidden(); return; }
        var act = Platform.CurrentActivity; if (act?.Window?.DecorView is not Android.Widget.FrameLayout decor) { callback?.OnCustomViewHidden(); return; }
        full = view; fullDone = callback; oldOrientation = act.RequestedOrientation;
        view.SetBackgroundColor(Android.Graphics.Color.Black);
        decor.AddView(view, new Android.Widget.FrameLayout.LayoutParams(Android.Views.ViewGroup.LayoutParams.MatchParent, Android.Views.ViewGroup.LayoutParams.MatchParent));
        Bars(act, false);
        act.RequestedOrientation = Android.Content.PM.ScreenOrientation.FullSensor;   // turn the phone to watch sideways
    }
    public override void OnHideCustomView()
    {
        var act = Platform.CurrentActivity;
        if (full != null && act?.Window?.DecorView is Android.Widget.FrameLayout decor) decor.RemoveView(full);
        full = null; var done = fullDone; fullDone = null;
        if (act != null) { Bars(act, true); act.RequestedOrientation = oldOrientation; }
        done?.OnCustomViewHidden();
    }
    static void Bars(Android.App.Activity act, bool show)
    {
        var w = act.Window; if (w == null) return;
        var c = AndroidX.Core.View.WindowCompat.GetInsetsController(w, w.DecorView);
        AndroidX.Core.View.WindowCompat.SetDecorFitsSystemWindows(w, show);
        if (show) c.Show(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars());
        else { c.Hide(AndroidX.Core.View.WindowInsetsCompat.Type.SystemBars()); c.SystemBarsBehavior = AndroidX.Core.View.WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe; }
    }
    public override bool OnShowFileChooser(WebView? webView, IValueCallback? filePathCallback, FileChooserParams? fileChooserParams) => inner.OnShowFileChooser(webView, filePathCallback, fileChooserParams);
    public override bool OnCreateWindow(WebView? view, bool isDialog, bool isUserGesture, Android.OS.Message? resultMsg) => inner.OnCreateWindow(view, isDialog, isUserGesture, resultMsg);
    public override void OnCloseWindow(WebView? window) => inner.OnCloseWindow(window);
    public override bool OnConsoleMessage(ConsoleMessage? consoleMessage) => inner.OnConsoleMessage(consoleMessage);
}
