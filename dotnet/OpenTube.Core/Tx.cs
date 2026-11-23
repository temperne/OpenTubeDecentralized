using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static OpenTube.Core.Js;

namespace OpenTube.Core;

/// <summary>The coin's constants (COIN, SERVE, PIECE, MARKET, XCHG, ADS in the JavaScript core).</summary>
public static class Coin
{
    public const double Unit = 100, TxBits = 10, StarterBits = 20, Starter = 1000, Reward = 100, MaxTxs = 200, MaxOuts = 100, Memo = 140, MaxAmt = 1e12;
    public const double Step = 0.125, Slack = 0.75;
    public static readonly double Epoch = Date(2026, 11, 19, 9);
    public static readonly string[] Resources = ["post", "view", "like", "piece", "list"];
    public static readonly Dictionary<string, double> Price0 = new() { ["post"] = 100, ["view"] = 5, ["like"] = 10, ["piece"] = 2, ["list"] = 25 };
    public static readonly Dictionary<string, double> Min = new() { ["post"] = 10, ["view"] = 1, ["like"] = 1, ["piece"] = 0.2, ["list"] = 1 };
    public static readonly Dictionary<string, double> Max = new() { ["post"] = 1e7, ["view"] = 1e5, ["like"] = 1e5, ["piece"] = 1e4, ["list"] = 1e6 };
    public static readonly Dictionary<string, double> Target = new() { ["post"] = 2, ["view"] = 30, ["like"] = 30, ["piece"] = 5000, ["list"] = 10 };
    // SERVE: resource pools paid every 10-minute period
    public const double EpochMs = 10 * 60000, Grace = 6, FeeShare = 0.1, MaxReceipts = 100;
    public static readonly double ServeStart = Date(2026, 11, 20, 2), PieceStart = Date(2026, 11, 20, 2), FeeStart = Date(2026, 11, 20, 6);
    public static readonly (string Res, double Pool, double Cap, double Weight)[] ServeRes = [("bytes", 200, 20e9, 0.5), ("uptime", 100, 600, 0.15), ("tracker", 50, 600, 0.15), ("storage", 100, 1e6, 0.2)];
    public const double CreatorShare = 0.5, MaxTabs = 50, MaxCerts = 200;
    public const double MarketMaxDays = 30, MinStep = 0.05, MarketTitle = 120, MarketAbout = 2000, MarketContact = 200, MarketImg = 24000;
    public static readonly string[] XchgAssets = ["BTC", "ETH", "USDC", "USDT", "XMR", "LTC", "USD", "EUR", "GBP", "other"];
    public static readonly string[] XchgMethods = ["lightning", "bitcoin", "ethereum", "stablecoin", "paypal", "bank", "cash", "other"];
    public const double XchgMaxDays = 30;
    public const double AdsSlotMs = 10000, AdsClaimWindowMs = 24 * 3600000, AdsMaxWitnesses = 20;
    public const double AppealDays = 14, MailKeys = 4;
    public static readonly string[] ListingTypes = ["goods", "service", "job", "housing", "vehicle", "rental", "digital", "event", "wanted"];

    static double Date(int y, int m, int d, int h) => new DateTimeOffset(y, m, d, h, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    public static double EpochOf(double ts) => Math.Floor(ts / EpochMs);
    public static double PostCost(double price, double size) => Math.Max(1, Math.Ceiling(price * size / 1e9));
    public static bool AmtOk(JsonNode? a) => IsSafeInteger(a) && Num(a) > 0 && Num(a) <= MaxAmt;
}

/// <summary>txShape, checkTx and checkClaim: is a transaction well formed and signed (the ledger decides if it applies).</summary>
public static partial class Tx
{
    static readonly string[] Acts = ["pay", "starter", "post", "view", "vote", "host", "claim", "name", "serve", "tabs", "redeem", "certs", "list", "unlist", "buy", "bid", "bind", "retire", "release", "dispute", "rule", "appeal", "court", "judge", "msgkey", "courtcfg", "linkvote", "htlc", "unlock", "refund", "offer", "endoffer"];
    /// <summary>Notes checkTx leaves for the ledger, per transaction object (the JavaScript keeps them in WeakMaps).</summary>
    public static readonly ConditionalWeakTable<JsonObject, string> ClaimTid = new(), UnlockHash = new(), ServeGrant = new();

