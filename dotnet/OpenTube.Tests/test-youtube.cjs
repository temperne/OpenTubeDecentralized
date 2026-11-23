// Run: dotnet build dotnet && node dotnet/OpenTube.Tests/test-youtube.cjs   (needs ffmpeg)
// YouTube forwarding: OBS → the .NET node → "YouTube" (an ffmpeg RTMP server here, via YOUTUBE_RTMP), unchanged.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-yt-')), N = 8902, RT = 1949, YT = 1950, BASE = `http://localhost:${N}/`;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return true; await new Promise(r => setTimeout(r, 300)); } throw new Error('timeout'); };
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let P; const start = (grant) => { P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, '--no-host', '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', `--obs-port=${RT}`, '--site-dir=' + SITE, ...(grant ? ['--grant=' + grant] : [])], { env: { ...process.env, YOUTUBE_RTMP: `rtmp://127.0.0.1:${YT}/live2/` } });
  P.stdout.on('data', d => { for (const l of String(d).split('\n')) if (/youtube|\[obs\] (live|stream ended)|rror/.test(l)) log('[C#]', l.trim().replace(/^\d\d:\d\d:\d\d /, '').slice(0, 160)); });
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); return; } catch { execSync('sleep 0.5'); } } };
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
(async () => {
  start();
  const ctx = await chromium.launchPersistentContext(W + '/b', { headless: true }); const A = ctx.pages()[0];
  await A.goto(BASE + '?folder=browser#/you/channels'); await A.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); await A.waitForTimeout(800);
  await A.fill('input[placeholder=Name]', 'YT Channel'); await A.click('text=Create & register');
  await A.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await A.evaluate(() => location.hash.split('/')[2]);
  await until(A, (c) => opentube.engine.directory.has(c), chain);
  const user = await A.evaluate(() => opentube.me.pub);
  const grant = await A.evaluate(async () => { const g = { v: 1, kind: 'nodegrant', user: opentube.me.pub, at: Date.now(), label: '' }; g.sig = await opentube.me.sign(OpenTubeCore.nodeGrantPreimage(g)); return btoa(JSON.stringify(g)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); });
  const g = new Promise(r => P.on('exit', r)); P.kill('SIGKILL'); await g; execSync('sleep 1');
  fs.writeFileSync(W + '/node/obs.json', JSON.stringify({ profiles: { [user]: { key: 'yt-obs-key', channel: chain, title: 'To YouTube', youtube: { on: true, key: 'my-youtube-key' } } }, users: {} }));
  start(grant);
  const nodeKey = JSON.parse(execSync(`curl -s localhost:${N}/status`)).node;
  await until(A, (k) => opentube.engine.nodes.has(k), nodeKey, 90000);
  await A.evaluate(async ({ c, k }) => { await opentube.engine.append(c, { type: 'relay', key: k, on: true }); }, { c: chain, k: nodeKey });
  for (let i = 0; i < 120; i++) { const s = JSON.parse(execSync(`curl -s localhost:${N}/status`)); if ((s.obs?.linked || []).includes(chain)) break; await new Promise(r => setTimeout(r, 500)); }
  const out = W + '/youtube.flv';
  const yt = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-listen', '1', '-i', `rtmp://127.0.0.1:${YT}/live2/my-youtube-key`, '-c', 'copy', '-y', out]);
  await new Promise(r => setTimeout(r, 1500));
  execSync(`ffmpeg -hide_banner -loglevel error -re -f lavfi -i testsrc=size=640x360:rate=30 -f lavfi -i sine=frequency=440 -t 15 -c:v libx264 -preset veryfast -g 60 -pix_fmt yuv420p -c:a aac -f flv rtmp://localhost:${RT}/live/yt-obs-key`);
  log('OBS streamed 15 s');
  await new Promise(r => { const t = setTimeout(() => { yt.kill('SIGINT'); r(); }, 15000); yt.on('exit', () => { clearTimeout(t); r(); }); });
  const probe = fs.existsSync(out) ? execSync(`ffprobe -v error -show_entries stream=codec_name:format=duration -of csv=p=0 "${out}"`).toString().trim().split('\n') : [];
  log('YouTube received:', probe.join(' | '));
  const dur = parseFloat(probe.find(x => /^\d+\.\d+$/.test(x)) || '0');
  check(probe.some(x => /h264/.test(x)) && probe.some(x => /aac/.test(x)) && dur > 10, `the stream reached YouTube unchanged: H.264 + AAC, ${dur.toFixed(1)} s of 15`);
  await ctx.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); try { P.kill(); } catch {} process.exit(1); });
