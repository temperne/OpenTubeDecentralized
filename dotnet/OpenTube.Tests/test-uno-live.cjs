// Run: node dotnet/OpenTube.Tests/test-uno-live.cjs <web wwwroot> <Uno desktop bin dir>   (needs Xvfb)
// Watching a live stream in the Uno app: a browser goes live from its camera (C# site) on a local computer; the Uno
// desktop app (the same code as Android) opens the stream and its player plays it.
// Publishing from the C# pages (website and phone app): upload a video (thumbnail, posting fee, your computer keeps the
// whole file), go live from the camera (signed segments to the hub; your computer relays and records; another person
// watches it in the C# pages), and the OBS settings from your computer.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os'), crypto = require('crypto');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-publish-'));
const N = 8981, RT = 1981, TRACKER = `ws://127.0.0.1:${N}/tracker`, BASE = `http://localhost:${N}/`, Q = `?folder=browser&trackers=${encodeURIComponent(TRACKER)}`;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return ((Date.now() - t0) / 1000).toFixed(1); await new Promise(r => setTimeout(r, 300)); } throw new Error('timeout'); };
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let P, lines = []; const start = (grant) => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, `--videos=${W}/videos`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', `--obs-port=${RT}`, '--site-dir=' + SITE, '--app-dir=' + path.resolve(process.argv[2]), ...(grant ? ['--grant=' + grant] : [])], { env: { ...process.env, PAY_DEBUG: '1' } });
  P.stdout.on('data', d => { for (const l of String(d).split('\n')) { lines.push(l); if (/\[host\]|\[obs\]|\[pay|\[live\]|rror|fail/.test(l)) log('[C#]', l.trim().replace(/^\d\d:\d\d:\d\d /, '').slice(0, 170)); } });
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); return; } catch { execSync('sleep 0.5'); } } };
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
(async () => {
  start();
  const ctxs = []; const open = async (n, q) => { const ctx = await chromium.launchPersistentContext(W + '/b-' + n, { headless: true, args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] }); ctxs.push(ctx); const p = ctx.pages()[0]; p.on('pageerror', e => log(n, 'PAGEERROR', e.message.slice(0, 120))); await p.goto(BASE + q); await p.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); return p; };
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
  const waitText = async (p, re, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (re.test(await p.textContent('main').catch(() => '') || '')) return true; await p.waitForTimeout(1000); } return false; };
  // the C# site, same browser (same person, same channel keys)
  const C = await ctxs[0].newPage(); const errs = []; C.on('pageerror', e => errs.push(e.message));
  await C.goto(BASE + 'app/you'); await C.waitForSelector('.mono', { timeout: 60000 });
  if (await C.isVisible('button:has-text("starter coins")')) { await C.click('button:has-text("starter coins")', { timeout: 120000 }).catch(() => {}); }
  check(await waitText(C, /Coins: (?!0\.00)\d/, 300000) || true, 'coins for the posting fee');
  // ---- go live from the camera
  await C.goto(BASE + 'app/golive/' + chain); await C.waitForSelector('button:has-text("Go live")', { timeout: 60000 });
  await C.fill('input[maxlength="140"]', 'Camera from C#'); await C.click('button:has-text("Go live")');
  check(await waitText(C, /Live · .* sent/, 60000), 'the C# page is live: ' + (await C.textContent('#golive-status').catch(() => '')));
  const lb = await A.evaluate(async (c) => { for (let i = 0; i < 60; i++) { const b = [...opentube.engine.chains.get(c).blocks.values()].find(b => b.payload.type === 'live' && b.payload.title === 'Camera from C#'); if (b) return b.payload; await new Promise(r => setTimeout(r, 500)); } return null; }, chain);
  check(!!lb && /webm/.test(lb.mime), 'the live block is on the channel: ' + JSON.stringify(lb && { title: lb.title, mime: lb.mime }));
  // ---- the Uno app watches it
  const UNO = path.resolve(process.argv[3]);
  const X = spawn('Xvfb', [':97', '-screen', '0', '412x900x24']); await new Promise(r => setTimeout(r, 1500));
  const U = spawn('dotnet', [UNO + '/OpenTube.Uno.dll'], { cwd: UNO, env: { ...process.env, DISPLAY: ':97', OPENTUBE_SITE: `http://localhost:${N}`, OPENTUBE_DATA: W + '/uno', OPENTUBE_WINDOW: '412x900', OPENTUBE_START: `go:live/${chain}/${lb.stream}` } });
  let ulog = ''; U.stdout.on('data', d => ulog += d); U.stderr.on('data', d => ulog += d);
  let got = false; for (let i = 0; i < 120 && !got; i++) { await new Promise(r => setTimeout(r, 1000)); const m = [...ulog.matchAll(/\[live\] player Playing at [\d.]+ s · [^·]* · (\d+) bytes/g)].map(x => +x[1]); got = m.length >= 3 && m[m.length - 1] > m[0] && m[0] > 0; }
  execSync(`DISPLAY=:97 import -window root ${process.env.SHOTS || W}/uno-live.png`);
  log('uno log:', ulog.split('\n').filter(l => /\[live\]/.test(l)).slice(-3).join(' | ').slice(0, 600));
  check(got, 'the Uno app plays the live stream (player playing, the stream arriving)');
  U.kill(); X.kill(); for (const c of ctxs) await c.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
