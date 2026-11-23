using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static OpenTube.Core.Js;

namespace OpenTube.Core;

/// <summary>
/// The network's block rules, ported from the JavaScript core (index.html, opentube-core) line for line, so a .NET node
/// decides exactly as browsers do. Each function names its JavaScript original; OpenTube.Tests compares the two on
/// generated valid and broken inputs.
/// </summary>
public static partial class Rules
{
    public static readonly string[] GenesisKinds = ["channel", "list", "group", "circle", "square", "mail", "org", "case"];
    public static readonly string[] OrgTypes = ["corporation", "llc", "partnership", "sole", "nonprofit", "cooperative", "government", "club", "arbitration", "other"];
    /// <summary>SQUARE.key: the monthly public-feed and mail chains have this fixed author.</summary>
    public static readonly string SquareKey = new('A', 43);

    /// <summary>checkSigned(b): well-formed numbers, under the size limit, not from the future, hash correct, signed by its author.</summary>
    public static bool CheckSigned(JsonNode? node, long? nowMs = null)
    {
        if (node is not JsonObject b || !IsHex(Get(b, "hash"), 64) || !IsString(Get(b, "sig")) || !IsSafeInteger(Get(b, "nonce")) || !IsSafeInteger(Get(b, "ts")) || !IsSafeInteger(Get(b, "height"))) return false;
        if (Blocks.Canon(b).Length > Limits.BlockBytes) return false;
        if (Num(Get(b, "ts")) > (nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) + 10 * 60000) return false;
        if (Blocks.Hash(b) != Str(Get(b, "hash"))) return false;
        return VerifySig(Get(b, "author"), Str(Get(b, "hash"))!, Get(b, "sig"));
    }

    /// <summary>verify(pub, msg, sig): false on anything malformed, as the JavaScript catch does.</summary>
    public static bool VerifySig(JsonNode? pub, string msg, JsonNode? sig)
    {
        if (Str(pub) is not { } p || Str(sig) is not { } s) return false;
        try { return Ed25519.Verify(B64u.Decode(p), B64u.Decode(s), Encoding.UTF8.GetBytes(msg)); }
        catch { return false; }
    }

    /// <summary>validGenesis(g): what the first block of a chain (channel, list, group, friends' circle, org, case, monthly chains) may be.</summary>
    public static bool ValidGenesis(JsonNode? node)
    {
        if (node is not JsonObject g || !NumIs(Get(g, "v"), 1) || !IsNull(g, "chain") || Get(g, "parents") is not JsonArray parents || parents.Count > 0 || !NumIs(Get(g, "height"), 0) || !IsKey(Get(g, "author"))) return false;
        var pn = Get(g, "payload");
        if (!Truthy(pn) || Str(Get(pn, "type")) != "genesis" || Str(Get(pn, "kind")) is not { } kind || !GenesisKinds.Contains(kind)) return false;
        var p = (JsonObject)pn!;   // a truthy value with type "genesis" is an object
        if (!Undef(p, "parent") && (!IsHex(Get(p, "parent"), 64) || kind is not ("case" or "org"))) return false;   // child chains
        if (kind == "case" && Undef(p, "parent")) return false;
        if (kind is "square" or "mail") return ValidSquareGenesis(g, p);
        if (!IsStr(Get(p, "name"), Limits.Name) || Trim(Str(Get(p, "name"))!).Length == 0 || !IsStr(Get(p, "about"), Limits.About)) return false;
        if (kind == "channel" && (!IsImg(Get(p, "avatar")) || Str(Get(p, "comments")) is not ("anyone" or "approved" or "off"))) return false;
        if (kind == "list" && !Undef(p, "ranking") && !IsTrue(Get(p, "ranking"))) return false;
        if (kind == "group" && (!IsImg(Get(p, "avatar")) || Str(Get(p, "join")) is not ("open" or "approved"))) return false;
        if (kind == "org" && (!IsImg(Get(p, "avatar")) || !(Get(p, "jurisdiction") is null || IsStr(Get(p, "jurisdiction"), 120))
            || !(Undef(p, "orgType") || (Str(Get(p, "orgType")) is { } t && OrgTypes.Contains(t))) || !DetailsShape(p, "details") || !PeopleShape(p, "people"))) return false;
        if (kind == "case" && (!(Undef(p, "listing") || IsHex(Get(p, "listing"), 64)) || Trim(Str(Get(p, "about"))!).Length == 0)) return false;
        if (kind == "circle" && !(Str(Get(p, "enc")) is { } enc && EncRe().IsMatch(enc))) return false;
        return true;
    }
    // (the JavaScript reads `p.jurisdiction ?? ''`: null and missing both pass)

