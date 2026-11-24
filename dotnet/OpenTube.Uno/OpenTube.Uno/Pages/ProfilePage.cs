using System.Text.Json.Nodes;
using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Uno.Controls;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Pages;

/// <summary>
/// A person: their name, @@name, about, place and links (from their friends page's public profile), message, follow
/// and friend; what they stream now; their channels and latest videos; their public posts; what they sell; and their
/// groups, lists and organizations.
/// </summary>
public sealed partial class ProfilePage : DataPage
{
    string key = ""; bool? following, friend; int months = 3;
    public ProfilePage() : base("Profile", 1200) { }
    protected override void Param(object? p) { if (p is string s) key = s; }
    public static void Open(string key) => App.Open(typeof(ProfilePage), key);
    string Key0 => Svc.S.Engine.Read(e => e.Store.Ledger.Resolve(key));
    IEnumerable<string> Months() { var m = Social.Month(DateTimeOffset.UtcNow); for (int i = 0; i < months; i++) { yield return m; m = Social.PrevMonth(m); } }
    protected override string Key()
    {
        var S = Svc.S; var k = Key0;
        if (following == null) _ = Task.Run(async () => { following = await S.Social.IsFollowing(k); friend = (await S.Social.Friends()).Any(f => f.Key == k); });
        return $"{k}|{following}|{friend}|{months}|{S.Directory().Count}|{S.Trade.Listings().Count}|{string.Join(",", Months().Select(m => S.Social.Posts(m).Count))}|{S.Social.CircleOf(k)}";
    }
    protected override void Build()
    {
        var S = Svc.S; var k = Key0; var mine = k == S.Me.pub;
        var circle = S.Social.CircleOf(k); var pr = circle != null ? S.Chain(circle)?.Profile : null;
        var name = (string?)pr?["name"] is { Length: > 0 } pn ? pn : mine ? "You" : S.NameOf(k);
        var owned = S.Directory().Where(c => c.Kind is not ("circle" or "case" or "square") && c.Owner != null && S.Engine.Read(e => e.Store.Ledger.Resolve(c.Owner)) == k).ToList();
        var channels = owned.Where(c => c.Kind == "channel").ToList(); var others = owned.Where(c => c.Kind is "group" or "org" or "list").ToList();

        // who they are
        var head = new StackPanel { Spacing = 6 };
        head.Children.Add(H(name, 24));
        if (circle != null && S.NameOfChain(circle) is { } handle) head.Children.Add(Text(handle, 15));
        if ((string?)pr?["about"] is { Length: > 0 } ab) head.Children.Add(Text(ab, 14));
        if ((string?)pr?["location"] is { Length: > 0 } loc) head.Children.Add(Sub("📍 " + loc));
        var links = (pr?["links"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (links.Count > 0) { var lr = new WrapRow(); foreach (var l in links) if (Uri.TryCreate((string?)l["url"], UriKind.Absolute, out var u)) lr.Children.Add(new HyperlinkButton { Content = "🔗 " + ((string?)l["label"] ?? u.Host), NavigateUri = u }); head.Children.Add(lr); }
        head.Children.Add(Code(k));
        if (!mine)
        {
            var acts = new WrapRow();
            acts.Children.Add(Btn("✉ Message", () => App.Open(typeof(MessagesPage), k), primary: true));
            acts.Children.Add(Btn(following == true ? "Following ✓" : "Follow", () => _ = Status.Do("Saving…", async () => { following = following != true; await S.Social.SetFollowing(k, following == true); Redraw(); }, following == true ? "Unfollowed." : "Following: their public posts come first.")));
            if (friend == true) acts.Children.Add(Sub("✓ friend"));
            else acts.Children.Add(Btn("＋ Friend", () => _ = Status.Do("Adding…", async () => { await S.Social.AddFriend(k, S.NameOf(k)); friend = true; Redraw(); }, "Added as a friend.")));
            head.Children.Add(acts);
        }
        head.Children.Add(Status);
        Col.Children.Add(Card(head));

        var live = channels.SelectMany(c => (S.Chain(c.Id)?.LiveNow ?? []).Select(lb => (c, lb))).ToList();
        if (live.Count > 0)
        {
            var lv = new StackPanel { Spacing = 6 };
            foreach (var (c, lb) in live) { var st = (string?)lb["payload"]?["stream"] ?? ""; lv.Children.Add(Btn($"🔴 {(string?)lb["payload"]?["title"]} · on {c.Name}", () => App.Open(typeof(LivePage), new LiveArgs(c.Id, st)), primary: true)); }
            Col.Children.Add(Card([H("Live now"), lv]));
        }
        var ch = new StackPanel { Spacing = 6 };
        foreach (var c in channels) ch.Children.Add(Btn($"{c.Name} · {c.Videos} videos", () => ChannelPage.Open(c.Id)));
        if (channels.Count == 0) ch.Children.Add(Sub("No channels."));
        Col.Children.Add(Card(H("Channels"), ch));

        var videos = channels.SelectMany(c => (S.Chain(c.Id)?.Videos ?? []).Select(v => (c, v))).OrderByDescending(x => Js.Num(x.v["ts"]) ?? 0).Take(12).ToList();
        Col.Children.Add(H("Recent videos"));
        if (videos.Count == 0) Col.Children.Add(Sub("No videos yet."));
        else Col.Children.Add(new VideoGrid { Items = videos.Select(x => VideoItem.From(x.c.Id, x.c.Name, (JsonObject)x.v.DeepClone())).ToList() });

        var posts = Months().SelectMany(m => S.Social.Posts(m).Where(p => S.Engine.Read(e => e.Store.Ledger.Resolve(p.Author)) == k).Select(p => (m, p))).OrderByDescending(x => x.p.Ts).Take(30).ToList();
        var ps = new StackPanel { Spacing = 10 };
        foreach (var (m, p) in posts)
        {
            var hash = (string)p.Block["hash"]!;
            ps.Children.Add(new StackPanel { Spacing = 2, Children = { Row(Sub(Svc.Ago(p.Ts)), Sub($"👍 {p.Likes} · {p.Replies} repl{(p.Replies == 1 ? "y" : "ies")}")), Text(p.Text, 14), Btn("Open", () => App.Open(typeof(PostsPage), m + "/" + hash)) } });
        }
        if (posts.Count == 0) ps.Children.Add(Sub($"No public posts in the last {months} months."));
        ps.Children.Add(Btn("Load older months", () => { months += 6; foreach (var m in Months()) S.Social.Square(m); Redraw(); }));
        Col.Children.Add(Card(H("Public posts"), ps));

        var selling = S.Trade.Listings().Where(l => l.State == "open" && S.Engine.Read(e => e.Store.Ledger.Resolve(l.Seller)) == k).ToList();
        var sl = new StackPanel { Spacing = 6 };
        foreach (var l in selling) sl.Children.Add(Btn($"{l.Title} · {Trade.Coins(l.PriceNow)} coins", () => App.Open(typeof(MarketItemPage), l.Id)));
        if (selling.Count == 0) sl.Children.Add(Sub("Nothing for sale."));
        Col.Children.Add(Card(H("Selling"), sl));

        if (others.Count > 0)
        {
            var ot = new StackPanel { Spacing = 6 };
            foreach (var c in others) ot.Children.Add(Btn($"{c.Name} · {c.Kind}", () => ChannelPage.Open(c.Id)));
            Col.Children.Add(Card(H("Groups, lists & organizations"), ot));
        }
    }
}
