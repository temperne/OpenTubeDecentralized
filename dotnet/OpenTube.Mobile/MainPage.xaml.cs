namespace OpenTube.Mobile;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
#if ANDROID
        // videos stream from the app's own BitTorrent client at http://127.0.0.1 (the page itself is https://0.0.0.1)
        blazorWebView.BlazorWebViewInitialized += (_, e) =>
        {
            e.WebView.Settings.MixedContentMode = Android.Webkit.MixedContentHandling.AlwaysAllow;
            e.WebView.Settings.MediaPlaybackRequiresUserGesture = false;
            if (e.WebView.WebChromeClient is { } inner) e.WebView.SetWebChromeClient(new CameraChromeClient(inner));   // the camera for going live
        };
#endif
    }
}
