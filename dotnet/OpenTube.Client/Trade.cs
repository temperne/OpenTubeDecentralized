using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>
/// The market, the exchange and organizations, as the JavaScript page does them: every action is a main-chain
/// transaction (its shape and the ledger's rules are OpenTube.Core's, checked against the JavaScript core) or a block
/// on an organization's chain. Coins are whole units (100 = 1 coin).
/// </summary>
public sealed class Trade
{
    readonly ClientSession s; readonly IClientStore store;
    public Trade(ClientSession s, IClientStore store) { this.s = s; this.store = store; }
    public static readonly string[] ListingTypes = ["goods", "service", "job", "housing", "vehicle", "rental", "digital", "event", "wanted"];
    public static readonly string[] Assets = ["BTC", "ETH", "USDC", "USDT", "XMR", "LTC", "USD", "EUR", "GBP", "other"];
    public static readonly string[] Methods = ["lightning", "bitcoin", "ethereum", "stablecoin", "paypal", "bank", "cash", "other"];
    public static readonly string[] OrgTypes = ["corporation", "llc", "partnership", "sole", "nonprofit", "cooperative", "government", "club", "arbitration", "other"];
    public static readonly string[] OrgRoles = ["moderator", "clerk", "judge"];
    public const double MinStep = 0.05;
    public static long Units(string coins) => double.TryParse(coins.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c) && c > 0 ? (long)Math.Round(c * 100) : 0;
    public static string Coins(double units) => (units / 100).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    public double Price(string k) => s.Engine.Read(e => Math.Max(1, Math.Ceiling(e.Store.Ledger.Prices[k])));
    public double Balance => s.Balance(s.Me.pub);
    Task<JsonObject> Tx(string act, JsonNode x) => s.Engine.SubmitTx(new JsonObject { [act] = x }, s.Me);

