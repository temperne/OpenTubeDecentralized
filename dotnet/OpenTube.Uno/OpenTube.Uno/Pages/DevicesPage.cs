using System.Text.Json.Nodes;
using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Uno.Controls;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Pages;

/// <summary>
/// Devices: your computers (and ones shared with you): what each is doing, testing the connection, its files, the
/// people you share it with, its log, public access and updating it; adding a computer (one command); and what your
/// computers earned.
/// </summary>
public sealed partial class DevicesPage : DataPage
{
    string? openNode, openWhat; JsonObject? view; string? grant;
    readonly Dictionary<string, string> tests = [];
    public DevicesPage() : base("Devices") { }
    protected override string Key() => string.Join(",", Svc.S.MyNodes().Select(n => n.Node + (Svc.S.Info(n.Node)?.ToJsonString().GetHashCode() ?? 0))) + $"|{openNode}{openWhat}{view?.ToJsonString().GetHashCode()}|{string.Join(",", tests.Values)}|{grant}|{Svc.S.Earnings().Count}";
    static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    protected override void Build()
    {
        var S = Svc.S;
        Col.Children.Add(Row(H("Devices", 22), Btn("Network & diagnostics", () => App.Open(typeof(NetworkPage)))));
        Col.Children.Add(Sub("Your computers run OpenTube: they keep the network, host your videos, receive OBS, and watch for you while your phone sleeps."));
        var nodes = S.MyNodes();
        if (nodes.Count == 0) Col.Children.Add(Sub("No computers yet. Add one below."));
        foreach (var (n, perms, at) in nodes)
        {
            var node = n; var info = S.Info(n); var online = Now - at < 120000; var owner = perms.Contains('o') && perms.Contains('m') && S.OwnsNode(n);
            var items = new List<UIElement>
            {
                Row(H((string?)info?["name"] ?? "computer", 17), Sub(online ? "● online" : "offline · last seen " + Svc.Ago(at))),
                Sub((owner ? "yours" : perms.Contains('g') ? "you’re invited to a group stream on it" : "shared with you") + " · " + string.Join(" · ", S.Roles(n).Select(Devices.RoleName)) + ((string?)info?["public"] is { } pub ? " · " + pub : "")),
                Sub(Devices.InfoLine(info)),
            };
            if (Devices.HostLine(info) is { } hl) items.Add(Sub(hl));
            if (Devices.ObsLine(info) is { } ol) items.Add(Sub(ol));
            var acts = new WrapRow();
            acts.Children.Add(Btn("Test connection", () => _ = Test(node)));
            if (owner)
            {
                acts.Children.Add(Btn("Files", () => _ = OpenView(node, "files")));
                acts.Children.Add(Btn("People", () => _ = OpenView(node, "users")));
                acts.Children.Add(Btn("Log", () => _ = OpenView(node, "logs")));
                acts.Children.Add(Btn("Update now", () => _ = Status.Do("Asking the computer…", () => S.NodeCtl(node, "update", null), "Updating: it restarts when it’s done.")));
                var pubMode = Pick([("", "Public access…"), ("auto", "Auto (own HTTPS, else a tunnel)"), ("tunnel", "Always a tunnel"), ("off", "Off")]); pubMode.MinWidth = 200; pubMode.HorizontalAlignment = HorizontalAlignment.Left;
                pubMode.SelectionChanged += (_, _) => { if (pubMode.Value().Length > 0) _ = Status.Do("Asking the computer…", () => S.NodeCtl(node, "public.set", new JsonObject { ["mode"] = pubMode.Value() })); };
                acts.Children.Add(pubMode);
            }
            items.Add(acts);
            if (tests.TryGetValue(n, out var tr)) items.Add(Sub(tr));
            if (openNode == n && view != null) items.Add(ViewOf(node));
            Col.Children.Add(Card([.. items]));
        }
        Col.Children.Add(Status);
        // ---- add a computer
        var label = Input("Its name (e.g. Home server)", max: 60);
        var add = new List<UIElement> { H("Add a computer"), Sub("Run one command on it (Linux, Mac, or Windows PowerShell). It installs .NET if needed, runs OpenTube as yours, and updates itself. Don’t share the command: whoever runs it runs a computer that is yours."), label,
            Btn("Make the command", () => { grant = S.Trade.Grant(label.Text); Redraw(); }, primary: true, id: "devices-make") };
        if (grant != null)
        {
            var (unix, win) = S.AddCommands(grant);
            add.Add(Sub("Linux or Mac:")); add.Add(Code(unix)); add.Add(Btn("Copy (Linux/Mac)", () => { Copy(unix); Status.Say("Command copied. Paste it into a terminal on that computer."); }));
            add.Add(Sub("Windows (PowerShell):")); add.Add(Code(win)); add.Add(Btn("Copy (Windows)", () => { Copy(win); Status.Say("Command copied. Paste it into PowerShell on that computer."); }));
        }
        Col.Children.Add(Card([.. add]));
        // ---- earnings
        var rows = S.Earnings(); var earn = new StackPanel { Spacing = 4 };
        if (rows.Count == 0) earn.Children.Add(Sub("Nothing yet."));
        else
        {
            earn.Children.Add(Sub($"Last {rows.Count}: {ChainActions.Coins(rows.Sum(x => x.Amount))} coins"));
            foreach (var x in rows) earn.Children.Add(Text($"+{ChainActions.Coins(x.Amount)}  {Devices.ResName(x.Res)} · {Devices.ResAmount(x.Res, x.Units)}" + (x.Key != S.Me.pub ? $" → {S.NameOf(x.Key)}" : "") + $"  ·  {DateTimeOffset.FromUnixTimeMilliseconds((long)(x.Epoch * Coin.EpochMs)).ToLocalTime():MMM d HH:mm}", 13));
        }
        Col.Children.Add(Card(H("Earned by your computers"), Sub($"Every {Coin.EpochMs / 60000} minutes the network pays computers for what they provided, from signed receipts by the people they served: " + string.Join(", ", Coin.ServeRes.Select(r => $"{ChainActions.Coins(r.Pool)} new coins by {Devices.ResName(r.Res)}")) + $", plus {Coin.FeeShare * 100}% of the period’s watch, like and post payments. Payouts land about an hour after the period."), earn));
    }
    static void Copy(string text) { var dp = new Windows.ApplicationModel.DataTransfer.DataPackage(); dp.SetText(text); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp); }

    async Task Test(string n)
    {
        var S = Svc.S; tests[n] = "Testing…"; Redraw();
        var res = new List<string>();
        try { var t0 = DateTime.UtcNow; await S.NodeCtl(n, "logs", new JsonObject { ["n"] = 1 }); res.Add($"through the network ✓ ({(int)(DateTime.UtcNow - t0).TotalMilliseconds} ms)"); }
        catch (Exception e) { res.Add("through the network ✗ " + e.Message); }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        foreach (var a in S.Addresses(n).Where(a => !Svc.Site.StartsWith("https://") || a.StartsWith("https://")).Distinct())
        {
            try { var j = JsonNode.Parse(await http.GetStringAsync(a.TrimEnd('/') + "/status")); res.Add($"{a} ✓ (main #{Js.Num(j?["height"])})"); }
            catch { res.Add(a + " ✗"); }
        }
        tests[n] = string.Join(" · ", res) + (res.Any(x => x.Contains('✗')) ? ". ✗ is normal for addresses inside another home network; the computer still syncs and hosts through the network." : "");
        Redraw();
    }
    async Task OpenView(string n, string what)
    {
        openNode = n; openWhat = what; view = null; Status.Say("Asking the computer…");
        try { view = await Svc.S.NodeCtl(n, what, what == "logs" ? new JsonObject { ["n"] = 120 } : null); Status.Say(null); } catch (Exception e) { Status.Say(e.Message); }
        Redraw();
    }
    UIElement ViewOf(string n)
    {
        var S = Svc.S; var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(Row(H(openWhat switch { "files" => "Files", "users" => "People", _ => "Log" }, 15), Btn("Close", () => { openNode = null; view = null; Redraw(); })));
        if (openWhat == "files")
        {
            foreach (var ch in view!["channels"] as JsonArray ?? [])
            {
                sp.Children.Add(Text((string?)ch?["name"] ?? "", 14));
                foreach (var v in ch?["videos"] as JsonArray ?? [])
                {
                    var ih = (string?)v?["ih"] ?? ""; var pinned = Js.IsTrue(v?["pinned"]);
                    sp.Children.Add(Row(Sub(((string?)v?["title"] ?? ih[..8]) + (Js.IsTrue(v?["complete"]) ? " · complete" : " · downloading") + (pinned ? " · pinned" : "")),
                        Btn(pinned ? "Unpin" : "Pin", () => _ = Status.Do("Asking the computer…", async () => { await S.NodeCtl(n, "pin", new JsonObject { ["ih"] = ih, ["on"] = !pinned }); await OpenView(n, "files"); })),
                        Btn("Delete here", () => _ = Status.Do("Asking the computer…", async () => { await S.NodeCtl(n, "drop", new JsonObject { ["ih"] = ih }); await OpenView(n, "files"); }))));
                }
            }
            sp.Children.Add(Sub($"Limit per joined channel: {Js.Num(view["capGB"]) ?? 0} GB"));
        }
        else if (openWhat == "users")
        {
            foreach (var (k, u) in view!["users"] as JsonObject ?? [])
            {
                var key = k;
                sp.Children.Add(Row(Text((string?)u?["name"] is { Length: > 0 } nm ? nm : S.NameOf(k), 14), Sub(string.Join(", ", (u?["can"] as JsonArray ?? []).Select(x => (string?)x))),
                    Btn("Remove", () => _ = Share(n, key, "", []))));
            }
            var who = Input("Their user ID", max: 43); var name = Input("Name", max: 60);
            var obs = new CheckBox { Content = "obs", IsChecked = true }; var host = new CheckBox { Content = "host" }; var manage = new CheckBox { Content = "manage" };
            sp.Children.Add(who); sp.Children.Add(name); sp.Children.Add(Row(obs, host, manage, Btn("Share", () => _ = Share(n, who.Text.Trim(), name.Text, new[] { (obs, "obs"), (host, "host"), (manage, "manage") }.Where(x => x.Item1.IsChecked == true).Select(x => x.Item2).ToList()), primary: true)));
        }
        else sp.Children.Add(new ScrollViewer { MaxHeight = 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Code(string.Join("\n", (view!["lines"] as JsonArray ?? []).Select(x => (string?)x).TakeLast(80))) });
        return new Border { BorderBrush = Ui.Muted, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Child = sp };
    }
    Task Share(string n, string key, string name, List<string> can) => Status.Do("Asking the computer…", async () =>
    {
        view = await Svc.S.NodeCtl(n, "users.set", new JsonObject { ["key"] = key, ["name"] = name, ["can"] = new JsonArray(can.Select(c => (JsonNode?)c).ToArray()) }); openWhat = "users"; Redraw();
    });
}
