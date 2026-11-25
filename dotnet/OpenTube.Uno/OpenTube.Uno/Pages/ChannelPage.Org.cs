using System.Text.Json.Nodes;
using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Pages;

/// <summary>
/// An organization's tabs: flags (follow it and choose what each of its flags does for you), reports, rules
/// (classifications and petitions), filings with its clerk, cases, courts (levels of arbitration) and people (roles,
/// appeal window); and a case's docket (status, filings, orders signed by arbitrators).
/// </summary>
public sealed partial class ChannelPage
{
    JsonObject? followedOrgs;
    void Run(Func<Task> f, string done = "Done.") => _ = status.Do("Working…", async () => { await f(); followedOrgs = await Svc.S.Followed(); shown = ""; Refresh(); }, done);
    UIElement VideoRef(string? chain, string? block)
    {
        var S = Svc.S; if (chain == null || block == null) return Sub("—");
        var title = (string?)S.Chain(chain)?.Videos.FirstOrDefault(v => (string?)v["hash"] == block)?["payload"]?["title"] ?? "video " + block[..Math.Min(8, block.Length)];
        return new HyperlinkButton { Content = title, Padding = new Thickness(0) }.Also(h => h.Click += (_, _) => App.Open(typeof(WatchPage), new WatchArgs(chain, block)));
    }
    UIElement Person(string key) => new HyperlinkButton { Content = Svc.S.NameOf(key), Padding = new Thickness(0) }.Also(h => h.Click += (_, _) => ProfilePage.Open(Svc.S.Engine.Read(e => e.Store.Ledger.Resolve(key))));

