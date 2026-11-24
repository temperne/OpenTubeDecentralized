using Microsoft.UI.Xaml.Automation;
using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Uno.Controls;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Pages;

/// <summary>A page that redraws when its data changed (not on every tick, so what you're typing stays).</summary>
public abstract partial class DataPage : Page
{
    protected readonly StackPanel Col; protected readonly TextBlock Status = Ui.Status();
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(3) };
    string shown = ""; readonly string title;
    protected DataPage(string title, double width = 900)
    {
        this.title = title;
        Background = Application.Current.Resources["BackgroundBrush"] as Brush;
        var (scroll, c) = Ui.Page(width); Col = c; Content = scroll;
        tick.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { tick.Start(); Refresh(); }; Unloaded += (_, _) => tick.Stop();
    }
    protected override void OnNavigatedTo(NavigationEventArgs e) { Param(e.Parameter); if (App.IsOpened(this) && Content is ScrollViewer sv) Content = Ui.WithBack(title, sv); }
    protected virtual void Param(object? p) { }
    /// <summary>What the page shows, as a key: it redraws when this changes.</summary>
    protected abstract string Key();
    protected abstract void Build();
    /// <summary>Redraw now (after an action).</summary>
    protected void Redraw() { shown = ""; Refresh(); }
    void Refresh()
    {
        var S = Svc.S; if (S == null || !S.Ready) { if (Col.Children.Count == 0) Col.Children.Add(Sub("Starting…")); return; }
        string k; try { k = Key(); } catch { k = Guid.NewGuid().ToString(); }
        if (k == shown) return; shown = k;
        Col.Children.Clear();
        try { Build(); } catch (Exception e) { Col.Children.Add(Sub("Something went wrong showing this: " + e.Message)); Svc.Report(e); }
    }
}

