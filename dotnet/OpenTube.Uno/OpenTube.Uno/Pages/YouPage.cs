using Microsoft.UI.Xaml.Automation;
using System.Text.Json.Nodes;
using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Uno.Controls;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Pages;

/// <summary>
/// You: who you are on OpenTube (name, user ID, coins), your profile, your channels (and making new ones), your
/// wallet (send coins, prices, history), your keys (password-locked backup and import, channel keys, messaging keys,
/// retiring a leaked key) and your settings (not-interested tags, subscriptions).
/// </summary>
public sealed partial class YouPage : Page
{
    readonly StackPanel col, body;
    string section = "profile";
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(3) };
    public YouPage()
    {
        Background = Application.Current.Resources["BackgroundBrush"] as Brush;
        var (scroll, c) = Ui.Page(); col = c; Content = scroll;
        body = new StackPanel { Spacing = 14 };
        tick.Tick += (_, _) => RefreshHeader();
        Loaded += (_, _) => { Build(); tick.Start(); }; Unloaded += (_, _) => tick.Stop();
    }
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is string s && s is "profile" or "channels" or "wallet" or "keys" or "settings") section = s;
        if (App.IsOpened(this) && Content is ScrollViewer sv) Content = Ui.WithBack("You", sv);
    }

    TextBlock? nameText, idText, coinsText; Button? starterBtn; TextBlock? headerStatus;
    void Build()
    {
        col.Children.Clear();
        var S = Svc.S;
        if (S == null || !S.Ready) { col.Children.Add(Sub("Starting…")); _ = Task.Delay(1500).ContinueWith(_ => DispatcherQueue.TryEnqueue(Build)); return; }
        nameText = H("", 22); idText = Code(S.Me.pub); coinsText = Text(""); headerStatus = Status();
        starterBtn = Btn("Get starter coins", () => _ = headerStatus.Do("Earning starter coins (a little proof of work)…", () => S.Engine.SubmitTx(new JsonObject { ["starter"] = true }, S.Me), "Starter coins on the way: they count once the next main-chain block includes them."), primary: true, id: "you-starter");
        col.Children.Add(Card(nameText, Row(Sub("Your user ID"), Btn("Copy", () => Copy(S.Me.pub), id: "you-copy-id")), idText, Row(coinsText, starterBtn), headerStatus));
        var (bar, _) = Segments([("profile", "Profile"), ("channels", "Channels"), ("wallet", "Wallet"), ("keys", "Keys & backup"), ("settings", "Settings")], section, s => { section = s; Show(); });
        col.Children.Add(Row(Btn("Friends", () => App.Open(typeof(FriendsPage)), id: "you-friends"), Btn("Notifications", () => App.Open(typeof(NotificationsPage))), Btn("Messages", () => App.Open(typeof(MessagesPage))), Btn("Devices", () => App.Open(typeof(DevicesPage)), id: "you-devices"), Btn("Network", () => App.Open(typeof(NetworkPage))), Btn("My profile", () => ProfilePage.Open(Svc.S.Me.pub))));
        col.Children.Add(bar); col.Children.Add(body);
        RefreshHeader(); Show();
    }
    async void RefreshHeader()
    {
        var S = Svc.S; if (S == null || !S.Ready || nameText == null) return;
        nameText.Text = await S.Keys.Name() is { Length: > 0 } n ? n : "You";
        coinsText!.Text = $"{ChainActions.Coins(S.Balance(S.Me.pub))} coins";
        starterBtn!.Visibility = S.Engine.Read(e => e.Store.Ledger.Starters.Contains(S.Me.pub)) ? Visibility.Collapsed : Visibility.Visible;
    }
    static void Copy(string text) { var dp = new Windows.ApplicationModel.DataTransfer.DataPackage(); dp.SetText(text); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp); }

    void Show()
    {
        body.Children.Clear();
        switch (section)
        {
            case "channels": Channels(); break;
            case "wallet": Wallet(); break;
            case "keys": _ = Keys(); break;
            case "settings": _ = Settings(); break;
            default: _ = Profile(); break;
        }
    }

    // ---- profile: your display name and your public profile page
    async Task Profile()
    {
        var S = Svc.S; var st = Status();
        var name = Input("Your display name", await S.Keys.Name(), 60);
        body.Children.Add(Card(H("Your name"), Sub("Shown on your comments and posts."), name, Btn("Save", () => _ = st.Do("Saving…", async () => { await S.Keys.SetName(name.Text.Trim()); RefreshHeader(); }), primary: true, id: "you-save-name"), st));
        var page = S.Social.MyPage; var ps = Status();
        if (page == null)
            body.Children.Add(Card(H("Your public profile"), Sub("Your name, picture and a few words about you, plus an @@name people can find you by. It also carries your encryption key, so friends can send you friends-only posts."),
                Btn("Start my profile", () => _ = ps.Do("Registering your profile (a little proof of work: seconds to a minute)…", async () => { await S.Social.StartPage(); Show(); }, "Your profile is registered."), primary: true, id: "you-start-profile"), ps));
        else if (S.Chain(page) == null) body.Children.Add(Card(H("Your public profile"), Sub("Being registered (a few seconds to a minute)…")));
        else body.Children.Add(Card(H("Your public profile"), Sub("Your profile is registered. Editing it (picture, about, links) comes with the channel pages."), Code(page)));
    }

    // ---- channels: make one, and yours
    void Channels()
    {
        var S = Svc.S; var st = Status();
        var kind = Pick([("channel", "Channel (your videos and live streams)"), ("list", "List (a curated feed of channels and videos)"), ("group", "Group (a discussion)")]);
        var name = Input("Name", max: 60); var about = Input("About (optional)", max: 500);
        var join = Pick([("open", "Anyone can post"), ("approved", "Only members you approve")]); join.Visibility = Visibility.Collapsed;
        kind.SelectionChanged += (_, _) => join.Visibility = kind.Value() == "group" ? Visibility.Visible : Visibility.Collapsed;
        body.Children.Add(Card(H("Create a channel, list or group"), kind, name, about, join,
            Sub("This makes a new key pair just for it, and registers it on the main chain (your device does a little proof of work: seconds to a minute)."),
            Btn("Create & register", () =>
            {
                if (string.IsNullOrWhiteSpace(name.Text)) { st.Say("Give it a name"); return; }
                _ = st.Do("Registering: proof of work on the main chain (a few seconds to a minute)…", async () =>
                {
                    var k = kind.Value(); var payload = new JsonObject { ["type"] = "genesis", ["kind"] = k, ["name"] = name.Text.Trim(), ["about"] = about.Text.Trim() };
                    if (k == "channel") { payload["avatar"] = ""; payload["comments"] = "anyone"; }
                    if (k == "group") { payload["avatar"] = ""; payload["join"] = join.Value(); }
                    await S.CreateChannel(payload); name.Text = about.Text = ""; Show();
                }, "Registered.");
            }, primary: true, id: "you-create"), st));
        var mine = S.Directory().Where(c => S.IsMine(c.Owner) && c.Kind is not ("circle" or "square")).ToList();
        var list = new StackPanel { Spacing = 6 };
        if (mine.Count == 0) list.Children.Add(Sub("None yet."));
        foreach (var c in mine) { var cid = c.Id; list.Children.Add(Row(Text(c.Name, 15), Sub(c.Kind), Btn("Open", () => ChannelPage.Open(cid), id: "you-open-" + cid[..8]))); }
        body.Children.Add(Card(H("Your channels & lists"), list));
    }

    // ---- wallet: balances, sending, prices, history
    void Wallet()
    {
        var S = Svc.S; var accounts = S.MyAccounts();
        foreach (var k in accounts) body.Children.Add(Card(H(k == S.Me.pub ? "You" : S.NameOf(k)), Code(k), Text($"{ChainActions.Coins(S.Balance(k))} coins", 20)));
        var st = Status();
        var from = Pick(accounts.Select(k => (k, $"{(k == S.Me.pub ? "You" : S.NameOf(k))} ({ChainActions.Coins(S.Balance(k))})")), S.Me.pub);
        var to = new TextBox { PlaceholderText = "One per line: a user ID, a channel ID, or a name (@channel, ~list, @@person). Several lines pay each one the amount.", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80 };
        var amount = Input("Coins (each)", max: 20); var memo = Input("Memo (optional, public)", max: 140);
        body.Children.Add(Card(H("Send coins"), Sub("From"), from, Sub("To"), to, Row(amount, memo), Btn("Send", () =>
        {
            var lines = to.Text.Split('\n', '\r').Select(l => l.Trim()).Where(l => l.Length > 0).ToList(); var recips = lines.Select(S.Recipient).ToList();
            if (lines.Count == 0 || recips.Any(r => r == null)) { st.Say("Every recipient must be a user ID, a known channel ID or a registered name"); return; }
            if (!double.TryParse(amount.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) || a <= 0) { st.Say("Enter an amount of coins"); return; }
            _ = st.Do("Signing (a little proof of work)…", async () => { await S.Send(from.Value(), recips!, Math.Round(a * 100), memo.Text); to.Text = memo.Text = amount.Text = ""; }, "Sent: it confirms in the next main-chain block.");
        }, primary: true, id: "you-send"), st));
        var pr = S.Engine.Read(e => new Dictionary<string, double>(e.Store.Ledger.Prices));
        body.Children.Add(Card(H("Prices right now"), Sub("The network sets these itself: after every main-chain block each price moves up to 12.5% toward keeping use at a target."),
            Text($"Like or dislike: {ChainActions.Coins(Math.Max(1, Math.Ceiling(pr["like"])))} coins\nEach piece of a video: {ChainActions.Coins(pr["piece"])} coins\nPost a video: {ChainActions.Coins(Math.Max(1, Math.Ceiling(pr["post"])))} coins per GB\nMarket listing: {ChainActions.Coins(Math.Max(1, Math.Ceiling(pr.GetValueOrDefault("list"))))} coins")));
        var hist = new StackPanel { Spacing = 8 }; var who = Pick(accounts.Select(k => (k, k == S.Me.pub ? "You" : S.NameOf(k))), S.Me.pub);
        void FillHist()
        {
            hist.Children.Clear(); var key = who.Value(); var h = S.History(key);
            if (h.Count == 0) hist.Children.Add(Sub("No transactions yet."));
            foreach (var e in h.Take(100))
            {
                var lines = new StackPanel { Spacing = 2 };
                foreach (var l in S.Describe(e, key))
                    lines.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = (l.Sign > 0 ? $"+{ChainActions.Coins(l.Units)}  " : l.Sign < 0 ? $"−{ChainActions.Coins(l.Units)}  " : "") + l.Text, Foreground = l.Sign > 0 ? new SolidColorBrush(Microsoft.UI.Colors.LightGreen) : l.Sign < 0 ? (Brush)Application.Current.Resources["OnSurfaceBrush"] : Muted });
                lines.Children.Add(Sub((e.Pending ? "pending" : $"block #{e.Height}") + " · " + Svc.Ago(e.Ts)));
                hist.Children.Add(lines);
            }
        }
        who.SelectionChanged += (_, _) => FillHist(); FillHist();
        body.Children.Add(Card(H("History"), who, hist));
    }

    // ---- keys: backup and import (password-locked files), channel keys, messaging keys, retiring a leaked key
    async Task Keys()
    {
        var S = Svc.S; var st = Status();
        var unbacked = await S.Keys.NotBackedUp();
        var pass = Password($"Password (at least {KeyFile.MinPassword} characters)"); var pass2 = Password("Repeat it");
        AutomationProperties.SetAutomationId(pass, "keys-pass"); AutomationProperties.SetAutomationId(pass2, "keys-pass2");
        string? Problem() => pass.Password.Length < KeyFile.MinPassword ? $"First choose a password of at least {KeyFile.MinPassword} characters: the file is locked with it." : pass.Password != pass2.Password ? "The two passwords don’t match." : null;
        var unlock = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed }; string? locked = null;
        var unlockPass = Password("Password of the file");
        async Task ImportText(string text, string? pw)
        {
            var (res, personal) = await S.Keys.Import(text, pw);
            if (personal != null)
            {
                var swap = new ContentDialog { Title = "Use the personal key from the file?", Content = $"This file has a different personal key{(res.Controls > 0 ? $" (it controls {res.Controls} older channel(s) or list(s))" : "")}. Your current personal key is replaced: back it up first if it matters. The app restarts with it.", PrimaryButtonText = "Use the key from the file", CloseButtonText = "Keep mine", XamlRoot = XamlRoot };
                if (await swap.ShowAsync() == ContentDialogResult.Primary) { await S.Keys.UsePersonal(personal); st.Say("Using the key from the file: close and reopen OpenTube to finish."); return; }
            }
            st.Say(res.Channels > 0 ? $"Imported {res.Channels} channel/list key{(res.Channels == 1 ? "" : "s")}." : "Those keys are already here."); Show();
        }
        unlock.Children.Add(Sub("This file is locked with a password. Enter it to import the keys.")); unlock.Children.Add(unlockPass);
        unlock.Children.Add(Row(Btn("Unlock and import", () => _ = st.Do("Unlocking…", async () => { await ImportText(locked!, unlockPass.Password); unlock.Visibility = Visibility.Collapsed; }), primary: true), Btn("Cancel", () => unlock.Visibility = Visibility.Collapsed)));
        body.Children.Add(Card(H("Back up"), Sub(unbacked > 0 ? $"⚠ {unbacked} key{(unbacked == 1 ? "" : "s")} not backed up." : "All keys backed up."),
            Sub("Your keys exist only on this device. Whoever holds a channel’s key controls that channel. “Back up all keys” saves your personal key (with your encryption and messaging keys) and every channel and list key in one file, locked with a password you choose: without it the file is useless, so remember it (nobody can recover it)."),
            pass, pass2,
            Row(Btn("Back up all keys", () => { if (Problem() is { } p) { st.Say(p); return; } _ = st.Do("Locking and saving…", async () => { var (n, t) = await S.Keys.BackupAll(pass.Password); if (!await Files.SaveText(n, t)) throw new Exception("Not saved."); Show(); }, "Saved."); }, primary: true, id: "keys-backup"),
                Btn("Import keys", () => _ = st.Do("Opening…", async () =>
                {
                    var text = await Files.OpenText(".json"); if (text == null) { st.Say(null); return; }
                    if (KeyFile.IsEncrypted(text)) { locked = text; unlockPass.Password = ""; unlock.Visibility = Visibility.Visible; st.Say(null); return; }
                    await ImportText(text, null);
                }), id: "keys-import")),
            unlock, st));
        var (bound, all) = S.Keys.BoundCount(); var chans = new StackPanel { Spacing = 6 };
        foreach (var c in S.Keys.Channels())
        {
            var ch = c;
            chans.Children.Add(Row(Text(ch.Name.Length > 0 ? ch.Name : ch.Pub[..8]), Sub(ch.Kind),
                Btn(ch.Exported ? "Export key" : "Export key ⚠", () => { if (Problem() is { } p) { st.Say(p); return; } _ = st.Do("Locking and saving…", async () => { var (n, t) = await S.Keys.ExportChannel(ch.Pub, pass.Password); if (!await Files.SaveText(n, t)) throw new Exception("Not saved."); }, "Saved."); }),
                ch.Chain != null ? Btn("New key…", async () =>
                {
                    var ok = new ContentDialog { Title = $"Give {ch.Name} a new key?", Content = "Its current key is retired. Export the new key afterwards.", PrimaryButtonText = "Replace its key", CloseButtonText = "Cancel", XamlRoot = XamlRoot };
                    if (await ok.ShowAsync() == ContentDialogResult.Primary) await st.Do("Working (a little proof of work)…", () => S.Keys.RekeyChannel(ch.Chain!));
                }) : null!));
        }
        if (S.Keys.Channels().Count == 0) chans.Children.Add(Sub("None yet."));
        body.Children.Add(Card(H("Your channels & lists"), Sub($"Each has its own key, bound to your identity so its coins land in your balance ({bound} of {all} bound)."), chans));
        var mk = new StackPanel { Spacing = 6 };
        foreach (var (pub, published, retired) in await S.Mail.KeyList())
        {
            var p = pub;
            mk.Children.Add(Row(Code(p[..24] + "… · " + (retired ? "retired" : published ? "published" : "not published yet")), published && !retired ? Btn("Retire", () => _ = st.Do("Working…", async () => { await S.Mail.RetireKey(p); Show(); })) : null!));
        }
        body.Children.Add(Card(H("Messaging keys"), Sub("Messages to you are encrypted to one of these (the newest is used). Retire one if it may have leaked: new messages then use the others."), mk,
            Btn("New messaging key", () => _ = st.Do("Working (a little proof of work)…", async () => { await S.Mail.NewKey(); Show(); }))));
        body.Children.Add(Card(H("If your personal key may have leaked"), Sub("Retire it: a new key takes over your coins, your channels (through their bindings) and your pages, and the old one can’t send anything any more. Your encryption and messaging keys stay, so friends still read you."),
            Btn("Retire my personal key…", async () =>
            {
                var ok = new ContentDialog { Title = "Retire your personal key?", Content = "A new key is made and takes over everything; this one is retired for good.", PrimaryButtonText = "Make a new key and retire this one", CloseButtonText = "Cancel", XamlRoot = XamlRoot };
                if (await ok.ShowAsync() == ContentDialogResult.Primary) await st.Do("Retiring…", () => S.Keys.RetirePersonal(m => DispatcherQueue.TryEnqueue(() => st.Say(m))));
            })));
    }

    // ---- settings: not interested, subscriptions
    async Task Settings()
    {
        var S = Svc.S; var muted = await S.Social.Muted(); var st = Status();
        var tags = new StackPanel { Spacing = 6 };
        void FillTags()
        {
            tags.Children.Clear();
            if (muted.Tags.Count + muted.Cats.Count == 0) tags.Children.Add(Sub("Nothing. Use ✕ next to a tag on a video to add one."));
            var row = new WrapRow();
            foreach (var t in muted.Tags) { var tt = t; row.Children.Add(Btn("#" + tt + "  ✕", () => _ = Unmute("tags", tt))); }
            foreach (var c in muted.Cats) { var cc = c; row.Children.Add(Btn((Market.Categories.FirstOrDefault(x => x.Key == cc).Name ?? cc) + " ads  ✕", () => _ = Unmute("cats", cc))); }
            tags.Children.Add(row);
        }
        async Task Unmute(string k, string v) { await S.Social.SetMuted(k, v, false); muted = await S.Social.Muted(); FillTags(); }
        var add = Input("Add a tag…", max: 24);
        FillTags();
        body.Children.Add(Card(H("Not interested"), Sub("Videos and ads with these tags, and ads in these categories, are hidden for you. This list stays on this device: it is never published or sent to anyone."), tags,
            Row(add, Btn("Add", () => _ = st.Do("Saving…", async () =>
            {
                foreach (var t in add.Text.ToLowerInvariant().Split([' ', ',', '#'], StringSplitOptions.RemoveEmptyEntries).Select(t => System.Text.RegularExpressions.Regex.Replace(t, "[^a-z0-9-]", "")).Where(t => t.Length is > 0 and <= 24)) await S.Social.SetMuted("tags", t, true);
                add.Text = ""; muted = await S.Social.Muted(); FillTags();
            }), id: "settings-add-tag")), st));
        var subs = new StackPanel { Spacing = 6 };
        foreach (var id in S.Notes.Subscribed) { var i = id; subs.Children.Add(Row(Text(S.Chain(i)?.Profile["name"]?.ToString() ?? i[..8]), Btn("Unsubscribe", () => _ = st.Do("Saving…", async () => { await S.Notes.Subscribe(i, false); Show(); })))); }
        if (S.Notes.Subscribed.Count == 0) subs.Children.Add(Sub("None yet."));
        body.Children.Add(Card(H("Subscriptions"), subs));
    }
}
