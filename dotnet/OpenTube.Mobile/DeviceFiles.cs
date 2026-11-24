using OpenTube.UI;

namespace OpenTube.Mobile;

/// <summary>Keys on the phone: a backup goes to the share sheet (save it to files, send it to yourself…); another
/// identity relaunches the app.</summary>
public sealed class DeviceFiles : IDeviceFiles
{
    public async Task Save(string name, string text)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, name); await File.WriteAllTextAsync(path, text);
        await MainThread.InvokeOnMainThreadAsync(() => Share.Default.RequestAsync(new ShareFileRequest { Title = "Keep your OpenTuber keys private", File = new ShareFile(path, "application/json") }));
    }
    public Task Restart()
    {
#if ANDROID
        var a = Platform.CurrentActivity;
        if (a?.PackageManager?.GetLaunchIntentForPackage(a.PackageName!) is { } i)
        {
            i.AddFlags(Android.Content.ActivityFlags.NewTask | Android.Content.ActivityFlags.ClearTask);
            a.StartActivity(i); Java.Lang.JavaSystem.Exit(0);
        }
#endif
        return Task.CompletedTask;
    }
}
