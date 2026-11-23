using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>Organizations (as the JavaScript page): classifications, flags and reports (moderation); petitions; filings
/// with the clerk, cases (child chains with their own key, kept by the clerk) and their dockets; sub-courts (levels of
/// arbitration); and which orgs you follow, with what each of their flags does for you.</summary>
public static class Orgs
{
    public static readonly string[] Filings = ["complaint", "motion", "response", "evidence", "appeal", "order", "other"];
    public static readonly string[] States = ["filed", "open", "hearing", "stayed", "decided", "appealed", "closed"];
    public static readonly System.Text.RegularExpressions.Regex Slug = new("^[a-z0-9][a-z0-9-]{1,30}$");
    public const int AppealDays = 14;

    public sealed record Class(string Id, string Name, string About);
    public sealed record Flag(string Chain, string Block, string Cls, string? Note, string By, double Ts);
    public sealed record State(Derived D, Dictionary<string, HashSet<string>> Roles, Dictionary<string, Class> Classes, Dictionary<string, Flag> Flags,
        List<JsonObject> Petitions, List<JsonObject> Reports, List<JsonObject> Submits, List<JsonObject> Cases, List<JsonObject> Courts, Dictionary<string, int> Support);

    /// <summary>An org's state, worked out from its chain (null until it has loaded).</summary>
    public static State? Org(this ClientSession s, string id)
    {
        var d = s.Chain(id); if (d == null) return null;
        var roles = s.Trade.Roles(id);
        return s.Engine.Read(e =>
        {
            if (!e.Store.Chains.TryGetValue(id, out var c)) return null;
            var blocks = c.Blocks.Values.Where(b => (string?)b["hash"] != id).OrderBy(b => Js.Num(b["height"]) ?? 0).ThenBy(b => Js.Num(b["ts"]) ?? 0).ToList();
            var classes = new Dictionary<string, Class>(); var flags = new Dictionary<string, Flag>();
            List<JsonObject> pet = [], rep = [], sub = [], cases = [], courts = []; var support = new Dictionary<string, int>();
            foreach (var b in blocks)
            {
                var p = b["payload"]; var t = (string?)p?["type"];
                if (t == "class" && c.IsOwnerBlock(b)) { var cid = (string?)p!["id"] ?? ""; if (p["on"] is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.False) classes.Remove(cid); else classes[cid] = new(cid, (string?)p["name"] ?? cid, (string?)p["about"] ?? ""); }
                if (t == "flag")
                {
                    var r = p!["ref"]; var k = (string?)r?["block"] + "|" + (string?)p["cls"];
                    if (Js.IsTrue(p["on"])) flags[k] = new((string?)r?["chain"] ?? "", (string?)r?["block"] ?? "", (string?)p["cls"] ?? "", (string?)p["note"], (string?)b["author"] ?? "", Js.Num(b["ts"]) ?? 0); else flags.Remove(k);
                }
                if (t == "petition") pet.Add((JsonObject)b.DeepClone());
                if (t == "report") rep.Add((JsonObject)b.DeepClone());
                if (t == "submit") sub.Add((JsonObject)b.DeepClone());
                if (t == "child" && p!["genesis"] is JsonObject g) { var kind = (string?)g["payload"]?["kind"]; if (kind == "case") cases.Add((JsonObject)g.DeepClone()); if (kind == "org") courts.Add((JsonObject)g.DeepClone()); }
                if (t == "vote" && (Js.Num(p!["v"]) ?? 0) > 0 && (string?)p["target"] is { } tg) support[tg] = support.GetValueOrDefault(tg) + 1;
            }
            return new State(d, roles, classes, flags, pet, rep, sub, cases, courts, support);
        });
    }
    /// <summary>Do you (your identity or one of your keys) hold this role here? The owner holds every role.</summary>
    public static bool IAm(this ClientSession s, string org, string role)
    {
        var d = s.Chain(org); if (d == null) return false;
        if (s.IsMine(d.Owner)) return true;
        var roles = s.Trade.Roles(org);
        return s.MyKeys().Any(k => roles.TryGetValue(k, out var r) && r.Contains(role));
    }
    static Task<JsonObject> Append(ClientSession s, string chain, JsonObject p) => Task.Run(() => s.Engine.Append(chain, p));

