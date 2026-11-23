using System.Security.Cryptography;
using System.Text.Json.Nodes;
using static OpenTube.Core.Js;

namespace OpenTube.Core;

/// <summary>
/// The node engine (createEngine in the JavaScript core) in C#: the chains (ChainStore) plus everything peers say to
/// each other — heads and block requests, transactions and registrations waiting to be mined, people's channel
/// memberships, node statuses, rankings, routed messages and live segments over the mesh — and mining. Same wire
/// protocol as the JavaScript (docs/PROTOCOL.md), so .NET nodes, JavaScript nodes and browsers form one network.
/// Thread-safe: every entry point takes the engine's lock.
/// </summary>
public sealed class Engine
{
    public ChainStore Store { get; }
    public Identity Signer { get; }
    public string MainId => Store.MainId;
    public readonly Dictionary<string, JsonObject> Memberships = [], Nodes = [], Rankings = [];
    readonly Dictionary<string, JsonObject> waitingRankings = [];
    readonly Func<string, Identity?> keyFor;
    readonly Action<string, string?> sendRaw;   // (json, peer or null for everyone)
    readonly Func<IEnumerable<string>> peers;
    readonly object gate = new();
    public string MineMode { get; set; } = "own";   // own | all | off
    public Action<string, JsonObject>? Persist;      // a block was added: (chain id, block)
    public Action<JsonObject>? OnMembership;
    public Action<string, string>? OnRoute;           // (body json, reply via SendReply)
    public Action<string>? Log;
    (string Tip, CancellationTokenSource Cancel)? miner;
    public long MinedHashes;
    public bool Mining => miner != null;

    public Engine(JsonObject mainGenesis, Identity signer, Action<string, string?> send, Func<IEnumerable<string>> peers, Func<string, Identity?>? keyFor = null)
    {
        Store = new ChainStore(mainGenesis); Signer = signer; sendRaw = send; this.peers = peers; this.keyFor = keyFor ?? (_ => null);
        Store.BlockAdded += (c, b) => { if (persisting) Persist?.Invoke(c.Id, b); if (c == Store.Main) MaybeMineSoon(); };
    }
    bool persisting = true;

    void Send(JsonObject m, string? to = null) { try { sendRaw(m.ToJsonString(), to); } catch { } }
    IEnumerable<string> HeadsOf(Chain c) => c == Store.Main ? [Str(Store.MainBest["hash"])!] : c.Heads;
    JsonArray Arr(IEnumerable<string> s) => new(s.Select(x => (JsonNode?)x).ToArray());
    public void Announce(Chain c, string? to = null) => Send(new JsonObject { ["t"] = "heads", ["c"] = c.Id, ["h"] = Arr(HeadsOf(c)) }, to);
    /// <summary>A video's posting fee is paid (or it's from before the coin): hosts only keep paid videos.</summary>
    public bool PostPaid(JsonObject v) { lock (gate) return Num(v["ts"]) < Coin.Epoch || IsPaid("post:" + Str(v["hash"]), null); }
    /// <summary>Ask peers for someone's signed list of joined channels.</summary>
    public void RequestMembership(string author) => Send(new JsonObject { ["t"] = "memrq", ["a"] = author }, null);
    public void RequestChain(Chain c, string? to = null) => Send(new JsonObject { ["t"] = "rq", ["c"] = c.Id, ["w"] = new JsonArray(), ["h"] = Arr(HeadsOf(c)) }, to);
    public Chain? OpenChain(string id) { lock (gate) return Store.OpenChain(id); }

    /// <summary>insert(chainId, blocks, from, persist, trusted): add blocks, ask the sender for missing parents, announce what's new.</summary>
    public void Insert(string chainId, IEnumerable<JsonNode?> blocks, string? from = null, bool persist = true, bool trusted = false)
    {
        lock (gate)
        {
            persisting = persist;
            List<JsonObject> added; List<string> missing;
            try { added = Store.Insert(chainId, blocks, trusted, out missing); } finally { persisting = true; }
            if (!Store.Chains.TryGetValue(chainId, out var chain)) return;
            if (missing.Count > 0) Send(new JsonObject { ["t"] = "rq", ["c"] = chainId, ["w"] = Arr(missing.Take(64)), ["h"] = Arr(HeadsOf(chain)) }, from);
            if (added.Count > 0) { Announce(chain); if (chain == Store.Main) RetryWaitingRankings(); }
            if (TrackAll) foreach (var id in Store.Directory.Keys) if (!requested.Contains(id) && Store.Chains.TryGetValue(id, out var c) && c.Blocks.Count == 1) { requested.Add(id); RequestChain(c); }
        }
    }
    readonly HashSet<string> requested = [];
    /// <summary>A usage receipt arrived (see SERVE): the node keeps its own and passes others on.</summary>
    public event Action<JsonObject, string>? OnReceipt;
    /// <summary>Send a message to every peer except one (null: all).</summary>
    public void SendAllBut(JsonObject m, string? except) { foreach (var p in peers()) if (p != except) Send(m, p); }
    public bool TrackAll { get => Store.TrackAll; set => Store.TrackAll = value; }

