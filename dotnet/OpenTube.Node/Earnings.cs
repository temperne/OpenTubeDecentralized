using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Node;

/// <summary>
/// Getting paid for resources (as the JavaScript node; core: SERVE, PIECE, PROOF):
///  • receipts: browsers sign "this node sent me N bytes / was connected N s / its tracker served me N s" for each
///    10-minute period; after the period the node submits them in a `serve` transaction (with the owner's grant, so the
///    owner is paid). Receipts for other nodes are passed on once.
///  • tabs: the viewers' signed tabs for the pieces this node sent are cashed in once a minute (`tabs` transaction).
///  • earnings: beyond a 20-coin reserve (for its own downloads), what the node's key earns goes to its owner.
///  • storage proofs: it challenges other hosts of the videos it holds (a random piece and a fresh nonce; the answer
///    is sha256("opentube-proof:nonce:key:" + piece), signed), counts the copies that pass (trimming keeps rare videos)
///    and signs storage receipts for them; and it answers other hosts' challenges.
///  • starter coins for its own key, once, so the receipts it signs count.
/// </summary>
public sealed class Earnings
{
    readonly OpenTubeNode node;
    readonly Dictionary<double, Dictionary<string, JsonObject>> byEpoch = [];
    readonly HashSet<string> seen = [], submitted = [], provedOnce = [];
    readonly Dictionary<double, Dictionary<string, Dictionary<string, double>>> storage = [];   // epoch -> provider -> ih -> MB
    readonly Dictionary<string, double> tabsDone = [];
    readonly object gate = new();
    public Func<List<JsonObject>>? PendingTabs;   // the viewers' tabs for what this node sent (pay per piece)
    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public Earnings(OpenTubeNode node) { this.node = node; node.Engine.OnReceipt += Take; }

    void Log(string m) => node.Log(m);
    public void Take(JsonObject r, string from)
    {
        if ((string?)r["sig"] is not { } sig) return;
        lock (gate) { if (!seen.Add(sig)) return; if (seen.Count > 50000) seen.Clear(); }
        if ((string?)r["provider"] != node.NodeKey) { node.Engine.SendAllBut(new JsonObject { ["t"] = "rcpt", ["r"] = r.DeepClone() }, from); return; }
        var res = (string?)r["res"];
        if (!Js.NumIs(r["v"], 1) || (string?)r["kind"] != "receipt" || !Coin.ServeRes.Any(x => x.Res == res) || !(Js.Num(r["amt"]) > 0) || Js.Num(r["epoch"]) is not double ep || ep >= Coin.EpochOf(Now())) return;
        var by = (string?)r["by"]; if (by == null) return;
        var unsigned = (JsonObject)r.DeepClone(); unsigned.Remove("sig");
        if (!Rules.VerifySig(by, "opentube-receipt:" + Blocks.Canon(unsigned), sig)) return;
        lock (gate)
        {
            if (submitted.Contains(ep.ToString())) return;
            var m = byEpoch.TryGetValue(ep, out var x) ? x : byEpoch[ep] = [];
            var k = res + "|" + by; if (!m.TryGetValue(k, out var cur) || Js.Num(r["amt"]) > Js.Num(cur["amt"])) m[k] = r;
        }
    }