    void OrgTab(string tab)
    {
        var S = Svc.S; var st = S.Org(id);
        if (st == null) { body.Children.Add(Sub("Loading…")); return; }
        if (followedOrgs == null) { _ = Task.Run(async () => { followedOrgs = await S.Followed(); DispatcherQueue.TryEnqueue(() => { shown = ""; Refresh(); }); }); }
        var mine = S.IsMine(st.D.Owner);
        switch (tab)
        {
            case "flags":
            {
                var prefs = followedOrgs?[id] as JsonObject;
                var c = new List<UIElement> { Row(H(prefs != null ? "You follow this organization" : "Follow this organization", 16), Btn(prefs != null ? "Unfollow" : "Follow", () => Run(() => S.Follow(id, prefs == null)), primary: prefs == null)) };
                if (prefs == null) c.Add(Sub("Follow it to see its flags on videos. You choose what each of its flags does for you."));
                else foreach (var cl in st.Classes.Values)
                {
                    var pick = Pick([("label", "Label it"), ("disable", "List it, but don’t open it"), ("hide", "Hide it")], (string?)prefs[cl.Id] ?? "label"); pick.MinWidth = 220;
                    pick.SelectionChanged += (_, _) => Run(() => S.SetFlagAction(id, cl.Id, pick.Value()), "Saved.");
                    c.Add(Row(Text(cl.Name, 14), Sub(cl.About), pick));
                }
                body.Children.Add(Card([.. c]));
                var f = new List<UIElement> { H("Flagged", 16) };
                if (st.Flags.Count == 0) f.Add(Sub("Nothing flagged."));
                foreach (var fl in st.Flags.Values.OrderByDescending(x => x.Ts))
                    f.Add(Row(VideoRef(fl.Chain, fl.Block), Text(st.Classes.TryGetValue(fl.Cls, out var cc) ? cc.Name : fl.Cls, 14), Sub((fl.Note != null ? fl.Note + " · " : "") + Svc.Ago(fl.Ts)),
                        S.IAm(id, "moderator") ? Btn("Remove flag", () => Run(() => S.SetFlag(id, fl.Chain, fl.Block, fl.Cls, false))) : null!));
                body.Children.Add(Card([.. f]));
                break;
            }
            case "reports":
            {
                var open = st.Reports.Where(r => !st.Flags.ContainsKey((string?)r["payload"]?["ref"]?["block"] + "|" + (string?)r["payload"]?["cls"])).OrderByDescending(r => Js.Num(r["ts"]) ?? 0).ToList();
                var c = new List<UIElement> { Sub("Reports from anyone. Moderators review them and flag what fits a classification. Report a video from its page (⚑ Report).") };
                if (open.Count == 0) c.Add(Sub("No open reports."));
                foreach (var r in open)
                {
                    var p = r["payload"]!; var cls = (string?)p["cls"] ?? "other"; var rc = (string?)p["ref"]?["chain"]; var rb = (string?)p["ref"]?["block"];
                    c.Add(Row(VideoRef(rc, rb), Text("reported as " + (st.Classes.TryGetValue(cls, out var cc) ? cc.Name : cls), 14), Sub(((string?)p["note"] is { } n ? "“" + n + "” · " : "") + Svc.Ago(Js.Num(r["ts"]) ?? 0)),
                        S.IAm(id, "moderator") && rc != null && rb != null ? Btn("Flag it", () => Run(() => S.SetFlag(id, rc, rb, cls, true, (string?)p["note"])), primary: true) : null!));
                }
                body.Children.Add(Card([.. c]));
                break;
            }
            case "rules":
            {
                var c = new List<UIElement> { H("Classifications", 16) };
                if (st.Classes.Count == 0) c.Add(Sub("None yet."));
                foreach (var cl in st.Classes.Values) c.Add(Row(Text(cl.Name, 14), Sub($"({cl.Id}) · {cl.About}"), mine ? Btn("Retire", () => Run(() => S.Classify(id, "class", cl.Id, cl.Name, "", false))) : null!));
                body.Children.Add(Card([.. c]));
                var p2 = new List<UIElement> { H("Petitions for new classifications", 16) };
                foreach (var b in st.Petitions.Where(b => !st.Classes.ContainsKey((string?)b["payload"]?["id"] ?? "")))
                {
                    var p = b["payload"]!; var h = (string)b["hash"]!;
                    p2.Add(Row(Text((string?)p["name"] ?? "", 14), Sub($"({(string?)p["id"]}) · {(string?)p["about"]} · {st.Support.GetValueOrDefault(h)} supporter(s)"), Btn("Support", () => Run(() => S.Support(id, h))),
                        mine ? Btn("Adopt", () => Run(() => S.Classify(id, "class", (string)p["id"]!, (string)p["name"]!, (string?)p["about"] ?? "")), primary: true) : null!));
                }
                var cid = Input("short-id, e.g. adult", "", 31); cid.Width = 170; var cname = Input("Name, e.g. Adult content", "", 60); var cabout = Input("What it covers", "", 300);
                p2.Add(Row(cid, cname)); p2.Add(cabout);
                p2.Add(Row(mine ? Btn("Add classification", () => Run(() => S.Classify(id, "class", cid.Text.Trim(), cname.Text, cabout.Text), "Added."), primary: true) : null!,
                    Btn("Petition for it", () => Run(() => S.Classify(id, "petition", cid.Text.Trim(), cname.Text, cabout.Text), "Petition filed."))));
                body.Children.Add(Card([.. p2]));
                break;
            }
            case "filings":
            {
                var clerk = S.IAm(id, "clerk");
                var filed = new HashSet<string>(st.Cases.SelectMany(g => S.Docket((string)g["hash"]!)).Select(b => (string?)b["payload"]?["ref"]).OfType<string>());
                var what = Pick(Orgs.Filings.Where(w => w != "order").Select(w => (w, w)), "complaint"); what.MinWidth = 150;
                var ftitle = Input("Title", "", 140); var fcase = Pick([("", "New matter"), .. st.Cases.Select(g => ((string)g["hash"]!, (string?)g["payload"]?["name"] ?? "case"))]); fcase.MinWidth = 180;
                var ftext = new TextBox { PlaceholderText = "What you’re filing (public)", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70 };
                body.Children.Add(Card(H("File with the clerk", 16), Row(what, fcase), ftitle, ftext,
                    Btn("File", () => Run(async () => { await S.Submit(id, what.Value(), ftitle.Text, ftext.Text, fcase.Value() is { Length: > 0 } fc ? fc : null); }, "Filed. The clerk dockets it."), primary: true)));
                var c = new List<UIElement> { H("Filed with this court", 16) };
                if (st.Submits.Count == 0) c.Add(Sub("Nothing filed yet."));
                foreach (var b in st.Submits.OrderByDescending(b => Js.Num(b["ts"]) ?? 0))
                {
                    var p = b["payload"]!; var bh = (string)b["hash"]!; var item = new StackPanel { Spacing = 4 };
                    item.Children.Add(Row(Text($"[{(string?)p["what"]}] {(string?)p["title"]}", 14), Sub($"by {S.NameOf((string?)b["author"] ?? "")} · {Svc.Ago(Js.Num(b["ts"]) ?? 0)}"),
                        (string?)p["case"] is { } pc ? Btn("Its case", () => Open(pc, "docket")) : null!));
                    if ((string?)p["text"] is { Length: > 0 } tx) item.Children.Add(Text(tx, 14));
                    if (clerk && !filed.Contains(bh) && (string?)p["what"] != "order")
                    {
                        var into = Pick([("", "File into case…"), .. st.Cases.Select(g => ((string)g["hash"]!, (string?)g["payload"]?["name"] ?? "case"))]); into.MinWidth = 180;
                        item.Children.Add(Row(Btn("Open a case", () => Run(async () => { var cs = await S.OpenCase(id, (string?)p["title"] ?? "Case", (string?)p["text"] ?? "", (string?)p["listing"]); await S.FileInto(cs, b); Open(cs, "docket"); }, "Case opened."), primary: true),
                            st.Cases.Count > 0 ? into : null!, st.Cases.Count > 0 ? Btn("File into case", () => { if (into.Value().Length > 0) Run(() => S.FileInto(into.Value(), b), "Docketed."); }) : null!));
                    }
                    c.Add(item);
                }
                body.Children.Add(Card([.. c]));
                break;
            }
            case "cases":
            {
                var c = new List<UIElement>();
                if (st.Cases.Count == 0) c.Add(Sub("No cases yet. Clerks open cases from filings."));
                foreach (var g in Enumerable.Reverse(st.Cases)) { var gh = (string)g["hash"]!; c.Add(Row(Btn((string?)g["payload"]?["name"] ?? "case", () => Open(gh, "docket")), Sub(S.CaseStatus(gh) ?? "filed"))); }
                body.Children.Add(Card([.. c]));
                break;
            }
            case "courts":
            {
                var parent = (string?)S.Genesis(id)?["payload"]?["parent"];
                var c = new List<UIElement> { Sub("Arbitration levels nest (for example by region). Appeals stay inside the network: they go to the next level up. The coins stay held until a ruling stands.") };
                c.Add(parent != null ? Row(Sub("Above:"), Btn((string?)S.Genesis(parent)?["payload"]?["name"] ?? "the level above", () => Open(parent))) : Sub("This is a top-level organization."));
                if ((string?)st.D.Profile["jurisdiction"] is { } j) c.Add(Sub("Jurisdiction: " + j));
                c.Add(H("Below", 15));
                if (st.Courts.Count == 0) c.Add(Sub("None."));
                foreach (var g in st.Courts) { var gh = (string)g["hash"]!; c.Add(Row(Btn((string?)g["payload"]?["name"] ?? "court", () => Open(gh)), Sub((string?)g["payload"]?["jurisdiction"] ?? ""))); }
                if (mine)
                {
                    var cn = Input("Name, e.g. Superior Court of Los Angeles County", "", 60); var cj = Input("Jurisdiction", "", 120);
                    c.Add(cn); c.Add(Row(cj, Btn("Add a level below this one", () => Run(() => S.CreateSubCourt(id, cn.Text, cj.Text), "Created. It appears here when its block is in."))));
                }
                body.Children.Add(Card([.. c]));
                break;
            }
            case "people":
            {
                var c = new List<UIElement> { Row(Sub("Owner:"), Person(S.OwnerIdentity(id))) };
                foreach (var (k, r) in st.Roles)
                {
                    var row = Row(Person(k), Text(string.Join(", ", r.Select(Trade.RoleName)), 14));
                    if (mine) foreach (var x in r) row.Children.Add(Btn("remove " + Trade.RoleName(x), () => Run(() => S.SetRoleOnLedger(id, k, x, false))));
                    c.Add(row);
                }
                if (st.Roles.Count == 0) c.Add(Sub("No roles given yet. Moderators flag videos, clerks take filings, arbitrators rule on disputes."));
                if (mine)
                {
                    var rk = Input("Their user ID", "", 64); var role = Pick(Trade.OrgRoles.Select(r => (r, Trade.RoleName(r))), "moderator"); role.MinWidth = 150;
                    c.Add(Row(rk, role, Btn("Give role", () => Run(() => S.SetRoleOnLedger(id, rk.Text.Trim(), role.Value(), true)), primary: true)));
                }
                var days = Input("days", ((int)S.AppealDaysOf(id)).ToString(), 3); days.Width = 90; days.IsEnabled = mine;
                c.Add(Row(Sub("Appeal window for this body’s rulings:"), days, Sub("days"), mine ? Btn("Save", () => Run(() => S.SetAppealDays(id, int.TryParse(days.Text, out var dd) ? Math.Clamp(dd, 0, 365) : Orgs.AppealDays), "Saved on the ledger.")) : null!));
                body.Children.Add(Card([.. c]));
                break;
            }
        }
        body.Children.Add(status);
    }

