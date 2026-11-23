using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;

namespace OpenTube.Node;

/// <summary>
/// Updating itself (as the JavaScript node): every 10 minutes it compares the commit it was built from with the
/// newest commit of the branch it came from (GitHub). When there's a new one and this computer isn't busy (OBS, a group
/// stream, relaying), it downloads that source next to itself, builds it (Linux, macOS), and exits with code 75: the
/// one-command launcher runs it again (rebuilding), a system service is restarted by the service manager.
/// Started by the launcher (OPENTUBE_APP_DIR); AUTO_UPDATE=0 turns it off. "Update" on the site does it at once.
/// </summary>
public sealed class Updater(OpenTubeNode node)
{
    readonly string? app = Environment.GetEnvironmentVariable("OPENTUBE_APP_DIR");
    readonly string repo = Environment.GetEnvironmentVariable("OPENTUBE_REPO") ?? "temperne/OpenTubeDecentralized";
    readonly string branch = Environment.GetEnvironmentVariable("OPENTUBE_REF") ?? "ccr-500677da-r1uf50";
    string? seen; int busyNoted;
    public bool Enabled => app != null && Directory.Exists(app);
    public string? Commit => app != null && File.Exists(Path.Combine(app, ".commit")) ? File.ReadAllText(Path.Combine(app, ".commit")).Trim() : null;
    static readonly HttpClient http = MakeHttp();
    static HttpClient MakeHttp() { var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) }; h.DefaultRequestHeaders.UserAgent.ParseAdd("OpenTube-node"); return h; }

    public string? Busy()
    {
        if (node.Rtmp?.Live > 0) return "receiving OBS";
        if (node.Obs != null) lock (node.Obs.Groups) if (node.Obs.Groups.Values.Any(g => g.Live || g.Starting)) return "a group stream";
        if (node.Relays?.Count > 0) return "relaying a live stream";
        return null;
    }
    public async Task Run(CancellationToken stop)
    {
        if (!Enabled || Environment.GetEnvironmentVariable("AUTO_UPDATE") == "0") return;
        var first = int.TryParse(Environment.GetEnvironmentVariable("UPDATE_FIRST_MS"), out var ms) ? ms : 2 * 60000;   // (tests: sooner)
        await Task.Delay(first, stop).ContinueWith(_ => { });
        while (!stop.IsCancellationRequested)
        {
            try { await Check(false); } catch (Exception e) { node.Log("[update] check failed: " + e.Message); }
            await Task.Delay(TimeSpan.FromMinutes(10), stop).ContinueWith(_ => { });
        }
    }
    async Task<string?> Latest()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/commits/{branch}");
        req.Headers.Accept.ParseAdd("application/vnd.github.sha");
        using var r = await http.SendAsync(req); if (!r.IsSuccessStatusCode) return null;
        var sha = (await r.Content.ReadAsStringAsync()).Trim(); return System.Text.RegularExpressions.Regex.IsMatch(sha, "^[0-9a-f]{40}$") ? sha : null;
    }
    /// <summary>Update now if there's a new version (and, unless <paramref name="force"/>, this computer is idle). True: restarting.</summary>
    public async Task<bool> Check(bool force)
    {
        if (!Enabled) throw new InvalidOperationException("this computer wasn’t started with the OpenTube command, so it can’t update itself: stop it and run the command again");
        var latest = await Latest(); var have = Commit;
        if (latest == null) { if (force) throw new InvalidOperationException("couldn’t ask GitHub for the newest version"); return false; }
        if (latest == have) { if (force) node.Log("[update] already the newest version"); return false; }
        var busy = Busy();
        if (busy != null && !force) { if (seen != latest || busyNoted++ % 6 == 0) node.Log($"[update] a new OpenTube is available ({latest[..8]}); updating once this computer is idle (now: {busy})"); seen = latest; return false; }
        node.Log($"[update] downloading OpenTube {latest[..8]}…");
        var tmp = Path.Combine(Path.GetTempPath(), "opentube-update-" + latest[..8]);
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        await using (var gz = new GZipStream(await http.GetStreamAsync($"https://codeload.github.com/{repo}/tar.gz/{latest}"), CompressionMode.Decompress))
        using (var tar = new TarReader(gz))
            while (await tar.GetNextEntryAsync() is { } e)
            {
                var rel = string.Join('/', e.Name.Split('/').Skip(1)); if (rel.Length == 0 || rel.Contains("..")) continue;
                var dest = Path.Combine(tmp, rel);
                if (e.EntryType == TarEntryType.Directory) Directory.CreateDirectory(dest);
                else if (e.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile) { Directory.CreateDirectory(Path.GetDirectoryName(dest)!); await e.ExtractToFileAsync(dest, true); }
            }
        // the new source over the old (build outputs stay), then build it in place where the running program allows
        foreach (var f in Directory.EnumerateFiles(tmp, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(app!, Path.GetRelativePath(tmp, f)); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(f, dest, true);
        }
        Directory.Delete(tmp, true);
        if (!OperatingSystem.IsWindows())
        {
            node.Log("[update] building…");
            var psi = new ProcessStartInfo("dotnet", "build -c Release OpenTube.App") { WorkingDirectory = Path.Combine(app!, "dotnet"), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!; var output = await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync();
            if (p.ExitCode != 0) { node.Log("[update] the new version didn’t build; staying on this one: " + output.Split('\n').LastOrDefault(l => l.Contains("error"))?.Trim()); return false; }
        }
        File.WriteAllText(Path.Combine(app!, ".commit"), latest);
        node.Log($"[update] restarting with OpenTube {latest[..8]}");
        _ = Task.Delay(1000).ContinueWith(_ => Environment.Exit(75));
        return true;
    }
}