    public async Task Run(CancellationToken stop)
    {
        _ = Task.Run(() => StarterOnce(stop));
        long n = 0;
        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(60000, stop).ContinueWith(_ => { });
            if (stop.IsCancellationRequested) break;
            try { await SubmitReceipts(); await CashTabs(); await SignStorage(); if (n % 10 == 9) await PassEarnings(); }
            catch (Exception e) { Log("[earn] " + e.Message); }
            n++;
        }
    }
    async Task StarterOnce(CancellationToken stop)
    {
        await Task.Delay(20000, stop).ContinueWith(_ => { });
        if (node.Hosting == null || node.Options.Mine == "off" || node.Engine.Read(e => e.Store.Ledger.Starters.Contains(node.NodeKey))) return;
        try { await node.Engine.SubmitTx(new JsonObject { ["starter"] = true }); Log("[earn] claimed starter coins for this node’s key (so its receipts count)"); }
        catch (Exception e) { Log("[earn] starter: " + e.Message); }
    }
    async Task SubmitReceipts()
    {
        var now = Coin.EpochOf(Now()); List<(double E, List<JsonObject> L)> due = [];
        lock (gate)
            foreach (var (e, m) in byEpoch.ToList())
            {
                if (e >= now) continue;
                byEpoch.Remove(e); submitted.Add(e.ToString());
                if (e >= now - Coin.Grace) due.Add((e, m.Values.ToList()));
            }
        var grant = node.Options.Grant != null ? Try(() => JsonNode.Parse(Encoding.UTF8.GetString(B64u.Decode(node.Options.Grant)))) : null;
        foreach (var (e, list) in due)
            for (int i = 0; i < list.Count; i += (int)Coin.MaxReceipts)
            {
                var serve = new JsonObject { ["epoch"] = e, ["receipts"] = new JsonArray(list.Skip(i).Take((int)Coin.MaxReceipts).Select(x => (JsonNode?)x.DeepClone()).ToArray()) };
                if (grant != null) serve["grant"] = grant.DeepClone();
                try { await node.Engine.SubmitTx(new JsonObject { ["serve"] = serve }); Log($"[earn] submitted {list.Count} receipt(s) for period {e}"); }
                catch (Exception ex) { Log("[earn] couldn’t submit receipts: " + ex.Message); }
            }
    }
    async Task CashTabs()
    {
        var pending = PendingTabs?.Invoke() ?? []; if (pending.Count == 0) return;
        var paid = node.Engine.Read(e => new Dictionary<string, double>(e.Store.Ledger.TabsPaid));
        var due = pending.Where(x => Js.Num(x["amt"]) > paid.GetValueOrDefault($"{x["payer"]}|{x["payee"]}|{x["video"]}") && Js.Num(x["amt"]) > tabsDone.GetValueOrDefault($"{x["payer"]}|{x["video"]}")).ToList();
        for (int i = 0; i < due.Count; i += (int)Coin.MaxTabs)
        {
            var part = due.Skip(i).Take((int)Coin.MaxTabs).ToList();
            try
            {
                await node.Engine.SubmitTx(new JsonObject { ["tabs"] = new JsonArray(part.Select(x => (JsonNode?)x.DeepClone()).ToArray()) });
                foreach (var x in part) tabsDone[$"{x["payer"]}|{x["video"]}"] = Js.Num(x["amt"]) ?? 0;
                Log($"[pay] cashing in {part.Count} viewer tab(s)");
            }
            catch (Exception e) { Log("[pay] couldn’t cash in tabs: " + e.Message); }
        }
    }
    async Task PassEarnings()
    {
        if (node.OwnerUser == null) return;
        var (extra, busy) = node.Engine.Read(e => (e.Store.Ledger.Balance(node.NodeKey) - 2000, e.Store.TxPool.Values.Any(t => (string?)t["from"] == node.NodeKey && t["pay"] != null)));
        if (extra < 500 || busy) return;
        try { await node.Engine.SubmitTx(new JsonObject { ["pay"] = new JsonArray(new JsonObject { ["to"] = node.OwnerUser, ["amt"] = extra }), ["memo"] = "node earnings" }); Log($"[pay] sent {extra / 100} coins of earnings to the owner"); }
        catch (Exception e) { Log("[pay] couldn’t send earnings: " + e.Message); }
    }

    // ---- storage proofs
    public static string PieceProof(string nonce, string key, byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData([.. Encoding.UTF8.GetBytes($"opentube-proof:{nonce}:{key}:"), .. bytes]));
    public JsonObject Answer(JsonObject q)
    {
        var no = new JsonObject { ["kind"] = "pproof", ["has"] = false };
        if (node.Hosting == null || !Js.IsHex(q["ih"], 40) || Js.Str(q["nonce"]) is not { Length: 32 } nonce || Js.Num(q["piece"]) is not double pd) return no;
        var bytes = node.Hosting.Piece((string)q["ih"]!, (int)pd); if (bytes == null) return no;
        var a = new JsonObject { ["v"] = 1, ["kind"] = "pproof", ["has"] = true, ["ih"] = q["ih"]!.DeepClone(), ["piece"] = (int)pd, ["nonce"] = nonce, ["node"] = node.NodeKey, ["at"] = Now(), ["h"] = PieceProof(nonce, node.NodeKey, bytes) };
        a["sig"] = node.Identity!.Sign("opentube-proof:" + Blocks.Canon(a));
        return a;
    }
    /// <summary>One round: a random video this node holds completely, up to 4 other hosts challenged on it.</summary>
    public async Task ChallengeRound()
    {
        var host = node.Hosting; if (host == null) return;
        var mine = host.Complete(); if (mine.Count == 0) return;
        var (ih, _) = mine[RandomNumberGenerator.GetInt32(mine.Count)];
        var size = host.ProgressOf(ih)?["size"] is JsonNode s ? (double?)s : null;
        var targets = node.Engine.Read(e => e.Nodes.Values.Where(n => n["roles"] is JsonArray r && r.Any(x => (string?)x == "host") && Now() - (Js.Num(n["at"]) ?? 0) < 120000).Select(n => (string?)n["node"]).Where(k => k != null && k != node.NodeKey).Select(k => k!).ToList())
            .OrderBy(_ => RandomNumberGenerator.GetInt32(1000)).Take(4).ToList();
        foreach (var key in targets)
        {
            var count = (int)Math.Ceiling((size ?? 0) / TorrentPieceLength(ih)); if (count <= 0) return;
            var index = RandomNumberGenerator.GetInt32(count); var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var bytes = host.Piece(ih, index); if (bytes == null) return;
            var expect = PieceProof(nonce, key, bytes);
            _ = Task.Run(async () =>
            {
                var a = await node.Engine.Route(key, new JsonObject { ["kind"] = "pchal", ["ih"] = ih, ["piece"] = index, ["nonce"] = nonce }, 20000) as JsonObject;
                if (a == null || !Js.IsTrue(a["has"])) return;
                var unsigned = (JsonObject)a.DeepClone(); unsigned.Remove("sig");
                var ok = (string?)a["node"] == key && (string?)a["nonce"] == nonce && Js.Num(a["piece"]) == index && (string?)a["h"] == expect && Rules.VerifySig(key, "opentube-proof:" + Blocks.Canon(unsigned), a["sig"]);
                host.NoteProof(ih, key, ok);
                if (!ok) { Log($"[proof] {key[..8]} failed a storage proof for {ih[..8]}"); return; }
                lock (gate)
                {
                    if (provedOnce.Add(key + ih)) Log($"[proof] {key[..8]} proved it holds {ih[..8]} (piece {index})");
                    var e = Coin.EpochOf(Now()); var m = storage.TryGetValue(e, out var x) ? x : storage[e] = [];
                    var v = m.TryGetValue(key, out var y) ? y : m[key] = []; v[ih] = Math.Max(1, Math.Round((size ?? 0) / 1e6));
                }
            });
        }
    }
    double TorrentPieceLength(string ih) => node.Hosting?.PieceLengthOf(ih) ?? 16384;
    async Task SignStorage()
    {
        var now = Coin.EpochOf(Now()); List<(double E, string P, double Amt)> rs = [];
        lock (gate) foreach (var (e, m) in storage.ToList()) { if (e >= now) continue; storage.Remove(e); foreach (var (p, v) in m) rs.Add((e, p, v.Values.Sum())); }
        foreach (var (e, p, amt) in rs)
        {
            var r = new JsonObject { ["v"] = 1, ["kind"] = "receipt", ["res"] = "storage", ["provider"] = p, ["by"] = node.NodeKey, ["epoch"] = e, ["amt"] = amt };
            r["sig"] = node.Identity!.Sign("opentube-receipt:" + Blocks.Canon(r));
            node.Engine.SendAllBut(new JsonObject { ["t"] = "rcpt", ["r"] = r }, null);
        }
        await Task.CompletedTask;
    }
    static T? Try<T>(Func<T> f) { try { return f(); } catch { return default; } }
}
