using System.Text.Json.Nodes;
using static OpenTube.Core.Js;

namespace OpenTube.Core;

/// <summary>
/// The engine's chains (createEngine in the JavaScript core): the main chain (proof of work; the highest chain wins,
/// ties to the lowest tip hash) with the directory of registered chains and the ledger as of each main block, every
/// other chain, and inserting blocks into them (orphans wait for their parents; invalid blocks are rejected with a
/// reason). Ported in the same order of checks; OpenTube.Tests feeds it the same blocks as the JavaScript engine.
/// </summary>
public sealed class ChainStore
{
    public string MainId { get; }
    public Chain Main { get; }
    public JsonObject MainBest { get; private set; }
    public int Difficulty { get; }
    public int CommentDifficulty { get; }
    public int MaxEntries { get; }
    public readonly Dictionary<string, Chain> Chains = [];
    public readonly Dictionary<string, JsonObject> Mempool = [];   // genesis hash -> genesis awaiting registration
    public readonly Dictionary<string, JsonObject> TxPool = [];
    public Dictionary<string, (JsonObject Genesis, double Height, double Ts)> Directory { get; private set; } = [];
    public Ledger Ledger { get; private set; } = new();
    public readonly Dictionary<string, int> Rejected = [];
    public bool TrackAll { get; set; } = true;
    /// <summary>A block was added (chain id, block).</summary>
    public event Action<Chain, JsonObject>? BlockAdded;
    /// <summary>Blocks named as parents that this computer doesn't have yet (ask peers for them).</summary>
    public event Action<string, IReadOnlyCollection<string>>? Missing;

    readonly Dictionary<string, JsonObject> squares = [];       // monthly public feed and mail chains (derived, never registered)
    readonly Dictionary<string, JsonObject> childGenesis = [];  // child chain id -> genesis (registered on its parent chain)
    readonly Dictionary<string, Ledger> ledgerCache = [];
    readonly LinkedList<string> ledgerOrder = new();

    public ChainStore(JsonObject mainGenesis, double? nowMs = null)
    {
        MainId = Blocks.Hash(mainGenesis);
        var g = (JsonObject)mainGenesis.DeepClone(); g["hash"] = MainId;
        Main = new Chain(MainId, g); MainBest = g; Chains[MainId] = Main;
        var cfg = Get(g, "payload")!;
        Difficulty = (int)(Num(Get(cfg, "difficulty")) ?? 18); CommentDifficulty = (int)(Num(Get(cfg, "commentDifficulty")) ?? 10); MaxEntries = (int)(Num(Get(cfg, "maxEntries")) ?? 16);
        CacheLedger(MainId, new Ledger());
        foreach (var m in SquareMonths((nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) + 40 * 86400000.0))
            foreach (var kind in new[] { "square", "mail" }) { var sg = SquareGenesis(m, kind); squares[Str(sg["hash"])!] = sg; }
    }

    // ---- the monthly chains: everyone derives the same genesis for each month
    public static List<string> SquareMonths(double now)
    {
        var o = new List<string>(); int y = 2026, mo = 10; var end = DateTimeOffset.FromUnixTimeMilliseconds((long)now); int ey = end.Year, em = end.Month;
        while (y < ey || (y == ey && mo <= em)) { o.Add($"{y}-{mo + 1:00}"); mo++; if (mo > 11) { mo = 0; y++; } }
        return o;
    }
    public static JsonObject SquareGenesis(string m, string kind)
    {
        var g = new JsonObject { ["v"] = 1, ["chain"] = null, ["parents"] = new JsonArray(), ["height"] = 0, ["ts"] = Rules.MonthStart(m), ["author"] = Rules.SquareKey,
            ["payload"] = new JsonObject { ["type"] = "genesis", ["kind"] = kind, ["name"] = Rules.MonthChainName(kind, m), ["about"] = "", ["month"] = m }, ["nonce"] = 0 };
        g["hash"] = Blocks.Hash(g); g["sig"] = "square";
        return g;
    }

