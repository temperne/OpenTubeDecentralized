// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-studio.cjs <dir>/wwwroot   (needs ffmpeg)
// The group studio in the C# pages (website and phone app): your computer receives OBS; on Go live, its group stream
// switches on, the layout and waiting room are set, a ticker is set, someone is invited (they get a key on that
// computer) and the invitation is cancelled. Every change goes to the computer, which reports it back.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-studio-')), N = 8895, RT = 1939, APP = path.resolve(process.argv[2]);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
let P; process.on('exit', () => { try { P?.kill(); } catch {} });
const start = (grant) => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, `--videos=${W}/videos`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', `--obs-port=${RT}`, '--site-dir=' + SITE, '--app-dir=' + APP, ...(grant ? ['--grant=' + grant] : [])]);
  P.stdout.on('data', d => { for (const l of String(d).split('\n')) if (/group|studio|rror/.test(l)) log('[C#]', l.trim().slice(0, 160)); });
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); return; } catch { execSync('sleep 0.5'); } }
};
const waitText = async (p, re, ms = 60000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (re.test(await p.textContent('main').catch(() => '') || '')) return true; await p.waitForTimeout(1000); } return false; };
(async () => {
  start();
  const BASE = `http://localhost:${N}/`; const errs = [];
  const cx = await chromium.launchPersistentContext(W + '/x', { headless: true }); const XJ = cx.pages()[0];
  await XJ.goto(BASE + '?folder=browser#/you/channels'); await XJ.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); await XJ.waitForTimeout(800);
  await XJ.fill('input[placeholder=Name]', 'Studio Channel'); await XJ.click('text=Create & register');
  await XJ.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 });
  const grant = await XJ.evaluate(async () => { const g = { v: 1, kind: 'nodegrant', user: opentube.me.pub, at: Date.now(), label: '' }; g.sig = await opentube.me.sign(OpenTubeCore.nodeGrantPreimage(g)); return btoa(JSON.stringify(g)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); });
  const gone = new Promise(r => P.on('exit', r)); P.kill('SIGKILL'); await gone; execSync('sleep 1'); start(grant);
  const guest = 'G'.repeat(42) + 'A';
  const X = await cx.newPage(); X.on('pageerror', e => errs.push(e.message));
  await X.goto(BASE + 'app/golive'); await X.waitForSelector('button:has-text("Show OBS settings")', { timeout: 120000 });
  await X.click('button:has-text("Show OBS settings")');
  check(await X.waitForSelector('.group-panel', { timeout: 60000 }).then(() => true, () => false), 'Go live shows your computer’s group stream');
  await X.waitForSelector('.group-panel input[type=checkbox]', { timeout: 60000 });
  await X.click('.group-panel label.check:has-text("Off: one stream") input');
  check(await waitText(X, /On: everyone connected is combined/, 30000), 'switching the group stream on (the computer says it’s on)');
  await X.click('.group-panel button:has-text("Spotlight")');
  check(await X.waitForSelector('.group-panel button.on:has-text("Spotlight")', { timeout: 30000 }).then(() => true, () => false), 'choosing the Spotlight layout');
  const waitBox = () => X.evaluate(() => [...document.querySelectorAll('.group-panel label.check')].find(l => /Waiting room/.test(l.textContent))?.querySelector('input').checked);
  check(await waitBox() === true, 'the waiting room is on (the default)');
  await X.click('.group-panel label.check:has-text("Waiting room") input');
  check(await X.waitForFunction(() => [...document.querySelectorAll('.group-panel label.check')].find(l => /Waiting room/.test(l.textContent))?.querySelector('input').checked === false, null, { timeout: 30000 }).then(() => true, () => false), 'turning the waiting room off (the computer says so)');
  await X.fill('.group-panel input[placeholder^="Ticker"]', 'Welcome to the show'); await X.click('.group-panel button:has-text("Set")');
  check(await X.waitForFunction(() => [...document.querySelectorAll('.group-panel label.check')].find(l => /show/.test(l.textContent) && l.closest('.row-actions')?.textContent.includes('Ticker'))?.querySelector('input').checked, null, { timeout: 30000 }).then(() => true, () => false), 'setting the ticker (shown)');
  await X.fill('.group-panel input[placeholder^="Their"]', guest); await X.click('.group-panel button:has-text("Invite")');
  check(await X.waitForSelector(`.group-panel a[href="u/${guest}"]`, { timeout: 30000 }).then(() => true, () => false), 'inviting someone: they’re listed as a guest');
  const obs = JSON.parse(fs.readFileSync(W + '/node/obs.json', 'utf8'));
  check(!!obs.users?.[guest], 'the computer gave them a stream key of their own');
  await X.click(`.group-panel .row-item:has(a[href="u/${guest}"]) button:has-text("Cancel invite")`);
  check(await X.waitForSelector(`.group-panel a[href="u/${guest}"]`, { state: 'detached', timeout: 30000 }).then(() => true, () => false), 'cancelling the invitation');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await cx.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