    /// <summary>handle(m, from): one message from a peer.</summary>
    public void Handle(JsonObject m, string from)
    {
        lock (gate)
        {
            var t = Str(Get(m, "t")); if (t == null) return;
            Chain? chain = null;
            if (Str(Get(m, "c")) is { } cid) chain = Store.Chains.TryGetValue(cid, out var c) ? c : (TrackAll && Store.IsSquare(cid) ? Store.OpenChain(cid) : null);
            switch (t)
            {
                case "heads" when chain != null && Get(m, "h") is JsonArray h:
                {
                    var w = h.Where(x => IsHex(x, 64) && !chain.Has(Str(x)!) && !chain.Orphans.ContainsKey(Str(x)!)).Select(x => Str(x)!).Take(64).ToList();
                    if (w.Count > 0) Send(new JsonObject { ["t"] = "rq", ["c"] = chain.Id, ["w"] = Arr(w), ["h"] = Arr(HeadsOf(chain)) }, from);
                    break;
                }
                case "rq" when chain != null && Get(m, "w") is JsonArray w && Get(m, "h") is JsonArray hv:
                {
                    if (w.Count == 0) { Announce(chain, from); break; }
                    var blocks = chain.MissingFor(w.Take(64).Select(x => Str(x) ?? ""), hv.Take(64).Select(x => Str(x) ?? ""));
                    if (blocks.Count > 0) Send(new JsonObject { ["t"] = "bk", ["c"] = chain.Id, ["b"] = new JsonArray(blocks.Select(b => (JsonNode?)b.DeepClone()).ToArray()) }, from);
                    break;
                }
                case "bk" when chain != null && Get(m, "b") is JsonArray b: Insert(chain.Id, b.Take(5000).Select(x => x?.DeepClone()).ToList(), from); break;
                case "watch": Send(m); break;   // watch-group traffic: nodes relay it to everyone
                case "tx": if (Get(m, "x") is JsonObject x && AddTx(x)) MaybeMineSoon(); break;
                case "mem": AcceptMembership(Get(m, "m")); break;
                case "route": HandleRoute(m, from); break;
                case "lwant" when IsHex(Get(m, "stream"), 64): LiveWant(Str(Get(m, "stream"))!, from, (int)Math.Min(8, Num(Get(m, "ttl")) ?? 0)); break;
                case "lseg" when Get(m, "seg") is JsonObject seg && IsHex(Get(seg, "stream"), 64): LiveSeg(seg, from); break;
                case "node": AcceptNodeInfo(Get(m, "n")); break;
                case "memrq" when IsKey(Get(m, "a")): if (Memberships.TryGetValue(Str(Get(m, "a"))!, out var mm)) Send(new JsonObject { ["t"] = "mem", ["m"] = mm.DeepClone() }, from); break;
                case "rank" when Get(m, "r") is JsonObject r: AcceptRanking(r, true); break;
                case "rcpt" when Get(m, "r") is JsonObject rc: OnReceipt?.Invoke((JsonObject)rc.DeepClone(), from); break;   // a usage receipt on its way to the node it pays
                case "mp" when Get(m, "e") is JsonArray e:
                {
                    bool any = false; foreach (var g in e.Take(64)) if (Store.AddToMempool(g?.DeepClone())) any = true;
                    if (any) MaybeMineSoon();
                    break;
                }
            }
        }
    }

