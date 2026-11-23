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
