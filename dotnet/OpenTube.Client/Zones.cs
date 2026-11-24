using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>
/// Domains on the network. A zone chain belongs to whoever owns a domain (opentuber.com is the network's own): it holds
/// the domain's subdomains. People claim a name ("tucson" → their node's address, and the page it opens); the owner
/// approves it with a record. A node that the domain's DNS delegates to (its name servers) answers for the zone, so
/// tucson.opentuber.com reaches that node, which serves the site and opens the page.
/// One-letter subdomains are wildcards for names already registered on the network, served by the zone's own node:
/// alice.u.opentuber.com (a person, @@alice), alice.c… (a channel), .g (group), .o (organization), .l (list).
/// </summary>
public static class Zones
{
    public sealed record Record(string Label, string Target, string Page, bool On, string Hash, double Ts);
    public sealed record Claim(string Hash, string Label, string Target, string Page, string Note, string Author, double Ts);
    /// <summary>The wildcard letters and what they open.</summary>
    public static readonly (string Letter, string Kind, string Prefix)[] Wildcards = [("u", "person", "@@"), ("c", "channel", "@"), ("g", "group", "@"), ("o", "organization", "@"), ("l", "list", "~")];

    static Derived? D(Engine e, string id) => e.Read(x => x.Store.Chains.TryGetValue(id, out var c) && c.Blocks.Count > 0 ? Derived.Of(c) : null);
    public static List<ChainInfo> ZonesAll(this ClientSession s) => s.Directory().Where(c => c.Kind == "zone").ToList();
    /// <summary>The zone for a domain: the one trusted for it (a node serving it names its zone), else the oldest
    /// registered zone with that name.</summary>
    public static string? ZoneOf(Engine e, string domain, string? trusted = null)
    {
        if (trusted != null && D(e, trusted) != null) return trusted;
        return e.Read(x => x.Store.Directory.Where(kv => (string?)kv.Value.Genesis["payload"]?["kind"] == "zone" && string.Equals((string?)kv.Value.Genesis["payload"]?["name"], domain, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => Js.Num(kv.Value.Genesis["ts"]) ?? 0).Select(kv => kv.Key).FirstOrDefault());
    }
    public static string? ZoneOf(this ClientSession s, string domain, string? trusted = null) => ZoneOf(s.Engine, domain, trusted);
    /// <summary>The approved subdomains, the owner's latest word on each (a record with on=false removes it).</summary>
    public static Dictionary<string, Record> Records(Engine e, string zone)
    {
        var d = D(e, zone); var o = new Dictionary<string, Record>();
        if (d == null) return o;
        foreach (var b in d.Visible.Where(b => (string?)b["payload"]?["type"] == "record" && (string?)b["author"] == d.Owner).OrderBy(b => Js.Num(b["ts"]) ?? 0))
        {
            var p = b["payload"]!; var r = new Record((string?)p["label"] ?? "", (string?)p["target"] ?? "", (string?)p["page"] ?? "", (bool?)p["on"] ?? true, (string)b["hash"]!, Js.Num(b["ts"]) ?? 0);
            if (r.On) o[r.Label] = r; else o.Remove(r.Label);
        }
        return o;
    }
    public static Dictionary<string, Record> Records(this ClientSession s, string zone) { _ = s.Chain(zone); return Records(s.Engine, zone); }
    /// <summary>Claims waiting for the owner (not yet answered by a record naming them).</summary>
    public static List<Claim> Claims(this ClientSession s, string zone)
    {
        var d = s.Chain(zone); if (d == null) return [];
        var answered = d.Visible.Where(b => (string?)b["payload"]?["type"] == "record").Select(b => (string?)b["payload"]?["claim"]).OfType<string>().ToHashSet();
        var taken = s.Records(zone);
        return d.Visible.Where(b => (string?)b["payload"]?["type"] == "claim" && !answered.Contains((string)b["hash"]!))
            .Select(b => { var p = b["payload"]!; return new Claim((string)b["hash"]!, (string?)p["label"] ?? "", (string?)p["target"] ?? "", (string?)p["page"] ?? "", (string?)p["note"] ?? "", (string?)b["author"] ?? "", Js.Num(b["ts"]) ?? 0); })
            .Where(c => !taken.ContainsKey(c.Label) || taken[c.Label].Ts < c.Ts).OrderBy(c => c.Ts).ToList();
    }
    /// <summary>Start a zone for a domain you own. Then point the domain's name servers at your node (ns1.DOMAIN).</summary>
    public static async Task<string> CreateZone(this ClientSession s, string domain, string about)
    {
        domain = domain.Trim().ToLowerInvariant().TrimEnd('.');
        var g = await s.CreateChannel(new JsonObject { ["type"] = "genesis", ["kind"] = "zone", ["name"] = domain, ["about"] = about.Trim(), ["avatar"] = "" });
        return (string)g["hash"]!;
    }
    static string Clean(string label) => label.Trim().ToLowerInvariant().TrimEnd('.');
    /// <summary>Ask for a subdomain: its label, your node's address (IP or host name; empty: the zone's node) and the page it opens.</summary>
    public static Task ClaimName(this ClientSession s, string zone, string label, string target, string page, string note)
    {
        label = Clean(label);
        if (!Rules.ZoneLabel(label, false)) throw new ArgumentException("A name is 2 to 63 letters, digits or hyphens (one-letter names are reserved)");
        if (!Rules.ZoneTarget(target.Trim())) throw new ArgumentException("The address is your node's IP address or host name (or empty)");
        if (!Rules.ZonePage(page.Trim())) throw new ArgumentException("The page is a link inside OpenTuber, such as c/… or n/@name");
        var p = new JsonObject { ["type"] = "claim", ["label"] = label, ["target"] = target.Trim(), ["page"] = page.Trim(), ["note"] = note.Trim() };
        return Task.Run(() => s.Engine.Append(zone, p));
    }
    /// <summary>The owner: set (approve) or remove a subdomain.</summary>
    public static Task SetRecord(this ClientSession s, string zone, string label, string target, string page, bool on = true, string? claim = null)
    {
        label = Clean(label);
        if (!Rules.ZoneLabel(label, true)) throw new ArgumentException("Letters, digits and hyphens only");
        var p = new JsonObject { ["type"] = "record", ["label"] = label, ["target"] = target.Trim(), ["page"] = page.Trim(), ["on"] = on };
        if (claim != null) p["claim"] = claim;
        return Task.Run(() => s.Engine.Append(zone, p));
    }
    /// <summary>The page a host name opens: a subdomain's page, or the wildcard's name. Null: the home page.</summary>
    public static string? RouteFor(Engine e, string host, string domain = Network.Domain, string? zone = null)
    {
        host = host.ToLowerInvariant().TrimEnd('.'); var colon = host.IndexOf(':'); if (colon > 0) host = host[..colon];
        if (!host.EndsWith("." + domain)) return null;
        var labels = host[..^(domain.Length + 1)].Split('.');
        if (labels.Length == 2 && Wildcards.FirstOrDefault(w => w.Letter == labels[1]) is { Letter: not null } w)
        {
            var name = w.Prefix + labels[0];
            var chain = e.Read(x => (string?)x.Store.Ledger.Names.GetValueOrDefault(name)?["chain"]);
            if (chain == null) return "n/" + Uri.EscapeDataString(name);
            if (w.Letter == "u") return "u/" + e.Read(x => x.Store.Ledger.Resolve((string?)(x.Store.Directory.TryGetValue(chain, out var dd) ? dd.Genesis["author"] : null) ?? ""));
            return "c/" + chain;
        }
        if (labels.Length == 1 && labels[0] != "www" && ZoneOf(e, domain, zone) is { } z && Records(e, z).TryGetValue(labels[0], out var r)) return r.Page.Length > 0 ? r.Page : null;
        return null;
    }
    public static string? RouteFor(this ClientSession s, string host, string domain = Network.Domain, string? zone = null) => RouteFor(s.Engine, host, domain, zone);
}
