using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OpenTube.Node;

/// <summary>
/// Which programs hold a port, and stopping an older OpenTube that still does (as the JavaScript launcher did): an
/// older OpenTube left running, the Node.js one from before the C# node, or a second copy, keeps ports such as OBS's
/// 1935, and this one then can't listen there.
/// </summary>
public static partial class Processes
{
    public sealed record Holder(int Pid, string Cmd);

    /// <summary>The processes listening on a TCP port (Linux: ss; macOS: lsof; Windows: netstat). Empty when unknown.</summary>
    public static List<Holder> PortHolders(int port)
    {
        var pids = new HashSet<int>();
        try
        {
            if (OperatingSystem.IsLinux()) foreach (var p in LinuxHolders(port)) pids.Add(p);
            else if (OperatingSystem.IsMacOS())
                foreach (var l in Run("lsof", $"-nP -iTCP:{port} -sTCP:LISTEN -t").Split('\n')) { if (int.TryParse(l.Trim(), out var p)) pids.Add(p); }
            else if (OperatingSystem.IsWindows())
                foreach (var l in Run("netstat", "-ano -p tcp").Split('\n'))
                {
                    var f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length >= 5 && f[3] == "LISTENING" && f[1].EndsWith(":" + port) && int.TryParse(f[4], out var p)) pids.Add(p);
                }
        }
        catch { }
        return pids.Select(p => new Holder(p, CommandOf(p))).ToList();
    }

    /// <summary>Linux without tools: the listening sockets on the port (/proc/net/tcp, tcp6), then the processes that hold them.</summary>
    static IEnumerable<int> LinuxHolders(int port)
    {
        var inodes = new HashSet<string>();
        foreach (var f in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            if (!File.Exists(f)) continue;
            foreach (var line in File.ReadLines(f).Skip(1))
            {
                var c = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (c.Length < 10 || c[3] != "0A") continue;   // 0A: listening
                var local = c[1]; var hexPort = local[(local.LastIndexOf(':') + 1)..];
                if (Convert.ToInt32(hexPort, 16) == port) inodes.Add(c[9]);
            }
        }
        if (inodes.Count == 0) yield break;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            string[] fds; try { fds = Directory.GetFiles(Path.Combine(dir, "fd")); } catch { continue; }   // (other users' processes: not readable without root)
            foreach (var fd in fds)
            {
                string? target; try { target = new FileInfo(fd).LinkTarget; } catch { continue; }
                if (target != null && target.StartsWith("socket:[") && inodes.Contains(target[8..^1])) { yield return pid; break; }
            }
        }
    }

    public static string CommandOf(int pid)
    {
        try { if (OperatingSystem.IsLinux()) return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim(); } catch { }
        try { if (OperatingSystem.IsMacOS()) return Run("ps", $"-o command= -p {pid}").Trim(); } catch { }
        try { using var p = Process.GetProcessById(pid); return p.MainModule?.FileName ?? p.ProcessName; } catch { }
        try { using var p = Process.GetProcessById(pid); return p.ProcessName; } catch { return ""; }
    }
    static int ParentOf(int pid)
    {
        try { if (OperatingSystem.IsLinux() && PPidRe().Match(File.ReadAllText($"/proc/{pid}/status")) is { Success: true } m) return int.Parse(m.Groups[1].Value); } catch { }
        return 0;
    }

    /// <summary>An OpenTube program: the C# node (opentube.dll, OpenTube.App) or the older JavaScript one (server.mjs, opentube.mjs).</summary>
    public static bool IsOpenTube(string cmd) => OpenTubeRe().IsMatch(cmd);

    /// <summary>Stop these processes (and the old JavaScript launcher that would start them again). True if any stopped.</summary>
    public static async Task<bool> Stop(IEnumerable<int> pids, Action<string> log)
    {
        var me = Environment.ProcessId; var parent = ParentOf(me);
        var all = new HashSet<int>();
        foreach (var pid in pids)
        {
            if (pid <= 1 || pid == me || pid == parent) continue;
            all.Add(pid);
            var pp = ParentOf(pid);   // "node -e …opentube.mjs…" restarts its children: stop it too
            if (pp > 1 && pp != me && pp != parent && CommandOf(pp).Contains("opentube.mjs")) all.Add(pp);
        }
        if (all.Count == 0) return false;
        foreach (var pid in all) try { using var p = Process.GetProcessById(pid); log($"stopping the older OpenTube (process {pid}: {Short(CommandOf(pid))})"); p.Kill(); } catch { }
        for (int i = 0; i < 50 && all.Any(Alive); i++) await Task.Delay(100);
        await Task.Delay(300);
        return true;
    }
    static bool Alive(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; } }
    public static string Short(string cmd) => cmd.Length > 80 ? cmd[..80] + "…" : cmd;

    /// <summary>
    /// One OpenTube per data folder: an earlier one still running (its pid in running.pid) is stopped first, so this
    /// one gets its ports (OBS's 1935, the web port) and its files.
    /// </summary>
    public static async Task StopEarlier(string dataDir, Action<string> log)
    {
        var file = Path.Combine(dataDir, "running.pid");
        try
        {
            if (File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out var pid) && pid != Environment.ProcessId && Alive(pid) && IsOpenTube(CommandOf(pid)))
                await Stop([pid], log);
        }
        catch { }
        try { File.WriteAllText(file, Environment.ProcessId.ToString()); } catch { }
    }

    static string Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(3000); return o;
    }
    [GeneratedRegex(@"PPid:\s*(\d+)")] private static partial Regex PPidRe();
    [GeneratedRegex(@"server\.mjs|opentube\.mjs|headless\.mjs|opentube\.dll|OpenTube\.App|[/\\]opentube(\.exe)?(\s|$)", RegexOptions.IgnoreCase)] private static partial Regex OpenTubeRe();
}
