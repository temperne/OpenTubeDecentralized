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
    public override bool OnShowFileChooser(WebView? webView, IValueCallback? filePathCallback, FileChooserParams? fileChooserParams) => inner.OnShowFileChooser(webView, filePathCallback, fileChooserParams);
    public override bool OnCreateWindow(WebView? view, bool isDialog, bool isUserGesture, Android.OS.Message? resultMsg) => inner.OnCreateWindow(view, isDialog, isUserGesture, resultMsg);
    public override void OnCloseWindow(WebView? window) => inner.OnCloseWindow(window);
    public override bool OnConsoleMessage(ConsoleMessage? consoleMessage) => inner.OnConsoleMessage(consoleMessage);
}
