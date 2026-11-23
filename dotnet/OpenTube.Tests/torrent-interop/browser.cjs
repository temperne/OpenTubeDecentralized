// Run: dotnet build dotnet && node dotnet/OpenTube.Tests/torrent-interop/browser.cjs
// BitTorrent with a browser over WebRTC, through a WebSocket tracker (the .NET node's /tracker): the site's own
// WebTorrent downloads a file the C# client seeds, and the C# client downloads a file the browser seeds.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os'), crypto = require('crypto');
const REPO = path.resolve(__dirname, '../../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-wt-')), T = 8898, TRACKER = `ws://127.0.0.1:${T}/tracker`;
const DLL = REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll';
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
const sha = (b) => crypto.createHash('sha256').update(b).digest('hex');
const procs = [];
(async () => {
  const node = spawn('dotnet', [DLL, '--service', `--port=${T}`, `--data=${W}/node`, '--mine=off', '--no-tunnel', '--public=off', '--no-ranking', '--no-obs', '--no-mesh', '--site-dir=' + REPO]); procs.push(node);
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${T}/status`); break; } catch { execSync('sleep 0.5'); } }
  const ctx = await chromium.launchPersistentContext(W + '/browser', { headless: true }); const P = ctx.pages()[0];
  await P.goto(`http://localhost:${T}/?trackers=${encodeURIComponent(TRACKER)}`); await P.waitForFunction(() => window.opentube?.wt, null, { timeout: 60000 });
  for (const size of [70000, 3 * 1024 * 1024 + 123]) {
    // C# seeds, the browser downloads
    const file = `${W}/seed-${size}.bin`; fs.writeFileSync(file, crypto.randomBytes(size));
    const s = spawn('dotnet', [DLL, 'torrent', 'seed', file, `--tracker=${TRACKER}`]); procs.push(s);
    const ih = await new Promise(r => s.stdout.once('data', d => r(String(d).split(' ')[0].trim())));
    const t0 = Date.now();
    const got = await P.evaluate(async ({ ih, tr }) => new Promise((res) => {
      const t = opentube.wt.add(`magnet:?xt=urn:btih:${ih}&tr=${encodeURIComponent(tr)}`, { announce: [tr] });
      const to = setTimeout(() => res({ error: 'timeout', progress: t.progress, peers: t.numPeers }), 60000);
      t.on('done', async () => { clearTimeout(to); const b = new Uint8Array(await t.files[0].arrayBuffer()); const h = [...new Uint8Array(await crypto.subtle.digest('SHA-256', b))].map(x => x.toString(16).padStart(2, '0')).join(''); res({ h, n: b.length, peers: t.numPeers }); });
    }), { ih, tr: TRACKER });
    check(got.h === sha(fs.readFileSync(file)), `browser <- C# over WebRTC, ${size} bytes: ${got.error || 'identical'} in ${((Date.now() - t0) / 1000).toFixed(1)} s`);
    s.kill();
    // the browser seeds, C# downloads
    const bytes = crypto.randomBytes(size);
    const bih = await P.evaluate(async ({ b64, tr, n }) => new Promise((res) => { const bin = Uint8Array.from(atob(b64), c => c.charCodeAt(0)); opentube.wt.seed(new File([bin], `b-${n}.bin`), { announce: [tr] }, (t) => res(t.infoHash)); }), { b64: bytes.toString('base64'), tr: TRACKER, n: size });
    const out = `${W}/got-${size}.bin`, t1 = Date.now();
    let r = ''; try { r = execSync(`timeout 90 dotnet ${DLL} torrent get ${bih} ${out} --tracker=${TRACKER} 2>&1 | tail -1`).toString().trim(); } catch (e) { r = String(e.stdout || e.message).trim(); }
    check(fs.existsSync(out) && sha(fs.readFileSync(out)) === sha(bytes), `C# <- browser over WebRTC, ${size} bytes: ${r} (${((Date.now() - t1) / 1000).toFixed(1)} s)`);
  }
  await ctx.close(); for (const p of procs) try { p.kill(); } catch {} process.exit(fail);
})().catch(e => { console.error(e); for (const p of procs) try { p.kill(); } catch {} process.exit(1); });
