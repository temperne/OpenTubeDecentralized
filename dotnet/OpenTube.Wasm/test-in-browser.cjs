// Loads the published WebAssembly (dotnet publish OpenTube.Wasm -c Release -o OUT) in Chromium, checks it against the
// JavaScript core on every case in OpenTube.Tests/data/rules.json, and times both. Run: node test-in-browser.cjs OUT/wwwroot
const { chromium } = require('playwright'); const { spawn } = require('child_process'); const fs = require('fs');
const OUT = process.argv[2], R = require('path').resolve(__dirname, '../..');
(async () => {
  const srv = spawn('python3', ['-m', 'http.server', '8899', '-d', OUT], { stdio: 'ignore' }); await new Promise(r => setTimeout(r, 800));
  const b = await chromium.launch(); const p = await b.newPage(); p.on('pageerror', e => console.log('page error', e.message)); p.on('console', m => /error/i.test(m.type()) && console.log('console', m.text()));
  const t0 = Date.now(); await p.goto('http://localhost:8899/'); await p.waitForFunction(() => globalThis.OpenTubeWasm, null, { timeout: 60000 });
  console.log('C# WebAssembly loaded in', Date.now() - t0, 'ms');
  const V = JSON.parse(fs.readFileSync(R + '/dotnet/OpenTube.Tests/data/rules.json', 'utf8'));
  const core = /<script id="opentube-core">([\s\S]*?)<\/script>/.exec(fs.readFileSync(R + '/index.html', 'utf8'))[1];
  const r = await p.evaluate(async ({ V, core }) => {
    new Function(core)(); const C = globalThis.OpenTubeCore, W = globalThis.OpenTubeWasm;
    const g = V.genesis.map(c => W.validGenesis(c.g) === c.ok).filter(x => !x).length;
    const blocks = V.signed.map(c => c.b), want = V.signed.map(c => c.ok);
    const s = W.checkSignedMany(blocks, V.generatedAt).filter((x, i) => x !== want[i]).length;
    const n = V.numbers.filter(c => W.canon(c.n) !== c.s).length;
    // speed: the browser's JavaScript core vs the C# rules in WebAssembly, same 300 blocks, 3 rounds each
    const ok = blocks.filter(b => JSON.stringify(b).length < 100000);
    let t = performance.now(); for (let k = 0; k < 3; k++) for (const b of ok) await C.checkSigned(b); const js = performance.now() - t;
    t = performance.now(); for (let k = 0; k < 3; k++) W.checkSignedMany(ok, Date.now()); const wasm = performance.now() - t;
    t = performance.now(); for (let k = 0; k < 3; k++) for (const gg of V.genesis) C.validGenesis(gg.g); const jsg = performance.now() - t;
    t = performance.now(); for (let k = 0; k < 3; k++) for (const gg of V.genesis) W.validGenesis(gg.g); const wg = performance.now() - t;
    return { g, s, n, total: [V.genesis.length, V.signed.length, V.numbers.length], js, wasm, jsg, wg, count: ok.length * 3 };
  }, { V, core });
  console.log(`disagreements with JavaScript: genesis ${r.g}/${r.total[0]}, signed ${r.s}/${r.total[1]}, numbers ${r.n}/${r.total[2]}`);
  console.log(`checkSigned × ${r.count}: browser JavaScript ${r.js.toFixed(0)} ms · C# WebAssembly ${r.wasm.toFixed(0)} ms`);
  console.log(`validGenesis × ${3 * r.total[0]}: browser JavaScript ${r.jsg.toFixed(0)} ms · C# WebAssembly ${r.wg.toFixed(0)} ms`);
  await b.close(); srv.kill(); process.exit(0);
})().catch(e => { console.error(e); process.exit(1); });
