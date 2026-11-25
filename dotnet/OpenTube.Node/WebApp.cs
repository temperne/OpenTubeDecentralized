using System.Diagnostics;

namespace OpenTube.Node;

/// <summary>
/// The C# website a node serves at /app/: the Uno app for the browser (OpenTube.Uno).
/// It's found ready-made (--app-dir, the site's app/ folder), or, when the node runs from the OpenTuber command (its
/// source is here), built once per version in the background: "dotnet publish" into app-web/ next to the source, marked
/// with the commit it was built from.
/// </summary>
public sealed class WebApp(OpenTubeNode node)
{
    readonly string? src = Environment.GetEnvironmentVariable("OPENTUBE_APP_DIR");
    string? built => src == null ? null : Path.Combine(src, "app-web");
    public bool Building { get; private set; }

    /// <summary>The folder with the site's index.html (ends with a separator), or null while there's none.</summary>
    public string? Root
    {
        get
        {
            foreach (var d in new[] { node.Options.AppDir, Path.Combine(node.SiteDir, "app"), built })
                if (d != null) foreach (var c in new[] { Path.Combine(d, "wwwroot", "app"), Path.Combine(d, "wwwroot"), Path.Combine(d, "app"), d })
                    if (File.Exists(Path.Combine(c, "index.html")) && File.Exists(Path.Combine(c, "uno-config.js"))) return Path.GetFullPath(c) + Path.DirectorySeparatorChar;
            return null;
        }
    }

    public async Task EnsureBuilt()
    {
        if (src == null || built == null || Environment.GetEnvironmentVariable("OPENTUBE_WEB_BUILD") == "0") return;
        var commit = File.Exists(Path.Combine(src, ".commit")) ? File.ReadAllText(Path.Combine(src, ".commit")).Trim() : "";
        var mark = Path.Combine(built, ".commit");
        if (File.Exists(mark) && File.ReadAllText(mark).Trim() == commit && commit != "") return;
        // the ready-made one GitHub built from this branch (quick, and needs no WebAssembly tools here)
        if (await Download(mark, commit)) return;
        var uno = Path.Combine(src, "dotnet", "OpenTube.Uno", "OpenTube.Uno", "OpenTube.Uno.csproj");
        var proj = uno;
        if (!File.Exists(uno)) return;
        Building = true;
        try
        {
            node.Log("[web] building the C# website (WebAssembly) for /app/ …");
            var psi = new ProcessStartInfo("dotnet") { ArgumentList = { "publish", proj, "-c", "Release", "-o", built, "--nologo" }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-f", "net10.0-browserwasm", "-p:WasmShellWebAppBasePath=/app/", "-p:OpenTuberWeb=true" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!; var output = await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync();
            if (p.ExitCode != 0) { node.Log("[web] the C# website didn’t build: " + output.Split('\n').LastOrDefault(l => l.Contains("error"))?.Trim()); return; }
            File.WriteAllText(mark, commit);
            node.Log("[web] the C# website is ready at /app/");
        }
        catch (Exception e) { node.Log("[web] couldn’t build the C# website: " + e.Message); }
        finally { Building = false; }
    }
    /// <summary>Fetch the website's app GitHub built (the web-latest release) into app-web/; false if it can't.</summary>
    async Task<bool> Download(string mark, string commit)
    {
        try
        {
            Building = true; node.Log("[web] downloading the website's app…");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var bytes = await http.GetByteArrayAsync("https://github.com/temperne/OpenTubeDecentralized/releases/download/web-latest/opentube-web.tar.gz");
            var dir = Path.Combine(built!, "wwwroot"); if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.CreateDirectory(dir);
            using (var gz = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress))
                await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(gz, dir, overwriteFiles: true);
            if (!File.Exists(Path.Combine(dir, "uno-config.js"))) throw new InvalidOperationException("not the app");
            File.WriteAllText(mark, commit);
            node.Log("[web] the website's app is ready at /app/");
            return true;
        }
        catch (Exception e) { node.Log("[web] couldn’t download the website's app (" + e.Message + "); building it here"); return false; }
        finally { Building = false; }
    }
}
