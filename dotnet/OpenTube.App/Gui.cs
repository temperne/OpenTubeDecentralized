using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using OpenTube.Node;

namespace OpenTube.App;

/// <summary>The node in a window (Windows, macOS, Linux desktop): status, connections, the log, and run-at-startup.</summary>
public static class Gui
{
    internal static string[] Args = [];
    public static int Run(string[] args)
    {
        Args = args;
        return AppBuilder.Configure<GuiApp>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class GuiApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.MainWindow = new MainWindow(Gui.Args);
        base.OnFrameworkInitializationCompleted();
    }
}

sealed class MainWindow : Window
{
    readonly NodeServer server;
    readonly TextBlock status = new() { FontFamily = new FontFamily("monospace"), TextWrapping = TextWrapping.Wrap };
    readonly ListBox peers = new();
    readonly TextBox log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("monospace"), FontSize = 12 };
    readonly TextBox peerInput = new() { PlaceholderText = "Connect to a node: 1.2.3.4, 1.2.3.4:8080, https://… or ws://…/ot" };

    public MainWindow(string[] args)
    {
        Title = "OpenTuber node"; Width = 1000; Height = 680;
        server = new NodeServer(NodeOptions.From(args));
        var start = server.StartAsync();

        Button Btn(string text, Action a) { var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) }; b.Click += (_, _) => a(); return b; }
        var connect = Btn("Connect", () => { var a = peerInput.Text?.Trim(); if (!string.IsNullOrEmpty(a)) { server.Node.AddPeer(a); peerInput.Text = ""; } });
        var website = Btn("Open the website", () => Open(server.Node.PublicUrl ?? $"http://localhost:{server.Node.Options.Port}"));
        var install = Btn("Run at startup", () => ShowText("Run at startup", Capture(() => ServiceInstaller.Install(args))));
        var howto = Btn("How to set it up by hand", () => ShowText("Run at startup", ServiceInstaller.Instructions(args)));
        var domainBtn = Btn("Domain…", ShowDomain);

        // public address: automatic (its own HTTPS when directly on the internet, else a Cloudflare tunnel), a tunnel always, off
        var publicPick = new ComboBox { Margin = new Thickness(0, 0, 8, 0), ItemsSource = PublicChoices.Select(x => x.Label).ToList(), SelectedIndex = 0 };
        bool picking = false;
        publicPick.SelectionChanged += async (_, _) =>
        {
            if (picking || publicPick.SelectedIndex < 0) return;
            var want = PublicChoices[publicPick.SelectedIndex].Setting;
            if (want == server.PublicSetting) return;
            publicPick.IsEnabled = false;
            try { await Task.Run(() => server.SetPublic(want)); } catch (Exception e) { server.Node.Log("[internet] " + e.Message); }
            publicPick.IsEnabled = true;
        };
        var publicLabel = new TextBlock { Text = "Public:", VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0) };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8), Children = { website, install, howto, domainBtn, publicLabel, publicPick } };
        var connectRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        connectRow.Children.Add(peerInput); Grid.SetColumn(connect, 1); connect.Margin = new Thickness(8, 0, 0, 0); connectRow.Children.Add(connect);

        var left = new DockPanel();
        var peersHeader = new TextBlock { Text = "Connected", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(peersHeader, Dock.Top); left.Children.Add(peersHeader); left.Children.Add(peers);
        var right = new DockPanel { Margin = new Thickness(12, 0, 0, 0) };
        var logHeader = new TextBlock { Text = "Log", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(logHeader, Dock.Top); right.Children.Add(logHeader); right.Children.Add(log);
        var lists = new Grid { ColumnDefinitions = new ColumnDefinitions("1*,2*"), Margin = new Thickness(0, 12, 0, 0) };
        lists.Children.Add(left); Grid.SetColumn(right, 1); lists.Children.Add(right);

        var root = new DockPanel { Margin = new Thickness(16) };
        foreach (var c in new Control[] { top, status, connectRow }) { DockPanel.SetDock(c, Dock.Top); root.Children.Add(c); }
        root.Children.Add(lists);
        Content = root;

        DispatcherTimer.Run(() =>
        {
            if (start.IsFaulted) { status.Text = "Couldn't start: " + start.Exception?.InnerException?.Message; return true; }
            var s = server.Node.Status;
            if (s.Count > 0)
                status.Text = $"Network  {((string?)s["network"])?[..16]}…   OpenTuber {s["version"]} on {s["impl"]}\n" +
                              $"Main     #{s["height"]} · {s["registered"]} registered · {s["chains"]} chains · {s["pending"]} pending\n" +
                              $"Peers    {s["peers"]} connected · tracker {s["trackerClients"]} · live {s["liveClients"]} · nodes known {s["nodes"]}\n" +
                              $"Website  http://localhost:{server.Node.Options.Port}   {server.Node.PublicUrl ?? "(no public address)"}\n" +
                              $"Internet {server.Describe()}\n" +
                              $"Domain   {server.DescribeDomain()}";
            var idx = Array.FindIndex(PublicChoices, x => x.Setting == server.PublicSetting);
            if (idx >= 0 && publicPick.SelectedIndex != idx && publicPick.IsEnabled) { picking = true; publicPick.SelectedIndex = idx; picking = false; }
            peers.ItemsSource = server.Node.PeerList.Where(p => p.Role is "node-in" or "node-out").Select(p => $"{(p.Role == "node-out" ? "→" : "←")} {p.Label}").ToList();
            var text = string.Join('\n', server.Node.Logs);
            if (log.Text != text) { log.Text = text; log.CaretIndex = text.Length; }
            return true;
        }, TimeSpan.FromSeconds(1));
        Closed += (_, _) => server.DisposeAsync().AsTask().Wait(3000);
        if (args.Contains("--open=domain")) Opened += (_, _) => ShowDomain();   // (straight to the domain setting)
    }

    public static readonly (string Setting, string Label)[] PublicChoices =
    [
        ("auto", "Automatic (its own HTTPS if it can, else a Cloudflare tunnel)"),
        ("tunnel", "Always a Cloudflare tunnel (too)"),
        ("off", "Off (only by IP and the peer mesh)"),
    ];
    /// <summary>Answer DNS for a domain you own (its name servers point at this computer): its subdomains come from its
    /// zone on the network, and the site is served as the domain with a certificate for every namespace.</summary>
    void ShowDomain()
    {
        var (domain, zone, port, ip, email) = server.DomainSetting;
        TextBox Field(string watermark, string? text) => new() { Watermark = watermark, Text = text ?? "", Margin = new Thickness(0, 0, 0, 8) };
        var fDomain = Field("Domain, e.g. opentuber.com (empty: no DNS)", domain); var fZone = Field("Zone chain ID (empty: the oldest zone registered with that name)", zone);
        var fIp = Field("This computer's public IP(s), comma separated (empty: found automatically)", ip); var fPort = Field("DNS port (53)", port.ToString());
        var fEmail = Field("Email for Let's Encrypt (optional)", email);
        var msg = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        var save = new Button { Content = "Save and apply", Margin = new Thickness(0, 0, 8, 0) }; var off = new Button { Content = "Turn DNS off" };
        var w = new Window { Title = "Domain", Width = 640, Height = 560 };
        async Task Apply(string? d)
        {
            save.IsEnabled = off.IsEnabled = false; msg.Text = "Applying (the web side restarts; peers reconnect)…";
            try { await Task.Run(() => server.SetDomain(d, fZone.Text, int.TryParse(fPort.Text, out var p) ? p : 53, fIp.Text, fEmail.Text)); msg.Text = server.DescribeDomain(); }
            catch (Exception e) { msg.Text = e.Message; }
            save.IsEnabled = off.IsEnabled = true;
        }
        save.Click += async (_, _) => await Apply(fDomain.Text);
        off.Click += async (_, _) => { fDomain.Text = ""; await Apply(null); };
        w.Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(16), Children = {
            new TextBlock { Text = "Answer DNS for a domain", FontWeight = FontWeight.SemiBold, FontSize = 16, Margin = new Thickness(0, 0, 0, 8) },
            new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Text =
                "At your registrar, set the domain's name servers to ns1.DOMAIN and ns2.DOMAIN, with glue records = this computer's public IP. " +
                "This computer then answers for the domain: its subdomains come from its zone on the network (approve claims in the app: You → Domains), " +
                "and name.u / name.c / name.g / name.o / name.l open people, channels, groups, organizations and lists. " +
                "It serves the site as the domain, with one Let's Encrypt certificate for all of it. Ports 53, 80 and 443 must reach this computer (53 needs administrator rights)." },
            fDomain, fZone, fIp, fPort, fEmail,
            new StackPanel { Orientation = Orientation.Horizontal, Children = { save, off } }, msg } } };
        w.ShowDialog(this);
    }
    static string Capture(Func<int> f) { var o = Console.Out; var w = new StringWriter(); Console.SetOut(w); try { f(); } finally { Console.SetOut(o); } return w.ToString(); }
    void ShowText(string title, string text) =>
        new Window { Title = title, Width = 760, Height = 560, Content = new ScrollViewer { Content = new SelectableTextBlock { Text = text, Margin = new Thickness(16), FontFamily = new FontFamily("monospace") } } }.ShowDialog(this);
    static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