    public JsonObject? GenesisFor(string id) =>
        Chains.TryGetValue(id, out var c) ? c.Genesis : Directory.TryGetValue(id, out var d) ? d.Genesis : Mempool.TryGetValue(id, out var mg) ? mg
        : squares.TryGetValue(id, out var sg) ? sg : childGenesis.TryGetValue(id, out var cg) ? cg : null;
    public Chain? OpenChain(string id)
    {
        if (Chains.TryGetValue(id, out var c)) return c;
        var g = GenesisFor(id); if (g == null) return null;
        return Chains[id] = new Chain(id, g);
    }

    // ---- the ledger after each main block
    void CacheLedger(string hash, Ledger l)
    {
        if (!ledgerCache.ContainsKey(hash)) ledgerOrder.AddLast(hash);
        ledgerCache[hash] = l;
        while (ledgerCache.Count > 300)
        {
            var k = ledgerOrder.First!.Value; ledgerOrder.RemoveFirst(); ledgerCache.Remove(k);
            if (k == MainId) CacheLedger(MainId, new Ledger());
        }
    }
    /// <summary>stateAfter(hash): the ledger after that main block (replaying from the nearest one known).</summary>
    public Ledger? StateAfter(string hash)
    {
        if (ledgerCache.TryGetValue(hash, out var hit)) return hit;
        var path = new List<JsonObject>(); var h = hash;
        while (!ledgerCache.ContainsKey(h)) { if (!Main.Blocks.TryGetValue(h, out var b)) return null; path.Add(b); h = Str(((JsonArray)b["parents"]!)[0])!; }
        var st = ledgerCache[h];
        for (int i = path.Count - 1; i >= 0; i--)
        {
            var b = path[i]; st = new Ledger(st);
            if (Get(Get(b, "payload"), "txs") is JsonArray txs) foreach (var t in txs) st.Apply((JsonObject)t!);
            st.EndBlock(Get(b, "author"), Num(Get(b, "ts")) ?? 0); CacheLedger(Str(b["hash"])!, st);
        }
        return st;
    }

    /// <summary>nameAllowed(t): a name goes to a chain of the right kind that its sender created.</summary>
    bool NameAllowed(JsonObject t)
    {
        if (!Truthy(Get(t, "name"))) return true;
        var n = Get(t, "name")!; var g = Str(Get(n, "chain")) is { } cid ? GenesisFor(cid) : null;
        if (g == null || Str(Get(g, "author")) != Str(Get(t, "from"))) return false;
        var kind = Str(Get(Get(g, "payload"), "kind")); var space = Str(Get(n, "space"));
        return space == "@@" ? kind == "circle" : space == "@" ? kind == "channel" : kind == "list";
    }

    /// <summary>validMainBlock(b, trusted): work, registrations, and every transaction applying in order.</summary>
    bool ValidMainBlock(JsonObject b, bool trusted)
    {
        if (((JsonArray)b["parents"]!).Count != 1 || Rules.ZeroBits(Str(Get(b, "hash"))) < Difficulty) return false;
        var p = Get(b, "payload");
        if (!Truthy(p) || Str(Get(p, "type")) != "register" || Get(p, "entries") is not JsonArray entries || entries.Count > MaxEntries) return false;
        JsonNode? txsNode = p is JsonObject po && !po.ContainsKey("txs") ? new JsonArray() : Get(p, "txs");
        if (txsNode is not JsonArray txs || txs.Count > Coin.MaxTxs || entries.Count + txs.Count == 0) return false;
        foreach (var g in entries) if (!Rules.ValidGenesis(g) || !Undef((JsonObject)Get(g, "payload")!, "parent") || !(trusted || Rules.CheckSigned(g))) return false;
        var before = StateAfter(Str(((JsonArray)b["parents"]!)[0])!); if (before == null) return false;
        var st = new Ledger(before);
        foreach (var t in txs) if (t is not JsonObject to || !Tx.Check(to, trusted) || !NameAllowed(to) || st.Apply(to) != null) return false;
        st.EndBlock(Get(b, "author"), Num(Get(b, "ts")) ?? 0);
        CacheLedger(Str(b["hash"])!, st);
        return true;
    }

