using System.Text.Json.Nodes;
using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Uno.Controls;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Pages;

/// <summary>Pieces the market, exchange and organization screens share.</summary>
static class TradeUi
{
    /// <summary>A picture from a data: URI (listing covers, avatars), loaded in the background.</summary>
    public static Image Pic(string? dataUri, double height)
    {
        var img = new Image { Height = height, Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (!string.IsNullOrEmpty(dataUri)) _ = Load();   // (on the UI thread: pictures are made there)
        async Task Load() => img.Source = await Svc.Thumb(dataUri);
        return img;
    }
    /// <summary>Editors for a kind's fields (text, number, date, yes/no, a choice), writing into <paramref name="values"/>.</summary>
    public static Panel Fields(IEnumerable<Market.Field> fields, Dictionary<string, string> values)
    {
        var p = new WrapRow();
        foreach (var f in fields)
        {
            FrameworkElement e;
            if (f.Type == "bool") { var cb = new CheckBox { Content = f.Label, IsChecked = values.GetValueOrDefault(f.Key) == "true" }; cb.Click += (_, _) => values[f.Key] = cb.IsChecked == true ? "true" : "false"; e = cb; }
            else if (f.Choices != null) { var pk = Pick([("", f.Label + "…"), .. f.Choices.Select(c => (c, c))], values.GetValueOrDefault(f.Key)); pk.SelectionChanged += (_, _) => values[f.Key] = pk.Value(); e = pk; }
            else
            {
                var hint = f.Type switch { "number" => " (number)", "date" => " (YYYY-MM-DD)", "datetime" => " (YYYY-MM-DD HH:MM)", _ => "" };
                var tb = Input(f.Label + hint, values.GetValueOrDefault(f.Key) ?? "", 160); tb.TextChanged += (_, _) => values[f.Key] = tb.Text; e = tb;
            }
            e.MinWidth = 220; p.Children.Add(e);
        }
        return p;
    }
    static string Left(double ms) { if (ms <= 0) return "ended"; var h = ms / 3600000; return h >= 48 ? $"{Math.Round(h / 24)} days left" : h >= 1 ? $"{Math.Round(h)} h left" : $"{Math.Max(1, Math.Round(ms / 60000))} min left"; }
    /// <summary>A listing as lists show it: cover, title, price, kind, seller and its main details. Opens it.</summary>
    public static FrameworkElement Card(Trade.Listing l)
    {
        var S = Svc.S; var K = Market.KindOf(l.L); var dd = l.L["details"] as JsonObject;
        var per = (string?)dd?["rateUnit"] ?? (string?)dd?["period"] ?? ((string?)l.L["deal"] == "rent" ? "month" : null);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); var price = Js.Num(l.L["price"]) ?? 0; var qty = Js.Num(l.L["qty"]) ?? 0; var certs = (l.L["certs"] as JsonArray)?.Count ?? 0;
        var what = l.Kind == "auction"
            ? $"{(l.L["high"] != null ? "bid" : "starts at")} {Trade.Coins(l.PriceNow)} · {(l.Open ? Left((Js.Num(l.L["ends"]) ?? 0) - now) : l.State)}"
            : $"{(price > 0 ? Trade.Coins(price) + " coins" : (string?)dd?["salary"] ?? (string?)dd?["budget"] ?? "price on request")}{(price > 0 && per != null ? " / " + per : "")}{(qty > 1 ? $" · {qty} left" : "")}{(l.State != "open" ? " · " + l.State : "")}";
        var sp = new StackPanel { Spacing = 4, Width = 250 };
        if ((string?)l.L["image"] is { Length: > 0 } img) sp.Children.Add(new Border { CornerRadius = new CornerRadius(8), Child = Pic(img, 140) });
        else sp.Children.Add(new Border { Height = 140, CornerRadius = new CornerRadius(8), Background = (Brush)Application.Current.Resources["BackgroundBrush"], Child = new TextBlock { Text = certs > 0 ? "🎟" : K.Icon, FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        sp.Children.Add(H(l.Title, 15)); sp.Children.Add(Text(what, 14));
        sp.Children.Add(Sub($"{K.Icon} {K.Name} · {S.NameOf(l.Seller)}{(certs > 0 ? $" · {certs} ad view{(certs == 1 ? "" : "s")}" : "")}{((string?)l.L["place"]?["label"] is { Length: > 0 } pl ? " · 📍 " + pl : "")}"));
        var det = Market.Details(l.L); if (det.Count > 0) sp.Children.Add(Sub(string.Join(" · ", det.Take(3).Select(x => x.Value))));
        var b = new Button { Content = sp, Padding = new Thickness(10), HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Surface, CornerRadius = new CornerRadius(12), Foreground = (Brush)Application.Current.Resources["OnSurfaceBrush"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, "listing-" + l.Id[..8]);
        b.Click += (_, _) => App.Open(typeof(MarketItemPage), l.Id); return b;
    }
    /// <summary>A place on OpenStreetMap (the app has no map of its own yet).</summary>
    public static HyperlinkButton MapLink(JsonNode place, string text) => new()
    {
        Content = text,
        NavigateUri = new Uri(FormattableString.Invariant($"https://www.openstreetmap.org/?mlat={Js.Num(place["lat"]) ?? 0}&mlon={Js.Num(place["lon"]) ?? 0}#map=15/{Js.Num(place["lat"]) ?? 0}/{Js.Num(place["lon"]) ?? 0}")),
    };
}

/// <summary>
/// The market: listings of every kind (goods, services, jobs, housing, vehicles…), auctions, ad-view bundles, the ad
/// board, places, your listings, bids and purchases, and selling something (with escrow through an arbitration body).
/// </summary>
public sealed partial class MarketPage : DataPage
{
    string tab = "items", q = "", kindFilter = "all", deal = "all"; Market.Draft d = new();
    public MarketPage() : base("Market", 1200) { }
    protected override void Param(object? p) { if (p is string s && s.Length > 0) tab = s == "browse" ? "items" : s; }
    protected override string Key()
    {
        var S = Svc.S;
        return tab == "new" ? $"new|{d.Type}|{d.Kind}|{S.Trade.Orgs().Count}|{S.MyCerts().Count}"
            : $"{tab}|{q}|{kindFilter}|{deal}|{S.Trade.Balance}|{string.Join(",", S.Trade.Listings().Select(l => l.Id[..6] + l.State + l.PriceNow))}|{S.MyCerts().Count}|{S.AdBoard().Count}|{S.Engine.Read(e => e.Store.TxPool.Count)}";
    }
    static readonly (string, string)[] Tabs = [("items", "Listings"), ("housing", "🏠 Housing"), ("auctions", "Auctions"), ("adviews", "Ad views"), ("ads", "Ad board"), ("places", "📍 Places"), ("mine", "Mine"), ("new", "＋ Sell")];
    protected override void Build()
    {
        var S = Svc.S;
        Col.Children.Add(Row(H("Market", 22), Sub($"You have {Trade.Coins(S.Trade.Balance)} coins")));
        var (bar, _) = Segments(Tabs, tab, k => { tab = k; Redraw(); }); Col.Children.Add(bar);
        switch (tab)
        {
            case "new": Sell(); break;
            case "adviews-mine": AdViews(); break;
            case "ads": AdBoard(); break;
            case "places": Col.Children.Add(Btn("🗺 Open the map", () => App.Open(typeof(PlacesPage)), primary: true)); Places(); break;
            default: Browse(); break;
        }
        Col.Children.Add(Status);
    }
    void Browse()
    {
        var S = Svc.S; var all = S.Trade.Listings(); var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var live = all.Where(l => l.Open && (l.Kind == "fixed" || (Js.Num(l.L["ends"]) ?? 0) > now)).ToList();
        var shown = tab switch
        {
            "auctions" => live.Where(l => l.Kind == "auction").ToList(),
            "housing" => live.Where(l => ((string?)l.L["category"] == "housing" || l.Type == "housing") && (deal == "all" || (string?)l.L["deal"] == deal)).ToList(),
            "adviews" => live.Where(l => (l.L["certs"] as JsonArray)?.Count > 0).ToList(),
            "mine" => all.Where(l => S.IsMine(l.Seller)).ToList(),
            _ => live.Where(l => l.Kind == "fixed" && (kindFilter == "all" || Market.KindOf(l.L).Key == kindFilter)).ToList(),
        };
        shown = shown.Where(l => q.Length == 0 || l.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || ((string?)l.L["about"] ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var search = new TextBox { PlaceholderText = "Search", Text = q, MinWidth = 220 };
        search.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { q = search.Text.Trim(); Redraw(); } };
        var tools = Row(search, Btn("Search", () => { q = search.Text.Trim(); Redraw(); }));
        if (tab == "items") { var k = Pick([("all", "All kinds"), .. Market.Kinds.Select(x => (x.Key, $"{x.Icon} {x.Name}"))], kindFilter); k.MinWidth = 180; k.SelectionChanged += (_, _) => { kindFilter = k.Value(); Redraw(); }; tools.Children.Add(k); }
        if (tab == "housing") { var h = Pick([("all", "All"), ("sale", "For sale"), ("rent", "For rent")], deal); h.MinWidth = 140; h.SelectionChanged += (_, _) => { deal = h.Value(); Redraw(); }; tools.Children.Add(h); }
        if (tab == "adviews") tools.Children.Add(Btn("Your ad views", () => { tab = "adviews-mine"; Redraw(); }));
        Col.Children.Add(tools);
        if (tab == "mine")
            foreach (var p in S.Engine.Read(e => e.Store.TxPool.Values.Where(t => t["list"] != null && S.IsMine((string?)t["from"])).Select(t => (string?)t["list"]?["title"]).ToList()))
                Col.Children.Add(Card(Text(p ?? ""), Sub("waiting for a block")));
        if (shown.Count == 0) Col.Children.Add(Sub(tab switch { "mine" => "You haven’t listed anything.", "auctions" => "No auctions running.", "housing" => "No housing listed yet.", "adviews" => "No ad-view bundles for sale.", _ => "Nothing listed yet. Be the first: ＋ Sell." }));
        var grid = new WrapRow { Gap = 12 }; foreach (var l in shown.Take(200)) grid.Children.Add(TradeUi.Card(l)); Col.Children.Add(grid);
        if (tab == "mine")
        {
            var bought = all.Where(l => S.IsMine((string?)l.L["high"]?["by"]) || S.IsMine((string?)l.L["buyer"])).ToList();
            Col.Children.Add(H("Your bids and purchases"));
            if (bought.Count == 0) Col.Children.Add(Sub("None yet."));
            var g2 = new WrapRow { Gap = 12 }; foreach (var l in bought) g2.Children.Add(TradeUi.Card(l)); Col.Children.Add(g2);
        }
    }
    void AdViews()
    {
        var S = Svc.S; var certs = S.MyCerts();
        var c = new StackPanel { Spacing = 10 };
        c.Children.Add(H("Your ad views")); c.Children.Add(Sub("Each verified ad you watch gives you a certificate holding your share of what the advertiser paid. Cash them in, give them to a creator, or bundle them into a listing."));
        if (certs.Count == 0) c.Children.Add(Sub("None yet. Watch ads before videos to earn them."));
        foreach (var g in certs.GroupBy(x => x.Adv))
        {
            var list = g.ToList(); var to = Input("Give to (user or channel ID)");
            c.Children.Add(new StackPanel { Spacing = 6, Children = { Text($"{list.Count} × {S.AdvName(g.Key)} · worth {Trade.Coins(list.Sum(x => x.Amt))} coins"),
                Row(Btn("Cash in", () => _ = Status.Do("Sending…", () => S.CashIn(list), "Cashed in: the coins arrive with the next main-chain block.")),
                    to, Btn("Give", () => { var r = S.Recipient(to.Text); if (r == null) Status.Say("Not a user or channel ID"); else _ = Status.Do("Sending…", () => S.GiveCerts(list, r), "Given."); }),
                    Btn("Sell", () => { tab = "new"; Redraw(); })) } });
        }
        Col.Children.Add(Card(c));
    }
    void AdBoard()
    {
        var S = Svc.S; var mineCh = S.Directory().Where(c => c.Kind == "channel" && S.IsMine(c.Owner)).ToList();
        Col.Children.Add(Sub("Ads published on the channels this device knows. Running an advertiser’s ads on your channel sponsors them: their ads play before your videos, and each verified view pays the viewer (as a certificate) and you."));
        var rows = S.AdBoard();
        if (rows.Count == 0) Col.Children.Add(Sub("No ads known yet. Advertisers create them on their channel’s Advertise tab."));
        foreach (var (c, b) in rows)
        {
            var p = b["payload"]!; var t = p["terms"];
            var card = new List<UIElement> { Text((string?)p["title"] ?? ""), Sub($"{S.Chain(c)?.Profile["name"]} · {Market.Categories.FirstOrDefault(x => x.Key == (string?)p["category"]).Name ?? "uncategorized"} · {Trade.Coins(Js.Num(t?["perView"]) ?? 0)} coins per full view · up to {t?["maxViews"]} views · {t?["duration"]} s") };
            if (mineCh.Count > 0 && !S.IsMine(S.Chain(c)?.Owner))
            {
                var on = Pick(mineCh.Select(m => (m.Id, m.Name))); on.MinWidth = 180;
                card.Add(Row(on, Btn("Run on my channel", () => _ = Status.Do("Saving…", () => S.Sponsor(on.Value(), c), "Their ads now run on your channel."))));
            }
            Col.Children.Add(Card([.. card]));
        }
    }
    void Places()
    {
        var S = Svc.S; var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Col.Children.Add(Sub("🛍 listings · 🏠 housing · 🏛 organizations · 🏪 channels and businesses. Each opens here; the pin opens OpenStreetMap."));
        var list = new StackPanel { Spacing = 8 }; var n = 0;
        foreach (var l in S.Trade.Listings().Where(l => l.Open && l.L["place"] is JsonObject && (l.Kind == "fixed" || (Js.Num(l.L["ends"]) ?? 0) > now)))
        {
            var pl = (JsonObject)l.L["place"]!; n++;
            list.Children.Add(Row(Btn($"{((string?)l.L["category"] == "housing" ? "🏠" : "🛍")} {l.Title} · {Trade.Coins(l.PriceNow)} coins", () => App.Open(typeof(MarketItemPage), l.Id)), TradeUi.MapLink(pl, "📍 " + ((string?)pl["label"] is { Length: > 0 } lb ? lb : "map"))));
        }
        foreach (var c in S.Directory().Where(c => c.Kind is "org" or "channel"))
            if (S.Chain(c.Id)?.Profile["place"] is JsonObject pl) { n++; list.Children.Add(Row(Btn($"{(c.Kind == "org" ? "🏛" : "🏪")} {c.Name}", () => ChannelPage.Open(c.Id)), TradeUi.MapLink(pl, "📍 " + ((string?)pl["label"] is { Length: > 0 } lb ? lb : "map")))); }
        if (n == 0) list.Children.Add(Sub("No places known to this device yet."));
        Col.Children.Add(Card(list));
    }
    void Sell()
    {
        var S = Svc.S; var K = Market.Kinds.First(k => k.Key == d.Type);
        var c = new List<UIElement> { H("Sell something"), Sub("Listings live on the main chain. Coins move through the network. Ad views move automatically. Anything else (goods, services, gift cards, cash deals) is delivered by you, using the contact you give.") };
        var type = Pick(Market.Kinds.Select(k => (k.Key, $"{k.Icon} {k.Name}")), d.Type);
        type.SelectionChanged += (_, _) => { d.Type = type.Value(); d.Fields.Clear(); if (d.Type == "housing") d.Category = "housing"; if (d.Type is "job" or "wanted") d.Kind = "fixed"; Redraw(); };
        c.Add(Sub("What are you listing?")); c.Add(type);
        var kind = Pick([("fixed", "Fixed price"), ("auction", "Auction")], d.Kind); kind.SelectionChanged += (_, _) => { d.Kind = kind.Value(); Redraw(); };
        c.Add(Sub("How it sells")); c.Add(kind);
        if (d.Kind == "auction") { var days = Input("Days", d.Days.ToString(), 2); days.TextChanged += (_, _) => d.Days = int.TryParse(days.Text, out var x) ? Math.Clamp(x, 1, 30) : 7; c.Add(Row(Sub("Ends in"), days, Sub("days"))); }
        var org = Pick([("", "No escrow: I’m paid at once"), .. S.Trade.Orgs().Select(a => (a.Id, "Escrow, disputes arbitrated by " + a.Name))], d.Org); org.SelectionChanged += (_, _) => d.Org = org.Value();
        c.Add(Sub("Payment protection")); c.Add(org);
        var title = Input("What are you selling?", d.Title, 120); title.TextChanged += (_, _) => d.Title = title.Text;
        var about = new TextBox { PlaceholderText = "Description, condition, delivery…", Text = d.About, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, MaxLength = 2000 }; about.TextChanged += (_, _) => d.About = about.Text;
        c.Add(title); c.Add(about);
        if (K.Fields.Length > 0) c.Add(TradeUi.Fields(K.Fields, d.Fields));
        var price = Input(K.PriceLabel ?? (d.Kind == "auction" ? "Starting bid (coins)" : "Price (coins)"), d.Price, 20); price.TextChanged += (_, _) => d.Price = price.Text;
        var contact = Input("Contact: how the buyer reaches you (email, site, shop link): public", d.Contact); contact.TextChanged += (_, _) => d.Contact = contact.Text;
        c.Add(price); c.Add(contact);
        var cat = Pick([("", "Category (optional)"), .. Market.Categories.Select(x => (x.Key, x.Name))], d.Category); cat.SelectionChanged += (_, _) => { var was = d.Category; d.Category = cat.Value(); if ((was == "housing") != (d.Category == "housing")) Redraw(); };
        var tags = Input("Tags (optional), e.g. bike, vintage", d.Tags); tags.TextChanged += (_, _) => d.Tags = tags.Text;
        c.Add(cat); c.Add(tags);
        if (d.Category == "housing" && d.Type != "housing") { var dl = Pick([("sale", "For sale"), ("rent", "For rent (price per month)")], d.Deal); dl.SelectionChanged += (_, _) => d.Deal = dl.Value(); c.Add(dl); }
        var cover = new StackPanel { Spacing = 6 };
        if (d.Image != null) cover.Children.Add(new Border { Width = 200, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left, Child = TradeUi.Pic(d.Image, 120) });
        cover.Children.Add(Row(Btn(d.Image == null ? "Add a cover picture" : "Change the picture", () => _ = Status.Do("Opening…", async () => { if (await Files.OpenImage(320, 24000) is { } img) { d.Image = img; Redraw(); } }, "")),
            d.Image != null ? Btn("Remove", () => { d.Image = null; Redraw(); }) : null!));
        c.Add(Sub("Cover picture (optional)")); c.Add(cover);
        var certs = S.MyCerts();
        if (certs.Count > 0)
        {
            c.Add(Sub("Include ad views (they go to the buyer)"));
            foreach (var g in certs.GroupBy(x => x.Adv))
            {
                var ids = g.Select(x => x.Id).ToList();
                var cb = new CheckBox { Content = $"{g.Count()} view{(g.Count() == 1 ? "" : "s")} of {S.AdvName(g.Key)}’s ads (worth {Trade.Coins(g.Sum(x => x.Amt))} coins if cashed in)", IsChecked = ids.All(d.Certs.Contains) };
                cb.Click += (_, _) => { d.Certs.RemoveAll(ids.Contains); if (cb.IsChecked == true) d.Certs.AddRange(ids); };
                c.Add(cb);
            }
        }
        c.Add(Row(Btn("List it", () => _ = Status.Do("Listing (a little proof of work)…", async () => { await S.ListDraft(d); d = new(); tab = "mine"; Redraw(); }, "Listed: it shows once the next main-chain block includes it."), primary: true, id: "market-list"),
            Sub($"Listing costs {Trade.Coins(S.Trade.Price("list"))} coins.")));
        Col.Children.Add(Card([.. c]));
    }
}

/// <summary>A listing: its pictures and details, price or bids, the seller, escrow and disputes, and buying or bidding.</summary>
public sealed partial class MarketItemPage : DataPage
{
    string id = "";
    public MarketItemPage() : base("Listing") { }
    protected override void Param(object? p) { if (p is string s) id = s; }
    protected override string Key() { var l = Svc.S.Trade.Get(id); return l == null ? "none" + Svc.S.Trade.PendingTx(id) : l.L.ToJsonString(); }
    protected override void Build()
    {
        var S = Svc.S; var l = S.Trade.Get(id);
        if (l == null) { Col.Children.Add(Sub(S.Trade.PendingTx(id) ? "This listing is waiting for the next block." : "Unknown listing (it may not have synced yet).")); return; }
        var mine = S.IsMine(l.Seller); var escrow = l.L["escrow"] as JsonObject; var K = Market.KindOf(l.L);
        var c = new List<UIElement>();
        if ((string?)l.L["image"] is { Length: > 0 } img) c.Add(new Border { CornerRadius = new CornerRadius(8), MaxHeight = 360, Child = TradeUi.Pic(img, 300) });
        c.Add(H(l.Title, 22));
        var seller = S.Engine.Read(e => e.Store.Ledger.Resolve(l.Seller));
        c.Add(Row(Sub($"{K.Icon} {K.Name} · {(l.Kind == "auction" ? "auction" : "fixed price")} · listed {Svc.Ago(Js.Num(l.L["ts"]) ?? 0)} by"), new HyperlinkButton { Content = S.NameOf(l.Seller), Padding = new Thickness(0), Command = null }.Also(h => h.Click += (_, _) => ProfilePage.Open(seller))));
        var badges = new List<string>();
        if ((string?)l.L["deal"] is { } deal) badges.Add(deal == "rent" ? "For rent · price per month" : "For sale");
        if ((Js.Num(l.L["qty"]) ?? 0) > 1) badges.Add($"{l.L["qty"]} left");
        if ((string?)l.L["category"] is { } cat && cat != "adviews") badges.Add(Market.Categories.FirstOrDefault(x => x.Key == cat).Name ?? cat);
        if ((l.L["certs"] as JsonArray)?.Count is > 0 and var nc) badges.Add($"🎟 {nc} ad view{(nc == 1 ? "" : "s")} (they go to the buyer)");
        if (badges.Count > 0) c.Add(Sub(string.Join(" · ", badges)));
        foreach (var (k, v) in Market.Details(l.L)) c.Add(Row(Sub(k + ":"), Text(v, 14)));
        if (l.L["place"] is JsonObject pl) c.Add(TradeUi.MapLink(pl, "📍 " + ((string?)pl["label"] is { Length: > 0 } plb ? plb : $"{pl["lat"]}, {pl["lon"]}")));
        if (l.L["tags"] is JsonArray { Count: > 0 } tg) c.Add(Sub(string.Join(" ", tg.Select(t => "#" + t))));
        c.Add(H($"{Trade.Coins(l.PriceNow)} coins", 26));
        if (l.Kind == "auction") c.Add(Sub(l.L["high"] != null ? $"high bid · {Js.Num(l.L["bids"])} bid(s)" : "starting bid"));
        if (l.Kind == "auction" && l.State == "open") c.Add(Sub($"Ends {DateTimeOffset.FromUnixTimeMilliseconds((long)(Js.Num(l.L["ends"]) ?? 0)).LocalDateTime:g}"));
        if ((string?)l.L["about"] is { Length: > 0 } ab) c.Add(Text(ab, 14));
        if ((string?)l.L["contact"] is { Length: > 0 } ct) c.Add(Sub("Contact: " + ct));
        if ((string?)l.L["org"] is { } org) c.Add(Sub($"🛡 Payment held in escrow; disputes heard by {S.Genesis(org)?["payload"]?["name"]} (released after {Js.Num(l.L["hold"]) ?? 14} days unless disputed)."));
        if (escrow != null)
        {
            var buyer = S.IsMine((string?)escrow["buyer"]);
            var e = new List<UIElement> { Text($"🛡 {(string?)escrow["state"]} · {Trade.Coins(Js.Num(escrow["amt"]) ?? 0)} coins") };
            if ((buyer || mine) && (string?)escrow["state"] == "held")
            {
                var why = Input("What went wrong?", "", 500);
                if (buyer) e.Add(Btn("I got it: pay the seller", () => Run(() => S.Trade.Release(id)), primary: true));
                e.Add(Row(why, Btn("Dispute", () => Run(() => S.Trade.Dispute(l, why.Text)))));
            }
            c.Add(Card([.. e]));
        }
        if (!l.Open) c.Add(Sub(l.State == "sold" ? $"Sold to {S.NameOf((string?)l.L["buyer"] ?? "")}." : l.State == "withdrawn" ? "Withdrawn by the seller." : "Ended."));
        else if (mine) c.Add(l.L["high"] == null ? Btn("Withdraw listing", () => Run(() => S.Trade.Unlist(id))) : Sub("You can’t withdraw it: it has a bid."));
        else if (l.Kind == "fixed" && (Js.Num(l.L["price"]) ?? 0) == 0) c.Add(Btn("✉ Contact", () => App.Open(typeof(MessagesPage), seller), primary: true));
        else if (l.Kind == "fixed") c.Add(Row(Btn($"{K.Act} for {Trade.Coins(Js.Num(l.L["price"]) ?? 0)} coins", () => Run(() => S.Trade.Buy(id)), primary: true, id: "item-buy"), Btn("✉ Ask", () => App.Open(typeof(MessagesPage), seller))));
        else { var bid = Input("Your bid", "", 20); bid.Width = 140; c.Add(Row(bid, Btn("Bid", () => Run(() => S.Trade.Bid(id, Trade.Units(bid.Text))), primary: true), Sub($"at least {Trade.Coins(S.Trade.MinBid(l))} · held until the end, refunded if someone outbids you"))); }
        c.Add(Status);
        Col.Children.Add(Card([.. c]));
    }
    void Run(Func<Task> f) => _ = Status.Do("Sending (a little proof of work)…", f, "Done: it counts once the next main-chain block includes it.");
}

/// <summary>
/// The exchange: an order book of people buying and selling coins for bitcoin, ether, stablecoins or cash; atomic
/// swaps (Lightning invoices or hash-locked contracts on other chains); and how it works.
/// </summary>
public sealed partial class ExchangePage : DataPage
{
    string tab = "book", asset = "all"; string? mySecret;
    readonly HashSet<string> methods = ["lightning"];
    public ExchangePage() : base("Exchange") { }
    protected override void Param(object? p) { if (p is string s && s.Length > 0) tab = s; }
    protected override string Key() { var S = Svc.S; return $"{tab}|{asset}|{S.Trade.Balance}|{string.Join(",", S.Trade.Offers().Select(o => o.Id[..6]))}|{string.Join(",", S.Trade.MySwaps().Select(x => x.Id[..6] + x.State))}|{mySecret}"; }
    protected override void Build()
    {
        var S = Svc.S;
        Col.Children.Add(Row(H("Exchange", 22), Sub($"You have {Trade.Coins(S.Trade.Balance)} coins")));
        Col.Children.Add(Sub("Trade coins for bitcoin, ether, stablecoins or cash, person to person. Crypto trades are atomic swaps: coins are locked until the other side pays."));
        var (bar, _) = Segments([("book", "Order book"), ("swaps", "Swaps"), ("how", "How it works")], tab, k => { tab = k; Redraw(); }); Col.Children.Add(bar);
        if (tab == "how") How(); else if (tab == "swaps") Swaps(); else Book();
        Col.Children.Add(Status);
    }
    void How() => Col.Children.Add(Card(H("Getting value in and out"),
        Text("1. Bitcoin over Lightning (atomic). Seller of coins: make a Lightning invoice for the agreed bitcoin, then Swaps → Lock coins with that invoice and the buyer’s user ID. Buyer: pay the invoice; your wallet shows the payment proof (preimage); paste it under Swaps → Claim. The seller is paid exactly when the buyer can claim. If the invoice isn’t paid before the deadline, the seller takes the coins back.", 14),
        Text("2. Other chains (atomic). Any chain with hash-locked contracts (Bitcoin scripts, Ethereum HTLC contracts, …): the coin seller clicks “Make a secret for me” and locks the coins (longer deadline). The other side locks their crypto on their chain to the seller with the same hash (shorter deadline). The seller claims it there, which reveals the secret; the buyer uses the secret here to claim the coins.", 14),
        Text("3. Cash, PayPal, bank (escrow). List coins (or buy them) in the Market under “Currency exchange” with an arbitration body: payment is held in escrow, and disputes go to its arbitrators.", 14),
        Text("4. Prices. The order book shows what people ask and bid for each currency; there is no central price.", 14),
        Sub("Your swap secrets stay on this device. Never paste a preimage anywhere but the claim box of the swap it belongs to.")));
    void Swaps()
    {
        var S = Svc.S; var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var list = new List<UIElement> { H("Your swaps") };
        foreach (var x in S.Trade.MySwaps())
        {
            var incoming = S.IsMine(x.To);
            var sp = new StackPanel { Spacing = 4 };
            sp.Children.Add(Text($"{(incoming ? "⬇" : "⬆")} {Trade.Coins(x.Amt)} coins {(incoming ? "from" : "to")} {S.NameOf(incoming ? x.From : x.To)} · {x.State} · deadline {DateTimeOffset.FromUnixTimeMilliseconds((long)x.Expires).LocalDateTime:g}", 14));
            sp.Children.Add(Code("hash " + x.Hash));
            if (incoming && x.State == "locked" && now < x.Expires) { var pre = Input("The secret (preimage), 64 hex", "", 64); sp.Children.Add(Row(pre, Btn("Claim", () => Run(() => S.Trade.Claim(x, pre.Text.Trim())), primary: true))); }
            if (!incoming && x.State == "locked" && now >= x.Expires) sp.Children.Add(Btn("Take the coins back", () => Run(() => S.Trade.Refund(x.Id))));
            list.Add(sp);
        }
        if (S.Trade.MySwaps().Count == 0) list.Add(Sub("No swaps yet."));
        Col.Children.Add(Card([.. list]));
        var to = Input("Who gets the coins (their user ID)", "", 64); var amt = Input("Coins", "", 20); amt.Width = 120; var hours = Input("Hours", "24", 3); hours.Width = 90;
        var invoice = new TextBox { PlaceholderText = "Paste the Lightning invoice you created for this trade (lnbc…). Its payment hash locks the coins: the buyer claims them with the proof of paying it.", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
        var info = Sub(""); var hash = Input("…or a sha256 hash (64 hex) for a swap on another chain", "", 64);
        invoice.TextChanged += (_, _) =>
        {
            if (invoice.Text.Trim().Length == 0) { info.Text = ""; return; }
            try { var d = Bolt11.Decode(invoice.Text); hash.Text = d.Hash; info.Text = $"⚡ {(d.Sats != null ? d.Sats.Value.ToString("N0") + " sats" : "any amount")}{(d.Desc.Length > 0 ? " · “" + d.Desc + "”" : "")} · expires {d.Expires.LocalDateTime:g} · its payment hash is filled in below"; }
            catch (Exception e) { info.Text = "✗ " + e.Message; }
        };
        Col.Children.Add(Card(H("Lock coins for a swap"), to, Row(amt, hours, Sub("hours to claim")), Sub("Lightning invoice"), invoice, info,
            Row(hash, Btn("Make a secret for me", () => _ = Status.Do("Making a secret…", async () => { var h = await S.Trade.MakeSecret(); mySecret = await S.Trade.SecretFor(h); hash.Text = h; }, ""))),
            mySecret != null ? Sub("Your secret for this hash (reveal it only to claim the other side): " + mySecret) : null!,
            Btn("Lock coins", () => Run(() => S.Trade.Lock(to.Text.Trim(), Trade.Units(amt.Text), hash.Text.Trim(), int.TryParse(hours.Text, out var hh) ? Math.Clamp(hh, 1, 720) : 24)), primary: true)));
    }
    void Book()
    {
        var S = Svc.S; var offers = S.Trade.Offers().Where(o => asset == "all" || o.Asset == asset).ToList();
        var a = Pick([("all", "All assets"), .. Trade.Assets.Select(x => (x, x))], asset); a.MinWidth = 160; a.SelectionChanged += (_, _) => { asset = a.Value(); Redraw(); };
        Col.Children.Add(Row(Sub("Show"), a));
        foreach (var side in new[] { "sell", "buy" })
        {
            var rows = new List<UIElement> { H(side == "sell" ? "People selling coins" : "People buying coins") };
            foreach (var o in offers.Where(o => o.Side == side))
                rows.Add(Row(new StackPanel { Children = { Text($"{Trade.Coins(o.Amt)} coins at {o.Rate} {o.Asset} each", 14), Sub($"{S.NameOf(o.From)} · {string.Join(", ", o.PayBy)}{((string?)o.O["note"] is { } n ? " · " + n : "")}") } },
                    S.IsMine(o.From) ? Btn("Withdraw", () => Run(() => S.Trade.EndOffer(o.Id))) : Btn("✉ Trade", () => App.Open(typeof(MessagesPage), o.From))));
            if (!offers.Any(o => o.Side == side)) rows.Add(Sub("None."));
            Col.Children.Add(Card([.. rows]));
        }
        var oside = Pick([("sell", "I sell coins"), ("buy", "I buy coins")]); var oamt = Input("How many coins", "", 20); oamt.Width = 150;
        var oasset = Pick(Trade.Assets.Select(x => (x, x)), "BTC"); oasset.MinWidth = 110; var orate = Input("Price per coin", "", 30); orate.Width = 150; var onote = Input("Terms (optional)", "", 500);
        var ms = new WrapRow();
        foreach (var m in Trade.Methods) { var cb = new CheckBox { Content = m, IsChecked = methods.Contains(m) }; cb.Click += (_, _) => { if (cb.IsChecked == true) methods.Add(m); else methods.Remove(m); }; ms.Children.Add(cb); }
        oside.MinWidth = 150;
        Col.Children.Add(Card(H("Post an offer"), Row(oside, oamt, Sub("for"), oasset, Sub("at"), orate), ms, onote,
            Btn("Post offer", () => Run(() => S.Trade.PostOffer(oside.Value(), oasset.Value(), Trade.Units(oamt.Text), orate.Text.Trim(), methods, onote.Text.Trim())), primary: true)));
    }
    void Run(Func<Task> f) => _ = Status.Do("Sending (a little proof of work)…", f, "Done: it counts once the next main-chain block includes it.");
}

/// <summary>Organizations: the list (with where they are), and creating one with its type, details and people.</summary>
public sealed partial class OrgsPage : DataPage
{
    string orgType = "arbitration"; readonly Dictionary<string, string> values = []; readonly List<(string Key, string Role, double? Share)> people = [];
    string name = "", jur = "", about = "";
    public OrgsPage() : base("Organizations") { }
    protected override string Key() => $"{orgType}|{people.Count}|{string.Join(",", Svc.S.Trade.Orgs().Select(o => o.Id[..6]))}";
    protected override void Build()
    {
        var S = Svc.S;
        Col.Children.Add(H("Organizations", 22));
        Col.Children.Add(Sub("Organizations moderate and arbitrate. Arbitration bodies take filings, run cases and rule on marketplace disputes."));
        var list = new StackPanel { Spacing = 6 };
        foreach (var c in S.Trade.Orgs())
        {
            var t = (string?)S.Genesis(c.Id)?["payload"]?["orgType"]; var pl = S.Chain(c.Id)?.Profile["place"];
            list.Children.Add(Row(Btn($"🏛 {c.Name} · {OrgKinds.Of(t).Name}", () => ChannelPage.Open(c.Id)), c.About.Length > 0 ? Sub(c.About) : null!, pl is JsonObject p ? TradeUi.MapLink(p, "📍 " + ((string?)p["label"] ?? "map")) : null!));
        }
        if (S.Trade.Orgs().Count == 0) list.Children.Add(Sub("No organizations yet."));
        Col.Children.Add(Card(list));
        var n = Input("Name", name, 60); n.TextChanged += (_, _) => name = n.Text;
        var j = Input("Jurisdiction (optional)", jur, 120); j.TextChanged += (_, _) => jur = j.Text;
        var a = new TextBox { PlaceholderText = "What it does", Text = about, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 }; a.TextChanged += (_, _) => about = a.Text;
        var type = Pick(OrgKinds.All.Select(k => (k.Key, k.Name)), orgType); type.SelectionChanged += (_, _) => { orgType = type.Value(); Redraw(); };
        var kind = OrgKinds.Of(orgType);
        var ppl = new StackPanel { Spacing = 4 };
        foreach (var q in people.ToList()) ppl.Children.Add(Row(Text($"{S.NameOf(q.Key)} · {q.Role}{(q.Share != null ? $" · {q.Share}%" : "")}", 14), Btn("Remove", () => { people.Remove(q); Redraw(); })));
        var pk = Input("Their user ID", "", 43); var pr = Input("Title, e.g. CEO, partner", "", 60); var ps = Input("Share %", "", 5); ps.Width = 100;
        Col.Children.Add(Card(H("Create an organization"), n, j, a, Sub("Type"), type, TradeUi.Fields(kind.Fields, values), Sub(kind.People), ppl,
            Row(pk, pr, ps, Btn("Add", () =>
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(pk.Text.Trim(), "^[A-Za-z0-9_-]{43}$")) { Status.Say("Paste their user ID"); return; }
                if (pr.Text.Trim().Length == 0) { Status.Say("Give a title"); return; }
                people.Add((pk.Text.Trim(), pr.Text.Trim(), double.TryParse(ps.Text, System.Globalization.CultureInfo.InvariantCulture, out var sh) ? Math.Clamp(sh, 0, 100) : null)); Redraw();
            })),
            Btn("Create", () => _ = Status.Do("Registering (proof of work on the main chain)…", async () =>
            {
                var x = new JsonObject { ["orgType"] = orgType };
                var det = OrgKinds.Details(orgType, values); if (det.Count > 0) x["details"] = det;
                if (people.Count > 0) x["people"] = new JsonArray(people.Select(q => { var p = new JsonObject { ["key"] = q.Key, ["role"] = q.Role }; if (q.Share != null) p["share"] = q.Share; return (JsonNode?)p; }).ToArray());
                var id = await S.Trade.CreateOrg(name, about, orgType, jur, x); ChannelPage.Open(id);
            }, ""), primary: true), Status));
    }
}

static class Fluent
{
    /// <summary>Configure an element inline.</summary>
    public static T Also<T>(this T x, Action<T> f) { f(x); return x; }
}