    // ---------------------------------------------------------------- market
    public sealed record Listing(string Id, JsonObject L)
    {
        public string Title => (string?)L["title"] ?? ""; public string Seller => (string?)L["seller"] ?? ""; public string State => (string?)L["state"] ?? "";
        public string Kind => (string?)L["kind"] ?? "fixed"; public string Type => (string?)L["type"] ?? "goods";
        public double PriceNow => L["high"] is JsonObject h ? Js.Num(h["amt"]) ?? 0 : Js.Num(L["price"]) ?? 0;
        public bool Open => State == "open" && (Kind == "fixed" || (Js.Num(L["ends"]) ?? 0) > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
    public List<Listing> Listings() => s.Engine.Read(e => e.Store.Ledger.Listings.Select(kv => new Listing(kv.Key, (JsonObject)kv.Value.DeepClone())).ToList())
        .OrderByDescending(l => Js.Num(l.L["ts"]) ?? 0).ToList();
    public Listing? Get(string id) => s.Engine.Read(e => e.Store.Ledger.Listings.TryGetValue(id, out var L) ? new Listing(id, (JsonObject)L.DeepClone()) : null);
    public bool PendingTx(string hash) => s.Engine.Read(e => e.Store.TxPool.ContainsKey(hash));
    /// <summary>Arbitration bodies (organizations of that type) for payment protection.</summary>
    public List<ChainInfo> Arbiters() => s.Directory().Where(c => c.Kind == "org" && (string?)s.Genesis(c.Id)?["payload"]?["orgType"] == "arbitration").ToList();
    /// <summary>List something. With an arbitration body, the buyer's payment is held in escrow for 14 days.</summary>
    public async Task<string> List(string type, string kind, string title, string about, long price, string contact, int days, string? org)
    {
        if (title.Trim().Length == 0) throw new ArgumentException("Give it a title");
        if (price <= 0 && !(type is "job" or "wanted" && kind == "fixed")) throw new ArgumentException("Give it a price");
        var x = new JsonObject { ["kind"] = kind, ["type"] = type, ["title"] = title.Trim()[..Math.Min(120, title.Trim().Length)], ["fee"] = Price("list") };
        if (price > 0) x["price"] = price;
        if (about.Trim().Length > 0) x["about"] = about.Trim()[..Math.Min(2000, about.Trim().Length)];
        if (contact.Trim().Length > 0) x["contact"] = contact.Trim()[..Math.Min(200, contact.Trim().Length)];
        if (kind == "auction") x["ends"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Math.Clamp(days, 1, 30) * 86400000L;
        if (org != null) { x["org"] = org; x["arbiter"] = s.Chain(org)?.Owner ?? (string?)s.Genesis(org)?["author"]; x["hold"] = 14; }
        return (string)(await Tx("list", x))["hash"]!;
    }
    public Task Buy(string id) => Tx("buy", new JsonObject { ["id"] = id });
    public double MinBid(Listing l) => l.L["high"] is JsonObject h ? (Js.Num(h["amt"]) ?? 0) + Math.Max(1, Math.Ceiling((Js.Num(h["amt"]) ?? 0) * MinStep)) : Js.Num(l.L["price"]) ?? 0;
    public Task Bid(string id, long amt) => Tx("bid", new JsonObject { ["id"] = id, ["amt"] = amt });
    public Task Unlist(string id) => Tx("unlist", new JsonObject { ["id"] = id });
    public Task Release(string id) => Tx("release", new JsonObject { ["id"] = id });
    /// <summary>A dispute: a complaint filed with the listing's arbitration body, then the dispute (the coins freeze).</summary>
    public async Task Dispute(Listing l, string why)
    {
        if ((string?)l.L["org"] is { } org)
        {
            s.Chain(org);
            await Task.Run(() => s.Engine.Append(org, new JsonObject { ["type"] = "submit", ["what"] = "complaint", ["title"] = ("Dispute: " + l.Title)[..Math.Min(120, ("Dispute: " + l.Title).Length)], ["text"] = why.Trim(), ["listing"] = l.Id }, s.Me));
        }
        await Tx("dispute", new JsonObject { ["id"] = l.Id });
    }

    // ---------------------------------------------------------------- exchange: the order book and swaps
    public sealed record Offer(string Id, JsonObject O)
    {
        public string Side => (string?)O["side"] ?? ""; public string Asset => (string?)O["asset"] ?? ""; public string From => (string?)O["from"] ?? "";
        public double Amt => Js.Num(O["amt"]) ?? 0; public string Rate => (string?)O["rate"] ?? ""; public string State => (string?)O["state"] ?? "";
        public IEnumerable<string> PayBy => (O["methods"] as JsonArray ?? []).Select(x => (string?)x).OfType<string>();
    }
    public List<Offer> Offers() => s.Engine.Read(e => e.Store.Ledger.Offers.Select(kv => new Offer(kv.Key, (JsonObject)kv.Value.DeepClone())).Where(o => o.State == "open").ToList())
        .OrderBy(o => double.TryParse(o.Rate, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0).ToList();
    public Task PostOffer(string side, string asset, long amt, string rate, IEnumerable<string> methods, string note)
    {
        if (amt <= 0 || !System.Text.RegularExpressions.Regex.IsMatch(rate.Trim(), @"^\d{1,12}(\.\d{1,12})?$") || double.Parse(rate.Trim(), System.Globalization.CultureInfo.InvariantCulture) == 0) throw new ArgumentException("Enter the coins and a price");
        var ms = methods.ToList(); if (ms.Count == 0) throw new ArgumentException("Choose how you trade");
        var x = new JsonObject { ["side"] = side, ["asset"] = asset, ["amt"] = amt, ["rate"] = rate.Trim(), ["methods"] = new JsonArray(ms.Select(m => (JsonNode?)m).ToArray()) };
        if (note.Trim().Length > 0) x["note"] = note.Trim()[..Math.Min(500, note.Trim().Length)];
        return Tx("offer", x);
    }
    public Task EndOffer(string id) => Tx("endoffer", new JsonObject { ["id"] = id });
    public sealed record Swap(string Id, JsonObject H)
    {
        public string From => (string?)H["from"] ?? ""; public string To => (string?)H["to"] ?? ""; public string State => (string?)H["state"] ?? "";
        public double Amt => Js.Num(H["amt"]) ?? 0; public string Hash => (string?)H["hash"] ?? ""; public double Expires => Js.Num(H["expires"]) ?? 0;
    }
    public List<Swap> MySwaps() => s.Engine.Read(e => e.Store.Ledger.Htlcs.Select(kv => new Swap(kv.Key, (JsonObject)kv.Value.DeepClone())).ToList())
        .Where(x => s.IsMine(x.From) || s.IsMine(x.To)).OrderByDescending(x => Js.Num(x.H["ts"]) ?? 0).ToList();
    /// <summary>Lock coins for someone until a secret (whose sha256 is <paramref name="hash"/>) is revealed, or the deadline.</summary>
    public Task Lock(string to, long amt, string hash, int hours)
    {
        if (!Js.IsKey(JsonValue.Create(to.Trim()))) throw new ArgumentException("Paste their user ID");
        if (amt <= 0) throw new ArgumentException("How many coins?");
        if (!Js.IsHex(JsonValue.Create(hash.Trim().ToLowerInvariant()), 64)) throw new ArgumentException("A 64-character hash");
        return Tx("htlc", new JsonObject { ["to"] = to.Trim(), ["amt"] = amt, ["hash"] = hash.Trim().ToLowerInvariant(), ["expires"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Math.Clamp(hours, 1, 720) * 3600000L });
    }
    public Task Claim(Swap x, string preimage)
    {
        var p = preimage.Trim().ToLowerInvariant();
        if (!Js.IsHex(JsonValue.Create(p), 64)) throw new ArgumentException("The secret is 64 hex characters");
        if (Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(p))) != x.Hash) throw new ArgumentException("That isn’t the secret for this lock");
        return Tx("unlock", new JsonObject { ["id"] = x.Id, ["preimage"] = p });
    }
    public Task Refund(string id) => Tx("refund", new JsonObject { ["id"] = id });
    /// <summary>A secret for a swap on another chain: kept on this device (swapSecrets, as the JavaScript page keeps them); returns its hash.</summary>
    public async Task<string> MakeSecret()
    {
        var sec = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); var h = Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(sec)));
        var all = await store.Get("swapSecrets") is { } j && JsonNode.Parse(j) is JsonObject o ? o : [];
        all[h] = sec; await store.Set("swapSecrets", all.ToJsonString()); return h;
    }
    public async Task<string?> SecretFor(string hash) => await store.Get("swapSecrets") is { } j && JsonNode.Parse(j) is JsonObject o ? (string?)o[hash] : null;

