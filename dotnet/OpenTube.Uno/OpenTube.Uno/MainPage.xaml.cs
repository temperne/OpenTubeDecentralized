using OpenTube.Uno.Pages;
using OpenTube.Uno.Services;
using OpenTube.Client;

namespace OpenTube.Uno;

/// <summary>The app frame: top bar, the selected tab's page, the tab bar.</summary>
public sealed partial class MainPage : Page
{
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(2) };
    public MainPage()
    {
        this.InitializeComponent();
        TabFrame.Navigate(typeof(HomePage));
        tick.Tick += (_, _) => { UpdateStatus(); TestStart(); }; tick.Start(); UpdateStatus();
    }
    // (tests and screenshots: OPENTUBE_START=first-video opens the top trending video once the network has it)
    string? testStart = Svc.StartOption;
    void TestStart()
    {
        if (testStart is { } ts && ts.StartsWith("you") && Svc.S is { Ready: true }) { testStart = null; App.Open(typeof(YouPage), ts.Contains(':') ? ts[(ts.IndexOf(':') + 1)..] : "profile"); return; }
        if (testStart is { } us && us.StartsWith("upload") && Svc.S is { Ready: true }) { testStart = null; App.Open(typeof(UploadPage), us.Contains(':') ? us[(us.IndexOf(':') + 1)..] : null); return; }
        if (testStart == "golive-auto" && Svc.S is { Ready: true }) { testStart = null; App.Open(typeof(GoLivePage)); return; }
        if (testStart is { } rs && rs.StartsWith("go:") && Svc.S is { Ready: true }) { testStart = null; Router.Go(rs[3..]); return; }
        if (testStart is { } cs && cs.StartsWith("channel:") && Svc.S is { Ready: true }) { testStart = null; ChannelPage.Open(cs[8..]); return; }
        if (testStart is not ("first-video" or "first-channel") || Svc.S is not { Ready: true } s) return;
        var top = s.Rank(null).Items.OrderByDescending(i => i.Score).FirstOrDefault(i => s.Chain(i.Chain)?.Videos.Any(v => (string?)v["hash"] == i.Video) == true);
        if (top == null) return;
        if (testStart == "first-channel") { testStart = null; ChannelPage.Open(top.Chain); return; }
        testStart = null; App.Open(typeof(WatchPage), new WatchArgs(top.Chain, top.Video));
    }
    void OnTab(global::Uno.Toolkit.UI.TabBar sender, global::Uno.Toolkit.UI.TabBarSelectionChangedEventArgs e)
    {
        var tag = (e.NewItem as FrameworkElement)?.Tag as string;
        var page = tag switch { "explore" => typeof(ExplorePage), "home" => typeof(HomePage), "you" => typeof(YouPage), "posts" => typeof(PostsPage), "messages" => typeof(MessagesPage), _ => typeof(ComingPage) };
        TabFrame.Navigate(page, tag);
    }
    void UpdateStatus()
    {
        var s = Svc.S; if (s == null) return;
        var peers = s.Peers;
        NetDot.Fill = new SolidColorBrush(peers > 0 ? Microsoft.UI.Colors.LimeGreen : Microsoft.UI.Colors.Gray);
        NetText.Text = !s.Ready ? "Starting…" : peers > 0 ? $"Online · {peers} node{(peers == 1 ? "" : "s")}" : "Connecting…";
        var unread = s.Ready ? s.Notes.Unread : 0;
        BellBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed; BellCount.Text = unread > 99 ? "99+" : unread.ToString();
    }
    void OnNetwork(object sender, RoutedEventArgs e) => App.Open(typeof(Pages.NetworkPage));
    void OnUpload(object sender, RoutedEventArgs e) => App.Open(typeof(UploadPage));
    void OnGoLive(object sender, RoutedEventArgs e) => App.Open(typeof(Pages.GoLivePage));
    void OnNotifications(object sender, RoutedEventArgs e) => App.Open(typeof(NotificationsPage));
}
