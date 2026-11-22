using System.Diagnostics;

namespace OpenTube.App;

/// <summary>
/// "Run at startup": installs this program as a service the way each system expects — a systemd unit on Linux, a
/// Windows service, a launchd agent on macOS — or explains how to do it by hand.
/// </summary>
public static class ServiceInstaller
{
    public const string Name = "OpenTube";
    const string Unit = "opentube.service", Label = "org.opentube.node";

    /// <summary>How to start this program: the executable, or "dotnet OpenTube.App.dll" when run through dotnet.</summary>
    public static string Command(string[] nodeArgs)
    {
        var exe = Environment.ProcessPath ?? "opentube";
        var dll = typeof(ServiceInstaller).Assembly.Location;
        var start = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ? $"\"{exe}\" \"{dll}\"" : $"\"{exe}\"";
        var keep = nodeArgs.Where(a => a.StartsWith("--", StringComparison.Ordinal) && a.Contains('=') || a == "--no-tunnel");
        return string.Join(' ', new[] { start, "--service" }.Concat(keep.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
    }

    static bool IsRoot => !OperatingSystem.IsWindows() && Environment.UserName == "root";

    public static int Install(string[] args)
    {
        var cmd = Command(args);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // system-wide when run as root (starts at boot); otherwise a user service (starts when you log in;
                // "loginctl enable-linger" makes it start at boot too)
                var dir = IsRoot ? "/etc/systemd/system" : Path.Combine(Home, ".config/systemd/user");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, Unit), SystemdUnit(cmd));
                var u = IsRoot ? "" : "--user ";
                Run("systemctl", u + "daemon-reload"); Run("systemctl", u + "enable --now " + Unit);
                Console.WriteLine($"Installed {Path.Combine(dir, Unit)} and started it.\nStatus: systemctl {u}status opentube · Log: journalctl {(IsRoot ? "" : "--user ")}-u opentube -f"
                    + (IsRoot ? "" : $"\nTo start it at boot without logging in: sudo loginctl enable-linger {Environment.UserName}"));
            }
            else if (OperatingSystem.IsWindows())
            {
                // needs an administrator prompt
                Run("sc.exe", $"create {Name} binPath= \"{cmd.Replace("\"", "\\\"")}\" start= auto DisplayName= \"OpenTube node\"");
                Run("sc.exe", $"description {Name} \"OpenTube: a node of the decentralized video network\"");
                Run("sc.exe", $"start {Name}");
                Console.WriteLine("Installed and started the OpenTube service (services.msc shows it).");
            }
            else if (OperatingSystem.IsMacOS())
            {
                var file = Path.Combine(Home, "Library/LaunchAgents", Label + ".plist");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, LaunchdPlist(cmd));
                Run("launchctl", $"unload \"{file}\"", quiet: true); Run("launchctl", $"load -w \"{file}\"");
                Console.WriteLine($"Installed {file}; it starts when you log in. Log: ~/Library/Logs/opentube.log");
            }
            else { Console.WriteLine(Instructions()); return 1; }
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Couldn't install the service: {e.Message}\n\n{Instructions()}");
            return 1;
        }
    }

    public static int Uninstall()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var u = IsRoot ? "" : "--user ";
                Run("systemctl", u + "disable --now " + Unit, quiet: true);
                File.Delete(Path.Combine(IsRoot ? "/etc/systemd/system" : Path.Combine(Home, ".config/systemd/user"), Unit));
                Run("systemctl", u + "daemon-reload", quiet: true);
            }
            else if (OperatingSystem.IsWindows()) { Run("sc.exe", $"stop {Name}", quiet: true); Run("sc.exe", $"delete {Name}"); }
            else if (OperatingSystem.IsMacOS()) { var f = Path.Combine(Home, "Library/LaunchAgents", Label + ".plist"); Run("launchctl", $"unload -w \"{f}\"", quiet: true); File.Delete(f); }
            Console.WriteLine("Removed the OpenTube service.");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("Couldn't remove the service: " + e.Message); return 1; }
    }

    public static string Instructions(string[]? args = null)
    {
        var cmd = Command(args ?? []);
        return $"""
          Run OpenTube at startup

          Linux (systemd) — save as /etc/systemd/system/{Unit} (or ~/.config/systemd/user/{Unit} for your user only):
          {Indent(SystemdUnit(cmd))}
            then: sudo systemctl daemon-reload && sudo systemctl enable --now opentube
            (user service: systemctl --user … and sudo loginctl enable-linger $USER to start at boot)

          Windows — in an administrator terminal:
            sc.exe create {Name} binPath= "{cmd.Replace("\"", "\\\"")}" start= auto
            sc.exe start {Name}

          macOS (launchd) — save as ~/Library/LaunchAgents/{Label}.plist:
          {Indent(LaunchdPlist(cmd))}
            then: launchctl load -w ~/Library/LaunchAgents/{Label}.plist

          Or let this program do it: opentube --install-service (and --uninstall-service to remove it).
          """;
    }

    static string SystemdUnit(string cmd) => $"""
        [Unit]
        Description=OpenTube node
        After=network-online.target
        Wants=network-online.target

        [Service]
        Type=notify
        ExecStart={cmd}
        Restart=always
        RestartSec=5
        Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1

        [Install]
        WantedBy={(IsRoot ? "multi-user.target" : "default.target")}
        """;

    static string LaunchdPlist(string cmd)
    {
        var parts = SplitArgs(cmd).Select(a => $"    <string>{System.Security.SecurityElement.Escape(a)}</string>");
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>Label</key><string>{Label}</string>
              <key>ProgramArguments</key><array>
            {string.Join("\n", parts)}
              </array>
              <key>RunAtLoad</key><true/>
              <key>KeepAlive</key><true/>
              <key>StandardOutPath</key><string>{Home}/Library/Logs/opentube.log</string>
              <key>StandardErrorPath</key><string>{Home}/Library/Logs/opentube.log</string>
            </dict></plist>
            """;
    }

    static IEnumerable<string> SplitArgs(string cmd)
    {
        var cur = new System.Text.StringBuilder(); bool q = false;
        foreach (var c in cmd) { if (c == '"') q = !q; else if (c == ' ' && !q) { if (cur.Length > 0) { yield return cur.ToString(); cur.Clear(); } } else cur.Append(c); }
        if (cur.Length > 0) yield return cur.ToString();
    }
    static string Indent(string s) => string.Join("\n", s.Split('\n').Select(l => "    " + l));
    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static void Run(string file, string args, bool quiet = false)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        p.WaitForExit();
        if (p.ExitCode != 0 && !quiet) throw new InvalidOperationException($"{file} {args} failed: {(p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd()).Trim()}");
    }
}
