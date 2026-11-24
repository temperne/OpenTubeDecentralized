using Windows.Storage;
using Windows.Storage.Pickers;

namespace OpenTube.Uno.Services;

/// <summary>Saving and opening files the person picks (key backups): the system's own save and open dialogs on every
/// platform (Android's file chooser, the browser's download and upload, the desktop's dialogs).</summary>
public static class Files
{
    /// <summary>(tests) save into this folder instead of asking, e.g. OPENTUBE_SAVE_DIR=/tmp/x</summary>
    static string? TestDir => Environment.GetEnvironmentVariable("OPENTUBE_SAVE_DIR");

    /// <summary>Save text as a file; true when saved.</summary>
    public static async Task<bool> SaveText(string name, string text)
    {
        if (TestDir is { } d) { Directory.CreateDirectory(d); await File.WriteAllTextAsync(Path.Combine(d, name), text); return true; }
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(name), SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add("OpenTube keys", [Path.GetExtension(name) is { Length: > 0 } ext ? ext : ".json"]);
        Init(picker);
        var file = await picker.PickSaveFileAsync(); if (file == null) return false;
        await FileIO.WriteTextAsync(file, text); return true;
    }
    /// <summary>Open a text file the person picks; null when they cancel.</summary>
    public static async Task<string?> OpenText(params string[] types)
    {
        if (TestDir is { } d && Environment.GetEnvironmentVariable("OPENTUBE_OPEN_FILE") is { } f) return await File.ReadAllTextAsync(Path.Combine(d, f));
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var t in types.Length > 0 ? types : ["*"]) picker.FileTypeFilter.Add(t);
        Init(picker);
        var file = await picker.PickSingleFileAsync(); if (file == null) return null;
        return await FileIO.ReadTextAsync(file);
    }
    /// <summary>A picture the person picks, made into a small JPEG (max side in pixels) as a data: URI; null when they cancel.</summary>
    public static async Task<string?> OpenImage(int maxSide = 256)
    {
        byte[] bytes;
        if (TestDir is { } d && Environment.GetEnvironmentVariable("OPENTUBE_OPEN_IMAGE") is { } f) bytes = await File.ReadAllBytesAsync(Path.Combine(d, f));
        else
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var t in new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" }) picker.FileTypeFilter.Add(t);
            Init(picker);
            var file = await picker.PickSingleFileAsync(); if (file == null) return null;
            using var s = await file.OpenStreamForReadAsync(); using var ms = new MemoryStream(); await s.CopyToAsync(ms); bytes = ms.ToArray();
        }
        using var bmp = SkiaSharp.SKBitmap.Decode(bytes) ?? throw new InvalidOperationException("That isn’t a picture this app can read.");
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(bmp.Width, bmp.Height));
        using var small = bmp.Resize(new SkiaSharp.SKImageInfo(Math.Max(1, (int)(bmp.Width * scale)), Math.Max(1, (int)(bmp.Height * scale))), SkiaSharp.SKSamplingOptions.Default) ?? bmp;
        using var img = SkiaSharp.SKImage.FromBitmap(small); using var jpg = img.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 85);
        return "data:image/jpeg;base64," + Convert.ToBase64String(jpg.ToArray());
    }
    public sealed record Picked(string Path, string Name, long Size, string Type);
    /// <summary>A video the person picks, copied into the app's own folder (where it's shared from), reporting bytes copied.</summary>
    public static async Task<Picked?> OpenVideo(Action<long, long> progress)
    {
        Stream src; string name; string type; long size;
        if (Environment.GetEnvironmentVariable("OPENTUBE_OPEN_VIDEO") is { } tp) { src = File.OpenRead(tp); name = Path.GetFileName(tp); size = src.Length; type = "video/mp4"; }
        else
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
            foreach (var t in new[] { ".mp4", ".webm", ".mov", ".mkv", ".m4v" }) picker.FileTypeFilter.Add(t);
            Init(picker);
            var file = await picker.PickSingleFileAsync(); if (file == null) return null;
            name = file.Name; type = file.ContentType is { Length: > 0 } ct ? ct : "video/mp4";
            src = await file.OpenStreamForReadAsync(); size = src.CanSeek ? src.Length : (long)(await file.GetBasicPropertiesAsync()).Size;
        }
        var dir = Path.Combine(Path.GetTempPath(), "opentube-upload", Guid.NewGuid().ToString("n")[..8]); Directory.CreateDirectory(dir);
        var clean = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' ? c : '_'));
        var path = Path.Combine(dir, clean.Length > 0 ? clean : "video.mp4");
        await using (src) await using (var dst = File.Create(path))
        {
            var buf = new byte[1 << 20]; int n; long done = 0; var shown = DateTime.UtcNow;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n)); done += n;
                if ((DateTime.UtcNow - shown).TotalMilliseconds > 200) { shown = DateTime.UtcNow; progress(done, size); await Task.Yield(); }
            }
            progress(done, done);
        }
        return new Picked(path, Path.GetFileName(path), new FileInfo(path).Length, type);
    }
    static void Init(object picker)
    {
#if WINDOWS
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
#endif
    }
}