    /// <summary>validSquareGenesis(g): the fixed monthly chains (public posts, messages) everyone derives the same way.</summary>
    static bool ValidSquareGenesis(JsonObject g, JsonObject p)
    {
        if (Str(Get(g, "author")) != SquareKey || Str(Get(g, "sig")) != "square") return false;
        var month = JsString(Get(p, "month"));
        if (!MonthRe().IsMatch(month)) return false;
        if (!(Num(Get(g, "ts")) is double ts && ts == MonthStart(month))) return false;
        return Str(Get(p, "name")) == MonthChainName(Str(Get(p, "kind"))!, month) && Str(Get(p, "about")) == "" && NumIs(Get(g, "nonce"), 0);
    }
    /// <summary>String(x), as RegExp.prototype.test converts its argument.</summary>
    static string JsString(JsonNode? n) => n switch
    {
        null => "null",
        JsonValue v when IsString(v) => v.GetValue<string>(),
        JsonValue v when IsNumber(v) => NumberToString(v.GetValue<double>()),
        JsonValue v when IsBool(v) => IsTrue(v) ? "true" : "false",
        JsonArray a => string.Join(",", a.Select(x => x == null ? "" : JsString(x))),
        _ => "[object Object]",
    };
    /// <summary>monthStart('2026-11') = Date.UTC(2026, 10, 1), months outside 1..12 rolling over as Date.UTC does.</summary>
    public static double MonthStart(string m)
    {
        var parts = m.Split('-'); var y = int.Parse(parts[0]); var mo = int.Parse(parts[1]);
        return new DateTimeOffset(y, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(mo - 1).ToUnixTimeMilliseconds();
    }
    public static string MonthChainName(string kind, string m) => (kind == "mail" ? "Messages " : "Public posts ") + m;

    /// <summary>detailsShape(d): up to 16 short fields (camelCase names; short text, true/false or a number).</summary>
    public static bool DetailsShape(JsonObject parent, string key)
    {
        if (Undef(parent, key)) return true;
        if (Get(parent, key) is not JsonObject d || d.Count > 16) return false;
        foreach (var (k, v) in d)
        {
            if (!DetailKeyRe().IsMatch(k)) return false;
            if (!(IsStr(v, 160) || IsBool(v) || (IsFinite(v) && Math.Abs(Num(v)!.Value) < 1e15))) return false;
        }
        return true;
    }
    /// <summary>peopleShape(x): up to 50 people, each a key, a role and an optional share 0–100.</summary>
    public static bool PeopleShape(JsonObject parent, string key)
    {
        if (Undef(parent, key)) return true;
        if (Get(parent, key) is not JsonArray a || a.Count > 50) return false;
        foreach (var q in a)
        {
            if (!Truthy(q) || !IsKey(Get(q, "key")) || !IsStr(Get(q, "role"), 60)) return false;
            if (q is JsonObject qo && !Undef(qo, "share") && !(IsFinite(Get(q, "share")) && Num(Get(q, "share")) is >= 0 and <= 100)) return false;
        }
        return true;
    }

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}$")] private static partial Regex MonthRe();
    [GeneratedRegex("^[a-z][a-zA-Z]{0,23}$")] private static partial Regex DetailKeyRe();
    [GeneratedRegex("^[A-Za-z0-9_-]{80,100}$")] private static partial Regex EncRe();
}

/// <summary>
/// class Chain: a chain's blocks as a DAG (each block names its parents), its current heads, blocks waiting for their
/// parents (orphans), and what a peer is missing. Iteration order matches the JavaScript (insertion order), so replies
/// list blocks in the same order.
/// </summary>
public sealed class Chain
{
    public string Id { get; }
    public JsonObject Genesis { get; }
    public readonly Dictionary<string, JsonObject> Blocks = [];
    readonly List<string> order = [];   // insertion order of Blocks (a JavaScript Map's iteration order)
    public readonly Dictionary<string, List<string>> Children = [];
    readonly OrderedSet heads = new();
    public readonly Dictionary<string, JsonObject> Orphans = [];
    public List<JsonObject> Rekeys { get; } = [];
    public int Version { get; private set; }
    public IReadOnlyList<string> Heads => heads.Items;