    /// <summary>rebuildDirectory: the registrations on the best main chain; reorged-out ones go back to the pools.</summary>
    void RebuildDirectory()
    {
        var path = new List<JsonObject>();
        for (var b = MainBest; b != null && Str(b["hash"]) != MainId; b = Main.Blocks.GetValueOrDefault(Str(((JsonArray)b["parents"]!)[0])!)) path.Add(b);
        path.Reverse();
        var d = new Dictionary<string, (JsonObject, double, double)>();
        foreach (var b in path) foreach (var g in (JsonArray)Get(Get(b, "payload"), "entries")!) { var gh = Str(g!["hash"])!; if (!d.ContainsKey(gh)) d[gh] = ((JsonObject)g, Num(b["height"])!.Value, Num(b["ts"])!.Value); }
        Directory = d;
        Ledger = StateAfter(Str(MainBest["hash"])!)!;
        var onPath = path.Select(b => Str(b["hash"])!).ToHashSet();
        foreach (var blk in Main.Blocks.Values)
        {
            if (Get(Get(blk, "payload"), "entries") is JsonArray es) foreach (var g in es) if (!d.ContainsKey(Str(g!["hash"])!)) Mempool[Str(g["hash"])!] = (JsonObject)g;
            if (!onPath.Contains(Str(blk["hash"])!) && Get(Get(blk, "payload"), "txs") is JsonArray txs)
                foreach (var t in txs) if (Num(Get(t, "seq")) > (Ledger.Seq.TryGetValue(Str(Get(t, "from"))!, out var s) ? s : 0)) TxPool[Str(t!["hash"])!] = (JsonObject)t;
        }
        foreach (var id in d.Keys) Mempool.Remove(id);
        foreach (var (h, t) in TxPool.ToList()) if (Num(Get(t, "seq")) <= (Ledger.Seq.TryGetValue(Str(Get(t, "from"))!, out var s) ? s : 0)) TxPool.Remove(h);
        if (TrackAll) foreach (var id in d.Keys) if (!Chains.ContainsKey(id)) OpenChain(id);
    }
    void OnMainBlock(JsonObject b)
    {
        var bh = Num(b["height"])!.Value; var besth = Num(MainBest["height"])!.Value;
        if (bh > besth || (bh == besth && string.CompareOrdinal(Str(b["hash"]), Str(MainBest["hash"])) < 0)) { MainBest = b; RebuildDirectory(); }
    }

    /// <summary>addToMempool(g): a registration waiting to be mined.</summary>
    public bool AddToMempool(JsonNode? node)
    {
        if (node is not JsonObject g || Str(Get(g, "hash")) is not { } h || Mempool.ContainsKey(h) || Directory.ContainsKey(h) || !Rules.ValidGenesis(g) || !Undef((JsonObject)Get(g, "payload")!, "parent") || !Rules.CheckSigned(g)) return false;
        Mempool[h] = g; return true;
    }
    public Ledger? LedgerAfter(string hash) => StateAfter(hash);
    public bool IsChildGenesis(string id) => childGenesis.ContainsKey(id);
    public bool IsSquare(string id) => squares.ContainsKey(id);
    public IEnumerable<JsonObject> Squares => squares.Values;
    public bool NameAllowedPublic(JsonObject t) => NameAllowed(t);

    void Reject(Chain c, JsonObject b, string why) { Rejected[why] = Rejected.GetValueOrDefault(why) + 1; BlockRejected?.Invoke(c, b, why); }
    /// <summary>A block was refused: (chain, block, why). For the diagnostics page.</summary>
    public event Action<Chain, JsonObject, string>? BlockRejected;

