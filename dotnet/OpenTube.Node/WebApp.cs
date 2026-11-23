using System.Diagnostics;

namespace OpenTube.Node;

/// <summary>
/// The C# website (Blazor WebAssembly, OpenTube.Web) a node serves at /app/. It's found ready-made (--app-dir, the site's
/// app/ folder), or, when the node runs from the OpenTube command (its source is here), built once per version in the
/// background: "dotnet publish" into app-web/ next to the source, marked with the commit it was built from.
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
                if (d != null) foreach (var c in new[] { Path.Combine(d, "wwwroot", "app"), Path.Combine(d, "app"), d })
                    if (File.Exists(Path.Combine(c, "index.html")) && File.Exists(Path.Combine(c, "opentube.js"))) return Path.GetFullPath(c) + Path.DirectorySeparatorChar;
            return null;
        }
    }

    public async Task EnsureBuilt()
    {
        if (src == null || built == null || Environment.GetEnvironmentVariable("OPENTUBE_WEB_BUILD") == "0") return;
        var commit = File.Exists(Path.Combine(src, ".commit")) ? File.ReadAllText(Path.Combine(src, ".commit")).Trim() : "";
        var mark = Path.Combine(built, ".commit");
        if (File.Exists(mark) && File.ReadAllText(mark).Trim() == commit && commit != "") return;
        var proj = Path.Combine(src, "dotnet", "OpenTube.Web");
        if (!Directory.Exists(proj)) return;
        Building = true;
        try
        {
            node.Log("[web] building the C# website (WebAssembly) for /app/ …");
            var psi = new ProcessStartInfo("dotnet") { ArgumentList = { "publish", proj, "-c", "Release", "-o", built, "--nologo" }, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!; var output = await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync();
            if (p.ExitCode != 0) { node.Log("[web] the C# website didn’t build: " + output.Split('\n').LastOrDefault(l => l.Contains("error"))?.Trim()); return; }
            File.WriteAllText(mark, commit);
            node.Log("[web] the C# website is ready at /app/");
        }
        catch (Exception e) { node.Log("[web] couldn’t build the C# website: " + e.Message); }
        finally { Building = false; }
    }
}
