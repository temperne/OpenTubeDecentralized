using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>
/// Places on the map: businesses and organizations (org chains with a place), channels with a place, and open
/// listings with a place; and reviews: a star rating and words, as a comment on the place's chain ("★4 Great food").
/// </summary>
public static partial class Places
{
    public sealed record Place(string Id, string Kind, string Name, string Category, string About, double Lat, double Lon, string Label, double Stars, int Reviews, bool Listing);
    public sealed record Review(string Author, int Stars, string Text, double Ts, string Hash = "", string? Reply = null);
    public sealed record Photo(string Image, string Caption, double Ts, string Hash);
    [GeneratedRegex(@"^★([1-5])\s?")] private static partial Regex StarRx();

    public static List<Review> Reviews(this ClientSession s, string chain)
    {
        var d = s.Chain(chain); if (d == null) return [];
        return d.CommentsFor(chain).Where(c => !d.Hidden.Contains((string)c["hash"]!))
            .Select(c => (c, t: (string?)c["payload"]?["text"] ?? "")).Select(x => (x.c, x.t, m: StarRx().Match(x.t))).Where(x => x.m.Success)
            .Select(x => new Review((string?)x.c["author"] ?? "", int.Parse(x.m.Groups[1].Value), x.t[x.m.Length..], Js.Num(x.c["ts"]) ?? 0, (string)x.c["hash"]!,
                // the place's answer: its owner's latest comment on the review
                d.CommentsFor((string)x.c["hash"]!).Where(r => (string?)r["author"] == d.Owner && !d.Hidden.Contains((string)r["hash"]!)).OrderBy(r => Js.Num(r["ts"]) ?? 0).Select(r => (string?)r["payload"]?["text"]).LastOrDefault()))
            .GroupBy(r => r.Author).Select(g => g.OrderByDescending(r => r.Ts).First())   // (one review per person: their latest)
            .OrderByDescending(r => r.Ts).ToList();
    }
    public static (double Stars, int Count) Rating(this ClientSession s, string chain) { var r = s.Reviews(chain); return (r.Count == 0 ? 0 : r.Average(x => x.Stars), r.Count); }
    public static Task AddReview(this ClientSession s, string chain, int stars, string text)
    {
        if (stars is < 1 or > 5) throw new ArgumentException("Choose 1 to 5 stars");
        return s.Comment(chain, chain, $"★{stars} {text.Trim()}");
    }
    /// <summary>The place answers a review (its owner only).</summary>
    public static Task ReplyToReview(this ClientSession s, string chain, string review, string text) => s.Comment(chain, review, text);
    /// <summary>The place's photos: its owner's posts with a picture, newest first.</summary>
    public static List<Photo> Photos(this ClientSession s, string chain)
    {
        var d = s.Chain(chain); if (d == null) return [];
        return d.Visible.Where(b => (string?)b["payload"]?["type"] == "post" && (string?)b["author"] == d.Owner && (string?)b["payload"]?["image"] is { Length: > 0 })
            .Select(b => new Photo((string)b["payload"]!["image"]!, (string?)b["payload"]?["text"] ?? "", Js.Num(b["ts"]) ?? 0, (string)b["hash"]!)).OrderByDescending(p => p.Ts).ToList();
    }
    /// <summary>Add a photo to your place (a picture as a data: URI, at most 60 KB, and a caption).</summary>
    public static Task AddPhoto(this ClientSession s, string chain, string image, string caption)
        => Task.Run(() => s.Engine.Append(chain, new JsonObject { ["type"] = "post", ["text"] = caption.Trim().Length > 0 ? caption.Trim() : "📷", ["image"] = image }));
    /// <summary>Kilometres between two points (great circle).</summary>
    public static double Km(double la1, double lo1, double la2, double lo2)
    {
        double r = Math.PI / 180, dLa = (la2 - la1) * r, dLo = (lo2 - lo1) * r;
        var x = Math.Pow(Math.Sin(dLa / 2), 2) + Math.Cos(la1 * r) * Math.Cos(la2 * r) * Math.Pow(Math.Sin(dLo / 2), 2);
        return 2 * 6371 * Math.Asin(Math.Sqrt(x));
    }
    public static string StarText(double stars) => stars <= 0 ? "no reviews" : new string('★', (int)Math.Round(stars)) + new string('☆', 5 - (int)Math.Round(stars)) + $" {stars:0.0}";

    /// <summary>Everything with a place that this device knows.</summary>
    public static List<Place> All(this ClientSession s)
    {
        var o = new List<Place>();
        foreach (var c in s.Directory().Where(c => c.Kind is "org" or "channel"))
        {
            var d = s.Chain(c.Id); if (d?.Profile["place"] is not JsonObject pl) continue;
            var g = s.Genesis(c.Id)?["payload"]; var (st, n) = s.Rating(c.Id);
            var cat = (string?)d.Profile["category"] ?? (string?)g?["details"]?["category"] ?? (c.Kind == "org" ? OrgKinds.Of((string?)g?["orgType"]).Name : "Channel");
            o.Add(new(c.Id, c.Kind, c.Name, cat, c.About, Js.Num(pl["lat"]) ?? 0, Js.Num(pl["lon"]) ?? 0, (string?)pl["label"] ?? "", st, n, false));
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var l in s.Trade.Listings().Where(l => l.Open && l.L["place"] is JsonObject && (l.Kind == "fixed" || (Js.Num(l.L["ends"]) ?? 0) > now)))
        {
            var pl = l.L["place"]!;
            o.Add(new(l.Id, (string?)l.L["category"] == "housing" ? "housing" : "listing", l.Title, Market.KindOf(l.L).Name, (string?)l.L["about"] ?? "", Js.Num(pl["lat"]) ?? 0, Js.Num(pl["lon"]) ?? 0, (string?)pl["label"] ?? "", 0, 0, true));
        }
        return o;
    }
    public static readonly string[] Categories = ["Restaurant", "Café", "Bar", "Shop", "Grocery", "Hotel", "Services", "Health", "Auto", "Entertainment", "Education", "Office", "Government", "Park", "Other"];

    /// <summary>Add a place: an organization chain (a business by default) with its place, category, hours and contacts.</summary>
    public static async Task<string> AddPlace(this ClientSession s, string name, string category, string about, double lat, double lon, string address, string hours, string phone, string website, string orgType = "sole")
    {
        var details = new JsonObject { ["category"] = category };
        if (hours.Trim().Length > 0) details["hours"] = hours.Trim();
        if (phone.Trim().Length > 0) details["phone"] = phone.Trim();
        if (website.Trim().Length > 0) details["website"] = website.Trim();
        var id = await s.Trade.CreateOrg(name, about, orgType, "", new JsonObject { ["details"] = details });
        await s.UpdateProfile(id, new JsonObject { ["place"] = new JsonObject { ["lat"] = Math.Round(lat, 5), ["lon"] = Math.Round(lon, 5), ["label"] = address.Trim() }, ["category"] = category });
        return id;
    }
}
