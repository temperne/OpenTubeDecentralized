using System.Text.Json.Nodes;
using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Controls;

/// <summary>
/// Flash computers: your own cloud login on a computer of yours (sealed to it), starting a server for a stream,
/// adding them as viewers grow, and the ones running (keep, shut down). That computer starts them, keeps their
/// recordings, and deletes them when they're done.
/// </summary>
public sealed partial class FlashPanel : StackPanel
{
    static readonly Dictionary<string, string> Names = new() { ["aws"] = "Amazon Web Services (EC2)", ["gcp"] = "Google Cloud (Compute Engine)", ["hetzner"] = "Hetzner Cloud", ["digitalocean"] = "DigitalOcean", ["vultr"] = "Vultr", ["linode"] = "Akamai / Linode", ["scaleway"] = "Scaleway", ["custom"] = "Custom commands (any provider)", ["local"] = "This computer itself (test)" };
    static string Name(string? p) => p != null && Names.TryGetValue(p, out var n) ? n : p ?? "";
    static string KeyHint(string p) => p switch { "aws" => "ACCESS_KEY_ID:SECRET_ACCESS_KEY", "gcp" => "paste the service account’s JSON key", "scaleway" => "PROJECT_ID:SECRET_KEY", "custom" or "local" => "(not needed)", _ => "API token" };
    readonly string node; JsonObject? data; bool editing, confirmRemove; string? confirmShut; readonly TextBlock st = Ui.Status(); readonly StackPanel c = new() { Spacing = 8 };
    public FlashPanel(string node)
    {
        this.node = node; Spacing = 8;
        Children.Add(H("⚡ Flash computers", 16)); Children.Add(Sub("Cloud servers for a stream, on your own cloud account.")); Children.Add(c); Children.Add(st);
        Loaded += (_, _) => _ = Run("flash", new JsonObject());
    }
    async Task Run(string cmd, JsonObject args)
    {
        st.Say("…");
        try { var r = await Svc.S.NodeCtl(node, cmd, args); data = r["data"] as JsonObject ?? r; st.Say(null); }
        catch (Exception e) { st.Say("✗ " + e.Message); }
        Draw();
    }
    void Draw()
    {
        c.Children.Clear(); if (data == null) { c.Children.Add(Sub("Loading…")); return; }
        var M = data["mine"] as JsonObject;
        c.Children.Add(Js.IsTrue(M?["ready"])
            ? Row(Sub($"your login: {Name((string?)M!["provider"])}{((string?)M["plan"] is { Length: > 0 } pl ? " · " + pl : "")}{((string?)M["region"] is { Length: > 0 } rg ? " · " + rg : "")}"), Btn("⚡ Start a flash computer", () => { st.Say("Creating the server…"); _ = Run("flash.start", new JsonObject { ["grant"] = Svc.S.Trade.Grant("flash") }); }, primary: true))
            : Sub("Attach your cloud login to start servers on your own account."));
        if (M != null && !editing) c.Children.Add(Row(Btn("Edit login", () => { editing = true; Draw(); }), Btn(confirmRemove ? "Really remove?" : "Remove login", () => { if (!confirmRemove) { confirmRemove = true; Draw(); return; } confirmRemove = false; Save(true, null); })));
        if (M == null || editing) c.Children.Add(Editor(M));
        if (Js.IsTrue(M?["ready"]))
        {
            var sc = data["scale"]; var vw = data["viewers"];
            var on = new CheckBox { Content = "Add flash computers as viewers grow", IsChecked = Js.IsTrue(sc?["on"]) };
            var per = Input("viewers each", ((int)(Js.Num(sc?["per"]) ?? 25)).ToString(), 5); per.Width = 120; var max = Input("up to", ((int)(Js.Num(sc?["max"]) ?? 3)).ToString(), 2); max.Width = 90;
            void Scale() => _ = Run("flash.scale", new JsonObject { ["on"] = on.IsChecked == true, ["per"] = int.TryParse(per.Text, out var p) ? Math.Max(5, p) : 25, ["max"] = int.TryParse(max.Text, out var m) ? Math.Clamp(m, 1, 10) : 3 });
            on.Click += (_, _) => Scale();
            c.Children.Add(Card(Text($"👁 {Js.Num(vw?["total"]) ?? 0} watching your live streams now", 14), on, Row(Sub("one per"), per, Sub("viewers, up to"), max, Btn("Save", Scale))));
        }
        foreach (var ch in (data["children"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var state = (string?)ch["state"]; var tag = (string?)ch["tag"] ?? "";
            var line = state == "deleted" ? $"deleted {Ago(Js.Num(ch["deletedAt"]))}: {(string?)ch["why"]}" : state == "starting" ? "installing (2–4 min)…" : (string?)ch["busy"] is { } b ? "busy: " + b : ch["idleSince"] != null ? $"idle since {Ago(Js.Num(ch["idleSince"]))}" : state ?? "";
            var row = Row(Text($"{(state == "deleted" ? "⚪" : state == "running" ? "🟢" : "🟡")} {tag}{(!Js.IsTrue(ch["mine"]) ? " (someone else’s)" : "")}", 14), Sub(line));
            if (state != "deleted")
            {
                var keep = new CheckBox { Content = "keep", IsChecked = Js.IsTrue(ch["keep"]) }; keep.Click += (_, _) => _ = Run("flash.keep", new JsonObject { ["tag"] = tag, ["keep"] = keep.IsChecked == true });
                row.Children.Add(keep);
                row.Children.Add(Btn(confirmShut == tag ? "Really shut down?" : "Shut down", () => { if (confirmShut != tag) { confirmShut = tag; Draw(); return; } confirmShut = null; _ = Run("flash.stop", new JsonObject { ["tag"] = tag }); }));
            }
            c.Children.Add(row);
        }
    }
    static string Ago(double? ms) => ms == null ? "" : Svc.Ago(ms.Value);
    UIElement Editor(JsonObject? M)
    {
        var provs = (data!["providers"] as JsonArray ?? []).Select(x => (string?)x).OfType<string>().ToList();
        var prov = Pick(provs.Select(p => (p, Name(p))), (string?)M?["provider"] ?? "hetzner"); prov.MinWidth = 240;
        var token = new TextBox { PlaceholderText = KeyHint(prov.Value()), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 40 };
        var def = data["defaults"]?[prov.Value()];
        var region = Input((string?)def?["region"] ?? "region", (string?)M?["region"] ?? "", 60); var plan = Input((string?)def?["plan"] ?? "plan / size", (string?)M?["plan"] ?? "", 60); var image = Input((string?)def?["image"] ?? "image", (string?)M?["image"] ?? "", 120);
        prov.SelectionChanged += (_, _) => { token.PlaceholderText = KeyHint(prov.Value()); var d2 = data["defaults"]?[prov.Value()]; region.PlaceholderText = (string?)d2?["region"] ?? "region"; plan.PlaceholderText = (string?)d2?["plan"] ?? "plan / size"; image.PlaceholderText = (string?)d2?["image"] ?? "image"; };
        var billing = Pick([("auto", "billing: as the provider bills"), ("hour", "billed by the hour"), ("second", "billed by the second")], (string?)M?["billing"] ?? "auto"); billing.MinWidth = 220;
        var maxH = Input("at most (hours)", ((int)(Js.Num(M?["maxHours"]) ?? 6)).ToString(), 2); maxH.Width = 120; var idle = Input("idle (minutes)", ((int)(Js.Num(M?["idleMin"]) ?? 5)).ToString(), 3); idle.Width = 120;
        TextBox? cCmd = null, dCmd = null;
        var items = new List<UIElement> { prov, token };
        if (Js.IsTrue(M?["tokenSet"])) items.Add(Sub("A key is saved: leave the field empty to keep it."));
        items.Add(Row(region, plan, image)); items.Add(Row(billing, maxH, idle));
        if (Js.IsTrue(data["owner"])) { cCmd = Input("create command (gets OT_NAME, OT_USERDATA_FILE; prints {\"id\":…,\"ip\":…})", "", 500); dCmd = Input("delete command (gets OT_ID)", "", 500); items.Add(cCmd); items.Add(dCmd); }
        items.Add(Row(Btn("Save (sealed for this computer)", () => Save(false, new JsonObject { ["provider"] = prov.Value(), ["token"] = token.Text.Trim(), ["region"] = region.Text, ["plan"] = plan.Text, ["image"] = image.Text, ["billing"] = billing.Value(),
            ["maxHours"] = int.TryParse(maxH.Text, out var h) ? Math.Clamp(h, 1, 48) : 6, ["idleMin"] = int.TryParse(idle.Text, out var m) ? Math.Clamp(m, 2, 240) : 5, ["createCmd"] = cCmd?.Text, ["deleteCmd"] = dCmd?.Text }), primary: true),
            M != null ? Btn("Cancel", () => { editing = false; Draw(); }) : null!));
        items.Add(Sub("Your key is encrypted on this device for this computer only; the peers and tunnels that carry it see only ciphertext. Servers are billed to your cloud account."));
        return Card([.. items]);
    }
    void Save(bool off, JsonObject? obj)
    {
        if (data?["enc"] is not JsonValue ev) { st.Say("✗ this computer has no encryption key"); return; }
        st.Say("Sealing your login for this computer…"); editing = false;
        if (obj != null) { if (obj["createCmd"] == null) obj.Remove("createCmd"); if (obj["deleteCmd"] == null) obj.Remove("deleteCmd"); }
        _ = Run("flash.setup", new JsonObject { ["sealed"] = Sealing.For((string)ev!, off ? new JsonObject { ["off"] = true } : obj!) });
    }
}
