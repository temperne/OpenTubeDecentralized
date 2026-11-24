using OpenTube.Client;
using OpenTube.Uno.Controls;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Pages;

/// <summary>
/// The map: find businesses, organizations, channels, housing and listings near you or anywhere — search, filter by
/// category, tap a pin to see it with its rating, and open it (reviews, details, people). Add a place by tapping where
/// it is.
/// </summary>
public sealed partial class PlacesPage : Page
{
    readonly MapView map = new() { Height = 420 };
    readonly StackPanel list = new() { Spacing = 8 }, sheet = new() { Spacing = 6 }, add = new() { Spacing = 8 };
    readonly TextBox search = new() { PlaceholderText = "Search places: name, category, address", MinWidth = 240 };
    readonly ComboBox cat = Pick([("", "All categories"), ("listing", "🛍 For sale"), ("housing", "🏠 Housing"), .. Places.Categories.Select(c => (c, c))]);
    readonly TextBlock status = Status();
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(5) };
    string shown = ""; bool fitted, adding, settingHere; (double Lat, double Lon)? picked, here;
    public PlacesPage()
    {
        Background = Application.Current.Resources["BackgroundBrush"] as Brush;
        var (scroll, col) = Ui.Page(1200); Content = Ui.WithBack("Map", scroll);
        cat.MinWidth = 180;
        search.TextChanged += (_, _) => Refresh(true); cat.SelectionChanged += (_, _) => Refresh(true);
        col.Children.Add(Row(search, cat, Btn("📍 Near me", () => _ = NearMe(), id: "places-near"), Btn("＋ Add a place", () => { adding = !adding; picked = null; DrawAdd(); }, primary: true, id: "places-add")));
        col.Children.Add(add);
        col.Children.Add(map); col.Children.Add(sheet); col.Children.Add(list); col.Children.Add(status);
        map.PinTapped += p => Show((Places.Place)p.Tag!);
        map.MapTapped += (la, lo) =>
        {
            if (settingHere) { settingHere = false; _ = SetHere(la, lo); return; }
            if (!adding) return; picked = (la, lo); DrawAdd(); Refresh(true);
        };
        tick.Tick += (_, _) => Refresh(false);
        Loaded += async (_, _) =>
        {
            tick.Start(); Refresh(false);
            try { if (await Svc.S.Social.Setting("here") is System.Text.Json.Nodes.JsonObject h && OpenTube.Core.Js.Num(h["lat"]) is double la && OpenTube.Core.Js.Num(h["lon"]) is double lo) { here = (la, lo); Refresh(true); } } catch { }
        }; Unloaded += (_, _) => tick.Stop();
    }
    static string Icon(Places.Place p) => p.Kind switch { "housing" => "🏠", "listing" => "🛍", "channel" => "🏪", _ => p.Category switch { "Restaurant" => "🍽", "Café" => "☕", "Bar" => "🍺", "Shop" or "Grocery" => "🛒", "Hotel" => "🛏", "Health" => "⚕", "Auto" => "🚗", "Park" => "🌳", "Government" => "🏛", "Education" => "🎓", _ => "📍" } };
    List<Places.Place> Matching()
    {
        var q = search.Text.Trim(); var c = cat.Value();
        return Svc.S.All().Where(p => (c == "" || p.Kind == c || p.Category == c) && (q.Length == 0 || $"{p.Name} {p.Category} {p.Label} {p.About}".Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => here is { } h ? Places.Km(h.Lat, h.Lon, p.Lat, p.Lon) : 0).ThenByDescending(p => p.Stars * Math.Log(1 + p.Reviews)).ThenBy(p => p.Name).ToList();
    }
    void Refresh(bool force)
    {
        var S = Svc.S; if (S == null || !S.Ready) return;
        var ps = Matching(); var key = string.Join(",", ps.Select(p => p.Id[..6] + p.Reviews + p.Stars)) + picked;
        if (!force && key == shown) return; shown = key;
        var pins = ps.Select(p => new MapView.Pin(p.Lat, p.Lon, Icon(p), p.Name, p)).ToList();
        if (picked is { } pk) pins.Add(new MapView.Pin(pk.Lat, pk.Lon, "📌", "The new place"));
        if (here is { } me) pins.Add(new MapView.Pin(me.Lat, me.Lon, "🔵", "You are here"));
        map.SetPins(pins, !fitted || force && picked == null); fitted = fitted || ps.Count > 0;
        list.Children.Clear();
        list.Children.Add(H($"{ps.Count} place{(ps.Count == 1 ? "" : "s")}", 16));
        if (ps.Count == 0) list.Children.Add(Sub("No places known to this device match. Add one: ＋ Add a place."));
        foreach (var p in ps.Take(100)) list.Children.Add(Item(p));
    }
    UIElement Item(Places.Place p)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(Text($"{Icon(p)} {p.Name}", 15));
        sp.Children.Add(Sub($"{(here is { } h ? Dist(Places.Km(h.Lat, h.Lon, p.Lat, p.Lon)) + " · " : "")}{p.Category}{(p.Listing ? "" : " · " + Places.StarText(p.Stars) + (p.Reviews > 0 ? $" ({p.Reviews})" : ""))}{(p.Label.Length > 0 ? " · " + p.Label : "")}"));
        var b = new Button { Content = sp, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Surface, Foreground = (Brush)Application.Current.Resources["OnSurfaceBrush"], CornerRadius = new CornerRadius(10), Padding = new Thickness(12) };
        b.Click += (_, _) => { map.CenterOn(p.Lat, p.Lon, 16); Show(p); };
        return b;
    }
    static string Dist(double km) => km < 1 ? $"{km * 1000:0} m" : km < 10 ? $"{km:0.0} km" : $"{km:0} km";
    /// <summary>Where you are: the device's location (asking first), else tap the map where you are. Kept on this device only.</summary>
    async Task NearMe()
    {
        status.Say("Finding where you are…");
        try
        {
            var access = await Windows.Devices.Geolocation.Geolocator.RequestAccessAsync();
            if (access != Windows.Devices.Geolocation.GeolocationAccessStatus.Allowed) throw new InvalidOperationException("location isn’t allowed");
            var pos = await new Windows.Devices.Geolocation.Geolocator { DesiredAccuracyInMeters = 100 }.GetGeopositionAsync();
            await SetHere(pos.Coordinate.Point.Position.Latitude, pos.Coordinate.Point.Position.Longitude);
        }
        catch (Exception e) { settingHere = true; status.Say($"Couldn’t get this device’s location ({e.Message}). Tap the map where you are."); }
    }
    async Task SetHere(double la, double lo)
    {
        here = (Math.Round(la, 5), Math.Round(lo, 5)); map.CenterOn(here.Value.Lat, here.Value.Lon, 14); fitted = true;
        try { await Svc.S.Social.SetSetting("here", new System.Text.Json.Nodes.JsonObject { ["lat"] = here.Value.Lat, ["lon"] = here.Value.Lon }); } catch { }
        status.Say("Nearest first. (Your location stays on this device.)"); Refresh(true);
        map.CenterOn(here.Value.Lat, here.Value.Lon, 14);
    }
    void Open(Places.Place p) { if (p.Listing) App.Open(typeof(MarketItemPage), p.Id); else ChannelPage.Open(p.Id); }
    void Show(Places.Place p)
    {
        sheet.Children.Clear();
        var c = new List<UIElement> { H($"{Icon(p)} {p.Name}"), Sub(p.Category + (p.Label.Length > 0 ? " · 📍 " + p.Label : "")) };
        if (!p.Listing) c.Add(Text(Places.StarText(p.Stars) + (p.Reviews > 0 ? $" · {p.Reviews} review{(p.Reviews == 1 ? "" : "s")}" : ""), 14));
        if (p.About.Length > 0) c.Add(Text(p.About.Length > 240 ? p.About[..240] + "…" : p.About, 14));
        c.Add(Row(Btn(p.Listing ? "Open the listing" : "Open · reviews · details", () => Open(p), primary: true), Btn("Close", () => sheet.Children.Clear())));
        sheet.Children.Add(Card([.. c]));
    }
    readonly TextBox name = Input("Name", "", 60), about = new() { PlaceholderText = "What it is, what it offers", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 },
        address = Input("Address", "", 160), hours = Input("Opening hours, e.g. Mon–Fri 9–17", "", 120), phone = Input("Phone (optional)", "", 40), web = Input("Website (optional)", "", 200);
    readonly ComboBox pcat = Pick(Places.Categories.Select(c => (c, c)), "Shop");
    readonly ComboBox ptype = Pick(OrgKinds.All.Select(k => (k.Key, k.Name)), "sole");
    void DrawAdd()
    {
        add.Children.Clear(); if (!adding) return;
        add.Children.Add(Card(H("Add a place"), Sub(picked is { } p ? $"📌 Spot picked: {p.Lat:0.00000}, {p.Lon:0.00000} (tap the map again to move it)" : "Tap the map where it is (zoom in first to be exact)."),
            name, Row(Sub("Category"), pcat, Sub("Run as"), ptype), about, address, hours, Row(phone, web),
            Row(Btn("Add it", () =>
            {
                if (picked is not { } at) { status.Say("Tap the map where it is first."); return; }
                _ = status.Do("Registering the place (proof of work on the main chain)…", async () =>
                {
                    var id = await Svc.S.AddPlace(name.Text, pcat.Value(), about.Text, at.Lat, at.Lon, address.Text, hours.Text, phone.Text, web.Text, ptype.Value());
                    adding = false; picked = null; DrawAdd(); ChannelPage.Open(id);
                }, "Added: it shows on everyone’s map once registered.");
            }, primary: true, id: "places-save"), Btn("Cancel", () => { adding = false; picked = null; DrawAdd(); Refresh(true); })),
            Sub("A place is an organization you run: you can edit it, add people and answer reviews. It costs a little proof of work.")));
    }
}