    public static bool Shape(JsonNode? node, double? nowMs = null)
    {
        if (node is not JsonObject t || !(NumIs(Get(t, "v"), 1) || NumIs(Get(t, "v"), 2)) || Str(Get(t, "kind")) != "tx" || !IsKey(Get(t, "from")) || !IsSafeInteger(Get(t, "seq")) || Num(Get(t, "seq")) < 1) return false;
        if (!IsSafeInteger(Get(t, "ts")) || Num(Get(t, "ts")) > (nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) + 10 * 60000 || !IsStr(Get(t, "memo") ?? JsonValue.Create(""), (int)Coin.Memo)) return false;
        var from = Str(Get(t, "from"));
        if (NumIs(Get(t, "v"), 1))
        {
            if (!Undef(t, "claim")) return Undef(t, "outs") && Undef(t, "burns") && ClaimShape(Get(t, "claim"));
            if (!Undef(t, "name")) return Undef(t, "outs") && Undef(t, "burns") && NameShape(Get(t, "name"));
            var outs = Truthy(Get(t, "outs")) ? Get(t, "outs") : new JsonArray(); var burns = Truthy(Get(t, "burns")) ? Get(t, "burns") : new JsonArray();
            if (outs is not JsonArray o || burns is not JsonArray bu || o.Count > 100 || bu.Count > 20 || o.Count + bu.Count == 0) return false;
            return o.All(x => Truthy(x) && IsKey(Get(x, "to")) && IsKey(Get(x, "token")) && Coin.AmtOk(Get(x, "amt")) && Str(Get(x, "to")) != from)
                && bu.All(x => Truthy(x) && IsKey(Get(x, "token")) && Str(Get(x, "token")) != from && Coin.AmtOk(Get(x, "amt")));
        }
        var acts = Acts.Where(k => !Undef(t, k)).ToList();
        if (acts.Count != 1 || !Undef(t, "outs") || !Undef(t, "burns")) return false;
        var x = Get(t, acts[0]); bool has(string k) => x is JsonObject xo && xo.ContainsKey(k);
        bool undef(string k) => !has(k);
        switch (acts[0])
        {
            case "pay": return x is JsonArray pay && pay.Count >= 1 && pay.Count <= Coin.MaxOuts && pay.All(o => Truthy(o) && IsKey(Get(o, "to")) && Str(Get(o, "to")) != from && Coin.AmtOk(Get(o, "amt")));
            case "starter": return IsTrue(x);
            case "post": return Truthy(x) && IsHex(Get(x, "chain"), 64) && IsHex(Get(x, "video"), 64) && IsSafeInteger(Get(x, "size")) && Num(Get(x, "size")) > 0 && Coin.AmtOk(Get(x, "amt"));
            case "view": return Truthy(x) && IsHex(Get(x, "chain"), 64) && IsHex(Get(x, "video"), 64) && IsKey(Get(x, "to")) && Str(Get(x, "to")) != from && Coin.AmtOk(Get(x, "amt"));
            case "vote": return Truthy(x) && IsHex(Get(x, "chain"), 64) && IsHex(Get(x, "target"), 64) && IsKey(Get(x, "to")) && (NumIs(Get(x, "v"), 1) || NumIs(Get(x, "v"), -1)) && Coin.AmtOk(Get(x, "amt"));
            case "host": return Truthy(x) && IsHex(Get(x, "chain"), 64) && IsBool(Get(x, "on"));
            case "claim": return ClaimShape(x);
            case "name": return NameShape(x);
            case "tabs": return x is JsonArray tabs && tabs.Count >= 1 && tabs.Count <= Coin.MaxTabs && tabs.All(TabShape) && tabs.Select(y => $"{Str(Get(y, "payer"))}|{Str(Get(y, "video"))}").Distinct().Count() == tabs.Count;
            case "redeem": return Truthy(x) && Get(x, "ids") is JsonArray ids && ids.Count >= 1 && ids.Count <= Coin.MaxCerts && ids.All(i => IsHex(i, 64)) && ids.Select(i => Str(i)).Distinct().Count() == ids.Count;
            case "certs": return Truthy(x) && IsKey(Get(x, "to")) && Str(Get(x, "to")) != from && Get(x, "ids") is JsonArray cids && cids.Count >= 1 && cids.Count <= Coin.MaxCerts && cids.All(i => IsHex(i, 64)) && cids.Select(i => Str(i)).Distinct().Count() == cids.Count;
            case "list": return ListingShape(x);
            case "htlc": return Truthy(x) && IsKey(Get(x, "to")) && Coin.AmtOk(Get(x, "amt")) && IsHex(Get(x, "hash"), 64) && IsSafeInteger(Get(x, "expires")) && IsStr(Get(x, "note") ?? JsonValue.Create(""), 200);
            case "unlock": return Truthy(x) && IsHex(Get(x, "id"), 64) && IsHex(Get(x, "preimage"), 64);
            case "refund": case "endoffer": return Truthy(x) && IsHex(Get(x, "id"), 64);
            case "offer": return OfferShape(x);
            case "linkvote": return Truthy(x) && IsStr(Get(x, "url"), 300) && DomainOf(Str(Get(x, "url"))!) is var d && (d == null ? IsNullValue(x, "domain") : Str(Get(x, "domain")) == d)
                && (NumIs(Get(x, "v"), 1) || NumIs(Get(x, "v"), -1)) && (undef("to") || IsKey(Get(x, "to"))) && Coin.AmtOk(Get(x, "amt"));
            case "courtcfg": return Truthy(x) && IsSafeInteger(Get(x, "appealDays")) && Num(Get(x, "appealDays")) is >= 0 and <= 365;
            case "msgkey": return Truthy(x) && Str(Get(x, "key")) is { } mk && MsgKeyRe().IsMatch(mk) && IsBool(Get(x, "on"));
            case "release": return Truthy(x) && IsHex(Get(x, "id"), 64);
            case "appeal": case "dispute": return Truthy(x) && IsHex(Get(x, "id"), 64) && (undef("case") || IsHex(Get(x, "case"), 64));
            case "court": return Truthy(x) && IsKey(Get(x, "parent")) && Str(Get(x, "parent")) != from && IsString(Get(x, "sig"));
            case "judge": return Truthy(x) && IsKey(Get(x, "key")) && IsBool(Get(x, "on"));
            case "rule": return Truthy(x) && IsHex(Get(x, "id"), 64) && IsFinite(Get(x, "buyerShare")) && Num(Get(x, "buyerShare")) is >= 0 and <= 1;
            case "bind": return Truthy(x) && IsKey(Get(x, "key")) && Str(Get(x, "key")) != from && IsString(Get(x, "sig"));
            case "retire": return Truthy(x) && IsKey(Get(x, "to")) && Str(Get(x, "to")) != from && IsString(Get(x, "sig"));
            case "unlist": case "buy": return Truthy(x) && IsHex(Get(x, "id"), 64);
            case "bid": return Truthy(x) && IsHex(Get(x, "id"), 64) && Coin.AmtOk(Get(x, "amt"));
            case "serve":
                return Truthy(x) && IsSafeInteger(Get(x, "epoch")) && Get(x, "receipts") is JsonArray rs && rs.Count >= 1 && rs.Count <= Coin.MaxReceipts && rs.All(ReceiptShape)
                    && rs.Select(r => Str(Get(r, "res")) + "|" + Str(Get(r, "by"))).Distinct().Count() == rs.Count && (undef("grant") || ValidNodeGrant(Get(x, "grant")));
        }
        return false;
    }
    static bool IsNullValue(JsonNode? o, string k) => o is JsonObject jo && jo.TryGetPropertyValue(k, out var v) && v == null;