    /// <summary>A case: the court, the sale it's about, its docket, and the clerk's and arbitrators' tools.</summary>
    void Docket(Derived d)
    {
        var S = Svc.S; var g = S.Genesis(id); var court = (string?)g?["payload"]?["parent"]; var listing = (string?)g?["payload"]?["listing"];
        var L = listing != null ? S.Trade.Get(listing) : null; var items = S.Docket(id);
        var clerk = S.IsMine(d.Owner); var judge = court != null && S.IAm(court, "judge");
        var cst = court != null ? S.Org(court) : null;
        var orders = (cst?.Submits ?? []).Where(b => (string?)b["payload"]?["what"] == "order" && (string?)b["payload"]?["case"] == id && b["payload"]?["sig"] != null && !items.Any(x => (string?)x["payload"]?["ref"] == (string?)b["hash"])).ToList();
        var head = new List<UIElement> { Row(Sub("Court:"), court != null ? Btn((string?)S.Genesis(court)?["payload"]?["name"] ?? "court", () => Open(court)) : Sub("—"), Sub("· status"), Text(S.CaseStatus(id) ?? "filed", 14)) };
        if (L != null) head.Add(Row(Sub("About the sale"), Btn("“" + L.Title + "”", () => App.Open(typeof(MarketItemPage), listing!)), L.L["escrow"] is JsonObject es ? Sub($"{Trade.Coins(Js.Num(es["amt"]) ?? 0)} coins {(string?)es["state"]}") : null!));
        if ((string?)g?["payload"]?["about"] is { Length: > 0 } ab) head.Add(Text(ab, 14));
        body.Children.Add(Card([.. head]));
        var dk = new List<UIElement> { H("Docket", 16) };
        if (items.Count == 0) dk.Add(Sub("Nothing on the docket yet."));
        foreach (var b in items)
        {
            var p = b["payload"]!; var ts = Svc.Ago(Js.Num(b["ts"]) ?? 0);
            switch ((string?)p["type"])
            {
                case "status": dk.Add(Sub($"Status → {(string?)p["state"]}{((string?)p["note"] is { } n ? " · " + n : "")} · {ts}")); break;
                case "order":
                    var o = new StackPanel { Spacing = 2, Children = { Text($"[order] {(string?)p["title"]}", 14), Sub($"signed by arbitrator {S.NameOf((string?)p["judge"] ?? "")} · {ts}") } };
                    if (Js.Num(p["buyerShare"]) is double bs) o.Children.Add(Text($"Buyer receives {Math.Round(bs * 100)}% of the held coins, the seller the rest.", 14));
                    if ((string?)p["text"] is { Length: > 0 } ot) o.Children.Add(Text(ot, 14));
                    dk.Add(o); break;
                default:
                    var f = new StackPanel { Spacing = 2, Children = { Text($"[{(string?)p["what"]}] {(string?)p["title"]}", 14), Sub($"by {S.NameOf((string?)p["by"] ?? "")} · {ts}") } };
                    if ((string?)p["text"] is { Length: > 0 } ft) f.Children.Add(Text(ft, 14));
                    dk.Add(f); break;
            }
        }
        body.Children.Add(Card([.. dk]));
        if (clerk)
        {
            var state = Pick(Orgs.States.Select(x => (x, x)), "open"); state.MinWidth = 140; var note = Input("Note", "", 300);
            var c = new List<UIElement> { H("Clerk", 16), Row(state, note, Btn("Set status", () => Run(() => S.SetCaseStatus(id, state.Value(), note.Text), "Status set."))) };
            foreach (var b in orders) c.Add(Row(Text($"Signed order from arbitrator {S.NameOf((string?)b["payload"]?["judge"] ?? "")}: {(string?)b["payload"]?["title"]}", 14), Btn("Put on the docket", () => Run(() => S.DocketOrder(id, b), "On the docket."), primary: true)));
            c.Add(Sub("You hold this case’s key. To hand the case to another clerk, export it under Keys & backup."));
            body.Children.Add(Card([.. c]));
        }
        if (judge)
        {
            var ot = Input("Order title", "", 140); var otext = new TextBox { PlaceholderText = "The order", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70 };
            var share = Input("% to buyer", "", 3); share.Width = 120; var escrow = L?.L["escrow"] != null;
            body.Children.Add(Card(H("Arbitrator: write an order", 16), ot, otext, Row(escrow ? share : null!, Btn("Sign order", () => Run(() => S.SignOrder(court!, id, ot.Text, otext.Text,
                escrow && double.TryParse(share.Text, System.Globalization.CultureInfo.InvariantCulture, out var sh) ? Math.Clamp(sh, 0, 100) / 100 : null, listing), "Signed and sent to the clerk."), primary: true))));
        }
        body.Children.Add(status);
    }
}