/// <summary>Posts: the public feed (a chain per month that everyone keeps), recent or popular, with replies.</summary>
public sealed partial class PostsPage : DataPage
{
    string month = Social.Month(DateTimeOffset.UtcNow); string? post; bool popular;
    public PostsPage() : base("Posts") { }
    protected override void Param(object? p) { if (p is string s) { var a = s.Split('/'); month = a[0]; post = a.Length > 1 ? a[1] : null; } }
    protected override string Key() => post != null ? $"{month}/{post}/{Svc.S.Social.Replies(month, post).Count}/{Svc.S.Social.PostOf(month, post)?.Likes}" : $"{month}/{popular}/{string.Join(",", Svc.S.Social.Posts(month, popular).Select(p => $"{(string?)p.Block["hash"]}{p.Likes}{p.Replies}"))}";
    protected override void Build()
    {
        var S = Svc.S;
        if (post != null)
        {
            var p = S.Social.PostOf(month, post);
            Col.Children.Add(Btn("← Posts " + month, () => { post = null; Redraw(); }));
            if (p == null) { Col.Children.Add(Sub("Fetching this post from the network…")); return; }
            var items = new List<UIElement> { PostView(p, true) };
            foreach (var r in S.Social.Replies(month, post)) items.Add(new Border { Padding = new Thickness(14, 0, 0, 0), BorderBrush = Ui.Muted, BorderThickness = new Thickness(2, 0, 0, 0), Child = PostView(r, true) });
            var reply = Input("Reply…", max: 1000);
            items.Add(Row(reply, Btn("Reply", () => _ = Status.Do("Doing anti-spam work…", async () => { await S.Social.PostTo(month, reply.Text, post, null); reply.Text = ""; Redraw(); }, "Replied."), primary: true)));
            items.Add(Status);
            Col.Children.Add(Card([.. items])); return;
        }
        var thisMonth = month == Social.Month(DateTimeOffset.UtcNow);
        Col.Children.Add(Row(H("Posts", 22), Sub(month), Btn("older ›", () => { month = Social.PrevMonth(month); Redraw(); }), !thisMonth ? Btn("this month", () => { month = Social.Month(DateTimeOffset.UtcNow); Redraw(); }) : null!));
        if (thisMonth)
        {
            var text = new TextBox { PlaceholderText = "What’s happening?", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxLength = 1000 };
            AutomationProperties.SetAutomationId(text, "posts-text");
            Col.Children.Add(Card(text, Row(Btn("Post", () => { if (string.IsNullOrWhiteSpace(text.Text)) return; _ = Status.Do("Doing anti-spam work…", async () => { await S.Social.PostTo(month, text.Text, null, null); text.Text = ""; Redraw(); }, "Posted."); }, primary: true, id: "posts-send"), Sub("Public: anyone can read it, forever.")), Status));
        }
        var (bar, _) = Segments([("recent", "Recent"), ("popular", "Popular")], popular ? "popular" : "recent", k => { popular = k == "popular"; Redraw(); });
        Col.Children.Add(bar);
        var posts = S.Social.Posts(month, popular);
        if (posts.Count == 0) Col.Children.Add(Sub("No posts yet this month (or still loading from the network)."));
        foreach (var p in posts.Take(150)) Col.Children.Add(Card(PostView(p, false)));
    }
    UIElement PostView(Social.Post p, bool full)
    {
        var S = Svc.S; var hash = (string)p.Block["hash"]!;
        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(Row(Text(p.Name), Sub(Svc.Ago(p.Ts))));
        sp.Children.Add(Text(p.Text));
        var like = Btn($"👍 {p.Likes}", () => _ = Status.Do("Liking (pays the author)…", async () => { await S.Social.Like(p.Chain, p.Block); Redraw(); })); like.IsEnabled = !p.Mine;
        var acts = new WrapRow(); acts.Children.Add(like);
        if (!full) acts.Children.Add(Btn($"{p.Replies} repl{(p.Replies == 1 ? "y" : "ies")}", () => { post = hash; Redraw(); }));
        if (p.Mine) acts.Children.Add(Btn("Delete", () => _ = Status.Do("Deleting…", async () => { await S.Social.Delete(p.Chain, hash); Redraw(); })));
        sp.Children.Add(acts);
        return sp;
    }
}