    // ---------------------------------------------------------------- organizations
    public List<ChainInfo> Orgs() => s.Directory().Where(c => c.Kind == "org").ToList();
    /// <summary>A new organization, with a key pair of its own (as a channel).</summary>
    public async Task<string> CreateOrg(string name, string about, string orgType, string jurisdiction, JsonObject? extra = null)
    {
        if (name.Trim().Length == 0) throw new ArgumentException("Name it");
        var p = new JsonObject { ["type"] = "genesis", ["kind"] = "org", ["name"] = name.Trim(), ["about"] = about.Trim(), ["avatar"] = "", ["orgType"] = orgType, ["comments"] = "anyone" };
        if (jurisdiction.Trim().Length > 0) p["jurisdiction"] = jurisdiction.Trim()[..Math.Min(120, jurisdiction.Trim().Length)];
        if (extra?["details"] is JsonObject dt) p["details"] = dt.DeepClone();
        if (extra?["people"] is JsonArray pe) p["people"] = pe.DeepClone();
        return (string)(await s.CreateChannel(p))["hash"]!;
    }
    /// <summary>Who holds which roles (moderator, clerk, judge = arbitrator): the owner's role blocks, in order.</summary>
    public Dictionary<string, HashSet<string>> Roles(string org) => s.Engine.Read(e =>
    {
        var roles = new Dictionary<string, HashSet<string>>();
        if (!e.Store.Chains.TryGetValue(org, out var c)) return roles;
        foreach (var b in c.Blocks.Values.Where(b => (string?)b["payload"]?["type"] == "role" && c.IsOwnerBlock(b)).OrderBy(b => Js.Num(b["height"])).ThenBy(b => Js.Num(b["ts"])))
        {
            var k = (string?)b["payload"]?["key"]; var r = (string?)b["payload"]?["role"]; if (k == null || r == null) continue;
            var set = roles.TryGetValue(k, out var x) ? x : roles[k] = [];
            if (Js.IsTrue(b["payload"]?["on"])) set.Add(r); else set.Remove(r);
        }
        return roles.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => kv.Value);
    });
    public async Task SetRole(string org, string key, string role, bool on)
    {
        if (!Js.IsKey(JsonValue.Create(key.Trim()))) throw new ArgumentException("Paste their user ID");
        await Task.Run(() => s.Engine.Append(org, new JsonObject { ["type"] = "role", ["key"] = key.Trim(), ["role"] = role, ["on"] = on }));
    }
    public static string RoleName(string r) => r == "judge" ? "arbitrator" : r;

    // ---------------------------------------------------------------- devices: adding a computer
    /// <summary>A grant for a new computer: it runs as yours (signed by your key), with a name.</summary>
    public string Grant(string label)
    {
        var g = new JsonObject { ["v"] = 1, ["kind"] = "nodegrant", ["user"] = s.Me.pub, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["label"] = label.Trim()[..Math.Min(60, label.Trim().Length)] };
        g["sig"] = s.Me.Sign("opentube-nodegrant:" + Blocks.Canon(g));
        return B64u.Encode(Encoding.UTF8.GetBytes(g.ToJsonString()));
    }
}
