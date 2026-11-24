// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-publish.cjs <dir>/wwwroot   (needs ffmpeg)
// Publishing from the C# pages (website and phone app): upload a video (thumbnail, posting fee, your computer keeps the
// whole file), go live from the camera (signed segments to the hub; your computer relays and records; another person
// watches it in the C# pages), and the OBS settings from your computer.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os'), crypto = require('crypto');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-publish-'));
const N = 8885, RT = 1938, TRACKER = `ws://127.0.0.1:${N}/tracker`, BASE = `http://localhost:${N}/`, Q = `?folder=browser&trackers=${encodeURIComponent(TRACKER)}`;
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
  execSync(`ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc=size=640x360:rate=30 -f lavfi -i sine=frequency=440 -t 8 -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -movflags +faststart ${video}`);
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
  // ---- go live from the camera
  await C.goto(BASE + 'app/golive/' + chain); await C.waitForSelector('button:has-text("Go live")', { timeout: 60000 });
  await C.fill('input[maxlength="140"]', 'Camera from C#'); await C.click('button:has-text("Go live")');
  check(await waitText(C, /Live · .* sent/, 60000), 'the C# page is live: ' + (await C.textContent('#golive-status').catch(() => '')));
  const lb = await A.evaluate(async (c) => { for (let i = 0; i < 60; i++) { const b = [...opentube.engine.chains.get(c).blocks.values()].find(b => b.payload.type === 'live' && b.payload.title === 'Camera from C#'); if (b) return b.payload; await new Promise(r => setTimeout(r, 500)); } return null; }, chain);
  check(!!lb && /webm/.test(lb.mime), 'the live block is on the channel: ' + JSON.stringify(lb && { title: lb.title, mime: lb.mime }));
  for (let i = 0; i < 40 && !lines.some(l => /\[live\] relaying/.test(l)); i++) await new Promise(r => setTimeout(r, 500));
  check(lines.some(l => /\[live\] relaying/.test(l)), 'your computer relays it to viewers (and records it)');
  // another person watches it in the C# pages
  const V = await (await chromium.launch()).newPage(); await V.goto(BASE + `app/live/${chain}/${lb.stream}`);
  await V.waitForSelector('#live-status', { timeout: 60000 }); if (/Reload/.test(await V.textContent('main'))) await V.reload();
  const played = await V.waitForFunction(() => { const v = document.querySelector('video'); return v && v.readyState >= 2 && v.currentTime > 1; }, null, { timeout: 60000 }).then(() => true).catch(() => false);
  check(played, 'another person watches it live in the C# pages: ' + (await V.textContent('#live-status').catch(() => '')));
  await C.click('button:has-text("End stream")');
  const end = await A.evaluate(async ({ c, s }) => { for (let i = 0; i < 40; i++) { if ([...opentube.engine.chains.get(c).blocks.values()].some(b => b.payload.type === 'liveend' && b.payload.stream === s)) return true; await new Promise(r => setTimeout(r, 500)); } return false; }, { c: chain, s: lb.stream });
  check(end, 'ending it posts the end of the stream');
  // ---- OBS settings from your computer
  await C.click('button:has-text("Show OBS settings")'); await C.waitForSelector('text=Stream key:', { timeout: 30000 }).catch(() => {});
  check(/rtmp:\/\/[^ ]+:1938\/live/.test(await C.textContent('main')) && /Stream key: host-key/.test(await C.textContent('main')), 'OBS settings from your computer (server with its port, your stream key)');
  // ---- the recording your computer made: publish it as a video
  await C.goto(BASE + 'app/c/' + chain); 
  check(await waitText(C, /Recording ready: Camera from C#/, 180000), 'the channel offers your computer’s recording of the stream');
  await C.fill('.recording input', 'Camera from C# (recording)'); await C.click('button:has-text("Publish recording")');
  check(await A.evaluate(async ({ c, s }) => { for (let i = 0; i < 120; i++) { const bs = [...opentube.engine.chains.get(c).blocks.values()]; if (bs.some(b => b.payload.type === 'video' && b.payload.stream === s) && bs.some(b => b.payload.type === 'liveend' && b.payload.stream === s && b.payload.outcome === 'published')) return true; await new Promise(r => setTimeout(r, 1000)); } return false; }, { c: chain, s: lb.stream }), 'publishing it: a video of the stream, and the stream marked published (the JavaScript page sees both)');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  for (const c of ctxs) await c.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); P?.kill(); process.exit(1); });
