using OpenTube.Client;
using System.Text.Json.Nodes;
using OpenTube.Core;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Controls;

/// <summary>
/// A computer's group stream (the studio): everyone connected combined into one picture; layout, waiting room, who's
/// on air (with previews), mute, disconnect, ticker, logo, and invited guests. Commands go to that computer (group.*).
/// </summary>
public sealed partial class GroupPanel : StackPanel
{
    readonly string node; JsonObject? g; readonly StackPanel c = new() { Spacing = 8 }; readonly TextBlock st = Ui.Status(); string tickText = ""; bool tickerSet, busy;
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(4) };
    public GroupPanel(string node, bool title = true)
    {
        this.node = node; Spacing = 8;
        if (title) Children.Add(H("👥 Group stream", 16));
        Children.Add(c);
        tick.Tick += (_, _) => _ = Run("group", new JsonObject { ["thumbs"] = true }, quiet: true);
        Loaded += (_, _) => { tick.Start(); _ = Run("group", new JsonObject { ["thumbs"] = true }, quiet: true); };
        Unloaded += (_, _) => tick.Stop();
        Draw();
    }
    async Task Run(string cmd, JsonObject args, bool quiet = false)
    {
        if (busy && quiet) return; busy = true;
        if (!quiet) st.Say("…");
        try
        {
            var r = await Svc.S.NodeCtl(node, cmd, args); g = r["data"] as JsonObject ?? r; if (!quiet) st.Say(null);
            if (!tickerSet && g["studio"]?["ticker"]?["text"] is { } t) { tickText = (string?)t ?? ""; tickerSet = true; }
        }
        catch (Exception e) { st.Say("✗ " + e.Message); }
        busy = false; Draw();
    }
    void Do(string cmd, JsonObject args) => _ = Run(cmd, args);
    void Draw()
    {
        c.Children.Clear();
        if (g == null) { c.Children.Add(Sub("Loading the group stream…")); c.Children.Add(st); return; }
        if (!Js.IsTrue(g["host"]))
        {
            c.Children.Add(Sub(Js.Num(g["until"]) is double until ? $"You’re invited by {(string?)g["invitedBy"]} until {DateTimeOffset.FromUnixTimeMilliseconds((long)until).LocalDateTime:g}. Stream with OBS using your key on that computer (Devices), or go live from this app into the group."
                : $"{(string?)g["invitedBy"]}’s group stream.")); c.Children.Add(st); return;
        }
        var on = Js.IsTrue(g["on"]); var ff = Js.IsTrue(g["ffmpeg"]);
        var onBox = new CheckBox { Content = on ? "On: everyone connected is combined into one picture" : "Off: each stream goes out on its own", IsChecked = on, IsEnabled = ff };
        onBox.Click += (_, _) => Do("group.set", new JsonObject { ["on"] = onBox.IsChecked == true });
        c.Children.Add(onBox);
        if (!ff) c.Children.Add(Sub("Needs ffmpeg on that computer."));
        if (on && g["studio"] is JsonObject S0)
        {
            var layout = (string?)S0["layout"] ?? "grid"; var spot = (string?)S0["spot"];
            var inputs = (g["inputs"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var air = inputs.Where(x => Js.IsTrue(x["onAir"])).ToList(); var wait = inputs.Where(x => !Js.IsTrue(x["onAir"])).ToList();
            var (bar, _) = Segments([("grid", "▦ Grid"), ("spotlight", "◧ Spotlight"), ("solo", "■ Solo")], layout, v => Do("group.studio", new JsonObject { ["layout"] = v }));
            var waiting = new CheckBox { Content = "Waiting room (people wait until you put them on air)", IsChecked = Js.IsTrue(S0["waiting"]) };
            waiting.Click += (_, _) => Do("group.studio", new JsonObject { ["waiting"] = waiting.IsChecked == true });
            c.Children.Add(Row(Sub("Layout"), bar)); c.Children.Add(waiting);
            c.Children.Add(Sub($"{(Js.IsTrue(g["live"]) ? "🔴 Live now · " : Js.IsTrue(g["starting"]) ? "⏳ Going live… · " : "")}On air ({air.Count})"));
            if (air.Count == 0) c.Children.Add(Sub("Nobody on air."));
            foreach (var x in air) c.Children.Add(Person(x, layout, spot));
            if (wait.Count > 0) { c.Children.Add(Sub($"⏳ Waiting room ({wait.Count})")); foreach (var x in wait) c.Children.Add(Person(x, layout, spot)); }
            if (inputs.Count == 0) c.Children.Add(Sub("Nobody is connected yet. Start Streaming in OBS (your key; key-2 … key-9 for more cameras), go live from the app into this group, or invite people below."));
            var tk = S0["ticker"] as JsonObject; var lg = S0["logo"] as JsonObject;
            var text = Input("Ticker text along the bottom", tickText, 500); text.TextChanged += (_, _) => tickText = text.Text;
            var speed = Pick([("2", "slow"), ("4", "normal"), ("7", "fast")], ((int)(Js.Num(tk?["speed"]) ?? 4)).ToString()); speed.MinWidth = 110;
            var show = new CheckBox { Content = "show", IsChecked = Js.IsTrue(tk?["on"]) };
            JsonObject Ticker(bool onT) => new() { ["ticker"] = new JsonObject { ["on"] = onT && tickText.Trim().Length > 0, ["text"] = tickText.Trim(), ["speed"] = int.TryParse(speed.Value(), out var sp) ? sp : 4 } };
            show.Click += (_, _) => Do("group.studio", Ticker(show.IsChecked == true));
            c.Children.Add(Card(H("Ticker", 14), text, Row(speed, show, Btn("Set", () => Do("group.studio", Ticker(true))))));
            var pos = Pick([("tl", "top left"), ("tr", "top right"), ("bl", "bottom left"), ("br", "bottom right")], (string?)lg?["pos"] ?? "tr"); pos.MinWidth = 140;
            pos.SelectionChanged += (_, _) => Do("group.studio", new JsonObject { ["logo"] = new JsonObject { ["pos"] = pos.Value() } });
            var showLogo = new CheckBox { Content = "show", IsChecked = Js.IsTrue(lg?["on"]) && Js.IsTrue(lg?["has"]), IsEnabled = Js.IsTrue(lg?["has"]) };
            showLogo.Click += (_, _) => Do("group.studio", new JsonObject { ["logo"] = new JsonObject { ["on"] = showLogo.IsChecked == true } });
            c.Children.Add(Card(H("Logo", 14), Sub(Js.IsTrue(lg?["own"]) ? "your image" : Js.IsTrue(lg?["has"]) ? "your channel’s avatar" : "no image yet (set a channel avatar, or choose one)"),
                Row(Btn("Choose an image", () => _ = st.Do("Opening…", async () => { if (await Files.OpenImage(320, 200000) is { } img) await Run("group.logo", new JsonObject { ["data"] = img }); }, "")), pos, showLogo,
                    Js.IsTrue(lg?["own"]) ? Btn("Use the avatar", () => Do("group.logo", new JsonObject { ["data"] = "" })) : null!)));
        }
        if (on)
        {
            var guests = (g["guests"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            var gl = new List<UIElement> { H("Guests", 14) };
            if (guests.Count == 0) gl.Add(Sub("None invited."));
            foreach (var u in guests)
            {
                var k = (string?)u["key"] ?? "";
                gl.Add(Row(Text((string?)u["name"] is { Length: > 0 } nm ? nm : Svc.S.NameOf(k), 14), Sub($"until {DateTimeOffset.FromUnixTimeMilliseconds((long)(Js.Num(u["until"]) ?? 0)).LocalDateTime:g}"), Btn("Cancel invite", () => Do("group.uninvite", new JsonObject { ["key"] = k }))));
            }
            var who = Input("Their @@name or user ID", "", 64); var hours = Pick([("2", "2 hours"), ("6", "6 hours"), ("24", "1 day"), ("72", "3 days"), ("168", "1 week")], "6"); hours.MinWidth = 120;
            gl.Add(Row(who, hours, Btn("Invite", () => Invite(who.Text, int.Parse(hours.Value())), primary: true)));
            gl.Add(Sub("Invited people get their own stream key on this computer for that time (it shows on their Devices page), or go live from their app into your group."));
            c.Children.Add(Card([.. gl]));
        }
        c.Children.Add(st);
    }
    UIElement Person(JsonObject x, string layout, string? spot)
    {
        var id = (string?)x["id"] ?? ""; var onAir = Js.IsTrue(x["onAir"]); var muted = Js.IsTrue(x["muted"]);
        var pic = new Image { Width = 96, Height = 54, Stretch = Stretch.UniformToFill };
        if ((string?)x["thumb"] is { Length: > 0 } th) _ = Thumb(pic, th);
        var info = new StackPanel { Children = { Text(((string?)x["kind"] == "web" ? "🌐 " : "🎥 ") + (string?)x["label"] + (muted ? " 🔇" : ""), 14),
            Sub((onAir ? (Js.IsTrue(x["shown"]) ? "on air" : "on air (not in this layout)") : "waiting") + " · " + Svc.Ago(Js.Num(x["since"]) ?? 0)) } };
        var acts = Row(Btn(onAir ? "Take off air" : "▶ Put on air", () => Do("group.air", new JsonObject { ["id"] = id, ["on"] = !onAir }), primary: !onAir),
            layout != "grid" ? Btn(spot == id ? "★ (big)" : "★", () => Do("group.studio", new JsonObject { ["spot"] = id })) : null!,
            Btn(muted ? "Unmute" : "Mute", () => Do("group.mute", new JsonObject { ["id"] = id, ["mute"] = !muted })),
            Btn("Disconnect", () => Do("group.kick", new JsonObject { ["id"] = id })),
            Js.IsTrue(x["guest"]) ? Btn("Cancel invite", () => Do("group.uninvite", new JsonObject { ["key"] = (string?)x["user"] })) : null!);
        return Card(Row(new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Child = pic }, info), acts);
    }
    static async Task Thumb(Image img, string th) => img.Source = await Svc.Thumb(th);
    void Invite(string v, int hours)
    {
        var S = Svc.S; v = v.Trim(); string? key = null; var name = "";
        if (System.Text.RegularExpressions.Regex.IsMatch(v, "^[A-Za-z0-9_-]{43}$")) key = S.Engine.Read(e => e.Store.Ledger.Resolve(v));
        else { var full = "@@" + v.TrimStart('@'); if (S.NameHolder(full.ToLowerInvariant()) is { } c) { key = S.Engine.Read(e => e.Store.Ledger.Resolve((string?)S.Genesis(c)?["author"] ?? "")); name = full; } }
        if (key == null) { st.Say("No one is called that. Paste their user ID."); return; }
        if (name.Length == 0) name = S.NameOf(key).StartsWith("User ") ? "" : S.NameOf(key);
        Do("group.invite", new JsonObject { ["key"] = key, ["hours"] = hours, ["name"] = name });
    }
}
