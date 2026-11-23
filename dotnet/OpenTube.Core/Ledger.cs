using System.Text.Json.Nodes;
using static OpenTube.Core.Js;

namespace OpenTube.Core;

/// <summary>
/// class Ledger: the coin. Balances belong to identities; transactions pay for posting, viewing and votes, run ads,
/// a marketplace with escrow and courts, swaps with other chains, names, and pay nodes for what they serve. Ported line
/// for line from the JavaScript core (same checks in the same order, same arithmetic in doubles, JavaScript's rounding);
/// OpenTube.Tests replays random transaction histories through both and compares the whole state.
/// </summary>
public sealed class Ledger
{
    public Dictionary<string, double> Bal = [], Seq = [], AdViews = [], AdPaid = [], AdBusy = [], Views = [], RcptUse = [], AppealDaysOf = [], TabsPaid = [];
    public HashSet<string> Starters = [], Claimed = [], SlotUsed = [], Receipts = [], LinkVotes = [], Retired = [], ViewersSeen = [];
    public Dictionary<string, JsonObject> Paid = [], Names = [], Htlcs = [], Offers = [], Certs = [], Listings = [];
    public Dictionary<string, string> NameOf = [], Superior = [], Owners = [];
    public Dictionary<string, HashSet<string>> Hosts = [], Judges = [];
    public Dictionary<string, Dictionary<string, double>> Served = [];
    public Dictionary<double, double> PoolFees = [];
    public HashSet<double> Settled = [];
    public Dictionary<string, List<JsonObject>> Earned = [];
    public Dictionary<string, (double Up, double Down)> Domains = [];
    public Dictionary<string, List<string>> MsgKeys = [];
    public Dictionary<string, double> Prices = new(Coin.Price0);
    Dictionary<string, double> use = NewUse();
    public double Fees, Minted, Burned, TxCount, Clock;