    /// <summary>peerJoined(peer): tell a new peer what this node knows.</summary>
    public void PeerJoined(string peer)
    {
        lock (gate)
        {
            foreach (var r in Rankings.Values) Send(new JsonObject { ["t"] = "rank", ["r"] = r.DeepClone() }, peer);
            if (Memberships.TryGetValue(Signer.pub, out var mine)) Send(new JsonObject { ["t"] = "mem", ["m"] = mine.DeepClone() }, peer);
            var cutoff = Now() - 30 * 60000;
            foreach (var n in Nodes.Values) if (Num(n["at"]) > cutoff) Send(new JsonObject { ["t"] = "node", ["n"] = n.DeepClone() }, peer);
            foreach (var t in Store.TxPool.Values) Send(new JsonObject { ["t"] = "tx", ["x"] = t.DeepClone() }, peer);
            foreach (var c in Store.Chains.Values) Announce(c, peer);
            if (Store.Mempool.Count > 0) Send(new JsonObject { ["t"] = "mp", ["e"] = new JsonArray(Store.Mempool.Values.Select(g => (JsonNode?)g.DeepClone()).ToArray()) }, peer);
        }
    }
    /// <summary>gossip(): the repair pass (every minute).</summary>
    public void Gossip()
    {
        lock (gate)
        {
            foreach (var c in Store.Chains.Values) Announce(c);
            if (Store.Mempool.Count > 0) Send(new JsonObject { ["t"] = "mp", ["e"] = new JsonArray(Store.Mempool.Values.Select(g => (JsonNode?)g.DeepClone()).ToArray()) });
            foreach (var t in Store.TxPool.Values) Send(new JsonObject { ["t"] = "tx", ["x"] = t.DeepClone() });
        }
    }
    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---- transactions
    public bool AddTx(JsonObject t)
    {
        lock (gate)
        {
            if (Str(Get(t, "hash")) is not { } h || Store.TxPool.ContainsKey(h) || !Tx.Check(t) || !Store.NameAllowedPublic(t) || Num(Get(t, "seq")) <= Store.Ledger.Seq.GetValueOrDefault(Str(Get(t, "from"))!)) return false;
            Store.TxPool[h] = t; return true;
        }
    }
    /// <summary>The ledger with this key's pending transactions applied (for the next sequence number and checks).</summary>
    public Ledger PendingState(string from)
    {
        lock (gate)
        {
            var st = new Ledger(Store.Ledger);
            foreach (var t in Store.TxPool.Values.Where(x => Str(x["from"]) == from).OrderBy(x => Num(x["seq"]))) st.Apply(t);
            return st;
        }
    }
    readonly SemaphoreSlim txLock = new(1, 1);
    /// <summary>
    /// submitTx: a transaction from this node's key (or another signer it holds) with exactly one action (pay, starter,
    /// serve, tabs, …) and an optional memo; checked against the ledger, proof of work done (10 bits, 20 for starter
    /// coins), signed, added to the pool and sent. One at a time per key (the sequence number depends on the last).
    /// </summary>
    public async Task<JsonObject> SubmitTx(JsonObject fields, Identity? signer = null)
    {
        signer ??= Signer;
        await txLock.WaitAsync();
        try
        {
            var from = signer.pub; var st = PendingState(from);
            var body = new JsonObject { ["v"] = 2, ["kind"] = "tx", ["from"] = from, ["seq"] = st.Seq.GetValueOrDefault(from) + 1, ["ts"] = Now(), ["nonce"] = 0 };
            foreach (var (k, v) in fields) if (k != "memo" && v != null && !(v is JsonValue jv && jv.GetValueKind() == System.Text.Json.JsonValueKind.False)) body[k] = v.DeepClone();
            if (Str(fields["memo"]) is { Length: > 0 } memo) body["memo"] = memo;
            if (!Tx.Shape(body)) throw new InvalidOperationException("invalid transaction");
            if (st.Check(body) is { } why) throw new InvalidOperationException(why);
            var bits = Truthy(Get(body, "starter")) ? (int)Coin.StarterBits : (int)Coin.TxBits;
            body["nonce"] = await Task.Run(() => Miner.Find(Blocks.Preimage(body), bits, CancellationToken.None) ?? 0);
            body["hash"] = Blocks.Hash(body); body["sig"] = signer.Sign(Str(body["hash"])!);
            if (!AddTx(body)) throw new InvalidOperationException("transaction rejected");
            Send(new JsonObject { ["t"] = "tx", ["x"] = body.DeepClone() }, null);
            MaybeMineSoon();
            return body;
        }
        finally { txLock.Release(); }
    }
    /// <summary>selectTxs(state): pending transactions that apply, in order, on top of a state.</summary>
    List<JsonObject> SelectTxs(Ledger state)
    {
        var st = new Ledger(state); var picked = new List<JsonObject>();
        var pending = Store.TxPool.Values.OrderBy(t => Str(t["from"]), StringComparer.Ordinal).ThenBy(t => Num(t["seq"])).ToList();
        bool progress = true;
        while (progress && picked.Count < Coin.MaxTxs)
        {
            progress = false;
            foreach (var t in pending) if (!picked.Contains(t) && st.Check(t) == null) { st.Apply(t); picked.Add(t); progress = true; if (picked.Count >= Coin.MaxTxs) break; }
        }
        return picked;
    }
    bool ShouldMine(string? author) => MineMode == "all" || author == Signer.pub || (author != null && keyFor(author) != null);