    /// <summary>checkTx(t, trusted): shape, work, hash and every signature it carries (trusted: blocks this computer already verified).</summary>
    public static bool Check(JsonNode? node, bool trusted = false, double? nowMs = null)
    {
        if (!Shape(node, nowMs)) return false;
        var t = (JsonObject)node!;
        if (!IsHex(Get(t, "hash"), 64) || !IsString(Get(t, "sig")) || !IsSafeInteger(Get(t, "nonce"))) return false;
        if (Blocks.Canon(t).Length > 40000 || Rules.ZeroBits(Str(Get(t, "hash"))) < (Undef(t, "starter") ? Coin.TxBits : Coin.StarterBits) || (!trusted && Blocks.Hash(t) != Str(Get(t, "hash")))) return false;
        bool ok(JsonNode? pub, string msg, JsonNode? sig) => trusted || Rules.VerifySig(pub, msg, sig);
        if (!ok(Get(t, "from"), Str(Get(t, "hash"))!, Get(t, "sig"))) return false;
        if (Get(t, "tabs") is JsonArray tabs) foreach (var x in tabs) if (!ok(Get(x, "payer"), "opentube-tab:" + Blocks.Canon(Without(x, "sig")), Get(x, "sig"))) return false;
        if (Get(t, "bind") is { } bind && !ok(Get(bind, "key"), $"opentube-bind:{Str(Get(t, "from"))}:{Str(Get(bind, "key"))}", Get(bind, "sig"))) return false;
        if (Get(t, "retire") is { } ret && !ok(Get(ret, "to"), $"opentube-retire:{Str(Get(t, "from"))}:{Str(Get(ret, "to"))}", Get(ret, "sig"))) return false;
        if (Get(t, "court") is { } court && !ok(Get(court, "parent"), $"opentube-court:{Str(Get(t, "from"))}:{Str(Get(court, "parent"))}", Get(court, "sig"))) return false;
        if (Get(t, "unlock") is { } un) UnlockHash.AddOrUpdate(t, Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(Str(Get(un, "preimage"))!))));
        if (Get(t, "serve") is { } serve)
        {
            foreach (var r in (JsonArray)Get(serve, "receipts")!) if (!ok(Get(r, "by"), "opentube-receipt:" + Blocks.Canon(Without(r, "sig")), Get(r, "sig"))) return false;
            if (Get(serve, "grant") is { } g) { if (!ok(Get(g, "user"), "opentube-nodegrant:" + Blocks.Canon(Without(g, "sig")), Get(g, "sig"))) return false; ServeGrant.AddOrUpdate(t, Str(Get(g, "user"))!); }
        }
        return Undef(t, "claim") || CheckClaim(t);
    }
    static JsonObject Without(JsonNode? o, string k) { var c = (JsonObject)o!.DeepClone(); c.Remove(k); return c; }

    /// <summary>checkClaim(t): an ad view, with the advertiser's signed terms and enough other viewers who saw it.</summary>
    /// <summary>Verify a claim being made here (before the pending state checks it: that needs its terms id).</summary>
    public static bool VerifyClaim(JsonObject t) => CheckClaim(t);
    static bool CheckClaim(JsonObject t)
    {
        var c = Get(t, "claim")!; var terms = Get(c, "terms")!;
        if (!(TermsShape(terms) && Rules.VerifySig(Get(terms, "advertiser"), "opentube-adterms:" + Blocks.Canon(Without(terms, "sig")), Get(terms, "sig"))) || Str(Get(terms, "advertiser")) == Str(Get(t, "from"))) return false;
        var slotTs = Num(Get(c, "slot"))!.Value * Coin.AdsSlotMs; var ts = Num(Get(t, "ts"))!.Value;
        if (slotTs > ts + 60000 || slotTs < ts - Coin.AdsClaimWindowMs) return false;
        var tid = Blocks.Sha256Hex("opentube-adterms:" + Blocks.Canon(Without(terms, "sig")));
        var session = Blocks.Sha256Hex("opentube-session:" + tid + ":" + NumberToString(Num(Get(c, "slot"))!.Value));
        var witnesses = new HashSet<string>(); var from = Str(Get(t, "from"));
        foreach (var w in (JsonArray)Get(c, "witnesses")!)
        {
            if (Str(Get(w, "session")) != session || Str(Get(w, "terms")) != tid || Num(Get(w, "slot")) != Num(Get(c, "slot")) || Str(Get(w, "video")) != Str(Get(c, "video")) || Str(Get(w, "creator")) != Str(Get(c, "creator"))) return false;
            var who = Str(Get(w, "witness"))!;
            if (who == from || !((JsonArray)Get(w, "saw")!).Any(s => Str(s) == from) || witnesses.Contains(who)) return false;
            if (!Rules.VerifySig(Get(w, "witness"), "opentube-witness:" + Blocks.Canon(Without(w, "sig")), Get(w, "sig"))) return false;
            witnesses.Add(who);
        }
        if (witnesses.Count < (Num(Get(terms, "witnesses")) ?? 0)) return false;
        ClaimTid.AddOrUpdate(t, tid);
        return true;
    }

    // ---- shapes
    public static bool TermsShape(JsonNode? t) => t is JsonObject o && NumIs(Get(o, "v"), 1) && Str(Get(o, "kind")) == "adterms" && IsKey(Get(o, "advertiser")) && IsHex(Get(o, "channel"), 64) && IsHex(Get(o, "infohash"), 40)
        && IsSafeInteger(Get(o, "size")) && Num(Get(o, "size")) > 0 && IsSafeInteger(Get(o, "duration")) && Num(Get(o, "duration")) is >= 1 and <= 120
        && IsSafeInteger(Get(o, "perView")) && Num(Get(o, "perView")) is >= 2 and <= 1000 && IsSafeInteger(Get(o, "maxViews")) && Num(Get(o, "maxViews")) is >= 1 and <= 1e6
        && IsSafeInteger(Get(o, "created")) && IsString(Get(o, "sig")) && (Undef(o, "witnesses") || (IsSafeInteger(Get(o, "witnesses")) && Num(Get(o, "witnesses")) is >= 0 and <= Coin.AdsMaxWitnesses));
    static bool WitnessShape(JsonNode? w) => w is JsonObject o && NumIs(Get(o, "v"), 1) && Str(Get(o, "kind")) == "witness" && IsHex(Get(o, "session"), 64) && IsHex(Get(o, "terms"), 64) && IsSafeInteger(Get(o, "slot"))
        && IsHex(Get(o, "video"), 64) && IsKey(Get(o, "creator")) && IsKey(Get(o, "witness")) && Get(o, "saw") is JsonArray saw && saw.Count <= 50 && saw.All(IsKey) && IsString(Get(o, "sig"));
    static bool ClaimShape(JsonNode? c) => c is JsonObject o && TermsShape(Get(o, "terms")) && IsSafeInteger(Get(o, "slot")) && IsHex(Get(o, "video"), 64) && IsKey(Get(o, "creator"))
        && (Undef(o, "bytes") || (IsSafeInteger(Get(o, "bytes")) && Num(Get(o, "bytes")) > 0)) && Get(o, "witnesses") is JsonArray ws && ws.Count <= Coin.AdsMaxWitnesses && ws.All(WitnessShape);
    static bool NameShape(JsonNode? n) => n is JsonObject o && Str(Get(o, "space")) is "@" or "~" or "@@" && IsHex(Get(o, "chain"), 64)
        && (IsNullValue(o, "handle") || (Str(Get(o, "handle")) is { } h && NameRe().IsMatch(h) && !DoublePunctRe().IsMatch(h)));
    static bool TabShape(JsonNode? x) => x is JsonObject o && NumIs(Get(o, "v"), 1) && Str(Get(o, "kind")) == "tab" && IsKey(Get(o, "payer")) && IsKey(Get(o, "payee")) && Str(Get(o, "payer")) != Str(Get(o, "payee"))
        && IsHex(Get(o, "chain"), 64) && IsHex(Get(o, "video"), 64) && IsKey(Get(o, "creator")) && IsSafeInteger(Get(o, "n")) && Num(Get(o, "n")) > 0 && Coin.AmtOk(Get(o, "amt")) && IsSafeInteger(Get(o, "at")) && IsString(Get(o, "sig"));
    static bool ReceiptShape(JsonNode? r) => r is JsonObject o && NumIs(Get(o, "v"), 1) && Str(Get(o, "kind")) == "receipt" && Str(Get(o, "res")) is { } res && Coin.ServeRes.Any(x => x.Res == res) && IsKey(Get(o, "provider")) && IsKey(Get(o, "by"))
        && Str(Get(o, "by")) != Str(Get(o, "provider")) && IsSafeInteger(Get(o, "epoch")) && IsSafeInteger(Get(o, "amt")) && Num(Get(o, "amt")) > 0 && IsString(Get(o, "sig"));
    public static bool ValidNodeGrant(JsonNode? g) => g is JsonObject o && NumIs(Get(o, "v"), 1) && Str(Get(o, "kind")) == "nodegrant" && IsKey(Get(o, "user")) && IsSafeInteger(Get(o, "at")) && IsStr(Get(o, "label") ?? JsonValue.Create(""), 60) && IsString(Get(o, "sig"));
    static bool OfferShape(JsonNode? x) => Truthy(x) && Str(Get(x, "side")) is "buy" or "sell" && Str(Get(x, "asset")) is { } a && Coin.XchgAssets.Contains(a) && Coin.AmtOk(Get(x, "amt"))
        && Str(Get(x, "rate")) is { } rate && RateRe().IsMatch(rate) && double.Parse(rate, System.Globalization.CultureInfo.InvariantCulture) > 0
        && (!(x is JsonObject xo && xo.ContainsKey("min")) || Coin.AmtOk(Get(x, "min"))) && Get(x, "methods") is JsonArray ms && ms.Count is >= 1 and <= 6 && ms.All(m => Str(m) is { } s && Coin.XchgMethods.Contains(s))
        && IsStr(Get(x, "note") ?? JsonValue.Create(""), 500) && IsStr(Get(x, "contact") ?? JsonValue.Create(""), 200);

    /// <summary>
    /// listingShape(x). The JavaScript has a comment in the middle of one line ("// several of the same: fixed price, paid
    /// at once"), which comments out the rest of that line: the arbiter, hold, org, title and about checks never run.
    /// The network's rules are what the JavaScript does, so they don't run here either.
    /// </summary>
    static bool ListingShape(JsonNode? node)
    {
        if (node is not JsonObject x || Str(Get(x, "kind")) is not ("fixed" or "auction") || !(Undef(x, "type") || (Str(Get(x, "type")) is { } ty && Coin.ListingTypes.Contains(ty))) || !Rules.DetailsShape(x, "details")) return false;
        if (!(Undef(x, "qty") || (IsSafeInteger(Get(x, "qty")) && Num(Get(x, "qty")) is >= 1 and <= 1e6))) return false;
        if (!(Undef(x, "qty") || NumIs(Get(x, "qty"), 1) || (!Truthy(Get(x, "arbiter")) && Str(Get(x, "kind")) == "fixed"))) return false;
        if (!IsStr(Get(x, "contact") ?? JsonValue.Create(""), (int)Coin.MarketContact) || !(Undef(x, "image") || (IsImg(Get(x, "image")) && Str(Get(x, "image"))!.Length <= Coin.MarketImg))) return false;
        if (!(Coin.AmtOk(Get(x, "price")) || (Undef(x, "price") && Str(Get(x, "type")) is "job" or "wanted" && Str(Get(x, "kind")) == "fixed"))) return false;
        if (!(Str(Get(x, "kind")) == "fixed" || (IsSafeInteger(Get(x, "ends")) && Num(Get(x, "ends")) > 0))) return false;
        if (!(Undef(x, "category") || (Str(Get(x, "category")) is { } cat && (Payload.AdCategories.Contains(cat) || cat == "adviews")))) return false;
        if (!(Undef(x, "certs") || (Get(x, "certs") is JsonArray cs && cs.Count <= Coin.MaxCerts && cs.All(c => IsHex(c, 64)) && cs.Select(c => Str(c)).Distinct().Count() == cs.Count))) return false;
        if (!Payload.ValidTags(x, "tags")) return false;
        if (!(Undef(x, "place") || Payload.PlaceShape(Get(x, "place")))) return false;
        if (!(Undef(x, "deal") || Str(Get(x, "deal")) is "sale" or "rent")) return false;
        return Payload.MediaListOk(x) && (Undef(x, "fee") || Coin.AmtOk(Get(x, "fee")));
    }

    /// <summary>domainOf(url): the host name of an http(s) address, lower case, without "www."; null otherwise.</summary>
    public static string? DomainOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return null;
        var h = u.IdnHost.ToLowerInvariant();
        return h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{80,100}$")] private static partial Regex MsgKeyRe();
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9._-]{1,28}[a-z0-9])$")] private static partial Regex NameRe();
    [GeneratedRegex("[._-]{2}")] private static partial Regex DoublePunctRe();
    [GeneratedRegex(@"^[0-9]{1,12}(\.[0-9]{1,12})?$")] private static partial Regex RateRe();
}
