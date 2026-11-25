using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Automation;
using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Uno.Controls;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Pages;

/// <summary>
/// A channel, list or group: its header (picture, name, @name, subscribe and join, notifications, friend, tip) and
/// its tabs: videos and posts for a channel; discussion and members for a group; feed and channels for a list; the
/// ranking of a ranking list; about; and manage for your own (profile, picture, links, policies, people, @name).
/// </summary>
public sealed partial class ChannelPage : Page
{
    string id = "", tab = "";
    readonly StackPanel col, body; readonly TextBlock status = Status();
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(3) };
    string shown = "";
    public ChannelPage()
    {
        Background = Application.Current.Resources["BackgroundBrush"] as Brush;
        var (scroll, c) = Ui.Page(1200); col = c; Content = Ui.WithBack("Channel", scroll);
        body = new StackPanel { Spacing = 14 };
        tick.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { tick.Start(); Refresh(); }; Unloaded += (_, _) => tick.Stop();
    }
    protected override void OnNavigatedTo(NavigationEventArgs e) { if (e.Parameter is string s) { var p = s.Split('/'); id = p[0]; tab = p.Length > 1 ? p[1] : ""; } }
    public static void Open(string chain, string tab = "") => App.Open(typeof(ChannelPage), tab.Length > 0 ? chain + "/" + tab : chain);

    static List<string> Tabs(Derived d, bool mine) => d.Kind switch
    {
        "channel" => mine ? ["videos", "posts", "about", "advertise", "manage"] : ["videos", "posts", "about"],
        "group" => [.. new[] { "discussion", "members", "about" }, .. mine ? new[] { "manage" } : []],
        _ when d.Profile["ranking"] != null => [.. new[] { "ranking", "about" }, .. mine ? new[] { "manage" } : []],
        "list" => [.. new[] { "feed", "channels", "about" }, .. mine ? new[] { "manage" } : []],
        "org" => [.. new[] { "about", "flags", "reports", "rules", "filings", "cases", "courts", "people" }, .. mine ? new[] { "manage" } : []],
        "case" => [.. new[] { "docket", "about" }, .. mine ? new[] { "manage" } : []],
        _ => [.. new[] { "about" }, .. mine ? new[] { "manage" } : []],
    };
    static string TabName(string t) => t switch { "feed" => "Feed", "discussion" => "Discussion", _ => char.ToUpperInvariant(t[0]) + t[1..] };

    /// <summary>Redraw when the channel changed (new blocks), not on every tick (keeps what you're typing).</summary>
    void Refresh()
    {
        var S = Svc.S; if (S == null || !S.Ready) return;
        var d = S.Chain(id);
        var key = d == null ? "loading" : $"{d.BlockCount}|{S.Notes.Subscription(id)}|{tab}|{S.NameOfChain(id)}";
        if (key == shown) return; shown = key; Build(d);
    }
    void Build(Derived? d)
    {
        var S = Svc.S; col.Children.Clear();
        if (d == null) { col.Children.Add(H((string?)S.Genesis(id)?["payload"]?["name"] ?? "Channel", 22)); col.Children.Add(Sub("Loading this channel from the network…")); S.Engine.Read(e => e.OpenChain(id)); return; }
        var mine = S.IsMine(d.Owner); var name = (string?)d.Profile["name"] ?? ""; var handle = S.NameOfChain(id);
        var who = S.OwnerIdentity(id); var registered = S.Engine.Read(e => e.Store.Directory.ContainsKey(id));
        var tabs = Tabs(d, mine); if (!tabs.Contains(tab)) tab = tabs[0];
        // ---- header
        var pic = new Border { Width = 72, Height = 72, CornerRadius = new CornerRadius(36), Background = Ui.Primary };
        if ((string?)d.Profile["avatar"] is { Length: > 30 } av) { var img = new Image { Stretch = Stretch.UniformToFill }; pic.Child = img; _ = Svc.Thumb(av).ContinueWith(t => DispatcherQueue.TryEnqueue(() => img.Source = t.Result)); }
        else pic.Child = new TextBlock { Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?", FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var info = new StackPanel { Spacing = 4 };
        info.Children.Add(H(name, 22));
        if (handle != null) info.Children.Add(Row(Text(handle), Btn("Copy link", () => { Copy($"{Svc.Site}/{handle}"); status.Say("Link copied."); })));
        info.Children.Add(Sub((d.Kind == "channel" ? $"{d.Videos.Count} video{(d.Videos.Count == 1 ? "" : "s")}" : d.Kind switch { "group" => "group", "list" => "list", "org" => "organization", _ => d.Kind })
            + (d.LiveNow.Count > 0 ? " · LIVE" : "") + (!registered ? " · registration pending" : "")));
        var head = new Grid { ColumnSpacing = 14 }; head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition());
        head.Children.Add(pic); Grid.SetColumn(info, 1); head.Children.Add(info);
        var actions = new WrapRow();
        var sub = S.Notes.Subscription(id);
        actions.Children.Add(sub != null ? Btn("Subscribed ✓", () => _ = status.Do("Saving…", () => S.Notes.Subscribe(id, false), "Unsubscribed.")) : Btn("Subscribe", () => _ = status.Do("Saving…", () => S.Notes.Subscribe(id, true), "Subscribed."), primary: true, id: "chan-subscribe"));
        if (d.Kind == "channel" && !mine)
        {
            var hosts = S.Publish.StorageNodes();
            if (sub == "join") actions.Children.Add(Btn("Joined ✓", () => _ = status.Do("Leaving…", () => S.Join(id, false), "Left: your computers stop hosting it (you stay subscribed).")));
            else { var j = Btn("Join (host its videos)", () => _ = status.Do("Joining…", () => S.Join(id, true), "Joined: your computers host this channel’s videos.")); j.IsEnabled = hosts.Count > 0; actions.Children.Add(j); }
        }
        if (sub != null)
        {
            var mode = Pick([("default", "Notifications: default"), ("all", "Notifications: all"), ("none", "Notifications: none")], S.Notes.ObjectMode(id)); mode.HorizontalAlignment = HorizontalAlignment.Left; mode.MinWidth = 220;
            mode.SelectionChanged += (_, _) => _ = S.Notes.SetObject(id, mode.Value()); actions.Children.Add(mode);
        }
        if (!mine && who.Length > 0 && who != S.Me.pub)
        {
            actions.Children.Add(Btn("＋ Friend", () => _ = status.Do("Adding…", () => S.Social.AddFriend(who, S.NameOf(who)), "Added as a friend.")));
            actions.Children.Add(Btn("Tip", () => ShowTip(d.Owner!, name)));
        }
        col.Children.Add(Card(head, Sub(who.Length > 0 && !mine ? "by " + S.NameOf(who) : ""), actions, status));
        if (mine && S.Keys.Channels().Any(c => c.Chain == id && !c.Exported))
            col.Children.Add(Card(Text($"⚠ This {d.Kind}’s private key exists only on this device. If you lose the device, you lose the {d.Kind}."), Btn("Back up keys", () => App.Open(typeof(YouPage), "keys"), primary: true)));
        var (bar, _) = Segments(tabs.Select(t => (t, TabName(t))), tab, t => { tab = t; shown = ""; Refresh(); });
        col.Children.Add(bar); col.Children.Add(body);
        body.Children.Clear();
        switch (tab)
        {
            case "videos": Videos(d, mine, name); break;
            case "posts": case "discussion": Posts(d, mine, name); break;
            case "members": Members(d, name); break;
            case "feed": Feed(d, mine); break;
            case "channels": ListChannels(d, mine); break;
            case "ranking": Ranking(); break;
            case "manage": Manage(d); break;
            case "flags": case "reports": case "rules": case "filings": case "cases": case "courts": case "people": OrgTab(tab); break;
            case "docket": Docket(d); break;
            case "advertise": Advertise(d); break;
            default: About(d, name, who, registered); break;
        }
    }
    static void Copy(string text) { var dp = new Windows.ApplicationModel.DataTransfer.DataPackage(); dp.SetText(text); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp); }
    async void ShowTip(string to, string name)
    {
        var amt = Input("Coins", "1", 12);
        var dlg = new ContentDialog { Title = $"Tip {name}", Content = new StackPanel { Spacing = 8, Children = { Sub($"You have {ChainActions.Coins(Svc.S.Balance(Svc.S.Me.pub))} coins."), amt } }, PrimaryButtonText = "Send", CloseButtonText = "Cancel", XamlRoot = XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        if (!double.TryParse(amt.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) || a <= 0) { status.Say("Enter an amount of coins"); return; }
        await status.Do("Sending…", () => Svc.S.Tip(to, Math.Round(a * 100), "tip for " + name), $"Tipped {a:0.##} coins. It confirms in the next main-chain block.");
    }

    void Videos(Derived d, bool mine, string name)
    {
        var S = Svc.S;
        if (d.LiveNow.Count > 0) { var g = new VideoGrid { Items = d.LiveNow.Select(b => VideoItem.From(id, name, (JsonObject)b.DeepClone(), live: true)).ToList() }; body.Children.Add(g); }
        if (mine) body.Children.Add(Row(Btn("＋ Add video", () => App.Open(typeof(UploadPage), id), primary: true), Btn("● Go live", () => App.Open(typeof(GoLivePage), id))));
        var vids = d.Videos.OrderByDescending(v => Js.Num(v["ts"])).ToList();
        if (vids.Count == 0) body.Children.Add(Sub("No videos yet."));
        else body.Children.Add(new VideoGrid { Items = vids.Select(v => VideoItem.From(id, name, (JsonObject)v.DeepClone())).ToList() });
    }

    void Posts(Derived d, bool mine, string name)
    {
        var S = Svc.S;
        if (mine || d.CanPost(S.Me.pub))
        {
            var text = new TextBox { PlaceholderText = d.Kind == "group" ? $"Write to {name}…" : "Write a post for your channel…", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxLength = 5000 };
            AutomationProperties.SetAutomationId(text, "post-text");
            body.Children.Add(Card(text, Btn("Post", () => _ = status.Do(mine ? "Posting…" : "Doing anti-spam work…", async () => { await S.Post(id, text.Text); text.Text = ""; shown = ""; }, "Posted."), primary: true, id: "post-send")));
        }
        else if (d.Kind == "group") body.Children.Add(Card(Sub((string?)d.Profile["join"] == "approved" ? $"Only approved members can post here. Send the owner your user ID to be approved: {S.Me.pub}" : "You can’t post in this group.")));
        var posts = d.Posts.OrderByDescending(p => Js.Num(p["ts"]) ?? 0).ToList();
        if (posts.Count == 0) body.Children.Add(Sub(d.Kind == "group" ? "No posts yet. Say hello!" : "No posts."));
        foreach (var p in posts.Take(100))
        {
            var ph = (string)p["hash"]!; var pa = (string)p["author"]!; var a = S.AuthorOf(pa, d, (string?)p["payload"]?["name"] ?? "");
            var votes = S.Votes(d, ph);
            var card = new List<UIElement> { Row(Text(a.Name), a.Owner ? Sub("owner") : null!, Sub(Svc.Ago(Js.Num(p["ts"]) ?? 0))), Text((string?)p["payload"]?["text"] ?? "") };
            var acts = new WrapRow();
            if (d.Kind == "group") { var like = Btn($"👍 {votes.Up}", () => _ = status.Do("Liking (pays the author)…", () => S.Vote(id, ph, 1, pa))); like.IsEnabled = !S.IsMine(pa) && votes.Mine <= 0; acts.Children.Add(like); }
            if (mine || S.IsMine(pa)) acts.Children.Add(Btn("Hide", () => _ = status.Do("Hiding…", async () => { await S.Hide(id, ph); shown = ""; })));
            card.Add(acts);
            foreach (var c in d.CommentsFor(ph).Where(c => !d.Hidden.Contains((string)c["hash"]!)).OrderBy(c => Js.Num(c["ts"]) ?? 0))
            {
                var ca = S.AuthorOf((string)c["author"]!, d, (string?)c["payload"]?["name"] ?? "");
                card.Add(new Border { Padding = new Thickness(14, 4, 0, 4), BorderBrush = Ui.Muted, BorderThickness = new Thickness(2, 0, 0, 0), Child = new StackPanel { Children = { Row(Text(ca.Name, 13), Sub(Svc.Ago(Js.Num(c["ts"]) ?? 0))), Text((string?)c["payload"]?["text"] ?? "") } } });
            }
            if (d.Kind == "group" && (mine || d.CanComment(S.Me.pub, ph)))
            {
                var reply = Input("Reply…", max: 2000);
                card.Add(Row(reply, Btn("Reply", () => _ = status.Do("Replying…", async () => { await S.Comment(id, ph, reply.Text); reply.Text = ""; shown = ""; }))));
            }
            body.Children.Add(Card([.. card]));
        }
    }

    void Members(Derived d, string name)
    {
        var S = Svc.S;
        var posters = d.Posts.Concat(d.Visible.Where(b => (string?)b["payload"]?["type"] == "comment")).Select(b => (string)b["author"]!).Where(k => k != d.Owner).Distinct().ToList();
        body.Children.Add(Card(Sub((string?)d.Profile["join"] == "open" ? "Anyone can post in this group." : "Only approved members can post. The owner approves people under Manage."),
            H("Owner", 15), Text(name), H("Approved", 15), Text(d.Allowed.Count == 0 ? "—" : string.Join("\n", d.Allowed.Select(S.NameOf))),
            H("Have posted", 15), Text(posters.Count == 0 ? "—" : string.Join("\n", posters.Select(S.NameOf)))));
    }

    void Feed(Derived d, bool mine)
    {
        var S = Svc.S;
        if (mine)
        {
            var link = Input("Add a video: paste its link (…/v/…)", max: 300); var note = Input("Note (optional)", max: 200);
            body.Children.Add(Card(link, note, Btn("Add video", () =>
            {
                var m = System.Text.RegularExpressions.Regex.Match(link.Text, "/v/([0-9a-f]{64})/([0-9a-f]{64})");
                if (!m.Success) { status.Say("Paste a video link (it contains /v/…)"); return; }
                _ = status.Do("Adding…", async () => { await S.AddVideo(id, m.Groups[1].Value, m.Groups[2].Value, note.Text.Trim()); link.Text = note.Text = ""; shown = ""; }, "Added to the list.");
            }, primary: true)));
        }
        var feed = S.ListFeed(id);
        body.Children.Add(Sub($"Subscribe to this list and its feed shows up on your Home. {d.Adds.Count(b => b["payload"]?["video"] == null)} channel(s), {d.Adds.Count(b => b["payload"]?["video"] != null)} single video(s)."));
        if (feed.Count == 0) body.Children.Add(Sub("Nothing in this list yet."));
        else body.Children.Add(new VideoGrid { Items = feed.Take(120).Select(x => VideoItem.From(x.Chain, (string?)S.Chain(x.Chain)?.Profile["name"] ?? "", (JsonObject)x.Video.DeepClone())).ToList() });
    }

    void ListChannels(Derived d, bool mine)
    {
        var S = Svc.S;
        if (mine)
        {
            var all = S.Directory().Where(c => c.Kind == "channel").OrderBy(c => c.Name).ToList();
            var pick = Pick(all.Select(c => (c.Id, c.Name))); var note = Input("Why you recommend it (optional)", max: 200);
            body.Children.Add(Card(H("Add a channel to this list", 15), pick, note, Btn("Add", () => _ = status.Do("Adding…", async () => { await S.AddChannel(id, pick.Value(), note.Text.Trim()); note.Text = ""; shown = ""; }, "Added to the list."), primary: true)));
        }
        var list = new StackPanel { Spacing = 8 };
        if (d.Adds.Count == 0) list.Children.Add(Sub("This list is empty."));
        foreach (var b in d.Adds)
        {
            var target = (string?)b["payload"]?["target"] ?? ""; var vid = (string?)b["payload"]?["video"]; var tc = S.Chain(target); var bb = b;
            var label = vid != null ? "🎬 " + ((string?)tc?.Videos.FirstOrDefault(x => (string?)x["hash"] == vid)?["payload"]?["title"] ?? "a video") : (string?)tc?.Profile["name"] ?? (string?)S.Genesis(target)?["payload"]?["name"] ?? "Unknown channel";
            list.Children.Add(Row(Btn(label, () => { if (vid != null) App.Open(typeof(WatchPage), new WatchArgs(target, vid)); else Open(target); }),
                (string?)b["payload"]?["note"] is { Length: > 0 } n ? Sub(n) : null!, mine ? Btn("Remove", () => _ = status.Do("Removing…", async () => { await S.Hide(id, (string)bb["hash"]!); shown = ""; })) : null!));
        }
        body.Children.Add(Card(list));
    }

    void Ranking()
    {
        var S = Svc.S; var rk = S.Rank(id);
        if (rk.Local) { body.Children.Add(Sub("This ranking list has not published a ranking yet.")); return; }
        body.Children.Add(Sub($"{rk.Items.Count} videos · algorithm {rk.Algo} · published {Svc.Ago(rk.At)}, signed by this list’s key."));
        body.Children.Add(new VideoGrid { Items = rk.Items.Take(100).Select(i => S.Chain(i.Chain)?.Videos.FirstOrDefault(x => (string?)x["hash"] == i.Video) is { } v ? VideoItem.From(i.Chain, i.Channel, (JsonObject)v.DeepClone()) : null).OfType<VideoItem>().ToList() });
    }

    void About(Derived d, string name, string who, bool registered)
    {
        var S = Svc.S; var items = new List<UIElement>();
        items.Add((string?)d.Profile["about"] is { Length: > 0 } about ? Text(about) : Sub("No description."));
        if ((string?)d.Profile["location"] is { Length: > 0 } loc) items.Add(Sub("📍 " + loc));
        var links = (d.Profile["links"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (links.Count > 0)
        {
            var lr = new WrapRow();
            foreach (var l in links) { var url = (string?)l["url"] ?? ""; lr.Children.Add(new HyperlinkButton { Content = "🔗 " + ((string?)l["label"] ?? url), NavigateUri = Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "https" ? u : null }); }
            items.Add(lr);
        }
        if (who.Length > 0 && who != d.Owner) items.Add(Sub($"Owned by {S.NameOf(who)} (coins paid to this {d.Kind} go to them)"));
        body.Children.Add(Card([.. items])); items = [];
        PlaceAndOrg(d);
        items.Add(H("Technical details", 14));
        items.Add(Sub("Chain ID")); items.Add(Code(id)); items.Add(Sub("Channel key")); items.Add(Code(d.Owner ?? ""));
        items.Add(Sub($"Blocks: {d.BlockCount} · Registered: {(registered ? "yes" : "pending")}" + (d.Kind == "channel" ? $" · Default comments: {((string?)d.Profile["comments"] switch { "approved" => "Approved users only", "off" => "Off", _ => "Anyone" })}" : "")));
        body.Children.Add(Card([.. items]));
    }

    // ---- a place and an organization: where it is, its rating and reviews, what kind it is, its details and people
    void PlaceAndOrg(Derived d)
    {
        var S = Svc.S; var g = S.Genesis(id)?["payload"];
        if (d.Profile["place"] is JsonObject pl)
        {
            var m = new MapView { Height = 260 };
            m.Loaded += (_, _) => { m.SetPins([new MapView.Pin(Js.Num(pl["lat"]) ?? 0, Js.Num(pl["lon"]) ?? 0, "📍", (string?)d.Profile["name"] ?? "")], false); m.CenterOn(Js.Num(pl["lat"]) ?? 0, Js.Num(pl["lon"]) ?? 0, 16); };
            body.Children.Add(Card(m, Row(Sub("📍 " + ((string?)pl["label"] is { Length: > 0 } lb ? lb : $"{pl["lat"]}, {pl["lon"]}")), TradeUi.MapLink(pl, "Directions (OpenStreetMap)"))));
        }
        if (d.Kind == "org")
        {
            var t = OrgKinds.Of((string?)g?["orgType"]); var o = new List<UIElement> { H(t.Name) };
            if ((string?)d.Profile["category"] is { Length: > 0 } cat) o.Add(Sub("Category: " + cat));
            if ((string?)g?["jurisdiction"] is { Length: > 0 } j) o.Add(Row(Sub("Jurisdiction:"), Text(j, 14)));
            if (g?["details"] is JsonObject det)
                foreach (var (k, v) in det)
                {
                    var label = t.Fields.FirstOrDefault(f => f.Key == k)?.Label ?? k switch { "hours" => "Opening hours", "phone" => "Phone", "website" => "Website", "category" => "Category", _ => k };
                    if (k == "category" && d.Profile["category"] != null) continue;
                    var f = t.Fields.FirstOrDefault(f => f.Key == k);
                    var text = f?.Type == "date" && Js.Num(v) is double ms ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToString("yyyy-MM-dd") : v?.ToString() ?? "";
                    o.Add(k == "website" && Uri.TryCreate(text, UriKind.Absolute, out var wu) ? Row(Sub(label + ":"), new HyperlinkButton { Content = text, NavigateUri = wu }) : Row(Sub(label + ":"), Text(text, 14)));
                }
            var people = (g?["people"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            o.Add(H(t.People, 15));
            if (people.Count == 0) o.Add(Sub("Nobody listed."));
            foreach (var q in people)
            {
                var k = (string?)q["key"] ?? "";
                o.Add(Row(Btn(S.NameOf(k), () => ProfilePage.Open(k)), Text(((string?)q["role"] ?? "") + (Js.Num(q["share"]) is double sh ? $" · {sh}%" : ""), 14)));
            }
            var roles = S.Trade.Roles(id);
            if (roles.Count > 0) { o.Add(H("Roles on OpenTuber", 15)); foreach (var (k, rs) in roles) o.Add(Row(Btn(S.NameOf(k), () => ProfilePage.Open(k)), Sub(string.Join(", ", rs.Select(Trade.RoleName))))); }
            var listings = S.Trade.Listings().Where(l => l.Open && (string?)l.L["org"] == id).Count();
            if ((string?)g?["orgType"] == "arbitration") o.Add(Sub($"Arbitrates marketplace disputes · {listings} open listing(s) use it for escrow."));
            body.Children.Add(Card([.. o]));
        }
        if (d.Kind == "org" || d.Profile["place"] != null) { Photos(d); Reviews(d); }
    }
    void Photos(Derived d)
    {
        var S = Svc.S; var ph = S.Photos(id); var mine = S.IsMine(d.Owner);
        if (ph.Count == 0 && !mine) return;
        var row = new WrapRow { Gap = 8 };
        foreach (var p in ph.Take(30)) row.Children.Add(new StackPanel { Width = 220, Spacing = 2, Children = { new Border { CornerRadius = new CornerRadius(8), Child = TradeUi.Pic(p.Image, 150) }, Sub(p.Caption == "📷" ? Svc.Ago(p.Ts) : p.Caption) } });
        var c = new List<UIElement> { H($"Photos ({ph.Count})"), row };
        if (ph.Count == 0) c.Add(Sub("No photos yet."));
        if (mine)
        {
            var cap = Input("Caption (optional)", "", 200);
            c.Add(Row(cap, Btn("Add a photo", () => _ = status.Do("Opening…", async () => { if (await Files.OpenImage(900, 60000) is { } img) { status.Say("Adding…"); await S.AddPhoto(id, img, cap.Text); shown = ""; Refresh(); } }, "Added."), primary: true)));
        }
        body.Children.Add(Card([.. c]));
    }
    void Reviews(Derived d)
    {
        var S = Svc.S; var rs = S.Reviews(id); var (avg, n) = S.Rating(id);
        var c = new List<UIElement> { H("Reviews"), Text(Places.StarText(avg) + (n > 0 ? $" · {n} review{(n == 1 ? "" : "s")}" : ""), 16) };
        var owns = S.IsMine(d.Owner);
        foreach (var r in rs.Take(50))
        {
            var one = new StackPanel { Spacing = 2, Children = { Row(Btn(S.NameOf(r.Author), () => ProfilePage.Open(S.Engine.Read(e => e.Store.Ledger.Resolve(r.Author)))), Text(new string('★', r.Stars) + new string('☆', 5 - r.Stars), 14), Sub(Svc.Ago(r.Ts))), Text(r.Text, 14) } };
            if (r.Reply != null) one.Children.Add(new Border { Padding = new Thickness(12, 4, 0, 4), BorderBrush = Muted, BorderThickness = new Thickness(2, 0, 0, 0), Child = new StackPanel { Children = { Sub("Reply from the owner"), Text(r.Reply, 14) } } });
            if (owns)
            {
                var ans = Input(r.Reply == null ? "Reply to this review (public)" : "Change your reply", "", 1000);
                one.Children.Add(Row(ans, Btn("Reply", () => _ = status.Do("Replying…", async () => { await S.ReplyToReview(id, r.Hash, ans.Text); shown = ""; Refresh(); }, "Replied."))));
            }
            c.Add(one);
        }
        if (!owns)
        {
            var stars = 0; var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            void Paint() { for (int i = 0; i < bar.Children.Count; i++) ((Button)bar.Children[i]).Content = i < stars ? "★" : "☆"; }
            for (int i = 1; i <= 5; i++) { var v = i; var b = new Button { Content = "☆", FontSize = 22, Padding = new Thickness(6, 0, 6, 0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) }; Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "star-" + v); b.Click += (_, _) => { stars = v; Paint(); }; bar.Children.Add(b); }
            var text = new TextBox { PlaceholderText = "What was it like? (public)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, MaxLength = 1500 };
            c.Add(H(rs.Any(r => S.IsMine(r.Author)) ? "Update your review" : "Write a review", 15)); c.Add(bar); c.Add(text);
            c.Add(Btn("Post review", () => _ = status.Do("Posting…", async () => { await S.AddReview(id, stars, text.Text); shown = ""; Refresh(); }, "Thanks: your review is public."), primary: true, id: "review-post"));
            c.Add(status);
        }
        body.Children.Add(Card([.. c]));
    }

    /// <summary>Where it is on the map (tap to set; a business, venue or organization) and, for an organization, its jurisdiction.</summary>
    void PlaceEditor(Derived d)
    {
        var S = Svc.S; var pl = d.Profile["place"] as JsonObject; var st = Status();
        (double Lat, double Lon)? at = pl != null ? (Js.Num(pl["lat"]) ?? 0, Js.Num(pl["lon"]) ?? 0) : null;
        var label = Input("Address or place name (shown with it)", (string?)pl?["label"] ?? "", 160);
        var m = new MapView { Height = 280 };
        void Pin() => m.SetPins(at is { } a ? [new MapView.Pin(a.Lat, a.Lon, "📍", "here")] : [], false);
        m.Loaded += (_, _) => { Pin(); if (at is { } a) m.CenterOn(a.Lat, a.Lon, 15); };
        m.MapTapped += (la, lo) => { at = (Math.Round(la, 5), Math.Round(lo, 5)); Pin(); st.Say($"Picked {at.Value.Lat:0.00000}, {at.Value.Lon:0.00000}: Save to use it."); };
        var items = new List<UIElement> { H(d.Kind == "org" ? "On the map (where this organization is)" : "On the map (for a business or venue; optional)", 16), Sub("Zoom in and tap where it is."), m, label };
        TextBox? jur = null;
        if (d.Kind == "org") { jur = Input("Jurisdiction", (string?)d.Profile["jurisdiction"] ?? "", 120); items.Add(jur); }
        items.Add(Row(Btn("Save", () => _ = st.Do("Saving…", async () =>
        {
            var ch = new JsonObject();
            if (at is { } a) ch["place"] = new JsonObject { ["lat"] = a.Lat, ["lon"] = a.Lon, ["label"] = label.Text.Trim() };
            if (jur != null) ch["jurisdiction"] = jur.Text.Trim();
            if (ch.Count > 0) await S.UpdateProfile(id, ch);
        }, "Saved."), primary: true), pl != null ? Btn("Remove from the map", () => _ = st.Do("Saving…", () => S.UpdateProfile(id, new JsonObject { ["place"] = null }), "Removed.")) : null!));
        items.Add(st);
        body.Children.Add(Card([.. items]));
    }

    // ---- manage your own: profile, picture, location, links, policies, people, @name
    void Manage(Derived d)
    {
        var S = Svc.S; var p = d.Profile; var st = Status();
        var name = Input("Name", (string?)p["name"] ?? "", 60);
        var about = new TextBox { PlaceholderText = "About", Text = (string?)p["about"] ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxLength = 5000 };
        var location = Input("Location (optional)", (string?)p["location"] ?? "", 120);
        string? avatar = null; var avatarNote = Sub((string?)p["avatar"] is { Length: > 30 } ? "Has a picture." : "No picture yet.");
        var links = (p["links"] as JsonArray ?? []).OfType<JsonObject>().Select(l => ((string?)l["label"] ?? "", (string?)l["url"] ?? "")).ToList();
        var linkList = new StackPanel { Spacing = 4 }; var linkLabel = Input("Label", max: 40); var linkUrl = Input("https://…", max: 300);
        void FillLinks() { linkList.Children.Clear(); foreach (var l in links.ToList()) { var ll = l; linkList.Children.Add(Row(Text($"{ll.Item1}: {ll.Item2}", 13), Btn("Remove", () => { links.Remove(ll); FillLinks(); }))); } }
        FillLinks();
        var policy = Pick([("anyone", "Anyone can comment"), ("approved", "Approved users only"), ("off", "Comments off")], (string?)p["comments"] ?? "anyone");
        var join = Pick([("open", "Anyone can post"), ("approved", "Only members you approve")], (string?)p["join"] ?? "open");
        var items = new List<UIElement> { H("Profile", 16), name, about, location, Row(Btn("Choose a picture…", () => _ = st.Do("Opening…", async () => { avatar = await Files.OpenImage(); avatarNote.Text = avatar != null ? "New picture chosen: Save to use it." : avatarNote.Text; st.Say(null); })), avatarNote),
            H("Links", 15), linkList, Row(linkLabel, linkUrl, Btn("Add link", () =>
            {
                var u = linkUrl.Text.Trim(); if (!System.Text.RegularExpressions.Regex.IsMatch(u, @"^https://\S+$")) { st.Say("Links must start with https://"); return; }
                if (links.Count >= 12) { st.Say("Up to 12 links"); return; }
                var label = linkLabel.Text.Trim(); if (label.Length == 0) label = new Uri(u).Host.Replace("www.", "");
                links.Add((label[..Math.Min(40, label.Length)], u)); linkLabel.Text = linkUrl.Text = ""; FillLinks();
            })) };
        if (d.Kind == "channel") { items.Add(H("Comments on your videos", 15)); items.Add(policy); }
        if (d.Kind == "group") { items.Add(H("Who can post", 15)); items.Add(join); }
        items.Add(Btn("Save", () =>
        {
            var ch = new JsonObject();
            if (name.Text.Trim().Length > 0 && name.Text.Trim() != (string?)p["name"]) ch["name"] = name.Text.Trim();
            if (about.Text != ((string?)p["about"] ?? "")) ch["about"] = about.Text;
            if (avatar != null) ch["avatar"] = avatar;
            if (location.Text.Trim() != ((string?)p["location"] ?? "")) ch["location"] = location.Text.Trim();
            var cur = (p["links"] as JsonArray ?? []).OfType<JsonObject>().Select(l => ((string?)l["label"] ?? "", (string?)l["url"] ?? "")).ToList();
            if (!cur.SequenceEqual(links)) ch["links"] = new JsonArray(links.Select(l => (JsonNode?)new JsonObject { ["label"] = l.Item1, ["url"] = l.Item2 }).ToArray());
            if (d.Kind == "channel" && policy.Value() != ((string?)p["comments"] ?? "anyone")) ch["comments"] = policy.Value();
            if (d.Kind == "group" && join.Value() != ((string?)p["join"] ?? "open")) ch["join"] = join.Value();
            if (ch.Count == 0) { st.Say("Nothing changed."); return; }
            _ = st.Do("Saving…", async () => { await S.UpdateProfile(id, ch); avatar = null; shown = ""; }, "Profile update appended to your chain.");
        }, primary: true, id: "manage-save"));
        items.Add(st);
        body.Children.Add(Card([.. items]));
        // people: approve (groups, approved comments) and block
        var key = Input("Their user ID", max: 43); var pst = Status();
        Task KeyAct(bool approve)
        {
            var k = key.Text.Trim(); if (!System.Text.RegularExpressions.Regex.IsMatch(k, "^[A-Za-z0-9_-]{43}$")) { pst.Say("Not a valid user ID"); return Task.CompletedTask; }
            return pst.Do("Working…", async () => { if (approve) await S.Allow(id, k); else await S.Block(id, k); key.Text = ""; shown = ""; }, approve ? "Approved." : "Blocked.");
        }
        var blocked = new StackPanel { Spacing = 4 };
        foreach (var r in d.Revokes) { var k = r.Key; blocked.Children.Add(Row(Text(S.NameOf(k), 13), Btn("Unblock", () => _ = pst.Do("Working…", async () => { await S.Allow(id, k); shown = ""; }, "Unblocked.")))); }
        body.Children.Add(Card(H("People", 16), Sub("Approve someone to post (in a group with approved members) or to comment (with approved comments); block someone to stop them commenting or posting."), key,
            Row(Btn("Approve", () => _ = KeyAct(true)), Btn("Block", () => _ = KeyAct(false))), d.Revokes.Count > 0 ? H("Blocked", 14) : null!, blocked, pst));
        // the @name (channels), ~name (lists): a short link
        var handle = S.NameOfChain(id); var space = d.Kind == "channel" ? "@" : d.Kind == "circle" ? "@@" : "~";
        var want = Input("name (3–30: a–z, 0–9, . _ -)", handle?.TrimStart('@', '~') ?? "", 30); var nst = Status();
        PlaceEditor(d);
        body.Children.Add(Card(H("Short name", 16), Sub(handle != null ? $"This {d.Kind} is {handle}: {Svc.Site}/{handle}" : $"Claim a {space}name so people can find this {d.Kind} at {Svc.Site}/{space}name."), want,
            Row(Btn(handle != null ? "Change" : "Claim", () => _ = nst.Do("Claiming (a little proof of work)…", async () => { await S.ClaimName(id, want.Text.Trim().ToLowerInvariant()); shown = ""; }, "Claimed: it’s yours once the next main-chain block includes it."), primary: true),
                handle != null ? Btn("Release", () => _ = nst.Do("Releasing…", () => S.ClaimName(id, null), "Released.")) : null!), nst));
    }
}
