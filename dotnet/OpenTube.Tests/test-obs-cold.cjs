// Run: dotnet build dotnet && node dotnet/OpenTube.Tests/test-obs-cold.cjs   (needs ffmpeg)
// OBS into a computer that doesn't have the channel yet (just started, its chains gone): it loads the channel from
// the network first, then goes live. The live block reaches the network (the owner's browser sees it), the stream's
// video goes out from its start (queued while the channel loaded), and the stream's end is posted too.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os'), crypto = require('crypto');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-cold-'));
const N = 8897, RT = 1946, TRACKER = `ws://127.0.0.1:${N}/tracker`, BASE = `http://localhost:${N}/`, Q = `?folder=browser&trackers=${encodeURIComponent(TRACKER)}`;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return ((Date.now() - t0) / 1000).toFixed(1); await new Promise(r => setTimeout(r, 300)); } throw new Error('timeout'); };
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let P, lines = []; const start = (grant) => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, `--videos=${W}/videos`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', `--obs-port=${RT}`, '--site-dir=' + SITE, ...(grant ? ['--grant=' + grant] : [])], { env: { ...process.env, PAY_DEBUG: '1' } });
  P.stdout.on('data', d => { for (const l of String(d).split('\n')) { lines.push(l); if (/\[host\]|\[obs\]|\[pay|\[live\]|rror|fail/.test(l)) log('[C#]', l.trim().replace(/^\d\d:\d\d:\d\d /, '').slice(0, 170)); } });
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); return; } catch { execSync('sleep 0.5'); } } };
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
(async () => {
  start();
  const ctxs = []; const open = async (n, q) => { const ctx = await chromium.launchPersistentContext(W + '/b-' + n, { headless: true }); ctxs.push(ctx); const p = ctx.pages()[0]; p.on('pageerror', e => log(n, 'PAGEERROR', e.message.slice(0, 120))); await p.goto(BASE + q); await p.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); return p; };
  const A = await open('A', Q + '#/you/channels'); await A.waitForTimeout(800);
  await A.fill('input[placeholder=Name]', 'Host Channel'); await A.click('text=Create & register');
  await A.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await A.evaluate(() => location.hash.split('/')[2]);
  await until(A, (c) => opentube.engine.directory.has(c), chain); log('channel registered');
  const user = await A.evaluate(() => opentube.me.pub);
  const grant = await A.evaluate(async () => { const g = { v: 1, kind: 'nodegrant', user: opentube.me.pub, at: Date.now(), label: '' }; g.sig = await opentube.me.sign(OpenTubeCore.nodeGrantPreimage(g)); return btoa(JSON.stringify(g)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); });
  const gone = new Promise(r => P.on('exit', r)); P.kill('SIGKILL'); await gone; execSync('sleep 1');
  fs.writeFileSync(W + '/node/obs.json', JSON.stringify({ profiles: { [user]: { key: 'host-key', channel: chain, title: 'Hosted show' } }, users: {} }));
  start(grant);
  const nodeKey = JSON.parse(execSync(`curl -s localhost:${N}/status`)).node;
  await until(A, (k) => opentube.engine.nodes.has(k), nodeKey, 90000);
  await A.evaluate(async ({ c, k }) => { await opentube.engine.append(c, { type: 'relay', key: k, on: true }); }, { c: chain, k: nodeKey });
  for (let i = 0; i < 120; i++) { const s = JSON.parse(execSync(`curl -s localhost:${N}/status`)); if ((s.obs?.linked || []).includes(chain)) break; await new Promise(r => setTimeout(r, 500)); }
  // the cold start: the computer forgets every chain (keeps its key, grant and OBS settings), and OBS starts at once
  { const gone = new Promise(r => P.on('exit', r)); P.kill('SIGKILL'); await gone; }
  fs.rmSync(W + '/node/blocks.jsonl', { force: true }); lines = [];
  start(grant);
  const t0 = Date.now();
  const ff = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-re', '-f', 'lavfi', '-i', 'testsrc=size=320x240:rate=30', '-f', 'lavfi', '-i', 'sine=frequency=440', '-t', '25', '-c:v', 'libx264', '-preset', 'veryfast', '-g', '60', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-f', 'flv', `rtmp://localhost:${RT}/live/host-key`]);
  const ffDone = new Promise(r => ff.on('exit', r));
  const lb = await A.evaluate(async (c) => { for (let i = 0; i < 120; i++) { const b = [...(opentube.engine.chains.get(c)?.blocks.values() || [])].find(b => b.payload.type === 'live'); if (b) return b; await new Promise(r => setTimeout(r, 500)); } return null; }, chain);
  check(!!lb, `the live block reaches the network after a cold start: ${lb ? `“${lb.payload.title}” after ${((Date.now() - t0) / 1000).toFixed(1)} s` : 'never'}`);
  check(lines.some(l => /loading the channel before going live/.test(l)) || lines.some(l => /\[obs\] live on/.test(l)), 'the computer waited for the channel instead of giving up');
  const seg0 = lines.find(l => /couldn’t go live/.test(l)); check(!seg0, 'no "couldn’t go live" ' + (seg0 || ''));
  await ffDone;
  const end = await A.evaluate(async ({ c, s }) => { for (let i = 0; i < 60; i++) { const b = [...(opentube.engine.chains.get(c)?.blocks.values() || [])].find(b => b.payload.type === 'liveend' && b.payload.stream === s); if (b) return b.payload; await new Promise(r => setTimeout(r, 500)); } return null; }, { c: chain, s: lb?.payload.stream });
  check(!!end, `the stream's end is posted too${end?.rec ? ` (recording ${end.rec.size} bytes: the video from its very start)` : ''}`);
  check((end?.rec?.size || 0) > 200000, 'the recording holds the whole stream (nothing was lost while the channel loaded)');
  for (const c of ctxs) await c.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); P?.kill(); process.exit(1); });