    void MaybeMineSoon() => ThreadPool.QueueUserWorkItem(_ => MaybeMine());
    /// <summary>maybeMine(): start proof of work on a main block with the waiting registrations and transactions this node should mine.</summary>
    public void MaybeMine()
    {
        JsonObject body; string tipHash; CancellationTokenSource cts;
        lock (gate)
        {
            if (MineMode == "off") return;
            var tip = Store.MainBest; tipHash = Str(tip["hash"])!;
            if (miner is { } m) { if (m.Tip == tipHash) return; m.Cancel.Cancel(); miner = null; }
            var pending = Store.Mempool.Values.Where(g => !Store.Directory.ContainsKey(Str(g["hash"])!)).ToList();
            var txs = SelectTxs(Store.LedgerAfter(tipHash)!);
            if (!pending.Any(g => ShouldMine(Str(g["author"]))) && !txs.Any(t => ShouldMine(Str(t["from"])))) return;
            var entries = pending.Select((g, i) => (g, i)).OrderByDescending(x => ShouldMine(Str(x.g["author"])) ? 1 : 0).ThenBy(x => x.i).Select(x => x.g).Take(Store.MaxEntries).ToList();
            var payload = new JsonObject { ["type"] = "register", ["entries"] = new JsonArray(entries.Select(g => (JsonNode?)g.DeepClone()).ToArray()) };
            if (txs.Count > 0) payload["txs"] = new JsonArray(txs.Select(t => (JsonNode?)t.DeepClone()).ToArray());
            body = new JsonObject { ["v"] = 1, ["chain"] = MainId, ["parents"] = new JsonArray(tipHash), ["height"] = Num(tip["height"])!.Value + 1, ["ts"] = Now(), ["author"] = Signer.pub, ["payload"] = payload, ["nonce"] = 0 };
            cts = new CancellationTokenSource(); miner = (tipHash, cts);
        }
        var nonce = Miner.Find(Blocks.Preimage(body), Store.Difficulty, cts.Token, n => MinedHashes = n);
        lock (gate)
        {
            if (miner?.Cancel != cts) return;
            miner = null;
            if (nonce is not long n) return;
            body["nonce"] = n; body["hash"] = Blocks.Hash(body); body["sig"] = Signer.Sign(Str(body["hash"])!);
            Insert(MainId, [body]);
            if (!Store.Main.Has(Str(body["hash"])!)) { Log?.Invoke("mined block was rejected; not retrying"); return; }
        }
        MaybeMine();
    }

    // ---- making blocks: genesis (a new chain, registered by mining), and appending to a chain this node holds a key for
    public JsonObject MakeBlock(string? chain, IEnumerable<string> parents, double height, JsonObject payload, int pow, Identity signer)
    {
        var body = new JsonObject { ["v"] = 1, ["chain"] = chain, ["parents"] = Arr(parents), ["height"] = height, ["ts"] = Now(), ["author"] = signer.pub, ["payload"] = payload, ["nonce"] = 0 };
        if (pow > 0) body["nonce"] = Miner.Find(Blocks.Preimage(body), pow, CancellationToken.None) ?? 0;
        body["hash"] = Blocks.Hash(body); body["sig"] = signer.Sign(Str(body["hash"])!);
        return body;
    }
    public JsonObject CreateGenesis(JsonObject payload, Identity? signer = null)
    {
        var g = MakeBlock(null, [], 0, payload, 0, signer ?? Signer);
        if (!Rules.ValidGenesis(g)) throw new InvalidOperationException("invalid channel data");
        lock (gate) { Store.Mempool[Str(g["hash"])!] = g; Store.OpenChain(Str(g["hash"])!); }
        Send(new JsonObject { ["t"] = "mp", ["e"] = new JsonArray(g.DeepClone()) });
        MaybeMineSoon();
        return g;
    }
    public JsonObject Append(string chainId, JsonObject payload, Identity? asKey = null)
    {
        Chain c; List<string> parents; double height; Identity signer; int pow;
        lock (gate)
        {
            c = Store.OpenChain(chainId) ?? throw new InvalidOperationException("This chain isn't known here.");
            if (c.Heads.Count == 0 || c.Owner == null) throw new InvalidOperationException("the channel isn't loaded here yet");
            parents = c.Heads.Take(Limits.Parents).ToList(); height = parents.Max(p => Num(c.Blocks[p]["height"])!.Value) + 1;
            signer = asKey ?? (c.Owner is { } o ? keyFor(o) : null) ?? Signer; pow = c.Owner == signer.pub ? 0 : Store.CommentDifficulty;
        }
        var b = MakeBlock(chainId, parents, height, payload, pow, signer);
        Insert(chainId, [b]);
        lock (gate)   // a block the chain's rules refuse goes nowhere: say so instead of reporting success
            if (!Store.Chains.TryGetValue(chainId, out var after) || !after.Blocks.ContainsKey(Str(b["hash"])!))
                throw new InvalidOperationException(signer.pub == c.Owner ? "the channel refused this block" : "the channel refused this block (signed by this computer: is it linked to the channel?)");
        return b;
    }

