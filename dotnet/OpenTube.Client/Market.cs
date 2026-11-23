using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>The market's kinds of listing and their fields, ad-view certificates, and full listings (as the
/// JavaScript page).</summary>
public static class Market
{
    /// <summary>A field of a kind of listing: its key, label, and type ("" text, number, date, datetime, bool, or a choice list).</summary>
    public sealed record Field(string Key, string Label, string Type = "", string[]? Choices = null);
    public sealed record Kind(string Key, string Name, string Icon, string Act, string? PriceLabel, bool OptionalPrice, Field[] Fields);
    public static readonly Kind[] Kinds =
    [
        new("goods", "Goods", "📦", "Buy", null, false, [new("condition", "Condition", "", ["new", "like new", "used", "refurbished", "for parts"]), new("brand", "Brand"), new("qty", "How many for sale", "number")]),
        new("service", "Service", "🛠", "Book", "Rate", false, [new("rateUnit", "Rate per", "", ["hour", "day", "job", "week", "month"]), new("area", "Where you work (area)"), new("availability", "Availability")]),
        new("job", "Job", "💼", "Apply", "Pay (optional, coins)", true, [new("employment", "Type", "", ["full-time", "part-time", "contract", "temporary", "internship", "volunteer"]), new("salary", "Pay (any currency, e.g. 25 USD/hour)"), new("company", "Company"), new("remote", "Remote possible", "bool")]),
        new("housing", "Housing", "🏠", "Buy", null, false, [new("deal", "For", "", ["sale", "rent"]), new("bedrooms", "Bedrooms", "number"), new("bathrooms", "Bathrooms", "number"), new("sizeM2", "Size (m²)", "number"), new("furnished", "Furnished", "bool"), new("available", "Available from (date)", "date")]),
        new("vehicle", "Vehicle", "🚗", "Buy", null, false, [new("make", "Make"), new("model", "Model"), new("year", "Year", "number"), new("mileageKm", "Mileage (km)", "number"), new("fuel", "Fuel", "", ["petrol", "diesel", "electric", "hybrid", "other"]), new("transmission", "Transmission", "", ["manual", "automatic"])]),
        new("rental", "Rental", "🔑", "Rent", "Price per period", false, [new("period", "Per", "", ["hour", "day", "week", "month"]), new("deposit", "Deposit (coins)", "number")]),
        new("digital", "Digital", "💾", "Buy", null, false, [new("format", "Format (e.g. PDF, MP3, license key)"), new("license", "License / usage terms"), new("qty", "How many copies (blank: unlimited)", "number")]),
        new("event", "Event / tickets", "🎟", "Get a ticket", "Price per ticket", false, [new("date", "Date and time", "datetime"), new("venue", "Venue"), new("qty", "Tickets available", "number")]),
        new("wanted", "Wanted", "🔎", "Make an offer", "Budget (optional, coins)", true, [new("budget", "Budget (any currency)"), new("needBy", "Needed by (date)", "date")]),
    ];
    static readonly Kind AdViews = new("adviews", "Ad views", "🎟", "Buy", null, false, []);
    public static Kind KindOf(JsonObject L) => Kinds.FirstOrDefault(k => k.Key == (string?)L["type"])
        ?? ((string?)L["category"] == "housing" ? Kinds[3] : (L["certs"] as JsonArray)?.Count > 0 ? AdViews : Kinds[0]);
    public static readonly (string Key, string Name)[] Categories =
    [
        ("apparel", "Clothing & apparel"), ("autos", "Cars & vehicles"), ("beauty", "Beauty"), ("books", "Books"), ("education", "Education"), ("electronics", "Electronics"),
        ("entertainment", "Entertainment"), ("finance", "Finance"), ("currency", "Currency exchange"), ("food", "Food & drink"), ("games", "Games"), ("health", "Health"),
        ("home", "Home & garden"), ("housing", "Housing & real estate"), ("jobs", "Jobs"), ("pets", "Pets"), ("services", "Services"), ("software", "Software & apps"),
        ("sports", "Sports"), ("travel", "Travel"), ("other", "Other"),
    ];
    /// <summary>A listing's details as label → text, for its kind's fields.</summary>
    public static List<(string Label, string Value)> Details(JsonObject L)
    {
        var k = KindOf(L); var d = L["details"] as JsonObject; var outp = new List<(string, string)>();
        foreach (var f in k.Fields)
        {
            var v = f.Key == "qty" ? L["qty"] : d?[f.Key]; if (v == null) continue;
            if (v is JsonValue jv && jv.GetValueKind() == System.Text.Json.JsonValueKind.False) continue;
            var text = f.Type == "bool" ? "yes" : f.Type is "date" or "datetime" && Js.Num(v) is double ms
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToLocalTime().ToString(f.Type == "date" ? "d MMM yyyy" : "d MMM yyyy HH:mm") : v.ToString();
            if (text.Length == 0) continue;
            var label = System.Text.RegularExpressions.Regex.Replace(f.Label, @" \(.*\)$", "");
            outp.Add((label, text));
        }
        return outp;
    }

