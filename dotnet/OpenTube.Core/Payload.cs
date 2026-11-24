using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static OpenTube.Core.Js;

namespace OpenTube.Core;

/// <summary>
/// validPayload(chain, b, CFG): what a block on a chain may say, by chain kind and by who wrote it (the owner, or
/// anyone else with anti-spam work). Ported line for line from the JavaScript core; OpenTube.Tests compares the two.
/// </summary>
public static partial class Payload
{
    public static readonly string[] AdCategories = ["apparel", "autos", "beauty", "books", "currency", "education", "electronics", "entertainment", "finance", "food", "games", "health", "home", "housing", "jobs", "pets", "services", "software", "sports", "travel", "other"];
    static readonly string[] OrgRoleNames = ["moderator", "clerk", "judge"];
    static readonly string[] Filings = ["complaint", "motion", "response", "evidence", "appeal", "order", "other"];
    static readonly string[] CaseStates = ["filed", "open", "hearing", "stayed", "decided", "appealed", "closed"];
    const int SquareText = 1000, SealCt = 280000, SealKeys = 200, MailCt = 60000;
    const int AdsMaxDuration = 120, AdsMaxPerView = 1000, AdsMaxWitnesses = 20;

    public static bool Valid(Chain chain, JsonObject b, int commentDifficulty = 10)
    {
        var pn = Get(b, "payload");
        if (!Truthy(pn) || !IsObjectLike(pn)) return false;
        var p = pn as JsonObject ?? new JsonObject();   // an array has no named fields
        var type = Str(Get(p, "type"));
        var author = Get(b, "author");
        var parents = Parents(b);
        var owner = Same(author, Has(b, "author"), chain.OwnerAt(parents));
        if (type == "rekey") return owner && GenesisKind(chain) != "square" && IsKey(Get(p, "key")) && !SameNodes(Get(p, "key"), author) && IsString(Get(p, "proof"));
        var kind = GenesisKind(chain);
        var work = ZeroBits(Get(b, "hash"));
        if (kind == "mail") return !owner && work >= commentDifficulty && MsgShape(p, type);
        if (kind == "square")
        {
            if (owner || work < commentDifficulty) return false;
            var month = Str(Get(Get(chain.Genesis, "payload"), "month"))!;
            double start = Rules.MonthStart(month), next = NextMonthStart(start);
            var ts = Num(Get(b, "ts"));
            switch (type)
            {
                case "post": return IsStr(Get(p, "text"), SquareText) && (Trim(Str(Get(p, "text"))!).Length > 0 || MediaLength(p) > 0) && IsStr(OrEmpty(p, "name"), Limits.Name)
                    && (Undef(p, "image") || IsImg(Get(p, "image"))) && MediaListOk(p) && ts >= start - 600000 && ts < next + 600000;
                case "comment": return IsHex(Get(p, "target"), 64) && chain.Ancestors(parents).Contains(Str(Get(p, "target"))!) && IsStr(Get(p, "text"), SquareText) && Trim(Str(Get(p, "text"))!).Length > 0 && IsStr(OrEmpty(p, "name"), Limits.Name);
                case "vote": return IsHex(Get(p, "target"), 64) && chain.Ancestors(parents).Contains(Str(Get(p, "target"))!) && VoteValue(Get(p, "v"));
                case "hide": return IsHex(Get(p, "target"), 64) && AuthorOf(chain, Get(p, "target")) is { } a && Same(author, Has(b, "author"), a);
                default: return false;
            }
        }
        // a zone (a domain's subdomains): the owner writes records; anyone may claim a name for the owner to approve
        if (kind == "zone" && type is "record" or "claim")
        {
            bool shape(bool asOwner) => Rules.ZoneLabel(Str(Get(p, "label")), asOwner) && Rules.ZoneTarget(Str(OrEmpty(p, "target"))) && Rules.ZonePage(Str(OrEmpty(p, "page"))) && IsStr(OrEmpty(p, "note"), Limits.Comment);
            if (type == "record") return owner && shape(true) && (Undef(p, "on") || IsBool(Get(p, "on"))) && (Undef(p, "claim") || IsHex(Get(p, "claim"), 64));
            return !owner && work >= commentDifficulty && shape(false);
        }
        if (kind == "zone" && !owner) return false;
        // child chains: an org's owner adds sub-jurisdictions and cases; its clerks open cases
        if (type == "child")
        {
            var g = Get(p, "genesis");
            return Truthy(g) && Rules.ValidGenesis(g) && Str(Get(Get(g, "payload"), "parent")) == chain.Id
                && (owner || (kind == "org" && Str(Get(Get(g, "payload"), "kind")) == "case" && HasRole(chain, parents, author, "clerk")));
        }
        if (kind == "org")
        {
            if (owner && type == "class") return SlugRe().IsMatch(JsToString(Get(p, "id"), Has(p, "id"))) && IsStr(Get(p, "name"), 60) && IsStr(OrEmpty(p, "about"), Limits.About) && (Undef(p, "on") || IsBool(Get(p, "on")));
            if (owner && type == "role") return IsKey(Get(p, "key")) && Str(Get(p, "role")) is { } r && OrgRoleNames.Contains(r) && IsBool(Get(p, "on"));
            if (owner && type is "post" or "profile" or "hide" or "rekey") { /* the common owner rules below */ }
            else
            {
                if (type == "flag") return RefShape(Get(p, "ref")) && SlugRe().IsMatch(JsToString(Get(p, "cls"), Has(p, "cls"))) && IsBool(Get(p, "on")) && IsStr(OrEmpty(p, "note"), Limits.Comment) && (owner || HasRole(chain, parents, author, "moderator"));
                if (!owner && work < commentDifficulty) return false;
                if (type == "submit") return (Undef(p, "judge") || IsKey(Get(p, "judge"))) && Str(Get(p, "what")) is { } w && Filings.Contains(w) && IsStr(Get(p, "title"), Limits.Title) && Trim(Str(Get(p, "title"))!).Length > 0 && IsStr(Get(p, "text"), Limits.Text)
                    && (Undef(p, "case") || IsHex(Get(p, "case"), 64)) && (Undef(p, "listing") || IsHex(Get(p, "listing"), 64)) && (Undef(p, "sig") || IsString(Get(p, "sig"))) && (Undef(p, "buyerShare") || (IsFinite(Get(p, "buyerShare")) && Num(Get(p, "buyerShare")) is >= 0 and <= 1));
                // reviews of the organization or place (and the owner's answers): comments on its chain
                if (type == "comment") return IsHex(Get(p, "target"), 64) && chain.Ancestors(parents).Contains(Str(Get(p, "target"))!) && IsStr(Get(p, "text"), Limits.Comment) && Trim(Str(Get(p, "text"))!).Length > 0 && IsStr(OrEmpty(p, "name"), Limits.Name);
                if (owner) return false;
                if (type == "report") return RefShape(Get(p, "ref")) && SlugRe().IsMatch(JsToString(Get(p, "cls"), Has(p, "cls"))) && IsStr(OrEmpty(p, "note"), Limits.Comment);
                if (type == "petition") return SlugRe().IsMatch(JsToString(Get(p, "id"), Has(p, "id"))) && IsStr(Get(p, "name"), 60) && IsStr(OrEmpty(p, "about"), Limits.About);
                if (type == "vote") return IsHex(Get(p, "target"), 64) && chain.Ancestors(parents).Contains(Str(Get(p, "target"))!) && VoteValue(Get(p, "v"));
                return false;
            }
        }
        // a case's docket: only its clerk (the chain's key) writes; orders carry the signing judge's signature
        if (kind == "case")
        {
            if (!owner) return false;
            if (type == "filing") return Str(Get(p, "what")) is { } w && Filings.Contains(w) && IsStr(Get(p, "title"), Limits.Title) && IsStr(Get(p, "text"), Limits.Text) && IsKey(Get(p, "by")) && (Undef(p, "ref") || IsHex(Get(p, "ref"), 64));
            if (type == "status") return Str(Get(p, "state")) is { } s && CaseStates.Contains(s) && IsStr(OrEmpty(p, "note"), Limits.Comment);
            if (type == "order") return IsStr(Get(p, "title"), Limits.Title) && IsStr(Get(p, "text"), Limits.Text) && IsKey(Get(p, "judge")) && IsString(Get(p, "sig")) && (Undef(p, "buyerShare") || (IsFinite(Get(p, "buyerShare")) && Num(Get(p, "buyerShare")) is >= 0 and <= 1));
            if (type is not ("profile" or "rekey" or "hide")) return false;
        }
        if (kind == "circle" && owner && type == "profile") { /* your public profile: the common rules below */ }
        else if (kind == "circle")
        {
            if (type == "sealed") return SealedShape(p) && (owner || (!Undef(p, "target") && work >= commentDifficulty && Str(Get(p, "target")) is { } t && chain.Ancestors(parents).Contains(t)));
            if (type == "hide") return IsHex(Get(p, "target"), 64) && (owner || (AuthorOf(chain, Get(p, "target")) is { } a && Same(author, Has(b, "author"), a)));
            return false;
        }
        if (!owner && kind == "group")
        {
            if (work < commentDifficulty) return false;
            switch (type)
            {
                case "post": return IsStr(Get(p, "text"), Limits.Text) && (Trim(Str(Get(p, "text"))!).Length > 0 || MediaLength(p) > 0) && (Undef(p, "image") || IsImg(Get(p, "image"))) && MediaListOk(p) && IsStr(OrEmpty(p, "name"), Limits.Name) && GroupPermitted(chain, b);
                case "comment": return IsHex(Get(p, "target"), 64) && IsStr(Get(p, "text"), Limits.Comment) && Trim(Str(Get(p, "text"))!).Length > 0 && IsStr(OrEmpty(p, "name"), Limits.Name) && GroupPermitted(chain, b);
                case "hide": return IsHex(Get(p, "target"), 64) && GroupPermitted(chain, b);
                case "vote": return IsHex(Get(p, "target"), 64) && VoteValue(Get(p, "v")) && GroupPermitted(chain, b);
                default: return false;
            }
        }
        if (!owner)
        {
            if (kind != "channel" || work < commentDifficulty) return false;
            switch (type)
            {
                // a computer the owner linked to the channel (a `relay` block) starts and ends live streams by itself (OBS)
                case "live": return RelayAllowed(chain, b) && IsStr(Get(p, "title"), Limits.Title) && Trim(Str(Get(p, "title"))!).Length > 0 && IsHex(Get(p, "stream"), 64) && IsStr(Get(p, "mime"), 80) && Undef(p, "relay");
                case "liveend": return RelayAllowed(chain, b) && IsHex(Get(p, "stream"), 64) && Str(Get(p, "outcome")) is "published" or "discarded" or "ended" && LiveStatsOk(p) && (Undef(p, "next") || IsHex(Get(p, "next"), 64));
                case "comment": return IsHex(Get(p, "target"), 64) && IsStr(Get(p, "text"), Limits.Comment) && Trim(Str(Get(p, "text"))!).Length > 0 && IsStr(OrEmpty(p, "name"), Limits.Name) && PermittedAt(chain, b);
                case "hide": return IsHex(Get(p, "target"), 64) && PermittedAt(chain, b);
                case "vote": return IsHex(Get(p, "target"), 64) && VoteValue(Get(p, "v")) && PermittedAt(chain, b);
                default: return false;
            }
        }
        switch (type)
        {
            case "video": return kind == "channel" && IsStr(Get(p, "title"), Limits.Title) && IsStr(Get(p, "desc"), Limits.Text) && IsHex(Get(p, "infohash"), 40) && IsStr(Get(p, "file"), 200) && IsSafeInteger(Get(p, "size")) && IsImg(Get(p, "thumb"))
                && (Undef(p, "comments") || Str(Get(p, "comments")) is "anyone" or "approved" or "off") && (Undef(p, "stream") || IsHex(Get(p, "stream"), 64)) && ValidTags(p, "tags");
            case "live": return kind == "channel" && IsStr(Get(p, "title"), Limits.Title) && Trim(Str(Get(p, "title"))!).Length > 0 && IsHex(Get(p, "stream"), 64) && IsStr(Get(p, "mime"), 80) && (Undef(p, "relay") || IsKey(Get(p, "relay")));
            case "liveend": return kind == "channel" && IsHex(Get(p, "stream"), 64) && Str(Get(p, "outcome")) is "published" or "discarded" or "ended" && LiveStatsOk(p) && (Undef(p, "next") || IsHex(Get(p, "next"), 64));
            case "settings": return kind == "channel" && IsHex(Get(p, "target"), 64) && Str(Get(p, "comments")) is "default" or "anyone" or "approved" or "off";
            case "post": return IsStr(Get(p, "text"), Limits.Text) && (Trim(Str(Get(p, "text"))!).Length > 0 || MediaLength(p) > 0) && (Undef(p, "image") || IsImg(Get(p, "image"))) && MediaListOk(p);
            case "comment": return IsHex(Get(p, "target"), 64) && IsStr(Get(p, "text"), Limits.Comment) && Trim(Str(Get(p, "text"))!).Length > 0;
            case "hide": return IsHex(Get(p, "target"), 64);
            case "allow": case "revoke": return IsKey(Get(p, "key"));
            case "profile": return ProfileOk(kind, p);
            case "add": return kind == "list" && IsHex(Get(p, "target"), 64) && (Undef(p, "video") || IsHex(Get(p, "video"), 64)) && IsStr(OrEmpty(p, "note"), Limits.About);
            case "vote": return kind is "channel" or "group" && IsHex(Get(p, "target"), 64) && VoteValue(Get(p, "v"));
            case "ad": return kind == "channel" && IsStr(Get(p, "title"), Limits.Title) && IsStr(Get(p, "file"), 200) && IsImg(Get(p, "thumb"))
                && IsStr(Get(p, "url"), 300) && (Str(Get(p, "url")) == "" || HttpsRe().IsMatch(Str(Get(p, "url"))!)) && TermsShape(Get(p, "terms")) && SameNodes(Get(Get(p, "terms"), "advertiser"), author) && Str(Get(Get(p, "terms"), "channel")) == chain.Id
                && ValidTags(p, "tags") && (Undef(p, "category") || (Str(Get(p, "category")) is { } c && AdCategories.Contains(c)));
            case "sponsor": return IsHex(Get(p, "advertiser"), 64);
            case "relay": return kind == "channel" && IsKey(Get(p, "key")) && IsBool(Get(p, "on"));
            default: return false;
        }
    }

