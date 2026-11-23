using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Tests;

// The C# port against the JavaScript core's own answers (data/rules.json, made by gen-vectors.mjs from index.html).
public class RulesTests
{
    static readonly JsonObject V = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../data/rules.json")))!.AsObject();
    static long At => (long)V["generatedAt"]!;

    static void AllAgree(string what, IEnumerable<(bool expected, bool actual, JsonNode? input)> results)
    {
        var bad = results.Where(r => r.expected != r.actual).Take(5).ToList();
        Assert.True(bad.Count == 0, $"{what}: C# disagrees with JavaScript on {bad.Count}+ cases, e.g.\n" + string.Join("\n", bad.Select(b => $"  JS {b.expected}, C# {b.actual}: {b.input?.ToJsonString()[..Math.Min(400, b.input.ToJsonString().Length)]}")));
    }

    [Fact]
    public void ValidGenesis_agrees_with_javascript() =>
        AllAgree("validGenesis", V["genesis"]!.AsArray().Select(c => ((bool)c!["ok"]!, Rules.ValidGenesis(c["g"]), c["g"])));

    [Fact]
    public void CheckSigned_agrees_with_javascript() =>
        AllAgree("checkSigned", V["signed"]!.AsArray().Select(c => ((bool)c!["ok"]!, Rules.CheckSigned(c["b"], At), c["b"])));

    [Fact]
    public void Numbers_print_as_javascript_prints_them()
    {
        foreach (var c in V["numbers"]!.AsArray())
            Assert.Equal((string)c!["s"]!, Blocks.Canon(c["n"]));
    }

    [Fact]
    public void Chain_heads_ancestors_and_missing_blocks_agree_with_javascript()
    {
        foreach (var c in V["chains"]!.AsArray())
        {
            var g = c!["genesis"]!.AsObject();
            var ch = new Chain((string)g["hash"]!, g);
            foreach (var b in c["adds"]!.AsArray()) ch.Add(b!.AsObject());
            Assert.Equal(c["heads"]!.AsArray().Select(x => (string)x!), ch.Heads);
            Assert.Equal(c["anc"]!.AsArray().Select(x => (string)x!), ch.Ancestors(c["ancQ"]!.AsArray().Select(x => (string)x!)).Items);
            Assert.Equal(c["missing"]!.AsArray().Select(x => (string)x!),
                ch.MissingFor(c["want"]!.AsArray().Select(x => (string)x!), c["have"]!.AsArray().Select(x => (string)x!)).Select(b => (string)b["hash"]!));
        }
    }
}

// The core running in .NET with the ported rule plugged in gives the same answers as its own JavaScript version.
// (Speed comes later, when whole layers leave the interpreter: through it, both take about the same time.)
public class NativeHookTests
{
    [Fact]
    public async Task Native_checkSigned_plugged_into_the_core_gives_the_same_answers()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        // the signed blocks only (not the oversized one: that one is rejected by size before any work)
        var signed = new JsonArray(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../data/rules.json")))!["signed"]!.AsArray()
            .Where(c => c!["b"]!.ToJsonString().Length < 100000).Take(10).Select(c => (JsonNode?)c!.DeepClone()).ToArray());
        var cases = new JsonObject { ["signed"] = signed }.ToJsonString();
        using var core = new JsCore(); core.LoadFromPage(File.ReadAllText(Path.Combine(repo, "index.html")));
        var run = $$"""(async () => { const V = {{cases}}; const t0 = Date.now(); const r = []; for (const c of V.signed) r.push(await OpenTubeCore.checkSigned(JSON.parse(JSON.stringify(c.b)))); return { ms: Date.now() - t0, r: r.slice(0, V.signed.length) }; })()""";
        var native = JsonNode.Parse((await core.RunAsync(run))!)!;
        await core.RunAsync("Promise.resolve(delete globalThis.OpenTubeNative)");
        var js = JsonNode.Parse((await core.RunAsync(run))!)!;
        Assert.Equal(js["r"]!.ToJsonString(), native["r"]!.ToJsonString());
        Console.WriteLine($"checkSigned × {js["r"]!.AsArray().Count}: JavaScript in .NET {js["ms"]} ms, native C# {native["ms"]} ms");
    }
}

// validPayload: the C# port against the JavaScript core's answers on 20,000 blocks over chains of every kind
public class PayloadTests
{
    [Fact]
    public void ValidPayload_agrees_with_javascript()
    {
        var V = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../data/payload.json")))!;
        var chains = V["chains"]!.AsArray().Select(c =>
        {
            var g = c!["genesis"]!.AsObject(); var ch = new Chain((string)g["hash"]!, g);
            foreach (var b in c["blocks"]!.AsArray()) ch.Add(b!.AsObject());
            return (kind: (string)c["kind"]!, ch);
        }).ToList();
        var bad = new List<string>(); int n = 0;
        foreach (var c in V["cases"]!.AsArray())
        {
            n++; var (kind, ch) = chains[(int)c!["c"]!];
            var want = c["ok"]!.ToJsonString(); bool got;
            try { got = Payload.Valid(ch, c["b"]!.AsObject()); } catch (Exception e) { bad.Add($"{kind}: C# threw {e.GetType().Name}: {e.Message} on {c["b"]!["payload"]!.ToJsonString()}"); continue; }
            if (want != (got ? "true" : "false")) bad.Add($"{kind}: JS {want}, C# {got} — author {((string?)c["b"]!["author"])?[..6]} hash {((string?)c["b"]!["hash"])?[..6]} payload {c["b"]!["payload"]!.ToJsonString()[..Math.Min(300, c["b"]!["payload"]!.ToJsonString().Length)]}");
        }
        Assert.True(bad.Count == 0, $"{bad.Count} of {n} disagree:\n" + string.Join("\n", bad.Take(12)));
    }
}