    // ---- ad-view certificates you own
    public sealed record Cert(string Id, string Adv, double Amt, string Owner);
    public static List<Cert> MyCerts(this ClientSession s) => s.Engine.Read(e => e.Store.Ledger.Certs.Select(kv => (kv.Key, kv.Value)).ToList())
        .Where(x => s.IsMine((string?)x.Value["owner"])).Select(x => new Cert(x.Key, (string?)x.Value["adv"] ?? "", Js.Num(x.Value["amt"]) ?? 0, (string?)x.Value["owner"] ?? "")).ToList();
    public static string AdvName(this ClientSession s, string key) =>
        s.Directory().FirstOrDefault(c => c.Kind == "channel" && c.Owner == key)?.Name ?? s.NameOf(key);
    public static Task CashIn(this ClientSession s, List<Cert> certs) =>
        s.Engine.SubmitTx(new JsonObject { ["redeem"] = new JsonObject { ["ids"] = new JsonArray(certs.Take(200).Select(c => (JsonNode?)c.Id).ToArray()) } }, s.KeyOf(certs[0].Owner) ?? s.Me);
    public static Task GiveCerts(this ClientSession s, List<Cert> certs, string to) =>
        s.Engine.SubmitTx(new JsonObject { ["certs"] = new JsonObject { ["ids"] = new JsonArray(certs.Take(200).Select(c => (JsonNode?)c.Id).ToArray()), ["to"] = to } }, s.KeyOf(certs[0].Owner) ?? s.Me);

    /// <summary>Everything a listing can carry.</summary>
    public sealed class Draft
    {
        public string Type = "goods", Kind = "fixed", Title = "", About = "", Contact = "", Org = "", Category = "", Deal = "sale", Tags = "";
        public string Price = ""; public int Days = 7; public string? Image; public JsonObject? Place;
        public Dictionary<string, string> Fields = []; public List<string> Certs = [];
    }
    public static async Task<string> ListDraft(this ClientSession s, Draft x)
    {
        var K = Kinds.FirstOrDefault(k => k.Key == x.Type) ?? Kinds[0];
        var amt = Trade.Units(x.Price);
        if (x.Title.Trim().Length == 0 || (amt <= 0 && !(K.OptionalPrice && x.Price.Trim().Length == 0))) throw new ArgumentException(K.OptionalPrice ? "Give it a title (the price can stay empty)" : "Give it a title and a price");
        var details = new JsonObject(); long qty = 0;
        foreach (var f in K.Fields)
        {
            if (!x.Fields.TryGetValue(f.Key, out var v) || v.Trim().Length == 0 || v == "false") continue;
            if (f.Key == "qty") { qty = Math.Max(1, (long)Math.Floor(double.TryParse(v, out var q) ? q : 1)); continue; }
            details[f.Key] = f.Type switch
            {
                "number" => double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0,
                "bool" => true,
                "date" or "datetime" => DateTimeOffset.TryParse(v, out var dt) ? dt.ToUnixTimeMilliseconds() : 0,
                _ => v.Trim()[..Math.Min(160, v.Trim().Length)],
            };
        }
        if (qty > 1 && (x.Org.Length > 0 || x.Kind != "fixed")) throw new ArgumentException("Several of the same sell at a fixed price, paid at once (no escrow)");
        var L = new JsonObject { ["kind"] = x.Kind, ["type"] = x.Type, ["title"] = x.Title.Trim()[..Math.Min(120, x.Title.Trim().Length)] };
        if (amt > 0) L["price"] = amt;
        if (details.Count > 0) L["details"] = details;
        if (qty > 1) L["qty"] = qty;
        if (x.About.Trim().Length > 0) L["about"] = x.About.Trim()[..Math.Min(2000, x.About.Trim().Length)];
        if (x.Contact.Trim().Length > 0) L["contact"] = x.Contact.Trim()[..Math.Min(200, x.Contact.Trim().Length)];
        if (x.Kind == "auction") L["ends"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Math.Clamp(x.Days, 1, 30) * 86400000L;
        if (x.Certs.Count > 0) { L["certs"] = new JsonArray(x.Certs.Take(200).Select(c => (JsonNode?)c).ToArray()); L["category"] = "adviews"; }
        else if (x.Category.Length > 0) L["category"] = x.Category;
        if (x.Place != null) L["place"] = x.Place.DeepClone();
        if (x.Type == "housing" && details["deal"] != null) L["deal"] = details["deal"]!.DeepClone();
        else if (x.Category == "housing" && x.Certs.Count == 0) L["deal"] = x.Deal;
        L["fee"] = s.Trade.Price("list");
        if (x.Org.Length > 0) { L["org"] = x.Org; L["arbiter"] = s.Chain(x.Org)?.Owner ?? (string?)s.Genesis(x.Org)?["author"]; L["hold"] = 14; }
        var tags = x.Tags.ToLowerInvariant().Split([' ', ',', '#'], StringSplitOptions.RemoveEmptyEntries).Select(t => System.Text.RegularExpressions.Regex.Replace(t, "[^a-z0-9-]", "").TrimStart('-'))
            .Where(t => t.Length > 0 && t.Length <= 24).Distinct().Take(8).ToList();
        if (tags.Count > 0) L["tags"] = new JsonArray(tags.Select(t => (JsonNode?)t).ToArray());
        if (x.Image != null) L["image"] = x.Image;
        var tx = await s.Engine.SubmitTx(new JsonObject { ["list"] = L }, s.Me);
        return (string)tx["hash"]!;
    }
    /// <summary>Ads published on channels loaded here, and running an advertiser's ads on your channel (sponsoring).</summary>
    public static List<(string Chain, JsonObject Ad)> AdBoard(this ClientSession s) => s.Engine.Read(e => e.Store.Chains.Values
        .Where(c => c != e.Store.Main && (string?)c.Genesis["payload"]?["kind"] == "channel").SelectMany(c => Derived.Of(c).Ads.Select(b => (c.Id, (JsonObject)b.DeepClone()))).ToList());
    public static Task Sponsor(this ClientSession s, string myChannel, string advertiserChain) => Task.Run(() => s.Engine.Append(myChannel, new JsonObject { ["type"] = "sponsor", ["advertiser"] = advertiserChain }));
}
