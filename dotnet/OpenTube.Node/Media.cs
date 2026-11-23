using System.Diagnostics;

namespace OpenTube.Node;

/// <summary>Small media helpers that use ffmpeg when it's installed (and do without it).</summary>
public static class Media
{
    static bool? ffmpeg;
    public static bool HasFfmpeg => ffmpeg ??= Try();
    static bool Try() { try { using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }); p!.WaitForExit(5000); return p.ExitCode == 0; } catch { return false; } }
    /// <summary>A small JPEG from 2 s in, as a data URL ("" without ffmpeg or if it's over 44 KB), as the JavaScript node's thumbOf.</summary>
    public static string Thumb(string file)
    {
        if (!HasFfmpeg) return "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg") { ArgumentList = { "-hide_banner", "-loglevel", "error", "-ss", "2", "-i", file, "-frames:v", "1", "-vf", "scale=320:-2", "-q:v", "6", "-f", "image2", "-c:v", "mjpeg", "pipe:1" }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            using var ms = new MemoryStream(); var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
            if (!p.WaitForExit(15000)) { p.Kill(); return ""; }
            copy.Wait(2000);
            return ms.Length is > 0 and < 44000 ? "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray()) : "";
        }
        catch { return ""; }
    }
}
