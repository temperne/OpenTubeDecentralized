using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;

namespace OpenTube.Core;

/// <summary>
/// Runs the network's rules — the same opentube-core script every browser runs — inside .NET, on one thread of its own
/// (an event loop: everything that touches the script goes through Post). The slow parts are native C#: SHA-256,
/// Ed25519, random numbers, UTF-8, and proof of work on every core. Until the rules are ported to C#, this keeps a
/// .NET node in exact agreement with browsers about what is valid.
/// </summary>
public sealed partial class JsCore : IDisposable
{
    readonly Engine js;
    readonly BlockingCollection<Action> queue = new();
    readonly Thread thread;
    readonly ConcurrentDictionary<int, Timer> timers = new();
    readonly ConcurrentDictionary<int, CancellationTokenSource> mining = new();
    int nextTimer, nextJob;
    public Action<string> Log { get; set; } = Console.WriteLine;
    /// <summary>The core's version string, once loaded.</summary>
    public string Version { get; private set; } = "";

    public JsCore()
    {
        js = new Engine(o => o.LimitRecursion(2000).Strict(false));
        thread = new Thread(Loop) { IsBackground = true, Name = "opentube-core" };
        Install();
        thread.Start();
    }

    void Loop()
    {
        foreach (var a in queue.GetConsumingEnumerable())
        {
            try { a(); js.Advanced.ProcessTasks(); }
            catch (Exception e) { Log("[core] " + e.Message); }
        }
    }
    /// <summary>Run on the script's thread (fire and forget).</summary>
    public void Post(Action<Engine> a) { if (!queue.IsAddingCompleted) queue.Add(() => a(js)); }

