using OpenTube.Core;
using OpenTube.Client;
using OpenTube.Uno.Controls;
using OpenTube.Uno.Services;

namespace OpenTube.Uno.Pages;

/// <summary>Home: what's live, then videos from what you subscribe to (hot or newest), or what's trending when you
/// don't follow anything yet. Updates as the network brings new blocks.</summary>
public sealed partial class HomePage : Page
{
    bool newest; string shown = "";
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(3) };
    public HomePage()
    {
        this.InitializeComponent();
        tick.Tick += (_, _) => Fill(); tick.Start();
        Unloaded += (_, _) => tick.Stop(); Loaded += (_, _) => { tick.Start(); Fill(); };
    }
    void OnHot(object sender, RoutedEventArgs e) { newest = false; HotButton.IsChecked = true; NewButton.IsChecked = false; shown = ""; Fill(); }
    void OnNew(object sender, RoutedEventArgs e) { newest = true; HotButton.IsChecked = false; NewButton.IsChecked = true; shown = ""; Fill(); }
    void OnExplore(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(ExplorePage));
    void OnRefresh(RefreshContainer sender, RefreshRequestedEventArgs args) { using var d = args.GetDeferral(); shown = ""; Fill(); }

    void ShowProblems()
    {
        var msg = Svc.StartError != null ? "Something went wrong starting up: " + Svc.StartError : null;
        if (Svc.LastCrash != null) msg = (msg != null ? msg + "\n\n" : "") + "OpenTuber closed on an error last time. Please send this to the developer:\n" + Svc.LastCrash[..Math.Min(1500, Svc.LastCrash.Length)];
        Problem.Visibility = msg != null ? Visibility.Visible : Visibility.Collapsed; ProblemText.Text = msg ?? "";
    }
    void Fill()
    {
        ShowProblems();
        var S = Svc.S;
        if (S == null || !S.Ready) return;
        Loading.IsActive = false; Loading.Visibility = Visibility.Collapsed;
        var subs = S.Notes.Subscribed;
        var live = S.LiveEverywhere().Where(x => subs.Count == 0 || subs.Contains(x.Chain)).ToList();
        var rank = S.Rank(null).Items.ToDictionary(i => i.Video, i => i.Score);
        List<(string Chain, string Channel, System.Text.Json.Nodes.JsonObject Block)> vids;
        if (subs.Count == 0)
        {
            Heading.Text = "Trending"; Intro.Visibility = Visibility.Visible; SortBar.Visibility = Visibility.Collapsed;
            vids = S.Rank(null).Items.OrderByDescending(i => i.Score).Take(40)
                .Select(i => (i.Chain, i.Channel, S.Chain(i.Chain)?.Videos.FirstOrDefault(x => (string?)x["hash"] == i.Video)))
                .Where(x => x.Item3 != null).Select(x => (x.Chain, x.Channel, (System.Text.Json.Nodes.JsonObject)x.Item3!.DeepClone())).ToList();
        }
        else
        {
            Heading.Text = "Home"; Intro.Visibility = Visibility.Collapsed; SortBar.Visibility = Visibility.Visible;
            vids = subs.SelectMany(id => S.Chain(id) is { Kind: "channel" } d ? d.Videos.Where(b => S.Engine.PostPaid(b)).Select(b => (id, (string?)d.Profile["name"] ?? "", (System.Text.Json.Nodes.JsonObject)b.DeepClone())) : []).ToList();
            vids = (newest ? vids.OrderByDescending(x => Js.Num(x.Item3["ts"])) : vids.OrderByDescending(x => rank.GetValueOrDefault((string)x.Item3["hash"]!)).ThenByDescending(x => Js.Num(x.Item3["ts"]))).Take(200).ToList();
        }
        // redraw only when something changed (the pictures stay put while you scroll)
        var key = string.Join(",", live.Select(l => (string?)l.Block["hash"])) + "|" + string.Join(",", vids.Select(v => (string?)v.Block["hash"]));
        if (key == shown) return; shown = key;
        LiveSection.Visibility = live.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LiveGrid.Items = live.Select(l => VideoItem.From(l.Chain, l.Name, l.Block, live: true)).ToList();
        Grid.Items = vids.Select(v => VideoItem.From(v.Chain, v.Channel, v.Block)).ToList();
        Empty.Visibility = vids.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Text = subs.Count == 0 ? "Nothing trending yet: the network is still arriving." : "No videos from your subscriptions yet.";
    }
}