    static bool ProfileOk(string kind, JsonObject p) =>
        (Undef(p, "orgType") || (kind == "org" && Str(Get(p, "orgType")) is { } t && Rules.OrgTypes.Contains(t)))
        && (Undef(p, "details") || (kind == "org" && Rules.DetailsShape(p, "details")))
        && (Undef(p, "people") || (kind == "org" && Rules.PeopleShape(p, "people")))
        && (Undef(p, "jurisdiction") || (kind == "org" && IsStr(Get(p, "jurisdiction"), 120)))
        && (Undef(p, "place") || PlaceShape(Get(p, "place")))
        && (Undef(p, "links") || (Get(p, "links") is JsonArray links && links.Count <= 12 && links.All(l => Truthy(l) && IsStr(Get(l, "label"), 40) && IsStr(Get(l, "url"), 300) && HttpsRe().IsMatch(Str(Get(l, "url"))!))))
        && (Undef(p, "location") || IsStr(Get(p, "location"), 100))
        && (Undef(p, "join") || (kind == "group" && Str(Get(p, "join")) is "open" or "approved"))
        && (Undef(p, "name") || (IsStr(Get(p, "name"), Limits.Name) && Trim(Str(Get(p, "name"))!).Length > 0))
        && (Undef(p, "about") || IsStr(Get(p, "about"), Limits.About))
        && (Undef(p, "avatar") || IsImg(Get(p, "avatar")))
        && (Undef(p, "comments") || Str(Get(p, "comments")) is "anyone" or "approved" or "off");

