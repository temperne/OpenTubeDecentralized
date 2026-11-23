// Run: dotnet build dotnet && node dotnet/OpenTube.Tests/test-tree.cjs   (needs ffmpeg)
// The segment tree from a .NET node: OBS streams to computer O, viewers watch through the live hub on computer H, and
// H hangs them under O, which sends them the segments over WebRTC data channels (SIPSorcery ↔ the browser).
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-tree-'));
const H = 8892, O = 8893, RT = 1947, BASE = `http://localhost:${H}/`;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return ((Date.now() - t0) / 1000).toFixed(1); await new Promise(r => setTimeout(r, 300)); } throw new Error('timeout'); };
// a site folder with no seed entry nodes: these two computers are the whole network
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
const procs = {}; let oLines = [];
const run = (name, port, extra) => {
  const p = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${port}`, `--data=${W}/${name}`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--site-dir=' + SITE, ...extra]);
  p.stdout.on('data', d => { for (const l of String(d).split('\n')) { if (name === 'O') oLines.push(l); if (/\[obs\]|\[live\]|rror|fail/.test(l)) log(`[${name}]`, l.trim().replace(/^\d\d:\d\d:\d\d /, '').slice(0, 170)); } });
  procs[name] = p; for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${port}/status`); return p; } catch { execSync('sleep 0.5'); } } };
const stop = async (name) => { const p = procs[name]; const g = new Promise(r => p.on('exit', r)); p.kill('SIGKILL'); await g; execSync('sleep 1'); };
(async () => {
  run('H', H, ['--obs-port=0']);
  const ctxs = [];
  const open = async (n, q) => { const ctx = await chromium.launchPersistentContext(W + '/b-' + n, { headless: true, args: ['--autoplay-policy=no-user-gesture-required'] }); ctxs.push(ctx); const p = ctx.pages()[0]; p.on('pageerror', e => log(n, 'PAGEERROR', e.message.slice(0, 120))); await p.goto(BASE + q); await p.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); return p; };
  const A = await open('A', '?folder=browser#/you/channels'); await A.waitForTimeout(800);
  await A.fill('input[placeholder=Name]', 'Tree Channel'); await A.click('text=Create & register');
  await A.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await A.evaluate(() => location.hash.split('/')[2]);
  log('channel registered after', await until(A, (c) => opentube.engine.directory.has(c), chain), 's');
  const user = await A.evaluate(() => opentube.me.pub);
  const grant = await A.evaluate(async () => { const g = { v: 1, kind: 'nodegrant', user: opentube.me.pub, at: Date.now(), label: '' }; g.sig = await opentube.me.sign(OpenTubeCore.nodeGrantPreimage(g)); return btoa(JSON.stringify(g)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); });
  fs.mkdirSync(W + '/O', { recursive: true });
  fs.writeFileSync(W + '/O/obs.json', JSON.stringify({ profiles: { [user]: { key: 'tree-key', channel: chain, title: 'Tree test' } }, users: {} }));
  run('O', O, [`--obs-port=${RT}`, '--grant=' + grant, `--site=http://localhost:${H}`]);
  const nodeKey = JSON.parse(execSync(`curl -s localhost:${O}/status`)).node;
  await until(A, (k) => opentube.engine.nodes.has(k), nodeKey, 90000);
  await A.evaluate(async ({ c, k }) => { await opentube.engine.append(c, { type: 'relay', key: k, on: true }); }, { c: chain, k: nodeKey });
  for (let i = 0; i < 120; i++) { const s = JSON.parse(execSync(`curl -s localhost:${O}/status`)); if ((s.obs?.linked || []).includes(chain)) { log('O sees the link'); break; } await new Promise(r => setTimeout(r, 500)); }
  const ff = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-re', '-f', 'lavfi', '-i', 'testsrc=size=640x360:rate=30', '-f', 'lavfi', '-i', 'sine=frequency=440', '-t', '40', '-c:v', 'libx264', '-preset', 'veryfast', '-g', '60', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-f', 'flv', `rtmp://localhost:${RT}/live/tree-key`]);
  log('live on the channel after', await until(A, (c) => opentube.engine.derive(c)?.liveNow.length > 0, chain, 60000), 's');
  const lb = await A.evaluate((c) => opentube.engine.derive(c).liveNow[0], chain);
  const viewers = [];
  for (const n of ['V1', 'V2']) viewers.push(await open(n, `#/live/${chain}/${lb.hash}`));
  for (const [i, V] of viewers.entries()) log(`viewer ${i + 1} playing after`, await until(V, () => { const v = document.querySelector('video'); return v && v.currentTime > 1; }, null, 60000).catch(() => 'NO'), 's');
  await new Promise(r => setTimeout(r, 6000));
  const opened = oLines.filter(l => /gets the stream from this computer over WebRTC/.test(l)).length;
  log(opened >= 1 ? 'ok  ' : 'FAIL', `viewers fed by O over WebRTC data channels: ${opened}`);
  for (const [i, V] of viewers.entries()) log(`viewer ${i + 1} at`, await V.evaluate(() => document.querySelector('video')?.currentTime.toFixed(1)), 's');
  ff.kill(); for (const c of ctxs) await c.close(); for (const p of Object.values(procs)) p.kill(); process.exit(opened >= 1 ? 0 : 1);
})().catch(e => { console.error(e); for (const p of Object.values(procs)) try { p.kill(); } catch {} process.exit(1); });