    static Dictionary<string, double> NewUse() => Coin.Resources.ToDictionary(r => r, _ => 0.0);
    public Ledger() { }
    /// <summary>new Ledger(src): a copy to apply the next block's transactions to.</summary>
    public Ledger(Ledger s)
    {
        Bal = new(s.Bal); Seq = new(s.Seq); Starters = new(s.Starters); Paid = s.Paid.ToDictionary(kv => kv.Key, kv => kv.Value);
        Hosts = s.Hosts.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value)); Prices = new(s.Prices);
        Minted = s.Minted; Burned = s.Burned; TxCount = s.TxCount;
        Claimed = new(s.Claimed); SlotUsed = new(s.SlotUsed); AdViews = new(s.AdViews); AdPaid = new(s.AdPaid); AdBusy = new(s.AdBusy); Views = new(s.Views);
        Served = s.Served.ToDictionary(kv => kv.Key, kv => new Dictionary<string, double>(kv.Value));
        Receipts = new(s.Receipts); RcptUse = new(s.RcptUse); PoolFees = new(s.PoolFees); Settled = new(s.Settled);
        Earned = s.Earned.ToDictionary(kv => kv.Key, kv => new List<JsonObject>(kv.Value)); Clock = s.Clock;
        Names = s.Names.ToDictionary(kv => kv.Key, kv => kv.Value); NameOf = new(s.NameOf);
        Htlcs = s.Htlcs.ToDictionary(kv => kv.Key, kv => (JsonObject)kv.Value.DeepClone()); Offers = s.Offers.ToDictionary(kv => kv.Key, kv => (JsonObject)kv.Value.DeepClone());
        Domains = new(s.Domains); LinkVotes = new(s.LinkVotes); AppealDaysOf = new(s.AppealDaysOf);
        MsgKeys = s.MsgKeys.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value)); Superior = new(s.Superior);
        Judges = s.Judges.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value)); Owners = new(s.Owners); Retired = new(s.Retired);
        TabsPaid = new(s.TabsPaid); ViewersSeen = new(s.ViewersSeen);
        Certs = s.Certs.ToDictionary(kv => kv.Key, kv => (JsonObject)kv.Value.DeepClone()); Listings = s.Listings.ToDictionary(kv => kv.Key, kv => (JsonObject)kv.Value.DeepClone());
    }

    // ---- keys: a map key the way JavaScript compares them (strings by value, null as "null")
    static string K(JsonNode? n) => Str(n) ?? (n == null ? "null" : "#" + Blocks.Canon(n));
    public string Resolve(string k) { for (int i = 0; i < 8 && Owners.TryGetValue(k, out var o); i++) k = o; return k; }
    public double Balance(string k) => Bal.TryGetValue(Resolve(k), out var v) ? v : 0;
    public void Add(string k, double d) { k = Resolve(k); var v = (Bal.TryGetValue(k, out var x) ? x : 0) + d; if (v != 0) Bal[k] = v; else Bal.Remove(k); }
    static double G(Dictionary<string, double> m, string k) => m.TryGetValue(k, out var v) ? v : 0;
    static double N(JsonNode? n) => Num(n) ?? double.NaN;
    static string S(JsonNode? n) => K(n);
    static string Ep(double e) => NumberToString(e);

    /// <summary>check(t): null if the transaction applies, or why it doesn't.</summary>
    public string? Check(JsonObject t)
    {
        var from = S(Get(t, "from")); var ts = N(Get(t, "ts"));
        if (N(Get(t, "seq")) != G(Seq, from) + 1) return "out of order (sequence)";
        if (Retired.Contains(from)) return "this key was retired";
        if (Truthy(Get(t, "bind"))) { var key = S(Get(Get(t, "bind"), "key")); return Resolve(key) == from ? "already bound to you" : key == from || Owners.ContainsKey(from) ? "only an identity can own keys" : null; }
        if (Truthy(Get(t, "retire"))) { var to = S(Get(Get(t, "retire"), "to")); return Owners.ContainsKey(to) || Retired.Contains(to) || to == from ? "that key can’t take over" : null; }
        if (NumIs(Get(t, "v"), 1) && !Truthy(Get(t, "name"))) return null;
        if (Truthy(Get(t, "name")))
        {
            var n = Get(t, "name")!; NameOf.TryGetValue(S(Get(n, "chain")), out var cur);
            if (IsNullValue(n, "handle")) return cur != null ? null : "there is no name to release";
            var k = Str(Get(n, "space")) + Str(Get(n, "handle")); Names.TryGetValue(k, out var holder);
            if (cur == k) return "that is already the name";
            if (holder != null && S(Get(holder, "chain")) != S(Get(n, "chain"))) return "that name is taken";
            return null;
        }
        if (Truthy(Get(t, "starter"))) return Starters.Contains(from) ? "starter coins were already claimed by this key" : null;
        if (Truthy(Get(t, "serve")))
        {
            var e = N(Get(Get(t, "serve"), "epoch"));
            if (e < Coin.EpochOf(Coin.ServeStart) || e >= Coin.EpochOf(ts) || Settled.Contains(e)) return "that period can’t take receipts";
            if (((JsonArray)Get(Get(t, "serve"), "receipts")!).Any(r => S(Get(r, "provider")) != from || N(Get(r, "epoch")) != e)) return "receipts for another node or period";
            return null;
        }
        if (Truthy(Get(t, "host"))) return null;
        if (Truthy(Get(t, "tabs"))) return ((JsonArray)Get(t, "tabs")!).All(x => S(Get(x, "payee")) == from) ? null : "tabs for someone else";
        if (Truthy(Get(t, "redeem")) || Truthy(Get(t, "certs")))
        {
            var ids = (JsonArray)Get(Truthy(Get(t, "redeem")) ? Get(t, "redeem") : Get(t, "certs"), "ids")!;
            return ids.All(id => Certs.TryGetValue(S(id), out var c) && S(Get(c, "owner")) == from) ? null : "you don’t own those ad views";
        }
        if (Truthy(Get(t, "list")))
        {
            var x = Get(t, "list")!;
            if (Str(Get(x, "kind")) == "auction" && (N(Get(x, "ends")) <= ts || N(Get(x, "ends")) > ts + Coin.MarketMaxDays * 86400000)) return "auctions end between now and 30 days from now";
            if (Get(x, "certs") is JsonArray cs && cs.Any(id => !(Certs.TryGetValue(S(id), out var c) && S(Get(c, "owner")) == from))) return "you don’t own those ad views";
            if (ts >= Coin.FeeStart)
            {
                if (!(N(Get(x, "fee")) >= Math.Floor(Math.Ceiling(Prices["list"]) * Coin.Slack))) return "the listing fee went up; try again";
                if (Balance(from) < N(Get(x, "fee"))) return $"listing costs {NumberToString(N(Get(x, "fee")))} units; you don’t have enough coins";
            }
            return null;
        }
        if (Truthy(Get(t, "release")) || Truthy(Get(t, "dispute")) || Truthy(Get(t, "rule")))
        {
            var act = Truthy(Get(t, "release")) ? "release" : Truthy(Get(t, "dispute")) ? "dispute" : "rule";
            Listings.TryGetValue(S(Get(Get(t, act), "id")), out var L); var E = L == null ? null : Get(L, "escrow") as JsonObject;
            if (E == null || Str(Get(E, "state")) is not ("held" or "disputed")) return "nothing is held for that listing";
            var party = new[] { Get(E, "buyer"), Get(L, "seller") }.Any(k => Resolve(S(k)) == Resolve(from));
            if (act == "release") return Str(Get(E, "state")) == "held" && Resolve(S(Get(E, "buyer"))) == Resolve(from) ? null : "only the buyer releases the payment";
            if (act == "dispute") return Str(Get(E, "state")) == "held" && party ? null : "only the buyer or the seller can dispute, once";
            return Str(Get(E, "state")) == "disputed" && MayRule(CourtOf(E, L!), from) ? null : "only the court hearing the dispute (or its judges) can rule";
        }
        if (Truthy(Get(t, "appeal")))
        {
            Listings.TryGetValue(S(Get(Get(t, "appeal"), "id")), out var L); var E = L == null ? null : Get(L, "escrow") as JsonObject;
            if (E == null || Str(Get(E, "state")) != "ruled" || ts > N(Get(E, "until"))) return "there’s no ruling open to appeal";
            if (!new[] { Get(E, "buyer"), Get(L, "seller") }.Any(k => Resolve(S(k)) == Resolve(from))) return "only the buyer or the seller can appeal";
            return Superior.ContainsKey(CourtOf(E, L!)) ? null : "this is the highest court: its ruling is final";
        }
        if (Truthy(Get(t, "court"))) return Superior.ContainsKey(from) ? "this court already has a court above it" : null;
        if (Truthy(Get(t, "courtcfg"))) return null;
        if (Truthy(Get(t, "htlc")))
        {
            var h = Get(t, "htlc")!; var exp = N(Get(h, "expires"));
            if (exp < ts + 10 * 60000 || exp > ts + Coin.XchgMaxDays * 86400000) return "the deadline must be between 10 minutes and 30 days away";
            if (Resolve(S(Get(h, "to"))) == Resolve(from)) return "that’s yourself";
            return Balance(from) < N(Get(h, "amt")) ? "not enough coins" : null;
        }
        if (Truthy(Get(t, "unlock")) || Truthy(Get(t, "refund")))
        {
            var unlock = Truthy(Get(t, "unlock"));
            Htlcs.TryGetValue(S(Get(Get(t, unlock ? "unlock" : "refund"), "id")), out var H);
            if (H == null || Str(Get(H, "state")) != "locked") return "no locked payment with that id";
            if (unlock)
            {
                if (Resolve(S(Get(H, "to"))) != Resolve(from)) return "only the recipient can unlock it";
                if (ts >= N(Get(H, "expires"))) return "too late: the deadline has passed";
                return Tx.UnlockHash.TryGetValue(t, out var uh) && uh == Str(Get(H, "hash")) ? null : "that isn’t the secret for this lock";
            }
            if (Resolve(S(Get(H, "from"))) != Resolve(from)) return "only the sender can take it back";
            return ts >= N(Get(H, "expires")) ? null : "it can be taken back only after the deadline";
        }
        if (Truthy(Get(t, "offer"))) return null;
        if (Truthy(Get(t, "endoffer"))) { Offers.TryGetValue(S(Get(Get(t, "endoffer"), "id")), out var O); return O != null && Str(Get(O, "state")) == "open" && Resolve(S(Get(O, "from"))) == Resolve(from) ? null : "not your open offer"; }
        if (Truthy(Get(t, "linkvote")))
        {
            var x = Get(t, "linkvote")!;
            if (LinkVotes.Contains(from + "|" + Str(Get(x, "url")))) return "you already voted on that link";
            if (N(Get(x, "amt")) < Math.Floor(Math.Ceiling(Prices["like"]) * Coin.Slack)) return "the voting price went up; try again";
            return Balance(from) < N(Get(x, "amt")) ? "not enough coins" : null;
        }
        if (Truthy(Get(t, "msgkey")))
        {
            var cur = MsgKeys.TryGetValue(from, out var c) ? c : []; var key = Str(Get(Get(t, "msgkey"), "key"));
            return IsTrue(Get(Get(t, "msgkey"), "on")) ? (cur.Contains(key!) ? "already published" : null) : (cur.Contains(key!) ? null : "not one of your messaging keys");
        }
        if (Truthy(Get(t, "judge"))) return null;
        if (Truthy(Get(t, "unlist"))) { Listings.TryGetValue(S(Get(Get(t, "unlist"), "id")), out var L); return L == null || S(Get(L, "seller")) != from ? "not your listing" : Truthy(Get(L, "high")) ? "it already has a bid" : null; }
        if (Truthy(Get(t, "buy")))
        {
            Listings.TryGetValue(S(Get(Get(t, "buy"), "id")), out var L);
            if (L == null || Str(Get(L, "kind")) != "fixed" || Str(Get(L, "state")) != "open" || !Truthy(Get(L, "price"))) return "that listing isn’t for sale";
            if (S(Get(L, "seller")) == from) return "it’s your own listing";
            return Balance(from) < N(Get(L, "price")) ? "not enough coins" : null;
        }
        if (Truthy(Get(t, "bid")))
        {
            Listings.TryGetValue(S(Get(Get(t, "bid"), "id")), out var L);
            if (L == null || Str(Get(L, "kind")) != "auction" || N(Get(L, "ends")) <= ts) return "that auction is over";
            if (S(Get(L, "seller")) == from) return "it’s your own auction";
            var high = Get(L, "high");
            var min = Truthy(high) ? N(Get(high, "amt")) + Math.Max(1, Math.Ceiling(N(Get(high, "amt")) * Coin.MinStep)) : N(Get(L, "price"));
            if (N(Get(Get(t, "bid"), "amt")) < min) return $"the lowest bid now is {NumberToString(min)} units";
            return Balance(from) < N(Get(Get(t, "bid"), "amt")) ? "not enough coins" : null;
        }
        if (Truthy(Get(t, "claim")))
        {
            var c = Get(t, "claim")!; var terms = Get(c, "terms")!; var adv = S(Get(terms, "advertiser"));
            if (!Tx.ClaimTid.TryGetValue(t, out var tid)) return "claim not verified";
            if (G(AdBusy, from) > N(Get(c, "slot"))) return "you were already paid for an ad at that time";
            if (G(AdViews, tid) >= N(Get(terms, "maxViews"))) return "this ad has reached its maximum views";
            if (Balance(adv) < N(Get(terms, "perView"))) return "the advertiser has run out of coins for this ad";
            return null;
        }
        var need = Cost(t);
        if (need.Why != null) return need.Why;
        if (Balance(from) < need.Amt) return "not enough coins";
        return null;
    }
    static bool IsNullValue(JsonNode? o, string k) => o is JsonObject jo && jo.TryGetPropertyValue(k, out var v) && v == null;
    static string CourtOf(JsonObject E, JsonObject L) => Truthy(Get(E, "court")) ? S(Get(E, "court")) : S(Get(L, "arbiter"));

    /// <summary>cost(t): what t takes from the sender, or why it's not allowed.</summary>
    (double Amt, string? Why) Cost(JsonObject t)
    {
        if (Truthy(Get(t, "pay"))) return (((JsonArray)Get(t, "pay")!).Sum(o => N(Get(o, "amt"))), null);
        if (Truthy(Get(t, "post"))) { var p = Get(t, "post")!; if (Paid.ContainsKey("post:" + Str(Get(p, "video")))) return (0, "this video is already paid for"); if (N(Get(p, "amt")) < Math.Floor(Coin.PostCost(Prices["post"], N(Get(p, "size"))) * Coin.Slack)) return (0, "the posting price went up; try again"); return (N(Get(p, "amt")), null); }
        if (Truthy(Get(t, "view"))) { var p = Get(t, "view")!; if (Paid.ContainsKey($"view:{Str(Get(p, "video"))}:{S(Get(t, "from"))}")) return (0, "you already paid for this video"); if (N(Get(p, "amt")) < Math.Floor(Math.Ceiling(Prices["view"]) * Coin.Slack)) return (0, "the viewing price went up; try again"); return (N(Get(p, "amt")), null); }
        if (Truthy(Get(t, "vote"))) { var p = Get(t, "vote")!; if (Paid.ContainsKey("vote:" + Str(Get(p, "target")))) return (0, "this vote is already paid"); if (N(Get(p, "amt")) < Math.Floor(Math.Ceiling(Prices["like"]) * Coin.Slack)) return (0, "the voting price went up; try again"); return (N(Get(p, "amt")), null); }
        return (0, "unknown transaction");
    }

    /// <summary>apply(t): applies the transaction and returns null, or returns why it doesn't apply (and changes nothing).</summary>
    public string? Apply(JsonObject t)
    {
        var why = Check(t); if (why != null) return why;
        var from = S(Get(t, "from")); var ts = N(Get(t, "ts")); var hash = S(Get(t, "hash"));
        Seq[from] = N(Get(t, "seq")); TxCount++;
        if (NumIs(Get(t, "v"), 1) && !Truthy(Get(t, "name"))) return null;
        if (Truthy(Get(t, "name")))
        {
            var n = Get(t, "name")!; var chain = S(Get(n, "chain"));
            if (NameOf.TryGetValue(chain, out var cur)) { Names.Remove(cur); NameOf.Remove(chain); }
            if (Truthy(Get(n, "handle"))) { var k = Str(Get(n, "space")) + Str(Get(n, "handle")); Names[k] = new JsonObject { ["chain"] = Get(n, "chain")?.DeepClone(), ["key"] = from }; NameOf[chain] = k; }
        }
        else if (Truthy(Get(t, "bind")))
        {
            var k = S(Get(Get(t, "bind"), "key")); var raw = Owners.ContainsKey(k) ? 0 : G(Bal, k);
            if (raw != 0) Bal.Remove(k);
            Owners[k] = from; if (raw != 0) Add(from, raw);
        }
        else if (Truthy(Get(t, "retire")))
        {
            var to = S(Get(Get(t, "retire"), "to")); var raw = G(Bal, from); if (raw != 0) Bal.Remove(from);
            Owners[from] = to; Retired.Add(from); if (raw != 0) Add(to, raw);
            if (MsgKeys.TryGetValue(from, out var mine)) { MsgKeys[to] = [.. (MsgKeys.TryGetValue(to, out var theirs) ? theirs : []), .. mine]; MsgKeys[to] = MsgKeys[to].TakeLast((int)Coin.MailKeys).ToList(); MsgKeys.Remove(from); }
            if (Starters.Contains(from)) Starters.Add(to);
        }
        else if (Truthy(Get(t, "starter"))) { Starters.Add(from); Add(from, Coin.Starter); Minted += Coin.Starter; }
        else if (Truthy(Get(t, "serve")))
        {
            var s = Get(t, "serve")!; var e = N(Get(s, "epoch")); var payee = Tx.ServeGrant.TryGetValue(t, out var g) ? g : from;
            foreach (var r in (JsonArray)Get(s, "receipts")!)
            {
                var res = Str(Get(r, "res"))!; var by = S(Get(r, "by"));
                var id = $"{Ep(e)}|{res}|{by}|{S(Get(r, "provider"))}";
                if (Receipts.Contains(id) || !Starters.Contains(by)) continue;
                var useK = $"{Ep(e)}|{res}|{by}"; var used = G(RcptUse, useK); var amt = Math.Min(N(Get(r, "amt")), Coin.ServeRes.First(x => x.Res == res).Cap - used);
                if (amt <= 0) continue;
                Receipts.Add(id); RcptUse[useK] = used + amt;
                var k = $"{Ep(e)}|{res}"; var m = Served.TryGetValue(k, out var mm) ? mm : Served[k] = [];
                m[payee] = G(m, payee) + amt;
            }
        }
        else if (Truthy(Get(t, "host")))
        {
            var h = Get(t, "host")!; var chain = S(Get(h, "chain"));
            var set = Hosts.TryGetValue(chain, out var hs) ? hs : Hosts[chain] = [];
            if (Truthy(Get(h, "on"))) set.Add(from); else { set.Remove(from); if (set.Count == 0) Hosts.Remove(chain); }
        }
        else if (Truthy(Get(t, "claim")))
        {
            var c = Get(t, "claim")!; var terms = Get(c, "terms")!; var adv = S(Get(terms, "advertiser")); Tx.ClaimTid.TryGetValue(t, out var tid);
            var perView = N(Get(terms, "perView")); var size = N(Get(terms, "size"));
            var p = IsUndefined(c, "bytes") ? perView : Math.Max(1, Math.Floor(perView * Math.Min(N(Get(c, "bytes")), size) / size)); var half = Math.Floor(p / 2);
            Add(adv, -p); Add(S(Get(c, "creator")), p - half);
            if (ts >= Coin.PieceStart) Certs[hash] = new JsonObject { ["owner"] = from, ["adv"] = adv, ["tid"] = tid, ["amt"] = half, ["ts"] = ts, ["video"] = Get(c, "video")?.DeepClone(), ["viewer"] = from };
            else Add(from, half);
            Claimed.Add(tid + "|" + from); SlotUsed.Add(from + "|" + NumberToString(N(Get(c, "slot"))));
            AdBusy[from] = N(Get(c, "slot")) + Math.Max(1, Math.Ceiling(N(Get(terms, "duration")) * 1000 / Coin.AdsSlotMs));
            AdViews[tid!] = G(AdViews, tid!) + 1; AdPaid[tid!] = G(AdPaid, tid!) + p;
        }
        else if (Truthy(Get(t, "tabs")))
        {
            foreach (var x in (JsonArray)Get(t, "tabs")!)
            {
                var payer = S(Get(x, "payer")); var payee = S(Get(x, "payee")); var creator = S(Get(x, "creator")); var amt = N(Get(x, "amt"));
                var k = $"{payer}|{payee}|{Str(Get(x, "video"))}"; var prev = G(TabsPaid, k);
                var pay = Math.Min(amt - prev, Balance(payer)); if (pay <= 0) continue;
                Add(payer, -pay);
                var cut = FeeCut(ts, pay); var toCreator = creator == payee ? 0 : Math.Floor((pay - cut) * Coin.CreatorShare);
                Add(creator, toCreator); Add(payee, pay - cut - toCreator);
                TabsPaid[k] = prev + pay; use["piece"] += Math.Max(1, JsRound(N(Get(x, "n")) * pay / amt));
                var vk = Str(Get(x, "video")) + "|" + payer;
                if (ViewersSeen.Add(vk)) Views[Str(Get(x, "video"))!] = G(Views, Str(Get(x, "video"))!) + 1;
            }
        }
        else if (Truthy(Get(t, "redeem"))) { foreach (var id in (JsonArray)Get(Get(t, "redeem"), "ids")!) { var c = Certs[S(id)]; Add(from, N(Get(c, "amt"))); Certs.Remove(S(id)); } }
        else if (Truthy(Get(t, "certs"))) { foreach (var id in (JsonArray)Get(Get(t, "certs"), "ids")!) Certs[S(id)]["owner"] = Get(Get(t, "certs"), "to")?.DeepClone(); }
        else if (Truthy(Get(t, "list")))
        {
            var x = (JsonObject)Get(t, "list")!;
            if (Get(x, "certs") is JsonArray cs) foreach (var id in cs) Certs[S(id)]["owner"] = "listing:" + hash;
            var fee = ts >= Coin.FeeStart ? N(Get(x, "fee")) : 0;
            if (fee != 0 && !double.IsNaN(fee))
            {
                Add(from, -fee);
                if (Truthy(Get(x, "arbiter"))) Add(S(Get(x, "arbiter")), fee - FeeCut(ts, fee));
                else { var e = Coin.EpochOf(ts); PoolFees[e] = (PoolFees.TryGetValue(e, out var pf) ? pf : 0) + fee; }
                use["list"]++;
            }
            var L = (JsonObject)x.DeepClone();
            L["certs"] = Get(x, "certs") is JsonArray c2 ? c2.DeepClone() : new JsonArray();
            L["seller"] = from; L["ts"] = ts; L["high"] = null; L["bids"] = 0; L["state"] = "open";
            Listings[hash] = L;
        }
        else if (Truthy(Get(t, "unlist"))) { var id = S(Get(Get(t, "unlist"), "id")); CloseListing(Listings[id], null, "withdrawn"); }
        else if (Truthy(Get(t, "buy")))
        {
            var id = S(Get(Get(t, "buy"), "id")); var L = Listings[id]; var price = N(Get(L, "price"));
            Add(from, -price); Pay(L, from, price, ts);
            var qty = Truthy(Get(L, "qty")) ? N(Get(L, "qty")) : 1;
            if (qty > 1)
            {
                L["qty"] = N(Get(L, "qty")) - 1; L["sold"] = (Truthy(Get(L, "sold")) ? N(Get(L, "sold")) : 0) + 1;
                if (Get(L, "buyers") is not JsonArray buyers || !Truthy(Get(L, "buyers"))) L["buyers"] = buyers = new JsonArray();
                buyers.Add(from);
            }
            else CloseListing(L, from, "sold");
        }
        else if (Truthy(Get(t, "release"))) { var L = Listings[S(Get(Get(t, "release"), "id"))]; PayOut(L, 0, ts, "released"); }
        else if (Truthy(Get(t, "appeal")))
        {
            var a = Get(t, "appeal")!; var L = Listings[S(Get(a, "id"))]; var E = (JsonObject)Get(L, "escrow")!;
            E["state"] = "disputed";
            SetOrRemove(E, "court", Superior.TryGetValue(CourtOf(E, L), out var up) ? up : null);
            E["appeals"] = (Truthy(Get(E, "appeals")) ? N(Get(E, "appeals")) : 0) + 1;
            var newCase = Truthy(Get(a, "case")) ? Get(a, "case")!.DeepClone() : Get(E, "case")?.DeepClone();
            if (Truthy(Get(a, "case")) || Has(E, "case")) E["case"] = newCase;
        }
        else if (Truthy(Get(t, "linkvote")))
        {
            var x = Get(t, "linkvote")!; var dk = Str(Get(x, "domain")) ?? "null"; var amt = N(Get(x, "amt"));
            var d = Domains.TryGetValue(dk, out var dd) ? dd : (0, 0);
            Add(from, -amt); var cut = FeeCut(ts, amt);
            if (N(Get(x, "v")) > 0 && Truthy(Get(x, "to")) && Resolve(S(Get(x, "to"))) != Resolve(from)) Add(S(Get(x, "to")), amt - cut); else Burned += amt - cut;
            if (N(Get(x, "v")) > 0) d.Up++; else d.Down++;
            Domains[dk] = d; LinkVotes.Add(from + "|" + Str(Get(x, "url"))); use["like"]++;
        }
        else if (Truthy(Get(t, "htlc")))
        {
            var h = (JsonObject)Get(t, "htlc")!; Add(from, -N(Get(h, "amt")));
            var H = (JsonObject)h.DeepClone(); H["from"] = from; H["ts"] = ts; H["state"] = "locked"; Htlcs[hash] = H;
        }
        else if (Truthy(Get(t, "unlock"))) { var H = Htlcs[S(Get(Get(t, "unlock"), "id"))]; Add(S(Get(H, "to")), N(Get(H, "amt"))); H["state"] = "unlocked"; H["preimage"] = Get(Get(t, "unlock"), "preimage")?.DeepClone(); H["at"] = ts; }
        else if (Truthy(Get(t, "refund"))) { var H = Htlcs[S(Get(Get(t, "refund"), "id"))]; Add(S(Get(H, "from")), N(Get(H, "amt"))); H["state"] = "refunded"; H["at"] = ts; }
        else if (Truthy(Get(t, "offer"))) { var O = (JsonObject)Get(t, "offer")!.DeepClone(); O["from"] = from; O["ts"] = ts; O["state"] = "open"; Offers[hash] = O; }
        else if (Truthy(Get(t, "endoffer"))) Offers[S(Get(Get(t, "endoffer"), "id"))]["state"] = "closed";
        else if (Truthy(Get(t, "courtcfg"))) AppealDaysOf[from] = N(Get(Get(t, "courtcfg"), "appealDays"));
        else if (Truthy(Get(t, "msgkey")))
        {
            var key = Str(Get(Get(t, "msgkey"), "key"))!;
            var cur = (MsgKeys.TryGetValue(from, out var c) ? c : []).Where(k => k != key).ToList();
            if (IsTrue(Get(Get(t, "msgkey"), "on"))) cur.Add(key);
            if (cur.Count > 0) MsgKeys[from] = cur.TakeLast((int)Coin.MailKeys).ToList(); else MsgKeys.Remove(from);
        }
        else if (Truthy(Get(t, "court"))) Superior[from] = S(Get(Get(t, "court"), "parent"));
        else if (Truthy(Get(t, "judge")))
        {
            var set = Judges.TryGetValue(from, out var js) ? js : Judges[from] = [];
            if (IsTrue(Get(Get(t, "judge"), "on"))) set.Add(S(Get(Get(t, "judge"), "key"))); else set.Remove(S(Get(Get(t, "judge"), "key")));
        }
        else if (Truthy(Get(t, "dispute")))
        {
            var dsp = Get(t, "dispute")!; var E = (JsonObject)Get(Listings[S(Get(dsp, "id"))], "escrow")!;
            E["state"] = "disputed"; E["disputedBy"] = from; E["case"] = Truthy(Get(dsp, "case")) ? Get(dsp, "case")!.DeepClone() : null;
        }
        else if (Truthy(Get(t, "rule")))
        {
            var L = Listings[S(Get(Get(t, "rule"), "id"))]; var E = (JsonObject)Get(L, "escrow")!;
            E["state"] = "ruled"; E["buyerShare"] = Get(Get(t, "rule"), "buyerShare")?.DeepClone(); E["ruledBy"] = from;
            var court = Truthy(Get(E, "court")) ? Get(E, "court")!.DeepClone() : Get(L, "arbiter")?.DeepClone();
            SetOrRemove(E, "court", court, Truthy(Get(E, "court")) || Has(L, "arbiter"));
            var days = AppealDaysOf.TryGetValue(K(Get(E, "court")), out var ad) ? ad : Coin.AppealDays;
            E["until"] = ts + days * 86400000;
        }
        else if (Truthy(Get(t, "bid")))
        {
            var b = Get(t, "bid")!; var L = Listings[S(Get(b, "id"))];
            if (Get(L, "high") is JsonObject high && Truthy(high)) Add(S(Get(high, "by")), N(Get(high, "amt")));
            Add(from, -N(Get(b, "amt"))); L["high"] = new JsonObject { ["by"] = from, ["amt"] = N(Get(b, "amt")), ["ts"] = ts }; L["bids"] = N(Get(L, "bids")) + 1;
        }
        else if (Truthy(Get(t, "pay"))) { foreach (var o in (JsonArray)Get(t, "pay")!) { Add(from, -N(Get(o, "amt"))); Add(S(Get(o, "to")), N(Get(o, "amt"))); } }
        else if (Truthy(Get(t, "post")))
        {
            var p = Get(t, "post")!; var chain = S(Get(p, "chain"));
            var hosts = (Hosts.TryGetValue(chain, out var hs) ? hs.ToList() : []).Where(k => k != from).ToList();
            Add(from, -N(Get(p, "amt")));
            var amt = N(Get(p, "amt")) - FeeCut(ts, N(Get(p, "amt")));
            if (hosts.Count > 0) { var each = Math.Floor(amt / hosts.Count); foreach (var k in hosts) Add(k, each); Burned += amt - each * hosts.Count; }
            else Fees += amt;
            Paid["post:" + Str(Get(p, "video"))] = new JsonObject { ["from"] = from, ["amt"] = amt, ["to"] = null };
            use["post"] += N(Get(p, "size")) / 1e9;
        }
        else if (Truthy(Get(t, "view")))
        {
            var p = Get(t, "view")!; var amt = N(Get(p, "amt")); var cut = FeeCut(ts, amt);
            Add(from, -amt); Add(S(Get(p, "to")), amt - cut);
            Paid[$"view:{Str(Get(p, "video"))}:{from}"] = new JsonObject { ["from"] = from, ["amt"] = amt, ["to"] = Get(p, "to")?.DeepClone() }; use["view"]++;
            Views[Str(Get(p, "video"))!] = G(Views, Str(Get(p, "video"))!) + 1;
        }
        else if (Truthy(Get(t, "vote")))
        {
            var p = Get(t, "vote")!; var amt = N(Get(p, "amt"));
            Add(from, -amt); var cut = FeeCut(ts, amt);
            if (N(Get(p, "v")) > 0) Add(S(Get(p, "to")), amt - cut); else Burned += amt - cut;
            Paid["vote:" + Str(Get(p, "target"))] = new JsonObject { ["from"] = from, ["amt"] = amt, ["to"] = N(Get(p, "v")) > 0 ? Get(p, "to")?.DeepClone() : null }; use["like"]++;
        }
        return null;
    }
    static bool IsUndefined(JsonNode? o, string k) => o is not JsonObject jo || !jo.ContainsKey(k);
    static void SetOrRemove(JsonObject o, string k, string? v) { if (v == null) o.Remove(k); else o[k] = v; }
    static void SetOrRemove(JsonObject o, string k, JsonNode? v, bool defined) { if (!defined) o.Remove(k); else o[k] = v; }
    /// <summary>Math.round: halves go up (toward +∞).</summary>
    public static double JsRound(double x) { var r = Math.Floor(x); return x - r >= 0.5 ? r + 1 : r; }

    public bool MayRule(string court, string key) =>
        Resolve(court) == Resolve(key) || (Judges.TryGetValue(court, out var j) && (j.Contains(key) || j.Any(x => Resolve(x) == Resolve(key))));
    void Pay(JsonObject L, string buyer, double amt, double ts)
    {
        if (Truthy(Get(L, "arbiter"))) L["escrow"] = new JsonObject { ["buyer"] = buyer, ["amt"] = amt, ["state"] = "held", ["until"] = ts + (Truthy(Get(L, "hold")) ? JsToNumber(Get(L, "hold")) : 14) * 86400000 };
        else Add(S(Get(L, "seller")), amt - FeeCut(ts, amt));
    }
    void PayOut(JsonObject L, double toBuyer, double ts, string state)
    {
        var E = (JsonObject)Get(L, "escrow")!; var toSeller = N(Get(E, "amt")) - toBuyer;
        if (toBuyer != 0) Add(S(Get(E, "buyer")), toBuyer);
        if (toSeller != 0) Add(S(Get(L, "seller")), toSeller - FeeCut(ts, toSeller));
        E["state"] = state; E["toBuyer"] = toBuyer; E["at"] = ts;
    }
    void CloseListing(JsonObject L, string? buyer, string state)
    {
        foreach (var c in (Get(L, "certs") as JsonArray) ?? []) if (Certs.TryGetValue(S(c), out var x)) x["owner"] = buyer ?? Get(L, "seller")?.DeepClone();
        L["state"] = state; L["buyer"] = buyer;
    }
    void SettleAuctions(double now)
    {
        foreach (var (id, L) in Listings)
        {
            if (Str(Get(L, "state")) != "open" || Str(Get(L, "kind")) != "auction" || N(Get(L, "ends")) > now) continue;
            if (Get(L, "high") is JsonObject high) { Pay(L, S(Get(high, "by")), N(Get(high, "amt")), N(Get(L, "ends"))); CloseListing(L, S(Get(high, "by")), "sold"); }
            else CloseListing(L, null, "ended");
        }
        foreach (var L in Listings.Values)
        {
            var E = Get(L, "escrow") as JsonObject; var st = Str(Get(E, "state"));
            if (st == "held" && N(Get(E, "until")) <= now) PayOut(L, 0, now, "released");
            else if (st == "ruled" && N(Get(E, "until")) <= now) PayOut(L, Math.Floor(N(Get(E, "amt")) * N(Get(E, "buyerShare"))), now, "final");
        }
    }
    /// <summary>feeCut(t, amt): a share of a payment goes to the resource pools of its period.</summary>
    double FeeCut(double ts, double amt)
    {
        if (ts < Coin.ServeStart) return 0;
        var cut = Math.Floor(amt * Coin.FeeShare); if (cut == 0 || double.IsNaN(cut)) return 0;   // !cut
        var e = Coin.EpochOf(ts); PoolFees[e] = (PoolFees.TryGetValue(e, out var v) ? v : 0) + cut;
        return cut;
    }
    /// <summary>settle(now): periods whose receipt window has closed: each resource's pool split by receipted amounts.</summary>
    void Settle(double now)
    {
        var done = Coin.EpochOf(now) - Coin.Grace - 1;
        var pending = Served.Keys.Select(k => double.Parse(k.Split('|')[0], System.Globalization.CultureInfo.InvariantCulture)).Concat(PoolFees.Keys).Distinct().OrderBy(x => x).ToList();
        foreach (var e in pending)
        {
            if (e > done || Settled.Contains(e)) continue;
            var fees = PoolFees.TryGetValue(e, out var f) ? f : 0; double paid = 0;
            foreach (var (res, pool0, _, weight) in Coin.ServeRes)
            {
                Served.TryGetValue($"{Ep(e)}|{res}", out var m); var total = m?.Values.Sum() ?? 0;
                var feePart = Math.Floor(fees * weight);
                if (total == 0) continue;
                var pool = pool0 + feePart; Minted += pool0;
                foreach (var (payee, units) in m!)
                {
                    var amt = Math.Floor(pool * units / total); if (amt == 0) continue;
                    Add(payee, amt); paid += amt;
                    var list = Earned.TryGetValue(payee, out var l) ? l : Earned[payee] = [];
                    list.Add(new JsonObject { ["e"] = e, ["res"] = res, ["units"] = units, ["amt"] = amt }); if (list.Count > 200) list.RemoveAt(0);
                }
                paid -= pool0;
            }
            Burned += Math.Max(0, fees - Math.Max(0, paid));
            Settled.Add(e); PoolFees.Remove(e);
            var pre = Ep(e) + "|";
            foreach (var k in Served.Keys.Where(k => k.StartsWith(pre, StringComparison.Ordinal)).ToList()) Served.Remove(k);
            foreach (var k in RcptUse.Keys.Where(k => k.StartsWith(pre, StringComparison.Ordinal)).ToList()) RcptUse.Remove(k);
            Receipts.RemoveWhere(k => k.StartsWith(pre, StringComparison.Ordinal));
        }
    }
    /// <summary>endBlock(miner, ts): settle due periods and auctions, pay the miner, move prices toward the target load.</summary>
    public void EndBlock(JsonNode? miner, double ts = 0)
    {
        if (ts > Clock) { Clock = ts; Settle(ts); SettleAuctions(ts); }
        if (IsKey(miner)) { Add(Str(miner)!, Coin.Reward + Fees); Minted += Coin.Reward; }
        else Burned += Fees;
        foreach (var k in Coin.Resources)
        {
            var f = Math.Max(-1, Math.Min(1, (use[k] - Coin.Target[k]) / Coin.Target[k]));
            Prices[k] = Math.Min(Coin.Max[k], Math.Max(Coin.Min[k], JsRound(Prices[k] * (1 + Coin.Step * f) * 1000) / 1000));
        }
        use = NewUse(); Fees = 0;
    }

    /// <summary>The whole state as canonical JSON (the tests compare it with the JavaScript ledger's).</summary>
    public string Snapshot()
    {
        JsonObject Obj<T>(IEnumerable<KeyValuePair<string, T>> m, Func<T, JsonNode?> f) { var o = new JsonObject(); foreach (var (k, v) in m) o[k] = f(v); return o; }
        JsonArray Sorted(IEnumerable<string> s) => new(s.Order(StringComparer.Ordinal).Select(x => (JsonNode?)x).ToArray());
        var o = new JsonObject
        {
            ["bal"] = Obj(Bal, v => v), ["seq"] = Obj(Seq, v => v), ["starters"] = Sorted(Starters), ["paid"] = Obj(Paid, v => v.DeepClone()),
            ["hosts"] = Obj(Hosts, v => Sorted(v)), ["prices"] = Obj(Prices, v => v), ["minted"] = Minted, ["burned"] = Burned, ["txCount"] = TxCount, ["clock"] = Clock,
            ["claimed"] = Sorted(Claimed), ["slotUsed"] = Sorted(SlotUsed), ["adViews"] = Obj(AdViews, v => v), ["adPaid"] = Obj(AdPaid, v => v), ["adBusy"] = Obj(AdBusy, v => v), ["views"] = Obj(Views, v => v),
            ["served"] = Obj(Served, m => Obj(m, v => v)), ["receipts"] = Sorted(Receipts), ["rcptUse"] = Obj(RcptUse, v => v),
            ["poolFees"] = Obj(PoolFees.Select(kv => new KeyValuePair<string, double>(Ep(kv.Key), kv.Value)), v => v), ["settled"] = new JsonArray(Settled.Order().Select(x => (JsonNode?)x).ToArray()),
            ["earned"] = Obj(Earned, l => new JsonArray(l.Select(x => (JsonNode?)x.DeepClone()).ToArray())), ["names"] = Obj(Names, v => v.DeepClone()), ["nameOf"] = Obj(NameOf, v => v),
            ["htlcs"] = Obj(Htlcs, v => v.DeepClone()), ["offers"] = Obj(Offers, v => v.DeepClone()), ["domains"] = Obj(Domains, v => new JsonObject { ["up"] = v.Up, ["down"] = v.Down }),
            ["linkVotes"] = Sorted(LinkVotes), ["appealDays"] = Obj(AppealDaysOf, v => v), ["msgkeys"] = Obj(MsgKeys, v => new JsonArray(v.Select(x => (JsonNode?)x).ToArray())),
            ["superior"] = Obj(Superior, v => v), ["judges"] = Obj(Judges, v => Sorted(v)), ["owners"] = Obj(Owners, v => v), ["retired"] = Sorted(Retired),
            ["tabsPaid"] = Obj(TabsPaid, v => v), ["viewers"] = Sorted(ViewersSeen), ["certs"] = Obj(Certs, v => v.DeepClone()), ["listings"] = Obj(Listings, v => v.DeepClone()),
        };
        return Blocks.Canon(o);
    }

    /// <summary>Number(x) for a JSON value (only reached by fields the network doesn't validate, e.g. a listing's hold).</summary>
    static double JsToNumber(JsonNode? n) => n switch
    {
        null => 0,
        JsonValue v when IsNumber(v) => Js.NumOf(v),
        JsonValue v when IsBool(v) => IsTrue(v) ? 1 : 0,
        JsonValue v when IsString(v) => StringToNumber(v.GetValue<string>()),
        JsonArray a => a.Count == 0 ? 0 : a.Count == 1 && a[0] is JsonValue x && (IsString(x) || IsNumber(x)) ? JsToNumber(x) : double.NaN,
        _ => double.NaN,
    };
    static double StringToNumber(string s)
    {
        s = Trim(s); if (s.Length == 0) return 0;
        if (s is "Infinity" or "+Infinity") return double.PositiveInfinity; if (s == "-Infinity") return double.NegativeInfinity;
        if (s.Length > 2 && s[0] == '0' && s[1] is 'x' or 'X') { try { return (double)Convert.ToUInt64(s[2..], 16); } catch { return double.NaN; } }
        return double.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowExponent, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    }
}
