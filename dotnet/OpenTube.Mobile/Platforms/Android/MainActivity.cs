using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace OpenTube.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    /// <summary>On screen (then the app's own bell is enough: no system notification).</summary>
    public static bool Visible;
    protected override void OnCreate(Bundle? savedInstanceState) { base.OnCreate(savedInstanceState); Tapped(Intent); }
    protected override void OnNewIntent(Intent? intent) { base.OnNewIntent(intent); Tapped(intent); }
    protected override void OnResume() { base.OnResume(); Visible = true; }
    protected override void OnPause() { base.OnPause(); Visible = false; }
    // in the background the app keeps no connections (your computers watch and push); back on screen it catches up
    protected override void OnStart() { base.OnStart(); Session()?.Resume(); }
    protected override void OnStop() { base.OnStop(); Session()?.Pause(); }
    static OpenTube.Client.ClientSession? Session() => IPlatformApplication.Current?.Services.GetService(typeof(OpenTube.Client.ClientSession)) as OpenTube.Client.ClientSession;
    static void Tapped(Intent? i) { if (i?.GetStringExtra(AndroidNotifier.HrefExtra) is { Length: > 0 } href) OpenTube.UI.AppNav.Open(href); }
}