    // ---- memberships (which channels a person joined), node statuses, rankings: latest signed one per author
    public bool AcceptMembership(JsonNode? node)
    {
        lock (gate)
        {
            if (node is not JsonObject m || !ValidMembership(m)) return false;
            var author = Str(m["author"])!;
            if (Memberships.TryGetValue(author, out var cur) && Num(cur["at"]) >= Num(m["at"])) return false;
            if (!Rules.VerifySig(m["author"], "opentube-membership:" + Blocks.Canon(Without(m, "sig")), m["sig"])) return false;
            Memberships[author] = (JsonObject)m.DeepClone(); OnMembership?.Invoke(m);
            return true;
        }
    }
    static bool ValidMembership(JsonObject m) => NumIs(Get(m, "v"), 1) && IsKey(Get(m, "author")) && IsSafeInteger(Get(m, "at")) && Num(Get(m, "at")) <= Now() + 10 * 60000
        && IsString(Get(m, "sig")) && Get(m, "joined") is JsonArray j && j.Count <= 1000 && j.All(x => IsHex(x, 64)) && j.Select(x => Str(x)).Distinct().Count() == j.Count
        && (Undef(m, "own") || (Get(m, "own") is JsonArray o && o.Count <= 1000 && o.All(x => IsHex(x, 64))));
    public JsonObject PublishMembership(IEnumerable<string> joined, IEnumerable<string>? own = null)
    {
        var ownL = (own ?? []).Distinct().ToList();
        var m = new JsonObject { ["v"] = 1, ["author"] = Signer.pub, ["at"] = Now(), ["joined"] = Arr(joined.Concat(ownL).Distinct()) };
        if (ownL.Count > 0) m["own"] = Arr(ownL);
        m["sig"] = Signer.Sign("opentube-membership:" + Blocks.Canon(m));
        AcceptMembership(m); Send(new JsonObject { ["t"] = "mem", ["m"] = m.DeepClone() });
        return m;
    }
    public bool AcceptNodeInfo(JsonNode? node)
    {
        lock (gate)
        {
            if (node is not JsonObject n || !ValidNodeInfo(n) || Num(n["at"]) > Now() + 5 * 60000 || Num(n["at"]) < Now() - 30 * 60000) return false;
            var key = Str(n["node"])!;
            if (Nodes.TryGetValue(key, out var cur) && Num(cur["at"]) >= Num(n["at"])) return false;
            var g = n["grant"]!;
            if (!Rules.VerifySig(Get(g, "user"), "opentube-nodegrant:" + Blocks.Canon(Without(g, "sig")), Get(g, "sig")) || !Rules.VerifySig(n["node"], "opentube-nodeinfo:" + Blocks.Canon(Without(n, "sig")), n["sig"])) return false;
            Nodes[key] = (JsonObject)n.DeepClone();
            foreach (var (k, x) in Nodes.ToList()) if (Num(x["at"]) < Now() - 30 * 60000) Nodes.Remove(k);
            Send(new JsonObject { ["t"] = "node", ["n"] = n.DeepClone() });
            return true;
        }
    }
    static bool ValidNodeInfo(JsonObject n) => NumIs(Get(n, "v"), 1) && Str(Get(n, "kind")) == "nodeinfo" && IsKey(Get(n, "node")) && Tx.ValidNodeGrant(Get(n, "grant")) && IsSafeInteger(Get(n, "at"))
        && Get(n, "roles") is JsonArray r && r.Count <= 8 && r.All(x => IsStr(x, 20)) && Get(n, "addrs") is JsonArray a && a.Count <= 8 && a.All(x => IsStr(x, 200))
        && (Undef(n, "info") || (Get(n, "info") is JsonObject && Blocks.Canon(Get(n, "info")).Length < 4000)) && IsString(Get(n, "sig"));
    public JsonObject PublishNodeInfo(JsonObject fields)
    {
        var n = new JsonObject { ["v"] = 1, ["kind"] = "nodeinfo", ["node"] = Signer.pub, ["at"] = Now(), ["roles"] = new JsonArray(), ["addrs"] = new JsonArray() };
        foreach (var (k, v) in fields) n[k] = v?.DeepClone();
        n["sig"] = Signer.Sign("opentube-nodeinfo:" + Blocks.Canon(n));
        AcceptNodeInfo(n);
        return n;
    }
    public bool AcceptRanking(JsonObject r, bool relay)
    {
        lock (gate)
        {
            var list = Str(Get(r, "list"));
            if (list != null && Rankings.TryGetValue(list, out var cur) && Num(cur["at"]) >= Num(Get(r, "at"))) return false;
            if (IsHex(Get(r, "list"), 64) && Store.GenesisFor(list!) == null)
            {
                if ((!waitingRankings.TryGetValue(list!, out var w) || Num(w["at"]) < Num(Get(r, "at"))) && waitingRankings.Count < 100) waitingRankings[list!] = r;
                return false;
            }
            if (!ValidRanking(r, list == null ? null : Store.GenesisFor(list)) || !Rules.VerifySig(Get(r, "author"), "opentube-ranking:" + Blocks.Canon(Without(r, "sig")), Get(r, "sig"))) return false;
            Rankings[list!] = r;
            if (relay) Send(new JsonObject { ["t"] = "rank", ["r"] = r.DeepClone() });
            return true;
        }
    }
    static bool ValidRanking(JsonObject r, JsonObject? g)
    {
        if (!NumIs(Get(r, "v"), 1) || !IsHex(Get(r, "list"), 64) || !IsSafeInteger(Get(r, "at")) || Num(Get(r, "at")) > Now() + 10 * 60000) return false;
        if (g == null || Str(Get(Get(g, "payload"), "kind")) != "list" || !IsTrue(Get(Get(g, "payload"), "ranking")) || Str(Get(r, "author")) != Str(Get(g, "author"))) return false;
        if (!IsStr(Get(r, "algo"), 40) || Get(r, "items") is not JsonArray items || items.Count > 200 || Blocks.Canon(r).Length > 200000) return false;
        return items.All(i => Truthy(i) && IsHex(Get(i, "c"), 64) && IsHex(Get(i, "v"), 64) && IsStr(Get(i, "t"), 140) && IsStr(Get(i, "n"), Limits.Name) && IsFinite(Get(i, "s"))
            && IsSafeInteger(Get(i, "up")) && IsSafeInteger(Get(i, "down")) && IsSafeInteger(Get(i, "ts")) && (i is JsonObject io && !io.ContainsKey("w") || (Get(i, "w") is JsonArray ww && ww.Count == 4 && ww.All(IsFinite))));
    }
    public JsonObject PublishRanking(string listId, JsonArray items, string algo = "waves-v1")
    {
        var g = Store.GenesisFor(listId); var signer = g == null ? null : keyFor(Str(g["author"])!);
        if (signer == null) throw new InvalidOperationException("this node does not hold that list key");
        var r = new JsonObject { ["v"] = 1, ["list"] = listId, ["at"] = Now(), ["algo"] = algo, ["author"] = signer.pub, ["items"] = items };
        r["sig"] = signer.Sign("opentube-ranking:" + Blocks.Canon(r));
        AcceptRanking(r, true);
        return r;
    }
    /// <summary>Rankings that arrived before their list was registered: retried whenever the directory changes.</summary>
    public void RetryWaitingRankings() { lock (gate) foreach (var (list, r) in waitingRankings.ToList()) if (Store.GenesisFor(list) != null) { waitingRankings.Remove(list); AcceptRanking(r, false); } }