    // ---- helpers (JavaScript names in comments)
    static string GenesisKind(Chain c) => Str(Get(Get(c.Genesis, "payload"), "kind")) ?? "";   // kind_(chain)
    static List<string> Parents(JsonObject b) => (Get(b, "parents") as JsonArray)?.Select(x => Str(x) ?? "\0").ToList() ?? [];
    static int ZeroBits(JsonNode? hash) => Rules.ZeroBits(Str(hash));
    static double NextMonthStart(double startMs) => DateTimeOffset.FromUnixTimeMilliseconds((long)startMs).AddMonths(1).ToUnixTimeMilliseconds();
    static bool VoteValue(JsonNode? v) => Num(v) is double d && (d == 1 || d == 0 || d == -1);   // [1, 0, -1].includes(v)
    static JsonNode? OrEmpty(JsonObject p, string k) => Get(p, k) ?? JsonValue.Create("");   // p[k] ?? ''
    static int MediaLength(JsonObject p) => Get(p, "media") switch { JsonArray a => a.Count, JsonValue v when IsString(v) => v.GetValue<string>().Length, _ => 0 };   // p.media?.length
    static string? AuthorOf(Chain c, JsonNode? target) => Str(target) is { } t && c.Blocks.TryGetValue(t, out var blk) ? Str(Get(blk, "author")) : null;
    /// <summary>a === b where a is a JSON value (or absent) and b a key string (or null for undefined).</summary>
    static bool Same(JsonNode? a, bool aPresent, string? b) => b == null ? !aPresent : Str(a) == b;
    static bool SameNodes(JsonNode? a, JsonNode? b) => Str(a) is { } x && Str(b) is { } y ? x == y : false;
    /// <summary>String(x), as RegExp.prototype.test converts it ("undefined" when absent).</summary>
    static string JsToString(JsonNode? n, bool present) => !present ? "undefined" : n switch
    {
        null => "null",
        JsonValue v when IsString(v) => v.GetValue<string>(),
        JsonValue v when IsNumber(v) => NumberToString(Js.NumOf(v)),
        JsonValue v when IsBool(v) => IsTrue(v) ? "true" : "false",
        JsonArray a => string.Join(",", a.Select(x => x == null ? "" : JsToString(x, true))),
        _ => "[object Object]",
    };