/// <summary>Friends: posts sealed to the friends you choose (nobody else can read them), and your friends list.</summary>
public sealed partial class FriendsPage : DataPage
{
    List<Social.Friend>? friends; List<Social.Sealed>? feed; Dictionary<string, string> names = [];
    public FriendsPage() : base("Friends") { }
    protected override string Key()
    {
        _ = Load();
        return $"{Svc.S.Social.MyPage}|{friends?.Count}|{string.Join(",", friends?.Select(f => f.Key + f.Level + f.HasPage) ?? [])}|{string.Join(",", feed?.Select(x => (string?)x.Block["hash"] + x.Replies.Count) ?? [])}";
    }
    bool loading;
    async Task Load()
    {
        if (loading) return; loading = true;
        try { friends = await Svc.S.Social.Friends(); names = friends.ToDictionary(f => f.Key, f => f.Name); if (Svc.S.Social.MyPage != null) feed = await Svc.S.Social.Feed(); }
        catch (Exception e) { Svc.Report(e); }
        loading = false;
    }
    string Name(string k) => k == Svc.S.Me.pub ? "You" : names.TryGetValue(k, out var n) ? n : Svc.S.NameOf(k);
    protected override void Build()
    {
        var S = Svc.S;
        Col.Children.Add(H("Friends", 22));
        if (S.Social.MyPage == null)
            Col.Children.Add(Card(Text("Your friends page is a chain only your friends can read: every post is encrypted to the friends you choose, and they keep a copy for you. Start it once:"),
                Btn("Start my friends page", () => _ = Status.Do("Registering your page (proof of work on the main chain)…", async () => { await S.Social.StartPage(); Redraw(); }), primary: true), Status));
        else
        {
            var text = new TextBox { PlaceholderText = "Share with your friends…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80 };
            var audience = Pick([("acquaintances", "All friends"), ("friends", "Friends and close friends"), ("close", "Close friends only")]);
            Col.Children.Add(Card(text, audience, Btn("Post to friends", () => { if (string.IsNullOrWhiteSpace(text.Text)) return; _ = Status.Do("Encrypting…", async () => { await S.Social.PostToFriends(text.Text, audience.Value(), null); text.Text = ""; await Load(); Redraw(); }, "Shared."); }, primary: true), Status));
            if (feed == null) Col.Children.Add(Sub("Opening your friends’ pages…"));
            else if (feed.Count == 0) Col.Children.Add(Sub("Nothing yet. Posts from you and your friends show up here."));
            else foreach (var x in feed.Take(100))
            {
                var items = new List<UIElement> { Row(Text(Name(x.Author)), Sub(Svc.Ago(x.Ts) + (x.Content?["to"] is System.Text.Json.Nodes.JsonArray to ? $" · shared with {to.Count - 1} friend(s)" : ""))) };
                if (x.Content == null) items.Add(Sub("🔒 A post that isn’t shared with you"));
                else
                {
                    items.Add(Text((string?)x.Content["text"] ?? ""));
                    foreach (var r in x.Replies) items.Add(new Border { Padding = new Thickness(14, 2, 0, 2), BorderBrush = Ui.Muted, BorderThickness = new Thickness(2, 0, 0, 0), Child = new StackPanel { Children = { Row(Text(Name(r.Author), 13), Sub(Svc.Ago(r.Ts))), Text(r.Content == null ? "🔒 not shared with you" : (string?)r.Content["text"] ?? "") } } });
                    var reply = Input("Reply (sealed to the same friends)…", max: 1000); var xx = x;
                    items.Add(Row(reply, Btn("Reply", () => _ = Status.Do("Encrypting…", async () => { await S.Social.ReplyToFriends(xx.Page, xx.Block, xx.Content!, reply.Text); reply.Text = ""; await Load(); Redraw(); }))));
                }
                Col.Children.Add(Card([.. items]));
            }
        }
        var list = new StackPanel { Spacing = 6 };
        foreach (var f in friends ?? [])
        {
            var ff = f; var level = Pick(Social.Levels.Select(l => (l.Key, l.Label)), f.Level); level.HorizontalAlignment = HorizontalAlignment.Left; level.MinWidth = 180;
            level.SelectionChanged += (_, _) => _ = S.Social.SetLevel(ff.Key, level.Value());
            list.Children.Add(Row(Text(f.Name), f.HasPage ? null! : Sub("hasn’t started a page yet"), level, Btn("✉", () => App.Open(typeof(MessagesPage), ff.Key)), Btn("Remove", () => _ = Status.Do("Removing…", async () => { await S.Social.RemoveFriend(ff.Key); await Load(); Redraw(); }))));
        }
        if (friends?.Count == 0) list.Children.Add(Sub("No friends added yet."));
        var addKey = Input("A friend’s user ID", max: 43); var addName = Input("Their name (for you)", max: 60);
        Col.Children.Add(Card(H("Your friends"), Sub("Only the friends you add can read what you share with them, once they add you too. Your user ID (give it to your friends):"), Mono(S.Me.pub), list,
            addKey, addName, Btn("Add friend", () => _ = Status.Do("Adding…", async () => { await S.Social.AddFriend(addKey.Text.Trim(), addName.Text.Trim()); addKey.Text = addName.Text = ""; await Load(); Redraw(); }, "Added."), id: "friends-add")));
    }
}

/// <summary>Messages: encrypted end to end; on the network nobody can see who wrote to whom.</summary>
public sealed partial class MessagesPage : DataPage
{
    string? who; List<Mail.Message>? all;
    public MessagesPage() : base("Messages") { }
    protected override void Param(object? p) => who = p as string;
    bool published, loading;
    protected override string Key()
    {
        if (!published) { published = true; _ = Svc.S.Mail.Publish(); }
        _ = Load(); return $"{who}|{all?.Count}|{all?.LastOrDefault()?.At}";
    }
    async Task Load() { if (loading) return; loading = true; try { all = await Svc.S.Mail.All(); } catch (Exception e) { Svc.Report(e); } loading = false; }
    protected override void Build()
    {
        var S = Svc.S;
        if (who == null)
        {
            Col.Children.Add(Row(H("Messages", 22), Btn("Friends", () => App.Open(typeof(FriendsPage)))));
            var to = Input("Their user ID, @@name, or a channel’s @name", max: 80);
            Col.Children.Add(Card(to, Btn("New message", () =>
            {
                var k = to.Text.Trim(); if (k.Length == 0) return;
                var r = S.Recipient(k.StartsWith('@') && !k.StartsWith("@@") && S.NameHolder(k.ToLowerInvariant()) == null ? "@" + k : k);
                if (r == null) { Status.Say("No one is called that. Paste their user ID."); return; }
                who = S.Engine.Read(e => e.Store.Ledger.Resolve(r)); Redraw();
            }, primary: true), Sub("Messages are encrypted for the other person: on the network nobody can see who wrote to whom. Your messaging key stays on this device."), Status));
            if (all == null) Col.Children.Add(Sub("Reading your messages…"));
            else if (all.Count == 0) Col.Children.Add(Sub("No messages yet. Open someone’s channel and tap Message, or start one above."));
            else foreach (var g in all.GroupBy(m => m.Other).OrderByDescending(g => g.Max(m => m.At)))
            {
                var last = g.MaxBy(m => m.At)!; var k = g.Key; var t = last.Text.Length > 80 ? last.Text[..80] + "…" : last.Text;
                var b = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Ui.Surface, Padding = new Thickness(14) };
                b.Content = new StackPanel { Children = { Row(Text(S.NameOf(k)), Sub(Svc.Ago(last.At))), Sub((last.Incoming ? "" : "You: ") + t) } };
                b.Click += (_, _) => { who = k; Redraw(); };
                Col.Children.Add(b);
            }
            return;
        }
        var other = S.Engine.Read(e => e.Store.Ledger.Resolve(who));
        Col.Children.Add(Row(Btn("← Messages", () => { who = null; Redraw(); }), H(S.NameOf(who), 20)));
        var thread = new StackPanel { Spacing = 8 };
        foreach (var m in (all ?? []).Where(m => m.Other == other))
        {
            var bubble = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 8, 12, 8), MaxWidth = 520, Background = m.Incoming ? Ui.Surface : (Brush)Application.Current.Resources["PrimaryContainerBrush"], HorizontalAlignment = m.Incoming ? HorizontalAlignment.Left : HorizontalAlignment.Right };
            bubble.Child = new StackPanel { Children = { Text(m.Text), Sub(Svc.Ago(m.At)) } };
            thread.Children.Add(bubble);
        }
        if (!(all ?? []).Any(m => m.Other == other)) thread.Children.Add(Sub("No messages with them yet."));
        Col.Children.Add(thread);
        var can = S.Mail.CanReceive(who);
        if (!can) Col.Children.Add(Sub("They haven’t published a messaging key yet (they need to open Messages once, on the site or in the app). You can write once they have."));
        var text = new TextBox { PlaceholderText = "Write a message…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
        var send = Btn("Send", () => { if (string.IsNullOrWhiteSpace(text.Text)) return; _ = Status.Do("Sending (encrypting, then a little proof of work)…", async () => { await S.Mail.Send(who!, text.Text); text.Text = ""; await Load(); Redraw(); }, "Sent."); }, primary: true);
        send.IsEnabled = can;
        Col.Children.Add(Card(text, send, Status));
    }
}