    // ---- ranking: waves of recent votes, plus freshness (rankVideos, waveScores, paidTally)
    static readonly double[] Tau = [20 * 60000, 8 * 3600000, 3 * 86400000], Fresh = [3600000, 12 * 3600000, 4 * 86400000], Mix = [0.5, 0.35, 0.15];
    const double Boost = 1.5;
    bool IsPaid(string reference, string? from)
    {
        if (Store.Ledger.Paid.TryGetValue(reference, out var p)) return from == null || Str(p["from"]) == from;
        foreach (var t in Store.TxPool.Values)
            if (NumIs(Get(t, "v"), 2) && ((Truthy(Get(t, "post")) && "post:" + Str(Get(Get(t, "post"), "video")) == reference) || (Truthy(Get(t, "vote")) && "vote:" + Str(Get(Get(t, "vote"), "target")) == reference)
                || (Truthy(Get(t, "view")) && $"view:{Str(Get(Get(t, "view"), "video"))}:{Str(t["from"])}" == reference)) && (from == null || Str(t["from"]) == from)) return true;
        return false;
    }
    public JsonArray Rank()
    {
        lock (gate)
        {
            var now = Now(); var all = new List<JsonObject>();
            foreach (var c in Store.Chains.Values)
            {
                if (c == Store.Main || Str(Get(Get(c.Genesis, "payload"), "kind")) != "channel") continue;
                var d = Derived.Of(c, now);
                foreach (var v in d.Videos)
                {
                    if (!(Num(v["ts"]) < Coin.Epoch || IsPaid("post:" + Str(v["hash"]), null))) continue;
                    d.Votes.TryGetValue(Str(v["hash"])!, out var t0);
                    int up = 0, down = 0; var recent = new List<(double, double)>();
                    if (t0 != null) foreach (var b in t0.Blocks) if (Num(b["ts"]) < Coin.Epoch || IsPaid("vote:" + Str(b["hash"]), Str(b["author"])))
                    { var vv = Num(Get(Get(b, "payload"), "v"))!.Value; if (vv > 0) up++; else down++; recent.Add((Num(b["ts"])!.Value, vv)); }
                    var w = new double[3];
                    foreach (var (ts, vv) in recent) { var age = Math.Max(0, now - Math.Min(ts, now)); for (int k = 0; k < 3; k++) w[k] += vv * Math.Exp(-age / Tau[k]); }
                    var vAge = Math.Max(0, now - Num(v["ts"])!.Value); for (int k = 0; k < 3; k++) w[k] += Boost * Math.Exp(-vAge / Fresh[k]);
                    static double R(double x) => Ledger.JsRound(x * 1e4) / 1e4;
                    all.Add(new JsonObject { ["c"] = c.Id, ["v"] = v["hash"]?.DeepClone(), ["t"] = Get(Get(v, "payload"), "title")?.DeepClone(), ["n"] = d.Profile["name"]?.DeepClone(), ["up"] = up, ["down"] = down, ["ts"] = v["ts"]?.DeepClone(),
                        ["s"] = R(w[0] * Mix[0] + w[1] * Mix[1] + w[2] * Mix[2]), ["w"] = new JsonArray(R(w[0]), R(w[1]), R(w[2]), up - down) });
                }
            }
            IEnumerable<JsonObject> Pick(Func<JsonObject, double> key, int n) => all.Select((x, i) => (x, i)).OrderByDescending(x => key(x.x)).ThenBy(x => x.i).Take(n).Select(x => x.x);
            double W(JsonObject x, int k) => Num(((JsonArray)x["w"]!)[k])!.Value;
            var seen = new HashSet<string>(); var items = new List<JsonObject>();
            foreach (var i in Pick(x => Num(x["s"])!.Value, 80).Concat(Pick(x => W(x, 0), 40)).Concat(Pick(x => W(x, 1), 40)).Concat(Pick(x => W(x, 2), 40)).Concat(Pick(x => W(x, 3) * 1e13 + Num(x["ts"])!.Value, 40)).Concat(Pick(x => Num(x["ts"])!.Value, 40)))
                if (seen.Add(Str(i["v"])!)) items.Add(i);
            return new JsonArray(items.Select((x, i) => (x, i)).OrderByDescending(x => Num(x.x["s"])).ThenBy(x => x.i).Take(200).Select(x => (JsonNode?)x.x.DeepClone()).ToArray());
        }
    }