    static bool MsgShape(JsonObject p, string? type) => type == "msg" && IsHex(Get(p, "tag"), 64) && IsHex(Get(p, "salt"), 32) && Str(Get(p, "eph")) is { } e && EphRe().IsMatch(e)
        && IsStr(Get(p, "iv"), 40) && Str(Get(p, "ct")) is { } ct && ct.Length <= MailCt;
    static bool SealedShape(JsonObject p) => IsStr(Get(p, "iv"), 40) && Str(Get(p, "ct")) is { } ct && ct.Length <= SealCt && Get(p, "keys") is JsonArray keys && keys.Count >= 1 && keys.Count <= SealKeys
        && keys.All(k => Truthy(k) && IsStr(Get(k, "t"), 24) && IsStr(Get(k, "iv"), 40) && IsStr(Get(k, "w"), 100)) && (Undef(p, "target") || IsHex(Get(p, "target"), 64));
    static bool RefShape(JsonNode? x) => Truthy(x) && IsHex(Get(x, "chain"), 64) && IsHex(Get(x, "block"), 64);
    static bool MediaRefShape(JsonNode? m) => m is JsonObject && IsHex(Get(m, "ih"), 40) && IsStr(Get(m, "name"), 200) && IsStr(Get(m, "mime"), 80) && IsSafeInteger(Get(m, "size")) && Num(Get(m, "size")) > 0
        && (Undef((JsonObject)m, "thumb") || (IsImg(Get(m, "thumb")) && Str(Get(m, "thumb"))!.Length <= 16000));
    internal static bool MediaListOk(JsonObject p) => Undef(p, "media") || (Get(p, "media") is JsonArray a && a.Count <= 10 && a.All(MediaRefShape));
    internal static bool PlaceShape(JsonNode? x) => x is JsonObject or JsonArray && IsFinite(Get(x, "lat")) && Math.Abs(Num(Get(x, "lat"))!.Value) <= 90 && IsFinite(Get(x, "lon")) && Math.Abs(Num(Get(x, "lon"))!.Value) <= 180 && IsStr(Get(x, "label") ?? JsonValue.Create(""), 120);
    internal static bool ValidTags(JsonObject p, string k)
    {
        if (Undef(p, k)) return true;
        if (Get(p, k) is not JsonArray t || t.Count > 8) return false;
        var seen = new HashSet<string>();
        foreach (var x in t) { if (Str(x) is not { } s || !TagRe().IsMatch(s) || !seen.Add(s)) return false; }
        return true;
    }
    /// <summary>liveStatsOk(p.stats): public statistics on a stream's end (peak, unique viewers, seconds, top viewers).</summary>
    static bool LiveStatsOk(JsonObject p)
    {
        if (Undef(p, "stats")) return true;
        if (Get(p, "stats") is not JsonObject s) return false;
        foreach (var k in new[] { "peak", "unique", "secs" }) if (!(IsSafeInteger(Get(s, k)) && Num(Get(s, k)) is >= 0 and < 1e12)) return false;
        return Get(s, "top") is JsonArray top && top.Count <= 50 && top.All(t => t is JsonArray a && a.Count == 2 && IsKey(a[0]) && IsSafeInteger(a[1]) && Num(a[1]) >= 0);
    }
    /// <summary>termsShape(t): an ad's signed terms (price per view, max views, …).</summary>
    static bool TermsShape(JsonNode? t) => t is JsonObject o && NumIs(Get(o, "v"), 1) && Str(Get(o, "kind")) == "adterms" && IsKey(Get(o, "advertiser")) && IsHex(Get(o, "channel"), 64) && IsHex(Get(o, "infohash"), 40)
        && IsSafeInteger(Get(o, "size")) && Num(Get(o, "size")) > 0 && IsSafeInteger(Get(o, "duration")) && Num(Get(o, "duration")) is >= 1 and <= AdsMaxDuration
        && IsSafeInteger(Get(o, "perView")) && Num(Get(o, "perView")) is >= 2 and <= AdsMaxPerView && IsSafeInteger(Get(o, "maxViews")) && Num(Get(o, "maxViews")) is >= 1 and <= 1e6
        && IsSafeInteger(Get(o, "created")) && IsString(Get(o, "sig")) && (Undef(o, "witnesses") || (IsSafeInteger(Get(o, "witnesses")) && Num(Get(o, "witnesses")) is >= 0 and <= AdsMaxWitnesses));

