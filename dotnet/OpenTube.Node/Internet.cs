using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenTube.Node;

/// <summary>
/// How this computer is on the internet, so the node picks the right way to be reachable by browsers:
///   directly (a server with a public IP, or ports 80/443 forwarded to it): its own HTTPS as &lt;ip&gt;.sslip.io or your domain;
///   behind a router, or the ports are taken: a Cloudflare tunnel.
/// </summary>
public static partial class Internet
{
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(6) };

    public static async Task<IPAddress?> PublicIPv4()
    {
        foreach (var u in new[] { "https://api.ipify.org", "https://ifconfig.me/ip", "https://icanhazip.com" })
            try { if (IPAddress.TryParse((await http.GetStringAsync(u)).Trim(), out var ip) && ip.AddressFamily == AddressFamily.InterNetwork) return ip; } catch { }
        return null;
    }
    public static string SslipName(IPAddress ip) => ip.ToString().Replace('.', '-') + ".sslip.io";

    /// <summary>The public address is on one of this computer's own network interfaces: no router in between.</summary>
    public static bool IsLocal(IPAddress ip) => NetworkInterface.GetAllNetworkInterfaces()
        .SelectMany(i => { try { return i.GetIPProperties().UnicastAddresses.Select(a => a.Address); } catch { return []; } }).Any(a => a.Equals(ip));

    public enum PortState { Free, InUse, NeedsAdmin }
    public static PortState Port(int port)
    {
        try { var l = new TcpListener(IPAddress.Any, port); l.Start(); l.Stop(); return PortState.Free; }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AccessDenied) { return PortState.NeedsAdmin; }
        catch (SocketException) { return PortState.InUse; }
    }
    /// <summary>The program listening on a port (Linux, where ss is installed), for a clear message.</summary>
    public static string? WhoHas(int port)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ss", $"-ltnpH sport = :{port}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            var o = p!.StandardOutput.ReadToEnd(); p.WaitForExit(3000);
            var m = UsersRe().Match(o); return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }
    [GeneratedRegex("users:\\(\\(\"([^\"]+)\"")] private static partial Regex UsersRe();

    public static string Explain(int port, PortState s) => s switch
    {
        PortState.InUse => $"port {port} is used by {(WhoHas(port) is { } w ? $"another program ({w})" : "another program")}",
        PortState.NeedsAdmin => $"port {port} needs admin rights (run as root, or once: sudo setcap cap_net_bind_service=+ep \"$(readlink -f \"$(which dotnet)\")\")",
        _ => "",
    };

    /// <summary>
    /// Can the internet reach this computer on <paramref name="port"/> at <paramref name="ip"/>? Briefly answers a random
    /// token there and asks for it from outside (works when ports are forwarded on a router that allows it).
    /// </summary>
    public static async Task<bool> Reachable(IPAddress ip, int port)
    {
        var token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        TcpListener l; try { l = new TcpListener(IPAddress.Any, port); l.Start(); } catch { return false; }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var serve = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var c = await l.AcceptTcpClientAsync(cts.Token); var s = c.GetStream();
                    var buf = new byte[2048]; await s.ReadAsync(buf, cts.Token);
                    await s.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {token.Length}\r\nConnection: close\r\n\r\n{token}"), cts.Token);
                }
                catch { return; }
            }
        });
        try { return (await http.GetStringAsync($"http://{ip}:{port}/.well-known/opentube-check")).Trim() == token; }
        catch { return false; }
        finally { cts.Cancel(); l.Stop(); try { await serve; } catch { } }
    }

    /// <summary>cloudflared: on the PATH, already downloaded, or downloaded now from Cloudflare's releases into DATA/bin.</summary>
    public static async Task<string?> Cloudflared(string dataDir, Action<string> log)
    {
        var name = OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared";
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
        if (onPath != null) return onPath;
        var bin = Path.Combine(dataDir, "bin"); var exe = Path.Combine(bin, name);
        if (File.Exists(exe)) return exe;
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        { System.Runtime.InteropServices.Architecture.Arm64 => "arm64", System.Runtime.InteropServices.Architecture.Arm => "arm", System.Runtime.InteropServices.Architecture.X86 => "386", _ => "amd64" };
        var file = OperatingSystem.IsWindows() ? $"cloudflared-windows-{arch}.exe" : OperatingSystem.IsMacOS() ? $"cloudflared-darwin-{(arch == "arm64" ? "arm64" : "amd64")}.tgz" : $"cloudflared-linux-{arch}";
        try
        {
            log("downloading cloudflared (for a Cloudflare tunnel; once)…");
            Directory.CreateDirectory(bin);
            using var dl = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            var bytes = await dl.GetByteArrayAsync("https://github.com/cloudflare/cloudflared/releases/latest/download/" + file);
            var tmp = exe + ".download";
            if (file.EndsWith(".tgz"))
            {
                using var gz = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
                using var tar = new System.Formats.Tar.TarReader(gz);
                while (tar.GetNextEntry() is { } e) if (Path.GetFileName(e.Name) == "cloudflared") { e.ExtractToFile(tmp, true); break; }
            }
            else await File.WriteAllBytesAsync(tmp, bytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(tmp, exe, true);
            return exe;
        }
        catch (Exception e) { log($"couldn't download cloudflared ({e.Message}); install it for a tunnel: https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/"); return null; }
    }
}