/// <summary>Notifications, and what you're told about (by kind, and per channel, group, list and organization).</summary>
public sealed partial class NotificationsPage : DataPage
{
    bool settings;
    public NotificationsPage() : base("Notifications") { }
    protected override void Param(object? p) => settings = p as string == "settings";
    protected override string Key() => $"{settings}|{Svc.S.Notes.All.Count}|{Svc.S.Notes.Unread}|{string.Join(",", Svc.S.Notes.Objects)}";
    protected override void Build()
    {
        var S = Svc.S;
        var (bar, _) = Segments([("all", "Notifications"), ("settings", "Settings")], settings ? "settings" : "all", k => { settings = k == "settings"; Redraw(); });
        Col.Children.Add(bar);
        if (settings)
        {
            var kinds = new StackPanel { Spacing = 4 };
            foreach (var (key, label, _) in Notifications.Types)
            {
                var k = key; var cb = new CheckBox { Content = label, IsChecked = S.Notes.TypeOn(k) };
                cb.Checked += (_, _) => _ = S.Notes.SetType(k, true); cb.Unchecked += (_, _) => _ = S.Notes.SetType(k, false); kinds.Children.Add(cb);
            }
            Col.Children.Add(Card(H("Kinds"), Sub("What you’re told about, in general. Below, each thing you follow can override this."), kinds));
            var objs = S.Notes.Objects.Where(id => S.Genesis(id) != null).ToList(); var per = new StackPanel { Spacing = 10 };
            if (objs.Count == 0) per.Children.Add(Sub("You don’t follow anything yet."));
            foreach (var id in objs)
            {
                var i = id; var mode = Pick([("default", "Default"), ("all", "All"), ("none", "None")], S.Notes.ObjectMode(i)); mode.HorizontalAlignment = HorizontalAlignment.Left; mode.MinWidth = 140;
                mode.SelectionChanged += (_, _) => _ = S.Notes.SetObject(i, mode.Value());
                var over = new StackPanel { Spacing = 2, Visibility = Visibility.Collapsed };
                foreach (var (key, label, _) in Notifications.Types)
                {
                    var k = key; var cur = S.Notes.TypeObj(i, k);
                    var pick = Pick([("", "as above"), ("on", "on"), ("off", "off")], cur == null ? "" : cur.Value ? "on" : "off"); pick.MinWidth = 130; pick.HorizontalAlignment = HorizontalAlignment.Left;
                    pick.SelectionChanged += (_, _) => _ = S.Notes.SetTypeObj(i, k, pick.Value() switch { "on" => true, "off" => false, _ => null });
                    over.Children.Add(Row(Sub(label), pick));
                }
                per.Children.Add(new StackPanel { Spacing = 4, Children = { Row(Text((string?)S.Genesis(i)?["payload"]?["name"] ?? i[..8]), mode, Btn("Kinds…", () => over.Visibility = over.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible)), over } });
            }
            Col.Children.Add(Card(H("Per channel, group, list and organization"), per));
            return;
        }
        Col.Children.Add(Row(Btn("Mark all read", () => { S.Notes.MarkAllRead(); Redraw(); }), Btn("Clear all", () => { S.Notes.ClearAll(); Redraw(); })));
        var all = S.Notes.All;
        if (all.Count == 0) Col.Children.Add(Sub("Nothing new. You’ll be told about the things you follow (Settings to choose what)."));
        foreach (var n in all.Take(200))
        {
            var nn = n;
            var b = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = n.Read ? Ui.Surface : (Brush)Application.Current.Resources["PrimaryContainerBrush"], Padding = new Thickness(14) };
            b.Content = new StackPanel { Spacing = 2, Children = { Row(Sub(Notifications.LabelOf(n.Type)), Sub(Svc.Ago(n.Ts))), Text(n.Text) } };
            b.Click += (_, _) => Router.Go(nn.Href);
            Col.Children.Add(b);
        }
    }
}
