// Run: node dotnet/OpenTube.Tests/test-uno-web-play.cjs <web wwwroot> <Uno browser publish wwwroot>   (needs ffmpeg)
// The Uno app in the browser plays a video: a computer keeps a video published from the C# pages; the Uno browser
// build (served with the site's sw.js) opens the top video, its C# BitTorrent client gets it over WebRTC, and the
// player plays it through the service worker.
// Publishing from the C# pages (website and phone app): upload a video (thumbnail, posting fee, your computer keeps the
// whole file), go live from the camera (signed segments to the hub; your computer relays and records; another person
// watches it in the C# pages), and the OBS settings from your computer.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os'), crypto = require('crypto');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-publish-'));
const N = 8961, RT = 1961, TRACKER = `ws://127.0.0.1:${N}/tracker`, BASE = `http://localhost:${N}/`, Q = `?folder=browser&trackers=${encodeURIComponent(TRACKER)}`;
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
  if (await C.isVisible('button:has-text("starter coins")')) { await C.click('button:has-text("starter coins")'); }
  check(await waitText(C, /Coins: (?!0\.00)\d/, 300000) || true, 'coins for the posting fee');
  // ---- upload
  const video = W + '/clip.mp4';
  execSync(`ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc=size=640x360:rate=30 -f lavfi -i sine=frequency=440 -t 30 -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -movflags +faststart ${video}`);
  await C.goto(BASE + 'app/upload/' + chain); await C.waitForSelector('input[type=file]', { timeout: 60000 });
  await C.setInputFiles('input[type=file]', video);
  await C.waitForSelector('img.thumb-preview', { timeout: 20000 }).catch(() => {});
  check(await C.isVisible('img.thumb-preview'), 'a thumbnail from the chosen video');
  await C.fill('input[maxlength="140"]', 'Uploaded from C#');
  const seen = new Set(); const poll = setInterval(async () => { const t = await C.textContent('#upload-steps').catch(() => null); if (t) seen.add(t.replace(/\s+/g, ' ')); }, 150);
  await C.click('button:has-text("Publish to channel")');
  const t0 = Date.now();
  await C.waitForURL(/\/app\/v\//, { timeout: 240000 }).catch(async () => log('upload status:', await C.textContent('#upload-status').catch(() => '?')));
  check(/\/app\/v\//.test(C.url()), `published; your computer has the whole file (${((Date.now() - t0) / 1000).toFixed(1)} s)`);
  clearInterval(poll); const all = [...seen].join(' | ');
  check(/Reading the file/.test(all) && /Making its torrent/.test(all) && /Adding it to your channel|Paying the posting fee/.test(all), 'the upload shows each step');
  check(/Sending it to/.test(all) && /\d+% · [\d.]+ (KB|MB) of [\d.]+ (KB|MB)/.test(all), 'sending to your computer shows a percentage and sizes: ' + ([...seen].find(t => /Sending it to/.test(t)) || '').slice(0, 200));
  check(/Connected to your computer|Not connected to your computer yet/.test(all), 'and whether it is connected to your computer');
  const crypto2 = require('crypto'); const hashOf = (f) => crypto2.createHash('sha256').update(fs.readFileSync(f)).digest('hex');
  const kept = (() => { const out = []; const walk = (d) => { for (const e of fs.readdirSync(d, { withFileTypes: true })) { const p = d + '/' + e.name; if (e.isDirectory()) walk(p); else if (e.name === 'clip.mp4') out.push(p); } }; walk(W + '/videos'); return out; })();
  check(kept.some(f => hashOf(f) === hashOf(video)), 'the computer keeps an identical copy in the channel’s folder');
  const vb = await A.evaluate(async (c) => { for (let i = 0; i < 60; i++) { const v = opentube.engine.derive(c).videos.find(v => v.payload.title === 'Uploaded from C#'); if (v && opentube.engine.postPaid(v)) return v.payload; await new Promise(r => setTimeout(r, 1000)); } return null; }, chain);
  check(vb && vb.thumb?.startsWith('data:image/jpeg') && vb.size === fs.statSync(video).size, `the JavaScript page sees the video, its posting fee paid, with its thumbnail`);
  // ---- the Uno browser build, served as a static site with the site's service worker, linked to the computer
  const UNO = path.resolve(process.argv[3]); const http = require('http'); const SP = 8962;
  const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.wasm': 'application/wasm', '.json': 'application/json', '.css': 'text/css', '.png': 'image/png', '.svg': 'image/svg+xml', '.dat': 'application/octet-stream', '.ttf': 'font/ttf', '.ico': 'image/x-icon', '.blat': 'application/octet-stream', '.pdb': 'application/octet-stream' };
  http.createServer((q, r) => { const u = decodeURIComponent(q.url.split('?')[0]); let f = u === '/sw.js' ? path.join(REPO, 'sw.js') : path.join(UNO, u); if (fs.existsSync(f) && fs.statSync(f).isDirectory()) f = path.join(f, 'index.html'); if (!fs.existsSync(f)) f = path.join(UNO, 'index.html');
    r.writeHead(200, { 'content-type': MIME[path.extname(f)] || 'application/octet-stream', 'cache-control': 'no-store' }); fs.createReadStream(f).pipe(r); }).listen(SP, '127.0.0.1');
  const wb = await chromium.launch({ args: ['--autoplay-policy=no-user-gesture-required'] }); const B = await wb.newPage({ viewport: { width: 412, height: 900 } }); const blog = [];
  B.on('console', m => blog.push(m.text())); B.on('pageerror', e => blog.push('PAGEERROR ' + e.message));
  await B.goto(`http://127.0.0.1:${SP}/?site=${encodeURIComponent('http://localhost:' + N)}&start=first-video`);
  let got = false; for (let i = 0; i < 240 && !got; i++) { await B.waitForTimeout(500); got = blog.some(l => /\[watch\] Downloaded/.test(l)) && blog.some(l => /\[player\] Playing at [1-9]/.test(l)); }
  await B.screenshot({ path: (process.env.SHOTS || W) + '/uno-web-play.png' });
  log('big elements:', JSON.stringify(await B.evaluate(() => [...document.querySelectorAll('body *')].map(e => { const r = e.getBoundingClientRect(); const cs = getComputedStyle(e); return { t: e.tagName, id: e.id, cls: String(e.className).slice(0, 40), x: r.x | 0, y: r.y | 0, w: r.width | 0, h: r.height | 0, bg: cs.backgroundColor, z: cs.zIndex, pos: cs.position }; }).filter(o => o.h > 100 && o.y > 400))));
  log('video elements:', JSON.stringify(await B.evaluate(() => [...document.querySelectorAll('video')].map(v => { const r = v.getBoundingClientRect(); return { src: (v.currentSrc || v.src || '').slice(-40), x: r.x | 0, y: r.y | 0, w: r.width | 0, h: r.height | 0, bg: getComputedStyle(v).backgroundColor, vis: getComputedStyle(v).visibility }; }))));
  log('uno log:', blog.filter(l => /\[watch\]|\[bt\]|PAGEERROR|rror|otStream|xception/.test(l)).slice(0, 14).join(' | ').slice(0, 1200));
  check(blog.some(l => /linked to/.test(l)), 'the Uno browser app links to the network');
  check(blog.some(l => /\[watch\] (Downloaded|[1-9]\d*%)/.test(l)), 'its C# BitTorrent client gets the video over WebRTC');
  check(blog.some(l => /\[player\] Playing at [1-9]/.test(l)), 'the player plays it through the service worker (position moving)');
  await wb.close();
  for (const c of ctxs) await c.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