    /// <summary>Evaluate an async JavaScript expression (e.g. "(async () => ...)()") and get its JSON-serialized result.</summary>
    public Task<string?> RunAsync(string code)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Interlocked.Increment(ref nextJob);
        pending[id] = tcs;
        Post(e => e.Invoke("__settle", e.Evaluate(code), id));
        return tcs.Task;
    }
    readonly ConcurrentDictionary<int, TaskCompletionSource<string?>> pending = new();

    /// <summary>Call a global JavaScript function with string/number arguments (on the script's thread).</summary>
    public void Call(string fn, params object[] args) => Post(e => e.Invoke(fn, args));

    /// <summary>Load the core from the page (index.html): the script inside &lt;script id="opentube-core"&gt;.</summary>
    public void LoadFromPage(string html)
    {
        var m = CoreScript().Match(html);
        if (!m.Success) throw new InvalidOperationException("index.html has no opentube-core script");
        var ready = new ManualResetEventSlim();
        Exception? err = null;
        Post(e => { try { e.Execute(m.Groups[1].Value); Version = e.Evaluate("OpenTubeCore.VERSION").AsString(); } catch (Exception x) { err = x; } finally { ready.Set(); } });
        ready.Wait();
        if (err != null) throw new InvalidOperationException("the core didn't load: " + err.Message, err);
    }
    [GeneratedRegex("<script id=\"opentube-core\">([\\s\\S]*?)</script>")] private static partial Regex CoreScript();

    /// <summary>Host functions (__h) and the browser APIs the core uses, built on them: crypto, TextEncoder, timers, atob/btoa.</summary>
    void Install()
    {
        var h = new JsObjectBuilder(js);
        JsValue Buf(byte[] b) => js.Intrinsics.ArrayBuffer.Construct(b);
        h.Fn("sha256", (byte[] d) => Buf(SHA256.HashData(d)));
        h.Fn("rand", (int n) => Buf(RandomNumberGenerator.GetBytes(n)));
        h.Fn("edGen", () => { var (p, k) = Ed25519.Generate(); return js.Intrinsics.Array.Construct([Buf(p), Buf(k)]); });
        h.Fn("edSign", (byte[] key, byte[] msg) => Buf(Ed25519.Sign(key, msg)));
        h.Fn("edVerify", (byte[] pub, byte[] sig, byte[] msg) => Ed25519.Verify(pub, sig, msg));
        h.Fn("utf8", (string s) => Buf(Encoding.UTF8.GetBytes(s)));
        h.Fn("utf8dec", (byte[] b) => Encoding.UTF8.GetString(b));
        h.Fn("atob", (string s) => Encoding.Latin1.GetString(Convert.FromBase64String(s)));
        h.Fn("btoa", (string s) => Convert.ToBase64String(Encoding.Latin1.GetBytes(s)));
        h.Fn("log", (string s) => Log(s));
        h.Fn("timer", (JsValue fn, double ms, bool repeat) =>
        {
            var id = Interlocked.Increment(ref nextTimer);
            var period = repeat ? TimeSpan.FromMilliseconds(Math.Max(1, ms)) : Timeout.InfiniteTimeSpan;
            timers[id] = new Timer(_ => Post(e => { if (!repeat) { if (timers.TryRemove(id, out var t)) t.Dispose(); } else if (!timers.ContainsKey(id)) return; e.Invoke(fn); }),
                null, TimeSpan.FromMilliseconds(Math.Max(0, ms)), period);
            return id;
        });
        h.Fn("untimer", (double id) => { if (timers.TryRemove((int)id, out var t)) t.Dispose(); });
        // proof of work on every core, off the script's thread; the answer comes back through __mined
        h.Fn("mine", (string prefix, int bits) =>
        {
            var id = Interlocked.Increment(ref nextJob); var cts = new CancellationTokenSource(); mining[id] = cts;
            Task.Run(() =>
            {
                var n = Miner.Find(prefix, bits, cts.Token);
                mining.TryRemove(id, out _);
                if (n is long v) Post(e => e.Invoke("__mined", id, (double)v));
            });
            return id;
        });
        h.Fn("unmine", (double id) => { if (mining.TryRemove((int)id, out var c)) c.Cancel(); });
        h.Fn("done", (double id, string? json) => { if (pending.TryRemove((int)id, out var t)) t.TrySetResult(json); });
        h.Fn("fail", (double id, string err) => { if (pending.TryRemove((int)id, out var t)) t.TrySetException(new InvalidOperationException(err)); });
        js.SetValue("__h", h.Build());
        js.Execute(Prelude);
    }

    const string Prelude = """
      const u8 = (d) => d instanceof Uint8Array ? d : d instanceof ArrayBuffer ? new Uint8Array(d) : ArrayBuffer.isView(d) ? new Uint8Array(d.buffer, d.byteOffset, d.byteLength) : new Uint8Array(d);
      globalThis.setTimeout = (f, ms, ...a) => __h.timer(() => f(...a), ms || 0, false);
      globalThis.setInterval = (f, ms, ...a) => __h.timer(() => f(...a), ms || 0, true);
      globalThis.clearTimeout = globalThis.clearInterval = (id) => { if (id != null) __h.untimer(id); };
      globalThis.setImmediate = (f, ...a) => __h.timer(() => f(...a), 0, false);
      globalThis.queueMicrotask = (f) => { Promise.resolve().then(f); };
      globalThis.atob = (s) => __h.atob(String(s)); globalThis.btoa = (s) => __h.btoa(String(s));
      globalThis.TextEncoder = class { encode(s) { return new Uint8Array(__h.utf8(String(s ?? ''))); } };
      globalThis.TextDecoder = class { decode(b) { return b ? __h.utf8dec(u8(b)) : ''; } };
      globalThis.console = { log: (...a) => __h.log(a.map(x => typeof x === 'string' ? x : JSON.stringify(x)).join(' ')), warn: (...a) => console.log(...a), error: (...a) => console.log(...a) };
      const key = (type, bytes) => ({ type, algorithm: { name: 'Ed25519' }, bytes: u8(bytes) });
      globalThis.crypto = {
        getRandomValues(a) { const r = new Uint8Array(__h.rand(a.length)); a.set(r); return a; },
        randomUUID() { const b = new Uint8Array(__h.rand(16)); b[6] = b[6] & 15 | 64; b[8] = b[8] & 63 | 128; const x = [...b].map(v => v.toString(16).padStart(2, '0')).join(''); return `${x.slice(0, 8)}-${x.slice(8, 12)}-${x.slice(12, 16)}-${x.slice(16, 20)}-${x.slice(20)}`; },
        subtle: {
          async digest(alg, data) { return __h.sha256(u8(data)); },
          async generateKey() { const [p, k] = __h.edGen(); return { publicKey: key('public', p), privateKey: key('private', k) }; },
          async importKey(fmt, data, alg) { return key(fmt === 'raw' ? 'public' : 'private', new Uint8Array(u8(data))); },
          async exportKey(fmt, k) { return k.bytes.slice().buffer; },
          async sign(alg, k, data) { return __h.edSign(k.bytes, u8(data)); },
          async verify(alg, k, sig, data) { return __h.edVerify(k.bytes, u8(sig), u8(data)); }
        }
      };
      // native proof of work: the same contract as the JavaScript node's mine(body, bits) -> { promise, cancel }
      const mineJobs = new Map();
      globalThis.__mined = (id, n) => { const r = mineJobs.get(id); if (r) { mineJobs.delete(id); r(n); } };
      globalThis.__mine = (body, bits) => { const id = __h.mine(OpenTubeCore.preimage(body), bits); return { promise: new Promise(r => mineJobs.set(id, r)), cancel: () => { mineJobs.delete(id); __h.unmine(id); } }; };
      globalThis.__settle = (v, id) => Promise.resolve(v).then(x => __h.done(id, x === undefined ? null : JSON.stringify(x)), e => __h.fail(id, String(e && e.stack || e)));
    """;

    public void Dispose()
    {
        queue.CompleteAdding();
        foreach (var t in timers.Values) t.Dispose();
        foreach (var c in mining.Values) c.Cancel();
    }
}

/// <summary>Builds a plain JavaScript object of host functions.</summary>
sealed class JsObjectBuilder(Engine js)
{
    readonly Dictionary<string, Delegate> fns = [];
    public void Fn(string name, Delegate d) => fns[name] = d;
    public JsValue Build()
    {
        var obj = js.Intrinsics.Object.Construct(Jint.Runtime.Arguments.Empty);
        foreach (var (k, d) in fns) obj.Set(k, JsValue.FromObject(js, d));
        return obj;
    }
}
