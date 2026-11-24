using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>Your accounts, sending coins and the history of an account (as the JavaScript page's wallet).</summary>
public static class Wallet
{
    public sealed record Entry(JsonObject? Tx, bool Pending, double Height, double Ts, bool Reward);

    /// <summary>Your accounts: your identity, and channel keys not (yet) bound to it.</summary>
    public static List<string> MyAccounts(this ClientSession s) =>
        s.MyKeys().Where(k => k == s.Me.pub || s.Engine.Read(e => e.Store.Ledger.Resolve(k) != s.Me.pub && !e.Store.Ledger.Retired.Contains(k))).ToList();

    static bool Touches(JsonObject t, string key)
    {
        if (!Js.NumIs(t["v"], 2)) return false;
        bool Is(JsonNode? n) => (string?)n == key;
        return Is(t["from"]) || (t["pay"] as JsonArray ?? []).Any(x => Is(x?["to"])) || Is(t["view"]?["to"]) || (Is(t["vote"]?["to"]) && (Js.Num(t["vote"]?["v"]) ?? 0) > 0)
            || Is(t["claim"]?["creator"]) || Is(t["claim"]?["terms"]?["advertiser"]) || Is(t["certs"]?["to"])
            || (t["tabs"] as JsonArray ?? []).Any(x => Is(x?["payer"]) || Is(x?["payee"]) || Is(x?["creator"]));
    }
    /// <summary>What touched an account: pending transactions first, then the main chain from its newest block back.</summary>
    public static List<Entry> History(this ClientSession s, string key, int limit = 200) => s.Engine.Read(e =>
    {
        var res = new List<Entry>();
        foreach (var t in e.Store.TxPool.Values) if (Touches(t, key)) res.Add(new((JsonObject)t.DeepClone(), true, 0, Js.Num(t["ts"]) ?? 0, false));
        var main = e.Store.Main; JsonObject? b = e.Store.MainBest;
        while (b != null && (string?)b["hash"] != e.MainId && res.Count < limit)
        {
            foreach (var t in ((b["payload"]?["txs"] as JsonArray) ?? []).Reverse().OfType<JsonObject>()) if (Touches(t, key)) res.Add(new((JsonObject)t.DeepClone(), false, Js.Num(b["height"]) ?? 0, Js.Num(b["ts"]) ?? 0, false));
            if ((string?)b["author"] == key) res.Add(new(null, false, Js.Num(b["height"]) ?? 0, Js.Num(b["ts"]) ?? 0, true));
            var parent = (string?)(b["parents"] as JsonArray)?[0];
            b = parent != null && main.Blocks.TryGetValue(parent, out var pb) ? pb : null;
        }
        return res.Take(limit).ToList();
    });
    /// <summary>One line of an account's history: +1 coins in, -1 out, 0 no amount; the amount (units); what it was.</summary>
    public sealed record Line(int Sign, double Units, string Text);
    /// <summary>What a history entry means for an account, in plain words (every UI shows these).</summary>
    public static List<Line> Describe(this ClientSession s, Entry h, string key)
    {
        var o = new List<Line>();
        if (h.Reward) { o.Add(new(1, Coin.Reward, "for confirming a main-chain block")); return o; }
        var t = h.Tx!; var mine = (string?)t["from"] == key; var from = (string?)t["from"] ?? "";
        string Ch(JsonNode? id) => (string?)id is { } c ? ((string?)s.Chain(c)?.Profile["name"] ?? (string?)s.Genesis(c)?["payload"]?["name"] ?? c[..Math.Min(8, c.Length)]) : "a channel";
        double N(JsonNode? n) => Js.Num(n) ?? 0;
        if (Js.IsTrue(t["starter"])) o.Add(new(1, Coin.Starter, "starter coins"));
        foreach (var p in (t["pay"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var to = (string?)p["to"] ?? "";
            if (mine) o.Add(new(-1, N(p["amt"]), "to " + s.NameOf(to))); else if (to == key) o.Add(new(1, N(p["amt"]), "from " + s.NameOf(from)));
        }
        if ((string?)t["memo"] is { Length: > 0 } m) o.Add(new(0, 0, $"“{m}”"));
        if (t["post"] is JsonObject po) o.Add(new(-1, N(po["amt"]), $"posted a video on {Ch(po["chain"])} (to the channel’s hosts)"));
        if (t["view"] is JsonObject vw) o.Add(mine ? new(-1, N(vw["amt"]), $"watched a video on {Ch(vw["chain"])}") : new(1, N(vw["amt"]), $"{s.NameOf(from)} watched your video"));
        if (t["vote"] is JsonObject vo)
        {
            var up = N(vo["v"]) > 0;
            o.Add(mine ? new(-1, N(vo["amt"]), $"{(up ? "liked" : "disliked")} on {Ch(vo["chain"])}{(up ? "" : " (burned)")}") : new(1, N(vo["amt"]), $"{s.NameOf(from)} liked your post on {Ch(vo["chain"])}"));
        }
        foreach (var x in (t["tabs"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if ((string?)x["payer"] == key) o.Add(new(-1, N(x["amt"]), $"watched {Ch(x["chain"])} ({x["n"]} pieces)"));
            else if ((string?)x["payee"] == key) o.Add(new(1, N(x["amt"]), $"sent {s.NameOf((string?)x["payer"] ?? "")} {x["n"]} pieces of a video on {Ch(x["chain"])} (half to the creator)"));
            else if ((string?)x["creator"] == key) o.Add(new(0, 0, $"your video on {Ch(x["chain"])} was watched ({x["n"]} pieces)"));
        }
        if (t["host"] is JsonObject ho) o.Add(new(0, 0, (Js.IsTrue(ho["on"]) ? "Registered as a host of " : "Stopped hosting ") + Ch(ho["chain"])));
        if (t["name"] is JsonObject nm) o.Add(new(0, 0, (string?)nm["handle"] is { } hd ? $"Claimed {(string?)nm["space"]}{hd}" : "Released a name"));
        if (t["bind"] != null) o.Add(new(0, 0, "Bound a channel key to this identity (its coins come here)"));
        if (t["retire"] is JsonObject re) o.Add(new(0, 0, "Retired this key: everything moved to " + s.NameOf((string?)re["to"] ?? "")));
        if (t["list"] is JsonObject li) o.Add(new(0, 0, $"Listed “{(string?)li["title"]}” on the market"));
        if (t["buy"] != null) o.Add(new(0, 0, "Bought a market listing"));
        if (t["bid"] is JsonObject bi) o.Add(new(-1, N(bi["amt"]), "bid on an auction (held until it ends)"));
        if (t["unlist"] != null) o.Add(new(0, 0, "Withdrew a market listing"));
        if (t["certs"] is JsonObject ce) o.Add(new(0, 0, "Gave ad-view certificates to " + s.NameOf((string?)ce["to"] ?? "")));
        if (t["redeem"] != null) o.Add(new(0, 0, "Cashed in ad-view certificates"));
        if (o.Count == 0) o.Add(new(0, 0, "Transaction"));
        return o;
    }
    /// <summary>A user ID, or a channel/list ID (pays its owner).</summary>
    public static string? Recipient(this ClientSession s, string text)
    {
        text = text.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(text, "^[A-Za-z0-9_-]{43}$")) return text;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, "^[0-9a-f]{64}$")) return s.Chain(text)?.Owner ?? (string?)s.Genesis(text)?["author"];
        if (text.StartsWith('@') || text.StartsWith('~')) { var c = s.NameHolder(text.ToLowerInvariant()); if (c != null) return text.StartsWith("@@") ? s.Engine.Read(e => e.Store.Ledger.Resolve((string?)s.Genesis(c)?["author"] ?? "")) : s.Chain(c)?.Owner ?? (string?)s.Genesis(c)?["author"]; }
        return null;
    }
    public static async Task Send(this ClientSession s, string from, List<string> to, double unitsEach, string memo)
    {
        if (to.Count == 0) throw new ArgumentException("Who gets the coins?");
        if (to.Contains(from)) throw new ArgumentException("You cannot pay yourself");
        if (unitsEach <= 0) throw new ArgumentException("Enter an amount of coins");
        var tx = new JsonObject { ["pay"] = new JsonArray(to.Select(r => (JsonNode?)new JsonObject { ["to"] = r, ["amt"] = unitsEach }).ToArray()) };
        if (memo.Trim().Length > 0) tx["memo"] = memo.Trim()[..Math.Min(140, memo.Trim().Length)];
        await s.Engine.SubmitTx(tx, s.KeyOf(from) ?? throw new InvalidOperationException("Not your account"));
    }
}
