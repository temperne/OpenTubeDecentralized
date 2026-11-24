// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-static-live.cjs <dir>/wwwroot
// The real deployment's shape: the site is static files only (no node behind it), and one OpenTube computer is an
// entry node (listed in seeds.json). A browser goes live from its camera on the C# site; another browser (another
// person) opens the stream and it plays. Nothing goes through the site.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-static-')), N = 8911, S = 8912, APP = path.resolve(process.argv[2]);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
// the static site: /app (the C# site) and seeds.json naming the entry node
const SITE = W + '/site'; fs.mkdirSync(SITE); fs.cpSync(APP, SITE, { recursive: true }); fs.copyFileSync(path.join(REPO, 'sw.js'), SITE + '/sw.js');   // the streaming service worker, as the Cloudflare build adds it
fs.writeFileSync(SITE + '/seeds.json', JSON.stringify({ entry: [`http://127.0.0.1:${N}`] }));
const procs = []; process.on('exit', () => { for (const p of procs) try { p.kill(); } catch {} });
// static files with the single-page fallback (as Cloudflare serves the site): unknown paths under /app/ get app/index.html
const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.wasm': 'application/wasm', '.json': 'application/json', '.css': 'text/css', '.png': 'image/png', '.svg': 'image/svg+xml', '.dat': 'application/octet-stream', '.ico': 'image/x-icon', '.webmanifest': 'application/manifest+json' };
require('http').createServer((q, r) => {
  let f = path.join(SITE, decodeURIComponent(q.url.split('?')[0])); if (fs.existsSync(f) && fs.statSync(f).isDirectory()) f = path.join(f, 'index.html');
  if (!fs.existsSync(f)) { if (q.url.startsWith('/app/')) f = SITE + '/app/index.html'; else { r.writeHead(404); return r.end(); } }
  r.writeHead(200, { 'content-type': MIME[path.extname(f)] || 'application/octet-stream', 'cache-control': 'no-store' }); fs.createReadStream(f).pipe(r);
}).listen(S, '127.0.0.1');
const NS = W + '/nsite'; fs.mkdirSync(NS); fs.writeFileSync(NS + '/seeds.json', '{"entry":[]}'); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(NS, f));
const node = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, `--videos=${W}/videos`, '--mine=all', '--no-tunnel', '--public=off', '--entry', '--no-ranking', '--no-mesh', '--site-dir=' + NS]); procs.push(node);
node.stdout.on('data', d => { for (const l of String(d).split('\n')) if (/\[live\]|hub|rror/.test(l)) log('[C#]', l.trim().slice(0, 160)); });
for (let i = 0; i < 60; i++) { try { execSync(`curl -sf 127.0.0.1:${N}/status`); execSync(`curl -sf 127.0.0.1:${S}/seeds.json`); break; } catch { execSync('sleep 0.5'); } }
const BASE = `http://127.0.0.1:${S}/app/`;
const waitText = async (p, re, ms = 60000, sel = 'main') => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (re.test(await p.textContent(sel).catch(() => '') || '')) return true; await p.waitForTimeout(1000); } return false; };
(async () => {
  const errs = [];
  const ca = await chromium.launchPersistentContext(W + '/a', { headless: true, args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] }); const A = ca.pages()[0]; A.on('pageerror', e => errs.push(e.message));
  A.on('response', r => { if (r.status() >= 400) log('[A] HTTP', r.status(), r.url()); }); A.on('requestfailed', r => log('[A] failed', r.url(), r.failure()?.errorText));
  A.on('console', m => { if (true) log('[A]', m.text().slice(0, 140)); });
  await A.goto(BASE + 'you/channels'); if (!await A.waitForSelector('input[placeholder=Name]', { timeout: 60000 }).then(() => true, () => false)) { log('PAGE:', (await A.textContent('body')).slice(0, 400)); log('URL', A.url()); }
  check(await waitText(A, /./, 1000, 'header a.net') && await A.waitForFunction(() => /Online|Limited/.test(document.querySelector('header a.net')?.textContent || ''), null, { timeout: 60000 }).then(() => true, () => false), 'the static site links to the entry node: ' + (await A.textContent('header a.net').catch(() => '')).trim());
  await A.fill('input[placeholder=Name]', 'Static Channel'); await A.click('button:has-text("Create & register")');
  await A.waitForURL(/\/app\/c\//, { timeout: 240000 }); const chain = A.url().split('/c/')[1].split(/[/?#]/)[0];
  check(await A.waitForFunction(() => !/being registered/.test(document.body.textContent), null, { timeout: 240000 }).then(() => true, () => false), 'channel registered through the entry node');
  await A.goto(BASE + 'golive/' + chain); await A.waitForSelector('button:has-text("● Go live")', { timeout: 60000 });
  await A.fill('input[maxlength="140"]', 'Static live'); await A.click('button:has-text("● Go live")');
  check(await waitText(A, /Live · .* sent/, 60000), 'live: ' + (await A.textContent('#golive-status').catch(() => '')));
  const st = await A.textContent('#golive-status'); check(st.includes(`hub: 127.0.0.1:${N}`), 'the stream is on the entry node’s hub, not the (static) site: ' + st);
  // another person, another browser
  const cb = await chromium.launchPersistentContext(W + '/b', { headless: true }); const B = cb.pages()[0]; B.on('pageerror', e => errs.push(e.message));
  await B.goto(BASE + 'c/' + chain);
  const href = await B.waitForSelector(`a[href^="live/${chain}/"]`, { timeout: 120000 }).then(e => e.getAttribute('href'), () => null);
  check(!!href, 'the viewer sees the stream live on the channel');
  if (href) await B.goto(BASE + href); else await B.goto(BASE + 'explore');
  const played = await B.waitForFunction(() => { const v = document.querySelector('video'); return v && v.readyState >= 2 && v.currentTime > 1; }, null, { timeout: 90000 }).then(() => true, () => false);
  check(played, 'it plays for the viewer: ' + (await B.textContent('#live-status').catch(() => '')));
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await ca.close(); await cb.close(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