    public static Task Report(this ClientSession s, string org, string chain, string block, string cls, string note)
    {
        var p = new JsonObject { ["type"] = "report", ["ref"] = new JsonObject { ["chain"] = chain, ["block"] = block }, ["cls"] = cls.Length > 0 ? cls : "other" };
        if (note.Trim().Length > 0) p["note"] = note.Trim();
        return Append(s, org, p);
    }
    public static Task SetFlag(this ClientSession s, string org, string chain, string block, string cls, bool on, string? note = null)
    {
        var p = new JsonObject { ["type"] = "flag", ["ref"] = new JsonObject { ["chain"] = chain, ["block"] = block }, ["cls"] = cls, ["on"] = on };
        if (on && !string.IsNullOrWhiteSpace(note)) p["note"] = note.Trim();
        return Append(s, org, p);
    }
    /// <summary>The owner adds (or retires) a classification, or anyone petitions for one.</summary>
    public static Task Classify(this ClientSession s, string org, string type, string id, string name, string about, bool on = true)
    {
        if (!Slug.IsMatch(id)) throw new ArgumentException("The id: 2–31 letters, digits or dashes");
        if (name.Trim().Length == 0) throw new ArgumentException("Give it a name");
        var p = new JsonObject { ["type"] = type, ["id"] = id, ["name"] = name.Trim() };
        if (type == "class" && !on) p["on"] = false; else p["about"] = about.Trim();
        return Append(s, org, p);
    }
    public static Task Support(this ClientSession s, string org, string petition) => Append(s, org, new JsonObject { ["type"] = "vote", ["target"] = petition, ["v"] = 1 });
    public static Task Submit(this ClientSession s, string org, string what, string title, string text, string? forCase)
    {
        if (title.Trim().Length == 0) throw new ArgumentException("Give it a title");
        var p = new JsonObject { ["type"] = "submit", ["what"] = what, ["title"] = title.Trim(), ["text"] = text.Trim() };
        if (!string.IsNullOrEmpty(forCase)) p["case"] = forCase;
        return Append(s, org, p);
    }
    /// <summary>The clerk opens a case: a child chain of the court with a key of its own (kept by the clerk).</summary>
    public static async Task<string> OpenCase(this ClientSession s, string court, string title, string about, string? listing = null)
    {
        var key = Identity.New(); var name = title.Trim()[..Math.Min(60, title.Trim().Length)];
        await s.AddChannelKey(key, "Case: " + name, null);
        var p = new JsonObject { ["type"] = "genesis", ["kind"] = "case", ["name"] = name, ["about"] = about.Length > 0 ? about : title };
        if (listing != null) p["listing"] = listing;
        var g = await Task.Run(() => s.Engine.CreateChild(court, p, key));
        var id = (string)g["hash"]!;
        await s.AddChannelKey(key, "Case: " + name, id);
        await Append(s, id, new JsonObject { ["type"] = "status", ["state"] = "open" });
        return id;
    }
    public static Task FileInto(this ClientSession s, string caseId, JsonObject submit)
    {
        var p = submit["payload"]!;
        return Append(s, caseId, new JsonObject { ["type"] = "filing", ["what"] = p["what"]?.DeepClone(), ["title"] = p["title"]?.DeepClone(), ["text"] = (string?)p["text"] ?? "", ["by"] = submit["author"]?.DeepClone(), ["ref"] = submit["hash"]?.DeepClone() });
    }
    public static Task SetCaseStatus(this ClientSession s, string caseId, string state, string note)
    {
        var p = new JsonObject { ["type"] = "status", ["state"] = state }; if (note.Trim().Length > 0) p["note"] = note.Trim();
        return Append(s, caseId, p);
    }
    public static string? CaseStatus(this ClientSession s, string caseId) => s.Engine.Read(e =>
    {
        if (!e.Store.Chains.TryGetValue(caseId, out var c)) return null;
        string? st = null; double h = -1;
        foreach (var b in c.Blocks.Values) if ((string?)b["payload"]?["type"] == "status" && (Js.Num(b["height"]) ?? 0) >= h) { st = (string?)b["payload"]?["state"]; h = Js.Num(b["height"]) ?? 0; }
        return st;
    });
    /// <summary>A case's docket: filings, status changes and orders, in order.</summary>
    public static List<JsonObject> Docket(this ClientSession s, string caseId) => s.Engine.Read(e => e.Store.Chains.TryGetValue(caseId, out var c)
        ? c.Blocks.Values.Where(b => (string?)b["hash"] != caseId && (string?)b["payload"]?["type"] is "filing" or "status" or "order").OrderBy(b => Js.Num(b["height"]) ?? 0).ThenBy(b => Js.Num(b["ts"]) ?? 0).Select(b => (JsonObject)b.DeepClone()).ToList()
        : []);
    /// <summary>An arbitrator signs an order for a case and files it with the court; with a buyer's share, it also
    /// rules on the coins held for the sale (the court's key if you hold it, else yours as one of its arbitrators).</summary>
    public static async Task SignOrder(this ClientSession s, string court, string caseId, string title, string text, double? buyerShare, string? listing)
    {
        if (title.Trim().Length == 0) throw new ArgumentException("Title the order");
        var x = new JsonObject { ["title"] = title.Trim(), ["text"] = text.Trim(), ["buyerShare"] = buyerShare };
        var p = new JsonObject { ["type"] = "submit", ["what"] = "order", ["case"] = caseId, ["title"] = title.Trim(), ["text"] = text.Trim(), ["judge"] = s.Me.pub, ["sig"] = s.Me.Sign("opentube-order:" + caseId + ":" + Blocks.Canon(x)) };
        if (buyerShare != null) p["buyerShare"] = buyerShare;
        await Append(s, court, p);
        if (buyerShare != null && listing != null && s.Trade.Get(listing) is { } L && (string?)L.L["escrow"]?["state"] == "disputed")
        {
            var courtKey = (string?)L.L["escrow"]?["court"] ?? (string?)L.L["arbiter"];
            await s.Engine.SubmitTx(new JsonObject { ["rule"] = new JsonObject { ["id"] = listing, ["buyerShare"] = buyerShare } }, (courtKey != null ? s.KeyOf(courtKey) : null) ?? s.Me);
        }
    }
    /// <summary>The clerk puts an arbitrator's signed order on the case's docket.</summary>
    public static Task DocketOrder(this ClientSession s, string caseId, JsonObject submit)
    {
        var p = submit["payload"]!;
        var o = new JsonObject { ["type"] = "order", ["title"] = p["title"]?.DeepClone(), ["text"] = p["text"]?.DeepClone(), ["judge"] = p["judge"]?.DeepClone(), ["sig"] = p["sig"]?.DeepClone(), ["ref"] = submit["hash"]?.DeepClone() };
        if (p["buyerShare"] != null) o["buyerShare"] = p["buyerShare"]!.DeepClone();
        return Append(s, caseId, o);
    }
    /// <summary>A level of arbitration below this one: a child org with its own key; appeals from it go to the parent.</summary>
    public static async Task<string> CreateSubCourt(this ClientSession s, string parent, string name, string jurisdiction)
    {
        if (name.Trim().Length == 0) throw new ArgumentException("Name it");
        var key = Identity.New(); await s.AddChannelKey(key, name.Trim(), null);
        var parentKey = s.Chain(parent)?.Owner ?? throw new InvalidOperationException("The parent hasn’t loaded");
        var p = new JsonObject { ["type"] = "genesis", ["kind"] = "org", ["name"] = name.Trim(), ["about"] = "", ["avatar"] = "", ["orgType"] = "arbitration" };
        if (jurisdiction.Trim().Length > 0) p["jurisdiction"] = jurisdiction.Trim();
        var g = await Task.Run(() => s.Engine.CreateChild(parent, p, key));
        var id = (string)g["hash"]!; await s.AddChannelKey(key, name.Trim(), id);
        var parentSigner = s.KeyOf(parentKey) ?? throw new InvalidOperationException("You don’t hold the parent’s key");
        await s.Engine.SubmitTx(new JsonObject { ["court"] = new JsonObject { ["parent"] = parentKey, ["sig"] = parentSigner.Sign($"opentube-court:{key.pub}:{parentKey}") } }, key);
        return id;
    }
    public static async Task SetRoleOnLedger(this ClientSession s, string org, string key, string role, bool on)
    {
        await s.Trade.SetRole(org, key, role, on);
        if (role == "judge") { var owner = s.Chain(org)?.Owner; if (owner != null && s.KeyOf(owner) is { } k) await s.Engine.SubmitTx(new JsonObject { ["judge"] = new JsonObject { ["key"] = key, ["on"] = on } }, k); }
    }
    public static double AppealDaysOf(this ClientSession s, string org) { var o = s.Chain(org)?.Owner; return o == null ? AppealDays : s.Engine.Read(e => e.Store.Ledger.AppealDaysOf.TryGetValue(o, out var d) ? d : AppealDays); }
    public static Task SetAppealDays(this ClientSession s, string org, int days) =>
        s.Engine.SubmitTx(new JsonObject { ["courtcfg"] = new JsonObject { ["appealDays"] = Math.Clamp(days, 0, 365) } }, s.KeyOf(s.Chain(org)!.Owner!) ?? throw new InvalidOperationException("You don’t hold its key"));