    /// <summary>Owner blocks among a block's ancestors (not the genesis), oldest first: what the owner had decided by then.</summary>
    static List<JsonObject> OwnerHistory(Chain c, OrderedSet history, bool byHash) =>
        history.Items.Select(x => c.Blocks[x]).Where(x => Str(Get(x, "hash")) != c.Id && c.IsOwnerBlock(x))
            .Select((x, i) => (x, i)).OrderBy(t => Num(Get(t.x, "height")) ?? 0).ThenBy(t => Num(Get(t.x, "ts")) ?? 0)
            .ThenBy(t => byHash ? Str(Get(t.x, "hash")) : null, StringComparer.Ordinal).ThenBy(t => t.i).Select(t => t.x).ToList();

    /// <summary>relayAllowed(chain, b): did the owner link b's author to the channel, as of b's place in the chain?</summary>
    static bool RelayAllowed(Chain c, JsonObject b)
    {
        var anc = c.Ancestors(Parents(b)); JsonNode? on = null; bool onSet = false; double h = -1;
        foreach (var x in anc.Items)
        {
            var y = c.Blocks[x]; var yp = Get(y, "payload");
            if (Str(Get(yp, "type")) == "relay" && Str(Get(yp, "key")) is { } k && Str(Get(b, "author")) == k && c.IsOwnerBlock(y) && (Num(Get(y, "height")) ?? double.NaN) >= h)
            { on = Get(yp, "on"); onSet = true; h = Num(Get(y, "height"))!.Value; }
        }
        return onSet && Truthy(on);
    }

