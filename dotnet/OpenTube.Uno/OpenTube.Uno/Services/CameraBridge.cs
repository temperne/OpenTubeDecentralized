#if __WASM__
using System.Runtime.InteropServices.JavaScript;

namespace OpenTube.Uno.Services;

/// <summary>The browser's camera for going live (opentube-bridge.js, otCam): devices, preview frames (the app draws them), and the recorder,
/// whose chunks arrive here and go to the broadcast.</summary>
public static partial class CameraBridge
{
    public static Action<byte[]>? OnChunk;
    [JSExport] public static void Chunk(byte[] bytes) => OnChunk?.Invoke(bytes);
    /// <summary>A preview frame: BGRA pixels, w × h.</summary>
    public static Action<byte[], int, int>? OnFrame;
    [JSExport] public static void Frame(byte[] bgra, int w, int h) => OnFrame?.Invoke(bgra, w, h);
    [JSImport("globalThis.otCam.devices")] public static partial Task<string> Devices();
    [JSImport("globalThis.otCam.preview")] public static partial Task<string> Preview(string cam, string mic);
    [JSImport("globalThis.otCam.start")] public static partial Task<string> Start();
    [JSImport("globalThis.otCam.stop")] public static partial Task Stop();
}
#endif