    public Chain(string id, JsonObject genesis) { Id = id; Genesis = genesis; Blocks[id] = genesis; order.Add(id); heads.Add(id); }

    public bool Has(string hash) => Blocks.ContainsKey(hash);
    static IEnumerable<string> Parents(JsonObject b) => (Js.Get(b, "parents") as JsonArray)?.Select(x => Js.Str(x)!) ?? [];

    /// <summary>ancestors(hashes): every block reachable through parents, in the JavaScript's depth-first order.</summary>
    public OrderedSet Ancestors(IEnumerable<string> hashes, OrderedSet? into = null)
    {
        into ??= new OrderedSet();
        var stack = new List<string>(hashes);
        while (stack.Count > 0)
        {
            var x = stack[^1]; stack.RemoveAt(stack.Count - 1);
            if (into.Contains(x) || !Blocks.TryGetValue(x, out var b)) continue;
            into.Add(x); stack.AddRange(Parents(b));
        }
        return into;
    }
    public void Add(JsonObject b)
    {
        var hash = Js.Str(Js.Get(b, "hash"))!;
        if (!Blocks.ContainsKey(hash)) order.Add(hash);
        Blocks[hash] = b;
        foreach (var p in Parents(b)) { heads.Remove(p); (Children.TryGetValue(p, out var c) ? c : Children[p] = []).Add(hash); }
        if (!Children.ContainsKey(hash)) heads.Add(hash);
        Version++;
    }
    /// <summary>ownerAt(parents): the genesis author until a rekey (in height order) among those blocks' ancestors hands the chain on.</summary>
    public string? OwnerAt(IEnumerable<string> parents)
    {
        var k = Js.Str(Js.Get(Genesis, "author"));
        if (Rekeys.Count == 0) return k;
        var anc = Ancestors(parents);
        foreach (var r in Rekeys) if (anc.Contains(Js.Str(Js.Get(r, "hash"))!)) k = Js.Str(Js.Get(Js.Get(r, "payload"), "key"));
        return k;
    }
    public string? Owner => OwnerAt(Heads);
    public bool IsOwnerBlock(JsonObject b) => Js.Str(Js.Get(b, "hash")) == Id || Js.Str(Js.Get(b, "author")) == OwnerAt(Parents(b));

    /// <summary>missingFor(want, have): ancestors of `want` the peer doesn't have (not ancestors of `have`), lowest first, up to 4 MB.</summary>
    public List<JsonObject> MissingFor(IEnumerable<string> want, IEnumerable<string> have)
    {
        var exclude = Ancestors(have.Where(Blocks.ContainsKey));
        var outList = Ancestors(want.Where(Blocks.ContainsKey)).Items.Where(x => !exclude.Contains(x) && x != Id).Select(x => Blocks[x]).ToList();
        // Array.prototype.sort is stable: equal height and ts keep their order
        var sorted = outList.Select((b, i) => (b, i)).OrderBy(t => Js.Num(Js.Get(t.b, "height")) ?? 0).ThenBy(t => Js.Num(Js.Get(t.b, "ts")) ?? 0).ThenBy(t => t.i).Select(t => t.b);
        var res = new List<JsonObject>(); long bytes = 0;
        foreach (var b in sorted) { bytes += OpenTube.Core.Blocks.Canon(b).Length; if (bytes > Limits.BatchBytes) break; res.Add(b); }
        return res;
    }
}

/// <summary>A set that remembers insertion order, like JavaScript's Set.</summary>
public sealed class OrderedSet
{
    readonly List<string> items = []; readonly Dictionary<string, int> index = [];
    public IReadOnlyList<string> Items => items;
    public bool Contains(string s) => index.ContainsKey(s);
    public void Add(string s) { if (index.ContainsKey(s)) return; index[s] = items.Count; items.Add(s); }
    public void Remove(string s)
    {
        if (!index.Remove(s, out var i)) return;
        items.RemoveAt(i); for (int j = i; j < items.Count; j++) index[items[j]] = j;
    }
    public int Count => items.Count;
}
