// Run: node dotnet/OpenTube.Tests/test-uno-upload.cjs <Uno desktop bin dir>   (needs ffmpeg, Xvfb)
// Adding a video in the Uno app (desktop; the same code as Android): the app makes its channel and earns starter
// coins (test setup), a local computer runs as that person, then the upload screen publishes a clip step by step and
// sends it to the computer, which ends up with an identical copy; the app opens the new video.
const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os'), crypto = require('crypto');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-uno-upload-')), N = 8971, UNO = path.resolve(process.argv[2]);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
const procs = []; process.on('exit', () => { for (const p of procs) try { p.kill(); } catch {} });
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let P; const startNode = (grant) => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, `--videos=${W}/videos`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', '--site-dir=' + SITE, ...(grant ? ['--grant=' + grant] : [])]); procs.push(P);
  P.stdout.on('data', d => { for (const l of String(d).split('\n')) if (/\[host\]|rror/.test(l)) log('[C#]', l.trim().slice(0, 150)); });
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); return; } catch { execSync('sleep 0.5'); } }
};
const X = spawn('Xvfb', [':95', '-screen', '0', '412x900x24']); procs.push(X);
let U, ulog = ''; const startApp = (env) => {
  ulog = ''; U = spawn('dotnet', [UNO + '/OpenTube.Uno.dll'], { cwd: UNO, env: { ...process.env, DISPLAY: ':95', OPENTUBE_SITE: `http://localhost:${N}`, OPENTUBE_DATA: W + '/app', OPENTUBE_WINDOW: '412x900', ...env } }); procs.push(U);
  U.stdout.on('data', d => ulog += d); U.stderr.on('data', d => ulog += d);
};
const until = async (fn, ms) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (fn()) return true; await new Promise(r => setTimeout(r, 500)); } return false; };
(async () => {
  startNode();
  startApp({ OPENTUBE_TEST: 'setup' });
  check(await until(() => /\[test\] setup ready/.test(ulog), 300000), 'the app makes its channel and earns starter coins');
  const setup = JSON.parse(fs.readFileSync(W + '/app/test-setup.json', 'utf8'));
  // wait for the starter coins to count (a main-chain block), then the computer runs as this person
  await until(() => false, 15000);
  const gone = new Promise(r => P.on('exit', r)); P.kill('SIGKILL'); await gone; execSync('sleep 1'); startNode(setup.grant);
  U.kill(); await new Promise(r => setTimeout(r, 1500));
  const clip = W + '/clip.mp4';
  execSync(`ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc=size=640x360:rate=30 -f lavfi -i sine=frequency=440 -t 20 -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -movflags +faststart ${clip}`);
  // the computer is known to the app (it checks in), then the upload screen publishes and sends it there
  startApp({ OPENTUBE_START: 'upload:' + setup.chain, OPENTUBE_OPEN_VIDEO: clip, OPENTUBE_AUTOPUBLISH: '1', OPENTUBE_WAIT_COMPUTER: '1' });
  const ok = await until(() => /\[upload\] published/.test(ulog) || /Couldn’t publish/.test(ulog), 300000);
  log('upload log:', ulog.split('\n').filter(l => /\[upload\]/.test(l)).slice(-6).join(' | ').slice(0, 900));
  check(ok && /\[upload\] published/.test(ulog), 'the upload screen publishes the video');
  check(/\[upload\] Sending it to/.test(ulog), 'it sends it to the computer, with progress');
  const hash = (f) => crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex');
  const kept = []; const walk = (d) => { if (!fs.existsSync(d)) return; for (const e of fs.readdirSync(d, { withFileTypes: true })) { const p = d + '/' + e.name; if (e.isDirectory()) walk(p); else if (e.name === 'clip.mp4') kept.push(p); } }; walk(W + '/videos');
  check(kept.some(f => hash(f) === hash(clip)), 'the computer keeps an identical copy');
  await new Promise(r => setTimeout(r, 3000)); execSync(`DISPLAY=:95 import -window root ${process.env.SHOTS || W}/uno-upload-done.png`);
  process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
