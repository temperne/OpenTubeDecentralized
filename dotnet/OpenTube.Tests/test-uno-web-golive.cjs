// Run: node dotnet/OpenTube.Tests/test-uno-web-golive.cjs <web wwwroot (viewer)> <Uno browser publish wwwroot>
// Going live from the camera in the Uno app in a browser: the app makes its channel (test setup) and goes live from a
// (fake) camera; another person watches it on the site and it plays.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os'), http = require('http');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-uno-golive-')), N = 8991, SP = 8992;
const VIEWER = path.resolve(process.argv[2]), UNO = path.resolve(process.argv[3]);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
const procs = []; process.on('exit', () => { for (const p of procs) try { p.kill(); } catch {} });
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
const P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, `--videos=${W}/videos`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', '--site-dir=' + SITE, '--app-dir=' + VIEWER]); procs.push(P);
P.stdout.on('data', d => { for (const l of String(d).split('\n')) if (/\[live\]|rror/.test(l)) log('[C#]', l.trim().slice(0, 150)); });
for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); break; } catch { execSync('sleep 0.5'); } }
// the Uno app, as a static site with the site's service worker
const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.wasm': 'application/wasm', '.json': 'application/json', '.css': 'text/css', '.png': 'image/png', '.svg': 'image/svg+xml', '.dat': 'application/octet-stream', '.ttf': 'font/ttf', '.ico': 'image/x-icon', '.blat': 'application/octet-stream', '.pdb': 'application/octet-stream' };
http.createServer((q, r) => { const u = decodeURIComponent(q.url.split('?')[0]); let f = u === '/sw.js' ? path.join(REPO, 'sw.js') : path.join(UNO, u); if (fs.existsSync(f) && fs.statSync(f).isDirectory()) f = path.join(f, 'index.html'); if (!fs.existsSync(f)) f = path.join(UNO, 'index.html');
  r.writeHead(200, { 'content-type': MIME[path.extname(f)] || 'application/octet-stream', 'cache-control': 'no-store' }); fs.createReadStream(f).pipe(r); }).listen(SP, '127.0.0.1');
(async () => {
  const b = await chromium.launch({ args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream', '--autoplay-policy=no-user-gesture-required'] });
  const ctx = await b.newContext({ viewport: { width: 412, height: 900 }, permissions: ['camera', 'microphone'] }); const A = await ctx.newPage(); const alog = [];
  A.on('console', m => alog.push(m.text())); A.on('pageerror', e => alog.push('PAGEERROR ' + e.message));
  await A.goto(`http://127.0.0.1:${SP}/?site=${encodeURIComponent('http://localhost:' + N)}&test=setup&start=golive-auto`);
  const wait = async (re, ms) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const l = alog.find(x => re.test(x)); if (l) return l; await A.waitForTimeout(500); } return null; };
  const setup = await wait(/\[test\] setup ready: [0-9a-f]{64}/, 300000);
  check(!!setup, 'the app makes its channel (test setup)'); if (!setup) { log(alog.filter(l => /rror|test/.test(l)).slice(-5).join(' | ')); process.exit(1); }
  const chain = /([0-9a-f]{64})/.exec(setup)[1];
  const live = await wait(/\[golive\] live [0-9a-f]{64}/, 240000);
  log('app log:', alog.filter(l => /golive|rror|camera/i.test(l)).slice(-5).join(' | ').slice(0, 600));
  check(!!live, 'the app goes live from the camera'); if (!live) process.exit(1);
  const stream = /([0-9a-f]{64})/.exec(live)[1];
  await A.waitForTimeout(4000); await A.screenshot({ path: (process.env.SHOTS || W) + '/uno-web-golive.png' });
  // another person watches it (the site's live page)
  const V = await (await chromium.launch({ args: ['--autoplay-policy=no-user-gesture-required'] })).newPage();
  await V.goto(`http://localhost:${N}/app/live/${chain}/${stream}`);
  const played = await V.waitForFunction(() => { const v = document.querySelector('video'); return v && v.readyState >= 2 && v.currentTime > 1; }, null, { timeout: 120000 }).then(() => true, () => false);
  check(played, 'another person watches it and it plays: ' + (await V.textContent('#live-status').catch(() => '')));
  process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