    /// <summary>permittedAt(chain, b): may this comment, vote or hide by a non-owner go on the channel?</summary>
    static bool PermittedAt(Chain c, JsonObject b)
    {
        var owner = c.Owner; var parents = Parents(b); var history = c.Ancestors(parents);
        JsonNode? def = Get(Get(c.Genesis, "payload"), "comments");
        var videoPolicy = new Dictionary<string, JsonNode?>(); var allowed = new HashSet<string>(); var revoked = new HashSet<string>();
        foreach (var x in OwnerHistory(c, history, byHash: true))
        {
            var q = Get(x, "payload"); var t = Str(Get(q, "type"));
            if (t == "profile" && Truthy(Get(q, "comments"))) def = Get(q, "comments");
            if (t == "video" && Truthy(Get(q, "comments"))) videoPolicy[KeyOf(Get(x, "hash"))] = Get(q, "comments");
            if (t == "settings") { var key = KeyOf(Get(q, "target")); if (Str(Get(q, "comments")) == "default") videoPolicy.Remove(key); else videoPolicy[key] = Get(q, "comments"); }
            if (t == "allow") { allowed.Add(KeyOf(Get(q, "key"))); revoked.Remove(KeyOf(Get(q, "key"))); }
            if (t == "revoke") { revoked.Add(KeyOf(Get(q, "key"))); allowed.Remove(KeyOf(Get(q, "key"))); }
        }
        var p = Get(b, "payload"); var authorKey = KeyOf(Get(b, "author"));
        if (revoked.Contains(authorKey) || Str(Get(p, "target")) is not { } targetHash || !history.Contains(targetHash)) return false;
        var target = c.Blocks[targetHash]; var tp = Get(target, "payload"); var ttype = Str(Get(tp, "type"));
        var type = Str(Get(p, "type"));
        if (type == "hide") return Str(Get(target, "author")) == Str(Get(b, "author")) && Str(Get(b, "author")) != null;
        if (type == "vote" && ttype == "comment") return true;
        var ownLive = ttype == "live" && Str(Get(target, "author")) != owner && RelayAllowed(c, target);
        if ((Str(Get(target, "author")) != owner && !ownLive) || ttype is not ("video" or "live")) return false;
        if (type == "vote") return true;
        var pol = videoPolicy.TryGetValue("s:" + targetHash, out var vp) && vp != null ? vp : def;
        return Str(pol) == "anyone" || (Str(pol) == "approved" && allowed.Contains(authorKey));
    }

