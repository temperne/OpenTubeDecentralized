// Run: dotnet build dotnet && node dotnet/OpenTube.Tests/test-mesh.cjs
// The peer mesh: a browser and the .NET node meet in the network's WebRTC room (Trystero, torrent strategy) through a
// tracker, and exchange the network's messages over the data channel. The page comes from another computer (B), so
// the only way between the browser and the node under test (A) is WebRTC.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-mesh-'));
const A = 8894, B = 8895, TRACKER = `ws://127.0.0.1:${A}/tracker`;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
const procs = {}, lines = { A: [], B: [] };
const run = (name, port, extra) => {
  const p = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${port}`, `--data=${W}/${name}`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-obs', '--site-dir=' + SITE, ...extra], { env: { ...process.env, MESH_DEBUG: '1' } });
  p.stdout.on('data', d => { for (const l of String(d).split('\n')) { lines[name].push(l); if (/mesh|WebRTC|peer |rror|fail/.test(l)) log(`[${name}]`, l.trim().replace(/^\d\d:\d\d:\d\d /, '').slice(0, 170)); } });
  procs[name] = p; for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${port}/status`); return; } catch { execSync('sleep 0.5'); } } };
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
(async () => {
  run('A', A, [`--mesh-trackers=${TRACKER}`]);
  run('B', B, ['--no-mesh']);
  const ctx = await chromium.launchPersistentContext(W + '/browser', { headless: true }); const P = ctx.pages()[0];
  P.on('pageerror', e => log('PAGEERROR', e.message.slice(0, 120)));
  await P.goto(`http://localhost:${B}/?trackers=${encodeURIComponent(TRACKER)}#/you/channels`);
  await P.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 });
  const aKey = JSON.parse(execSync(`curl -s localhost:${A}/status`)).node;
  // the browser's links: the node under test only as a WebRTC peer
  const t0 = Date.now(); let link = null;
  while (Date.now() - t0 < 60000 && !link) { link = await P.evaluate(() => [...opentube.net.links.entries()].filter(([, l]) => l.kind === 'p2p').map(([id, l]) => id)[0] || null); if (!link) await new Promise(r => setTimeout(r, 500)); }
  check(!!link, `the browser has a WebRTC peer after ${((Date.now() - t0) / 1000).toFixed(1)} s: ${link}`);
  check(lines.A.some(l => /\+ peer .* over WebRTC/.test(l)), 'A sees the browser as a WebRTC peer');
  const known = await P.evaluate(async (k) => { for (let i = 0; i < 60; i++) { if ([...opentube.engine.nodes.keys()].includes(k) || opentube.engine.nodeHi?.has?.(k)) return true; await new Promise(r => setTimeout(r, 500)); } return false; }, aKey).catch(() => false);
  // a channel registered in the browser reaches A only over the data channel (B and A aren't linked)
  await P.fill('input[placeholder=Name]', 'Mesh Channel'); await P.click('text=Create & register');
  await P.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await P.evaluate(() => location.hash.split('/')[2]);
  let got = false; for (let i = 0; i < 120 && !got; i++) { const s = JSON.parse(execSync(`curl -s localhost:${A}/status`)); got = s.registered >= 1 || (s.chains || 0) >= 2; if (!got) await new Promise(r => setTimeout(r, 500)); }
  check(got, 'the browser\'s new channel reached A over WebRTC');
  const aPeers = JSON.parse(execSync(`curl -s localhost:${A}/status`)).peers;
  check(!lines.A.some(l => /linked to|\+ browser/.test(l)), `A had no WebSocket peers (peers: ${aPeers})`);
  // messages over 16 KB go in chunks (offer, accepted, chunks), both ways: A echoes one back (test hook, MESH_DEBUG=1)
  const echo = await P.evaluate(async (id) => {
    const pad = 'x'.repeat(100000) + Math.random();
    const got = new Promise(res => { const orig = opentube.engine.handle.bind(opentube.engine); opentube.engine.handle = (m, from) => { if (m?.t === 'meshecho') { res(m.pad === pad); return; } return orig(m, from); }; setTimeout(() => res('timeout'), 20000); });
    opentube.net.links.get(id).send({ t: 'meshecho', pad }); return got;
  }, link);
  check(echo === true && lines.A.some(l => /echo of 1000\d\d bytes/.test(l)), `a 100 KB message went to A and back in chunks: ${echo}`);
  await ctx.close(); for (const p of Object.values(procs)) p.kill(); process.exit(fail);
})().catch(e => { console.error(e); for (const p of Object.values(procs)) try { p.kill(); } catch {} process.exit(1); });
