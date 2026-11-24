using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Pages;

/// <summary>
/// Domains: claim a subdomain (tucson.opentuber.com → your node, opening the page you choose); and for a domain you
/// own, its zone: approve or decline claims, add or remove names, and how to point the domain at your node.
/// </summary>
public sealed partial class DomainsPage : DataPage
{
    string domain = Network.Domain;
    public DomainsPage() : base("Domains") { }
    protected override void Param(object? p) { if (p is string s && s.Contains('.')) domain = s; }
    protected override string Key()
    {
        var S = Svc.S; var z = S.ZoneOf(domain);
        return $"{domain}|{z}|{string.Join(",", S.ZonesAll().Select(x => x.Id[..6]))}|{(z == null ? "" : string.Join(",", S.Records(z).Values.Select(r => r.Hash[..6])) + "|" + string.Join(",", S.Claims(z).Select(c => c.Hash[..6])))}";
    }
    protected override void Build()
    {
        var S = Svc.S; var zone = S.ZoneOf(domain); var mine = zone != null && S.IsMine(S.Chain(zone)?.Owner);
        Col.Children.Add(H("Domains", 22));
        var pick = Pick([.. S.ZonesAll().Select(z => z.Name).Append(Network.Domain).Distinct().Select(n => (n, n))], domain); pick.MinWidth = 220;
        pick.SelectionChanged += (_, _) => { domain = pick.Value(); Redraw(); };
        Col.Children.Add(Row(Sub("Domain"), pick));
        Col.Children.Add(Sub($"Short links on {domain}: a name.{domain} reaches the node it names and opens the page it names. Registered names work without asking: " +
            string.Join(", ", Zones.Wildcards.Select(w => $"name.{w.Letter}.{domain} ({w.Kind})")) + "."));
        if (zone == null)
        {
            Col.Children.Add(Card(Text($"{domain} has no zone on the network yet."), Sub("If you own this domain, start its zone below.")));
        }
        else
        {
            var recs = S.Records(zone).Values.OrderBy(r => r.Label).ToList();
            var rl = new List<UIElement> { H($"Names on {domain} ({recs.Count})") };
            foreach (var r in recs)
                rl.Add(Row(Text($"{r.Label}.{domain}", 14), Sub($"→ {(r.Target.Length > 0 ? r.Target : "the domain's node")} · opens {(r.Page.Length > 0 ? r.Page : "the home page")}"),
                    mine ? Btn("Remove", () => _ = Status.Do("Removing…", async () => { await S.SetRecord(zone, r.Label, "", "", on: false); Redraw(); }, "Removed.")) : null!));
            if (recs.Count == 0) rl.Add(Sub("None yet."));
            Col.Children.Add(Card([.. rl]));
            if (mine)
            {
                var claims = S.Claims(zone);
                var cl = new List<UIElement> { H($"Waiting for you ({claims.Count})") };
                foreach (var c in claims)
                    cl.Add(new StackPanel { Spacing = 4, Children = {
                        Text($"{c.Label}.{domain} → {(c.Target.Length > 0 ? c.Target : "the domain's node")} · opens {(c.Page.Length > 0 ? c.Page : "the home page")}", 14),
                        Sub($"asked by {S.NameOf(c.Author)} · {Svc.Ago(c.Ts)}{(c.Note.Length > 0 ? " · “" + c.Note + "”" : "")}"),
                        Row(Btn("Approve", () => _ = Status.Do("Approving…", async () => { await S.SetRecord(zone, c.Label, c.Target, c.Page, claim: c.Hash); Redraw(); }, "Approved: it answers within a minute."), primary: true),
                            Btn("Decline", () => _ = Status.Do("Declining…", async () => { await S.SetRecord(zone, c.Label, "", "", on: false, claim: c.Hash); Redraw(); }, "Declined."))) } });
                if (claims.Count == 0) cl.Add(Sub("No claims waiting."));
                Col.Children.Add(Card([.. cl]));
                var l = Input("Name (e.g. tucson)", "", 63); var t = Input("Address: IP or host name (empty: your node)", "", 200); var pg = Input("Page (e.g. c/…, n/@name; empty: home)", "", 200);
                Col.Children.Add(Card(H("Add a name"), l, t, pg, Btn("Add", () => _ = Status.Do("Adding…", async () => { await S.SetRecord(zone, l.Text, t.Text, pg.Text); Redraw(); }, "Added."), primary: true, id: "zone-add")));
                Col.Children.Add(Card(H("Point the domain at your node"),
                    Text($"1. At your registrar, set {domain}’s name servers to ns1.{domain} and ns2.{domain}, with glue records (their IP) = your node’s public IP.", 14),
                    Text($"2. Run your node with --dns={domain} --dns-zone={zone} (and --email=you@… for Let’s Encrypt). It answers DNS on port 53 and serves the site as {domain} with a certificate for every namespace.", 14),
                    Code($"opentube --service --dns={domain} --dns-zone={zone}")));
            }
            else
            {
                var l = Input("Name you want (e.g. tucson)", "", 63); var t = Input("Your node’s address: its public IP or host name", "", 200); var pg = Input("Page it opens (e.g. c/…, n/@name; empty: home)", "", 200); var note = Input("Note for the owner (optional)", "", 300);
                Col.Children.Add(Card(H($"Claim a name on {domain}"), Sub("The domain’s owner approves it. Your node then answers as name." + domain + " (run it with --public=name." + domain + " to get its certificate)."),
                    l, t, pg, note, Btn("Claim", () => _ = Status.Do("Sending your claim (a little proof of work)…", async () => { await S.ClaimName(zone, l.Text, t.Text, pg.Text, note.Text); Redraw(); }, "Sent: it works once the owner approves it."), primary: true, id: "zone-claim")));
            }
        }
        if (zone == null || !mine)
        {
            var d = Input("Your domain (e.g. example.com)", zone == null ? domain : "", 200); var about = Input("About (optional)", "", 200);
            Col.Children.Add(Card(H("Own a domain? Start its zone"), Sub("You decide which names exist under it; people claim names and you approve them."), d, about,
                Btn("Start the zone", () => _ = Status.Do("Registering (proof of work on the main chain)…", async () => { var id = await S.CreateZone(d.Text, about.Text); domain = d.Text.Trim().ToLowerInvariant(); Redraw(); }, "Started: it shows once registered."), id: "zone-create")));
        }
        Col.Children.Add(Status);
    }
}
