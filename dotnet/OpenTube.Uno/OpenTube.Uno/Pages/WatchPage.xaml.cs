using System.Text.Json.Nodes;
using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Uno.Controls;
using OpenTube.Uno.Services;

namespace OpenTube.Uno.Pages;

public sealed record WatchArgs(string Chain, string Hash);
public sealed record CommentItem(string Name, string When, string Text);

/// <summary>Watching a video: the player streams it from the C# BitTorrent client; likes, comments and subscribing go
/// on the chains through the same client code as the website.</summary>
public sealed partial class WatchPage : Page
{
    string chain = "", hash = ""; bool started; string shownComments = "";
#if __WASM__
    WebVideo? web;
#endif
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(2) };
    public WatchPage()
    {
        this.InitializeComponent();
        tick.Tick += (_, _) => { Fill(); if (started) LogPlayer(); };
        SizeChanged += (_, e) => { var wide = e.NewSize.Width >= 1000; Wide(wide); SizePlayer(Math.Min(e.NewSize.Width, 1500), e.NewSize.Height, wide); };
        Unloaded += (_, _) => { tick.Stop(); try { Player.MediaPlayer?.Pause(); } catch { } };
    }
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is WatchArgs a) { chain = a.Chain; hash = a.Hash; }
        tick.Start(); Fill();
    }
    void OnBack(object sender, RoutedEventArgs e) => App.Back();
    void OnChannel(object sender, RoutedEventArgs e) => ChannelPage.Open(chain);
    /// <summary>The player is 16:9 of the column it's in (at most 70% of the window's height).</summary>
    void SizePlayer(double pageWidth, double pageHeight, bool wide)
    {
        var w = pageWidth; var h = Math.Min(w * 9 / 16, pageHeight * (wide ? 0.6 : 0.45));
        if (h > 0 && Math.Abs(PlayerBox.Height - h) > 1) PlayerBox.Height = h;
    }
    /// <summary>Wide screens: the channel's other videos in a column beside the video.</summary>
    void Wide(bool wide)
    {
        // (the panel stays in the one grid: beside the video on a wide screen, below it on a phone)
        SideCol.Width = wide ? new GridLength(380) : new GridLength(0);
        Grid.SetColumn(More, wide ? 1 : 0); Grid.SetRow(More, wide ? 0 : 1);
    }

    void Fill()
    {
        var S = Svc.S; if (S == null || !S.Ready) { Status.Text = "Starting…"; return; }
        var d = S.Chain(chain); var v = d?.Videos.FirstOrDefault(x => (string?)x["hash"] == hash);
        if (d == null || v == null) { Status.Text = "Loading this video from the network…"; return; }
        var p = v["payload"]!; var name = (string?)d.Profile["name"] ?? "";
        TopTitle.Text = VideoTitle.Text = (string?)p["title"] ?? "Untitled";
        ChannelName.Text = name;
        Meta.Text = $"{Svc.Views(S.Views(hash))} · {Svc.Ago(Js.Num(v["ts"]) ?? 0)} · {Svc.Bytes(Js.Num(p["size"]) ?? 0)}";
        Description.Text = (string?)p["desc"] is { Length: > 0 } desc ? desc : "No description.";
        var mine = S.IsMine(d.Owner); var sub = S.Notes.Subscription(chain);
        SubButton.Visibility = mine ? Visibility.Collapsed : Visibility.Visible; SubButton.Content = sub != null ? "Subscribed" : "Subscribe";
        var votes = S.Votes(d, hash);
        LikeButton.Content = IconText("\uE8E1", votes.Up.ToString()); DislikeButton.Content = IconText("\uE8E0", votes.Down.ToString());
        var comments = d.CommentsFor(hash).Where(c => !d.Hidden.Contains((string)c["hash"]!)).OrderByDescending(c => Js.Num(c["ts"]) ?? 0).ToList();
        CommentsHeading.Text = comments.Count == 0 ? "Comments" : $"Comments ({comments.Count})";
        var canComment = mine || d.CanComment(S.Me.pub, hash); CommentBox.Visibility = canComment ? Visibility.Visible : Visibility.Collapsed;
        var key = string.Join(",", comments.Select(c => (string?)c["hash"]));
        if (key != shownComments)
        {
            shownComments = key;
            Comments.ItemsSource = comments.Select(c => new CommentItem(S.AuthorOf((string)c["author"]!, d, (string?)c["payload"]?["name"] ?? "").Name, Svc.Ago(Js.Num(c["ts"]) ?? 0), (string?)c["payload"]?["text"] ?? "")).ToList();
        }
        if (!started)
        {
            started = true;
            var more = d.Videos.Where(x => (string?)x["hash"] != hash).OrderByDescending(x => Js.Num(x["ts"]) ?? 0).Take(12).Select(x => VideoItem.From(chain, name, (JsonObject)x.DeepClone())).ToList();
            More.Visibility = more.Count > 0 ? Visibility.Visible : Visibility.Collapsed;   // (nothing else from the channel: no empty section)
            if (more.Count > 0) MoreGrid.Items = more;
            _ = StartPlaying((string)p["infohash"]!, (string?)p["file"] ?? "video.mp4", Js.Num(p["size"]) ?? 0);
        }
    }
    /// <summary>An icon (from the app's icon font, so it shows on every platform) and a label.</summary>
    static StackPanel IconText(string glyph, string text)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        p.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 }); p.Children.Add(new TextBlock { Text = text });
        return p;
    }
    async Task StartPlaying(string ih, string file, double size)
    {
        var uri = await Svc.Video.Play(ih, file, size, st => { Console.WriteLine("[watch] " + st); DispatcherQueue.TryEnqueue(() => Status.Text = st); });
        if (uri == null) { NoVideo.Visibility = Visibility.Visible; NoVideo.Text = Status.Text; return; }
#if __WASM__
        // in the browser: the browser's own video element over the player's spot (see WebVideo)
        if (web == null) { web = new WebVideo(); PlayerBox.Children.Remove(Player); PlayerBox.Children.Insert(0, web); }
        web.Play(uri);
#else
        Player.Source = Windows.Media.Core.MediaSource.CreateFromUri(uri);
#endif
        Console.WriteLine("[player] source " + uri);
    }
    void LogPlayer()
    {   // (the player's state and position, for tests and diagnostics)
#if __WASM__
        if (web != null) { var (st, pos, dur) = web.Status(); Console.WriteLine($"[player] {st} at {pos:0.0} s of {dur:0.0} s"); }
#else
        try { var ps = Player.MediaPlayer?.PlaybackSession; if (ps != null) Console.WriteLine($"[player] {ps.PlaybackState} at {ps.Position.TotalSeconds:0.0} s of {ps.NaturalDuration.TotalSeconds:0.0} s"); } catch { }
#endif
    }

    async Task Act(Func<Task> f, string doing)
    {
        ActionMsg.Visibility = Visibility.Visible; ActionMsg.Text = doing;
        try { await f(); ActionMsg.Text = "Done."; Fill(); } catch (Exception e) { ActionMsg.Text = e.Message; }
    }
    void OnLike(object sender, RoutedEventArgs e) => _ = Act(() => Svc.S.Vote(chain, hash, 1, Svc.S.Chain(chain)!.Owner!), "Signing and paying for your like…");
    void OnDislike(object sender, RoutedEventArgs e) => _ = Act(() => Svc.S.Vote(chain, hash, -1, Svc.S.Chain(chain)!.Owner!), "Signing your dislike…");
    void OnSubscribe(object sender, RoutedEventArgs e) => _ = Act(async () => { await Svc.S.Notes.Subscribe(chain, Svc.S.Notes.Subscription(chain) == null); }, "Saving…");
    void OnCopyLink(object sender, RoutedEventArgs e)
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage(); dp.SetText($"{Svc.Site}/app/v/{chain}/{hash}");
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp); ActionMsg.Visibility = Visibility.Visible; ActionMsg.Text = "Link copied.";
    }
    void OnCommentKey(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e) { if (e.Key == Windows.System.VirtualKey.Enter) OnComment(sender, e); }
    void OnComment(object sender, RoutedEventArgs e)
    {
        var text = CommentText.Text.Trim(); if (text.Length == 0) return;
        _ = Act(async () => { await Svc.S.Comment(chain, hash, text); CommentText.Text = ""; }, "Posting your comment…");
    }
}
