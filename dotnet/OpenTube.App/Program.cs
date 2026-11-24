using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTube.Node;

namespace OpenTube.App;

/// <summary>
/// One program, three ways to run the same node:
///   (default)  a window when there's a screen (Windows, macOS, Linux desktop) — the terminal UI when there isn't (SSH)
///   --tui      the terminal UI: menus and status in text, for servers and terminals
///   --service  no UI: run by systemd, Windows services or launchd (see --install-service)
/// Node options: --port=8080 --peers=1.2.3.4,wss://node.example/ot --data=DIR --site-dir=DIR --name=LABEL --grant=… --no-tunnel
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h")) { Console.WriteLine(Help); return 0; }
        if (args.Contains("--install-service")) return ServiceInstaller.Install(args);
        if (args.Contains("--uninstall-service")) return ServiceInstaller.Uninstall();
        if (args.Contains("--service-help")) { Console.WriteLine(ServiceInstaller.Instructions()); return 0; }
        if (args.Length > 0 && args[0] == "torrent") return TorrentTool.Run(args[1..]);
        if (args.FirstOrDefault(a => a.StartsWith("--obs-via=")) is { } via)   // only a bridge for OBS to a computer behind a tunnel
            return Ingest.Bridge(via[10..], int.TryParse(args.FirstOrDefault(a => a.StartsWith("--obs-port="))?[11..], out var op) ? op : 1935).GetAwaiter().GetResult();
        if (args.Contains("--service")) return RunService(args);
        if (Console.IsInputRedirected && !HasDisplay())
        {   // no keyboard to read (input is a pipe or a file): menus would show but no key would reach them
            Console.WriteLine("OpenTuber: no terminal to read keys from, so running without the menus (Ctrl+C stops it).");
            return RunService(args);
        }
        if (args.Contains("--tui") || !HasDisplay()) return TerminalUi.Run(args);
        return Gui.Run(args);
    }

    /// <summary>A desktop to draw a window on: always on Windows and macOS (unless over SSH); on Linux, an X11 or Wayland session.</summary>
    static bool HasDisplay()
    {
        if (Environment.GetEnvironmentVariable("SSH_CONNECTION") != null && Environment.GetEnvironmentVariable("DISPLAY") == null) return false;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return true;
        return Environment.GetEnvironmentVariable("DISPLAY") != null || Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") != null;
    }

    static int RunService(string[] args)
    {
        var b = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) || a.Contains('=')).ToArray());
        b.Services.AddSystemd();                                                       // Linux: talks to systemd (ready, stopping)
        b.Services.AddWindowsService(o => o.ServiceName = ServiceInstaller.Name);      // Windows: runs as a service
        b.Services.AddSingleton(NodeOptions.From(args));
        b.Services.AddHostedService<NodeService>();
        b.Build().Run();
        return 0;
    }

    const string Help = """
      OpenTuber node

        opentube                 a window (or the terminal UI when there's no screen)
        opentube --tui           the terminal UI
        opentube --service       no UI (for systemd, Windows services, launchd)
        opentube --install-service [node options]    run at startup on this computer
        opentube --uninstall-service
        opentube --service-help  how to set it up by hand

      Node options
        --port=8080              website, peers (/ot), tracker (/tracker), live hub (/live)
        --peers=ADDR[,ADDR]      other nodes: 1.2.3.4, 1.2.3.4:8080, https://node.example or ws://…/ot
        --data=DIR               key and blocks (default ~/.opentube/dotnet)
        --site-dir=DIR           the website to serve (the folder with index.html)
        --name=LABEL             this computer's name on your Devices page
        --grant=…                your grant from the site (My node), so the node shows up as yours
        --mine=own|all|off       proof of work for registrations (default own)
        --public=auto            reachable from the internet by itself when it's directly on it (a server with a
                                 public IP, or ports 80/443 forwarded): https://<ip>.sslip.io with a Let's
                                 Encrypt certificate. --public=your.domain for your own name, --public=off
        --email=YOU@…            for Let's Encrypt's expiry notices (optional)
        --tunnel-token=… --public-url=https://your.host
                                 a named Cloudflare tunnel (your account) for a fixed address
        --obs-via=https://…      only a bridge on the OBS computer: OBS → rtmp://127.0.0.1/live → that computer
                                 (through its tunnel or a firewall that blocks port 1935)
        --tunnel / --no-tunnel   a Cloudflare tunnel (cloudflared, downloaded if needed) is used when this
                                 computer can't be reached directly or ports 80/443 are taken by another
                                 program; --tunnel: always as well, --no-tunnel: never
      """;
}

/// <summary>The node as a background service (systemd / Windows service / launchd).</summary>
sealed class NodeService(NodeOptions options, IHostApplicationLifetime life) : BackgroundService
{
    NodeServer? server;
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { server = new NodeServer(options); await server.StartAsync(); }
        catch (Exception e) { Console.Error.WriteLine("OpenTuber couldn't start: " + e.Message); life.StopApplication(); return; }
        try { await Task.Delay(Timeout.Infinite, stop); } catch (OperationCanceledException) { }
    }
    public override async Task StopAsync(CancellationToken ct) { if (server != null) await server.DisposeAsync(); await base.StopAsync(ct); }
}