    // ---- following orgs: settings.orgs = { orgId: { cls: "label" | "disable" | "hide" } } (kept on this device)
    public static async Task<JsonObject> Followed(this ClientSession s) => await s.Social.Setting("orgs") as JsonObject ?? [];
    public static async Task Follow(this ClientSession s, string org, bool on)
    {
        var f = await s.Followed(); if (on) f[org] ??= new JsonObject(); else f.Remove(org);
        await s.Social.SetSetting("orgs", f);
    }
    public static async Task SetFlagAction(this ClientSession s, string org, string cls, string action)
    {
        var f = await s.Followed(); var o = f[org] as JsonObject ?? new JsonObject(); o[cls] = action; f[org] = o;
        await s.Social.SetSetting("orgs", f);
    }
    public sealed record FlagOn(string Org, string OrgName, string Cls, string Name, string Action);
    /// <summary>What the orgs you follow say about a block, with what you chose each flag does.</summary>
    public static List<FlagOn> FlagsOn(this ClientSession s, JsonObject followed, string block)
    {
        var outp = new List<FlagOn>();
        foreach (var (org, prefs) in followed)
        {
            var st = s.Org(org); if (st == null) continue;
            foreach (var (k, f) in st.Flags) if (k.StartsWith(block + "|"))
                outp.Add(new(org, (string?)st.D.Profile["name"] ?? "", f.Cls, st.Classes.TryGetValue(f.Cls, out var c) ? c.Name : f.Cls, (string?)prefs?[f.Cls] ?? "label"));
        }
        return outp;
    }
    public static string? Strongest(List<FlagOn> fl) => fl.Any(f => f.Action == "hide") ? "hide" : fl.Any(f => f.Action == "disable") ? "disable" : fl.Count > 0 ? "label" : null;
}