    // ---- routed messages: for one key, passed hop by hop; the reply goes back the way it came
    readonly Dictionary<string, string?> routes = [];   // route id -> link it came from (null: this node)
    readonly LinkedList<string> routeOrder = new();
    readonly Dictionary<string, TaskCompletionSource<JsonNode?>> waiting = [];
    void HandleRoute(JsonObject m, string from)
    {
        if (Str(Get(m, "id")) is not { } id || id.Length > 40 || routes.ContainsKey(id)) return;
        if (m.ContainsKey("re"))
        {
            var re = Str(Get(m, "re")) ?? ""; var known = routes.TryGetValue(re, out var back); Remember(id, null);
            if (!known) return;
            if (back == null) { if (waiting.Remove(re, out var w)) w.TrySetResult(Get(m, "body")?.DeepClone()); }
            else Send(m, back);
            return;
        }
        if (!IsKey(Get(m, "dst")) || !(Num(Get(m, "ttl")) > 0)) return;
        Remember(id, from);
        var dst = Str(Get(m, "dst"))!;
        if (dst == Signer.pub || keyFor(dst) != null) OnRoute?.Invoke(Get(m, "body")?.ToJsonString() ?? "null", id + "\n" + from);
        else { var fwd = (JsonObject)m.DeepClone(); fwd["ttl"] = Math.Min(8, Num(Get(m, "ttl"))!.Value) - 1; Send(fwd); }
    }
    void Remember(string id, string? via) { routes[id] = via; routeOrder.AddLast(id); if (routeOrder.Count > 20000) { routes.Remove(routeOrder.First!.Value); routeOrder.RemoveFirst(); } }
    /// <summary>Answer a routed message (replyTo is what OnRoute was given).</summary>
    public void SendReply(string replyTo, JsonNode? body)
    {
        var parts = replyTo.Split('\n', 2);
        Send(new JsonObject { ["t"] = "route", ["id"] = RandomId(), ["re"] = parts[0], ["body"] = body }, parts[1]);
    }
    public Task<JsonNode?> Route(string dst, JsonNode? body, int ms = 20000)
    {
        var id = RandomId(); var tcs = new TaskCompletionSource<JsonNode?>();
        lock (gate) { Remember(id, null); waiting[id] = tcs; }
        Send(new JsonObject { ["t"] = "route", ["id"] = id, ["dst"] = dst, ["ttl"] = 8, ["body"] = body });
        _ = Task.Delay(ms).ContinueWith(_ => { lock (gate) waiting.Remove(id); tcs.TrySetResult(null); });
        return tcs.Task;
    }
    static string RandomId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    // ---- live over the mesh: who wants a stream, and its recent segments, forwarded to them
    sealed class LiveState { public Dictionary<string, double> Wanted = []; public SortedDictionary<double, JsonObject> Segs = []; public JsonObject? Seg0; public double Last, FwdAt, Local; }
    readonly Dictionary<string, LiveState> lives = [];
    LiveState LiveOf(string st) => lives.TryGetValue(st, out var l) ? l : lives[st] = new LiveState();
    public void LiveWant(string stream, string? from, int ttl, bool local = false)
    {
        var L = LiveOf(stream); var now = Now();
        if (from != null) { L.Wanted[from] = now + 70000; foreach (var g in new[] { L.Seg0 }.Concat(L.Segs.Values).OfType<JsonObject>()) Send(new JsonObject { ["t"] = "lseg", ["seg"] = g.DeepClone() }, from); }
        if (local) L.Local = now + 70000;
        if (ttl > 0 && now - L.Last > 5000 && now - L.FwdAt > 15000) { L.FwdAt = now; foreach (var id in peers()) if (id != from) Send(new JsonObject { ["t"] = "lwant", ["stream"] = stream, ["ttl"] = ttl - 1 }, id); }
    }
    public Action<JsonObject>? OnLiveSeg;
    public void LiveSeg(JsonObject g, string? from)
    {
        if (!(IsSafeInteger(Get(g, "n")) && Num(Get(g, "n")) >= 0 && IsSafeInteger(Get(g, "o")) && IsHex(Get(g, "h"), 64) && IsString(Get(g, "sig")) && Str(Get(g, "d")) is { } d && d.Length <= (1 << 20) * 1.4)) return;
        var L = LiveOf(Str(g["stream"])!); var now = Now(); var n = Num(g["n"])!.Value;
        if (L.Segs.ContainsKey(n) || (n == 0 && L.Seg0 != null)) return;
        L.Last = now;
        if (n == 0) L.Seg0 = g; else { L.Segs[n] = g; foreach (var k in L.Segs.Keys.Where(k => k < n - 60).ToList()) L.Segs.Remove(k); }
        foreach (var (id, until) in L.Wanted.ToList()) { if (until < now) { L.Wanted.Remove(id); continue; } if (id != from) Send(new JsonObject { ["t"] = "lseg", ["seg"] = g.DeepClone() }, id); }
        OnLiveSeg?.Invoke(g);
        foreach (var (st, x) in lives.ToList()) if (now - x.Last > 10 * 60000 && !(x.Local > now)) lives.Remove(st);
    }
    /// <summary>Our own live segment (a broadcaster, or OBS through this node).</summary>
    public void PublishLiveSeg(JsonObject g) { lock (gate) LiveSeg(g, null); }

    static JsonObject Without(JsonNode o, string k) { var c = (JsonObject)o.DeepClone(); c.Remove(k); return c; }
    /// <summary>Run something against the engine's state under its lock (for status pages and the UIs).</summary>
    public T Read<T>(Func<Engine, T> f) { lock (gate) return f(this); }
}
