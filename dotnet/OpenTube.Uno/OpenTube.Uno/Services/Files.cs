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
    static void Init(object picker)
    {
#if WINDOWS
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
#endif
    }
}
