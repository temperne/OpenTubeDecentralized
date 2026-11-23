// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-play.cjs <dir>/wwwroot   (needs ffmpeg)
// Videos in the C# website: the browser's BitTorrent client is C# (WebAssembly); only WebRTC and the service worker's
// relay are JavaScript. A C# seeder shares a video on the node's tracker; the page finds it over WebRTC, the video
// element plays it while it downloads, and the bytes are identical.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os'), crypto = require('crypto');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-play-')), N = 8913, APP = path.resolve(process.argv[2]);
const D = REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll';
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
const procs = []; process.on('exit', () => { for (const p of procs) try { p.kill(); } catch {} });
(async () => {
  const video = W + '/clip.mp4';
  execSync(`ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc=size=640x360:rate=30 -f lavfi -i sine=frequency=440 -t 20 -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -movflags +faststart ${video}`);
  const P = spawn('dotnet', [D, '--service', `--port=${N}`, `--data=${W}/node`, '--no-host', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', '--no-obs', '--site-dir=' + SITE, '--app-dir=' + APP]); procs.push(P);
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); break; } catch { execSync('sleep 0.5'); } }
  const seed = spawn('dotnet', [D, 'torrent', 'seed', video, `--tracker=ws://localhost:${N}/tracker`]); procs.push(seed);
  const ih = await new Promise(r => seed.stdout.on('data', d => r(String(d).split(' ')[0].trim())));
  log('C# seeds', ih.slice(0, 12), fs.statSync(video).size, 'bytes');
  // (errors from public trackers this machine may not reach are left out)
  const b = await chromium.launch(); const p = await b.newPage(); const errs = [];
  p.on('pageerror', e => errs.push(e.message)); p.on('console', m => { if (m.type() === 'error' && !/favicon|WebSocket connection to 'wss:\/\/tracker/.test(m.text())) errs.push(m.text()); });
  const t0 = Date.now();
  await p.goto(`http://localhost:${N}/app/t/${ih}?name=clip.mp4`);
  // the first visit installs the service worker; the page asks to reload once if it isn't in control yet
  await p.waitForSelector('#play-status', { timeout: 60000 });
  if (/Reload/.test(await p.textContent('#play-status'))) { log('reloading once for the service worker'); await p.reload(); }
  const played = await p.waitForFunction(() => { const v = document.querySelector('video'); return v && v.readyState >= 3 && v.currentTime > 1.5; }, null, { timeout: 120000 }).then(() => true).catch(() => false);
  check(played, `the video plays in the page (C# BitTorrent over the browser's WebRTC) after ${((Date.now() - t0) / 1000).toFixed(1)} s`);
  log('status:', await p.textContent('#play-status').catch(() => '?'));
  const seek = await p.evaluate(async () => { const v = document.querySelector('video'); v.currentTime = 15; await new Promise(r => setTimeout(r, 3000)); return { t: v.currentTime, ready: v.readyState }; });
  check(seek.t >= 15 && seek.ready >= 2, `seeking works (at ${seek.t.toFixed(1)} s)`);
  const h = await p.evaluate(async (ih) => { const r = await fetch(`/webtorrent/${ih}/clip.mp4`); const buf = await r.arrayBuffer(); return [...new Uint8Array(await crypto.subtle.digest('SHA-256', buf))].map(x => x.toString(16).padStart(2, '0')).join(''); }, ih);
  check(h === crypto.createHash('sha256').update(fs.readFileSync(video)).digest('hex'), 'the bytes the page got are identical to the file');
  check(!/webtorrent\.min\.js/.test(await p.evaluate(() => [...document.scripts].map(s => s.src).join(' '))), 'no WebTorrent (JavaScript) loaded: the torrent client is C#');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await b.close(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
