using OpenTube.Client;
using Microsoft.UI.Xaml.Controls.Primitives;
using OpenTube.Uno.Controls;
using OpenTube.Uno.Services;

namespace OpenTube.Uno.Pages;

/// <summary>Explore: trending videos across the network (hot, rising, today, this week, all time), and search.</summary>
public sealed partial class ExplorePage : Page
{
    string view = "hot", query = "", shown = "";
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(4) };
    public ExplorePage()
    {
        this.InitializeComponent();
        foreach (var (k, label) in Feeds.Views)
        {
            var b = new ToggleButton { Content = label, Tag = k, IsChecked = k == view };
            b.Click += (s, _) => { view = (string)((FrameworkElement)s).Tag; foreach (var x in ViewBar.Children.OfType<ToggleButton>()) x.IsChecked = (string)x.Tag == view; shown = ""; Fill(); };
            ViewBar.Children.Add(b);
        }
        tick.Tick += (_, _) => Fill();
        Loaded += (_, _) => { tick.Start(); Fill(); }; Unloaded += (_, _) => tick.Stop();
    }
    void OnSearch(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) { query = sender.Text.Trim(); shown = ""; Fill(); }
    void Fill()
    {
        var S = Svc.S; if (S == null || !S.Ready) return;
        var w = view switch { "rising" => 0, "today" => 1, "week" => 2, "all" => 3, _ => -1 };
        var items = S.Rank(null).Items.Where(i => query.Length == 0 || i.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || i.Channel.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => w < 0 ? i.Score : i.Waves.ElementAtOrDefault(w)).Take(60).ToList();
        var key = view + query + string.Join(",", items.Select(i => i.Video)); if (key == shown) return; shown = key;
        Grid.Items = items.Select(i => S.Chain(i.Chain)?.Videos.FirstOrDefault(x => (string?)x["hash"] == i.Video) is { } b ? VideoItem.From(i.Chain, i.Channel, (System.Text.Json.Nodes.JsonObject)b.DeepClone()) : null).OfType<VideoItem>().ToList();
        Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = query.Length > 0 ? $"Nothing matches “{query}”." : "Nothing ranked yet: the network is still arriving.";
    }
    void OnMap(object sender, RoutedEventArgs e) => App.Open(typeof(PlacesPage));
    void OnMarket(object sender, RoutedEventArgs e) => App.Open(typeof(MarketPage));
    void OnExchange(object sender, RoutedEventArgs e) => App.Open(typeof(ExchangePage));
    void OnOrgs(object sender, RoutedEventArgs e) => App.Open(typeof(OrgsPage));
}