// The coin ledger: random histories replayed through C#, compared verdict by verdict and state by state with JavaScript
public class LedgerTests
{
    [Fact]
    public void Ledger_agrees_with_javascript()
    {
        var V = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../data/ledger.json")))!;
        int run = 0, txs = 0;
        foreach (var steps in V["runs"]!.AsArray())
        {
            run++; var L = new Ledger(); int blk = 0;
            foreach (var step in steps!.AsArray())
            {
                blk++;
                foreach (var x in step!["txs"]!.AsArray())
                {
                    txs++; var t = x!["t"]!.AsObject(); var want = (string?)x["why"];
                    var got = Tx.Check(t, trusted: true) ? L.Apply(t) : "checkTx";
                    Assert.True(want == got, $"run {run} block {blk}: {t.ToJsonString()[..Math.Min(400, t.ToJsonString().Length)]}\n  JS: {want ?? "applied"}\n  C#: {got ?? "applied"}");
                }
                L.EndBlock(step["miner"], (double)step["ts"]!); L = new Ledger(L);
                var js = (string)step["state"]!; var cs = L.Snapshot();
                if (js != cs)
                {
                    var a = JsonNode.Parse(js)!.AsObject(); var b = JsonNode.Parse(cs)!.AsObject();
                    var diff = a.Select(kv => kv.Key).Where(k => Blocks.Canon(a[k]) != Blocks.Canon(b[k])).Select(k => $"  {k}:\n    JS {Blocks.Canon(a[k])[..Math.Min(300, Blocks.Canon(a[k]).Length)]}\n    C# {Blocks.Canon(b[k])[..Math.Min(300, Blocks.Canon(b[k]).Length)]}");
                    Assert.Fail($"run {run} block {blk}: state differs\n" + string.Join("\n", diff.Take(4)));
                }
            }
        }
        Assert.True(txs > 1000);
    }
}

// The engine's chains: the same mined blocks through C#'s ChainStore as through the JavaScript engine, step by step
public class ChainStoreTests
{
    static string SnapL(Ledger L) => Blocks.Canon(new JsonObject
    {
        ["bal"] = Obj(L.Bal), ["seq"] = Obj(L.Seq), ["starters"] = new JsonArray(L.Starters.Order(StringComparer.Ordinal).Select(x => (JsonNode?)x).ToArray()),
        ["names"] = new JsonObject(L.Names.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value.DeepClone()))), ["nameOf"] = new JsonObject(L.NameOf.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))),
        ["minted"] = L.Minted, ["burned"] = L.Burned, ["txCount"] = L.TxCount, ["prices"] = Obj(L.Prices),
    });
    static JsonObject Obj(Dictionary<string, double> m) => new(m.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value)));

    [Fact]
    public void ChainStore_agrees_with_the_javascript_engine()
    {
        var V = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../data/engine.json")))!;
        var store = new ChainStore(V["mainGenesis"]!.AsObject(), (double)V["now"]!);
        foreach (var s in V["steps"]!.AsArray())
        {
            var label = (string)s!["label"]!; var want = s["after"]!;
            store.Insert((string)s["chain"]!, s["blocks"]!.AsArray().Select(b => b!.DeepClone()));
            Assert.True((string)want["best"]! == Str(store.MainBest["hash"]), $"{label}: best main block");
            Assert.True(Blocks.Canon(want["directory"]) == Blocks.Canon(new JsonArray(store.Directory.Keys.Order(StringComparer.Ordinal).Select(x => (JsonNode?)x).ToArray())), $"{label}: directory");
            Assert.True(Blocks.Canon(want["mempool"]) == Blocks.Canon(new JsonArray(store.Mempool.Keys.Order(StringComparer.Ordinal).Select(x => (JsonNode?)x).ToArray())), $"{label}: mempool");
            Assert.True(Blocks.Canon(want["txpool"]) == Blocks.Canon(new JsonArray(store.TxPool.Keys.Order(StringComparer.Ordinal).Select(x => (JsonNode?)x).ToArray())), $"{label}: txpool");
            Assert.True((string)want["ledger"]! == SnapL(store.Ledger), $"{label}: ledger\n  JS {want["ledger"]}\n  C# {SnapL(store.Ledger)}");
            Assert.True(Blocks.Canon(want["rejected"]) == Blocks.Canon(new JsonObject(store.Rejected.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value)))), $"{label}: rejected\n  JS {Blocks.Canon(want["rejected"])}\n  C# {Blocks.Canon(new JsonObject(store.Rejected.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))))}");
            var chains = new JsonObject(store.Chains.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, new JsonObject
            {
                ["heads"] = new JsonArray(kv.Value.Heads.Order(StringComparer.Ordinal).Select(x => (JsonNode?)x).ToArray()), ["n"] = kv.Value.Blocks.Count,
                ["orphans"] = new JsonArray(kv.Value.Orphans.Keys.Order(StringComparer.Ordinal).Select(x => (JsonNode?)x).ToArray()),
            })));
            Assert.True(Blocks.Canon(want["chains"]) == Blocks.Canon(chains), $"{label}: chains\n  JS {Blocks.Canon(want["chains"])[..Math.Min(500, Blocks.Canon(want["chains"]).Length)]}\n  C# {Blocks.Canon(chains)[..Math.Min(500, Blocks.Canon(chains).Length)]}");
        }
    }
    static string? Str(JsonNode? n) => Js.Str(n);
}