    /// <summary>groupPermitted(chain, b): may this member post here (open group, or approved by the owner)?</summary>
    static bool GroupPermitted(Chain c, JsonObject b)
    {
        var parents = Parents(b); var history = c.Ancestors(parents);
        JsonNode? join = Get(Get(c.Genesis, "payload"), "join"); var allowed = new HashSet<string>(); var revoked = new HashSet<string>();
        foreach (var x in OwnerHistory(c, history, byHash: false))
        {
            var q = Get(x, "payload"); var t = Str(Get(q, "type"));
            if (t == "profile" && Truthy(Get(q, "join"))) join = Get(q, "join");
            if (t == "allow") { allowed.Add(KeyOf(Get(q, "key"))); revoked.Remove(KeyOf(Get(q, "key"))); }
            if (t == "revoke") { revoked.Add(KeyOf(Get(q, "key"))); allowed.Remove(KeyOf(Get(q, "key"))); }
        }
        var authorKey = KeyOf(Get(b, "author"));
        if (revoked.Contains(authorKey)) return false;
        var p = (JsonObject)Get(b, "payload")!;
        if (!Undef(p, "target"))
        {
            if (Str(Get(p, "target")) is not { } th || !history.Contains(th)) return false;
            var t = c.Blocks[th]; var ttype = Str(Get(Get(t, "payload"), "type")); var type = Str(Get(p, "type"));
            if (type == "hide") return Str(Get(t, "author")) == Str(Get(b, "author")) && Str(Get(b, "author")) != null;
            if (type == "vote") return ttype is "post" or "comment";
            if (ttype != "post") return false;
        }
        return Str(join) == "open" || allowed.Contains(authorKey);
    }

    /// <summary>orgRoles(chain, parents).get(key)?.has(role)</summary>
    static bool HasRole(Chain c, List<string> parents, JsonNode? key, string role)
    {
        var roles = new Dictionary<string, HashSet<string>>();
        var list = c.Ancestors(parents).Items.Select(x => c.Blocks[x]).Where(x => Str(Get(Get(x, "payload"), "type")) == "role" && c.IsOwnerBlock(x))
            .Select((x, i) => (x, i)).OrderBy(t => Num(Get(t.x, "height")) ?? 0).ThenBy(t => Num(Get(t.x, "ts")) ?? 0).ThenBy(t => t.i).Select(t => t.x);
        foreach (var x in list)
        {
            var q = Get(x, "payload"); var k = KeyOf(Get(q, "key"));
            var r = roles.TryGetValue(k, out var set) ? set : roles[k] = [];
            var rn = JsToString(Get(q, "role"), Has((JsonObject)q!, "role"));
            if (Truthy(Get(q, "on"))) r.Add(rn); else r.Remove(rn);
        }
        return roles.TryGetValue(KeyOf(key), out var mine) && mine.Contains(role);
    }
    /// <summary>A Map/Set key the way JavaScript compares them (strings by value; anything else by its JSON).</summary>
    static string KeyOf(JsonNode? n) => Str(n) is { } s ? "s:" + s : n == null ? "null" : "j:" + n.ToJsonString();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,30}$")] private static partial Regex SlugRe();
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,23}$")] private static partial Regex TagRe();
    [GeneratedRegex("^[A-Za-z0-9_-]{80,100}$")] private static partial Regex EphRe();
    // /^https:\/\/\S+$/ with JavaScript's \S (its whitespace includes U+FEFF and not U+0085)
    [GeneratedRegex(@"^https://[^\t\n\v\f\r    -     　﻿]+$")] private static partial Regex HttpsRe();
}