    /// <summary>
    /// insert(chainId, blocks, trusted): adds what is valid, keeps blocks whose parents are missing (orphans) until
    /// they arrive, rejects the rest. trusted: blocks this computer verified before (signatures aren't checked again).
    /// Returns the blocks added.
    /// </summary>
    public List<JsonObject> Insert(string chainId, IEnumerable<JsonNode?> blocks, bool trusted = false) => Insert(chainId, blocks, trusted, out _);
    public List<JsonObject> Insert(string chainId, IEnumerable<JsonNode?> blocks, bool trusted, out List<string> stillMissing)
    {
        stillMissing = [];
        var added = new List<JsonObject>();
        if (!Chains.TryGetValue(chainId, out var chain)) return added;
        var todo = blocks.OfType<JsonObject>().Select((b, i) => (b, i)).OrderBy(x => Num(Get(x.b, "height")) ?? 0).ThenBy(x => x.i).Select(x => x.b).ToList();
        var missing = new OrderedSet();
        for (int q = 0; q < todo.Count; q++)
        {
            var b = todo[q]; var hash = Str(Get(b, "hash"));
            if (hash != null && chain.Has(hash)) continue;
            if (Str(Get(b, "chain")) != chainId || Get(b, "parents") is not JsonArray ps || ps.Count == 0 || ps.Count > Limits.Parents || !ps.All(p => IsHex(p, 64))) { Reject(chain, b, "malformed"); continue; }
            var absent = ps.Select(p => Str(p)!).Where(p => !chain.Has(p)).ToList();
            if (absent.Count > 0)
            {
                if (chain.Orphans.Count < Limits.Orphans && IsHex(Get(b, "hash"), 64)) chain.Orphans[hash!] = b;
                foreach (var p in absent) missing.Add(p);
                continue;
            }
            if (Num(Get(b, "height")) != ps.Max(p => Num(chain.Blocks[Str(p)!]["height"])!.Value) + 1) { Reject(chain, b, "wrong height"); continue; }
            if (!trusted && !Rules.CheckSigned(b)) { Reject(chain, b, "bad hash or signature"); continue; }
            if (chain == Main ? !ValidMainBlock(b, trusted) : !Payload.Valid(chain, b, CommentDifficulty)) { Reject(chain, b, chain == Main ? "breaks main-chain rules" : "not permitted by channel rules"); continue; }
            var p0 = Get(b, "payload"); var type = Str(Get(p0, "type"));
            if (type == "rekey" && !trusted && !Rules.VerifySig(Get(p0, "key"), $"opentube-rekey:{chainId}:{Str(Get(p0, "key"))}", Get(p0, "proof"))) { Reject(chain, b, "new key did not agree"); continue; }
            if (type == "child" && !trusted && !Rules.CheckSigned(Get(p0, "genesis"))) { Reject(chain, b, "bad child chain"); continue; }
            if (type == "order" && !trusted && !Rules.VerifySig(Get(p0, "judge"), "opentube-order:" + chainId + ":" + Blocks.Canon(new JsonObject { ["title"] = Get(p0, "title")?.DeepClone(), ["text"] = Get(p0, "text")?.DeepClone(), ["buyerShare"] = Get(p0, "buyerShare")?.DeepClone() }), Get(p0, "sig"))) { Reject(chain, b, "order not signed by its judge"); continue; }
            if (type == "child") { var cg = (JsonObject)Get(p0, "genesis")!; childGenesis[Str(cg["hash"])!] = cg; if (TrackAll && !Chains.ContainsKey(Str(cg["hash"])!)) OpenChain(Str(cg["hash"])!); }
            if (type == "rekey") { chain.Rekeys.Add(b); var sorted = chain.Rekeys.Select((x, i) => (x, i)).OrderBy(x => Num(x.x["height"])).ThenBy(x => Num(x.x["ts"])).ThenBy(x => x.i).Select(x => x.x).ToList(); chain.Rekeys.Clear(); chain.Rekeys.AddRange(sorted); }
            chain.Add(b); chain.Orphans.Remove(hash!); added.Add(b);
            foreach (var (oh, ob) in chain.Orphans.ToList()) if (((JsonArray)ob["parents"]!).Any(p => Str(p) == hash)) { chain.Orphans.Remove(oh); todo.Add(ob); }
            if (chain == Main) OnMainBlock(b);
            BlockAdded?.Invoke(chain, b);
        }
        var still = missing.Items.Where(m => !chain.Has(m)).ToList();
        stillMissing = still;
        if (still.Count > 0) Missing?.Invoke(chainId, still);
        return added;
    }
}
