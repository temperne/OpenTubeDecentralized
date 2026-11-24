// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-parity.cjs <dir>/wwwroot
// The pieces of the JavaScript page filled in by the last parity pass, in the C# pages: the header (👤 You, My
// channel(s), ＋ Add video, ● Go live, network status), Devices (info lines, Test connection, ⚡ Flash computers with a
// login sealed in C# to the computer's key and opened by the computer, Earned by your nodes), notification settings,
// upload tags and comment policy, You → Settings (not-interested tags), and the profile's older months.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-parity-')), N = 8897, APP = path.resolve(process.argv[2]);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
let P; process.on('exit', () => { try { P?.kill(); } catch {} });
const start = (grant) => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, `--videos=${W}/videos`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', '--site-dir=' + SITE, '--app-dir=' + APP, ...(grant ? ['--grant=' + grant] : [])]);
  P.stdout.on('data', d => { for (const l of String(d).split('\n')) if (/flash|rror/.test(l)) log('[C#]', l.trim().slice(0, 160)); });
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); return; } catch { execSync('sleep 0.5'); } }
};
const waitText = async (p, re, ms = 60000, sel = 'body') => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (re.test(await p.textContent(sel).catch(() => '') || '')) return true; await p.waitForTimeout(1000); } return false; };
(async () => {
  start();
  const BASE = `http://localhost:${N}/`; const errs = [];
  const cx = await chromium.launchPersistentContext(W + '/x', { headless: true }); const XJ = cx.pages()[0];
  await XJ.goto(BASE + '?folder=browser#/you/channels'); await XJ.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); await XJ.waitForTimeout(800);
  await XJ.fill('input[placeholder=Name]', 'Parity Channel'); await XJ.click('text=Create & register');
  await XJ.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await XJ.evaluate(() => location.hash.split('/')[2]);
  const grant = await XJ.evaluate(async () => { const g = { v: 1, kind: 'nodegrant', user: opentube.me.pub, at: Date.now(), label: '' }; g.sig = await opentube.me.sign(OpenTubeCore.nodeGrantPreimage(g)); return btoa(JSON.stringify(g)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); });
  const gone = new Promise(r => P.on('exit', r)); P.kill('SIGKILL'); await gone; execSync('sleep 1'); start(grant);
  const X = await cx.newPage(); X.on('pageerror', e => errs.push(e.message));
  // header
  await X.goto(BASE + 'app/'); await X.waitForSelector('header a[href="you"]', { timeout: 120000 });
  check(/👤|<img/.test(await X.innerHTML('header a[href="you"]')), 'the little person (or your picture) next to You');
  check(await X.waitForSelector('header a[href="mine"]', { timeout: 120000 }).then(() => true, () => false), 'My channel(s) in the header once you have one');
  check(await X.$('header a[href="upload"]') != null && await X.$('header a[href="golive"]') != null, '＋ Add video and ● Go live in the header');
  check(await waitText(X, /nodes|Online|Limited|Problem/, 30000, 'header a.net'), 'network status in the header: ' + (await X.textContent('header a.net').catch(() => '')).trim());
  await X.click('header a[href="mine"]'); check(await waitText(X, /Parity Channel/, 30000, 'main'), 'My channel(s) lists your channel');
  // Devices
  await X.goto(BASE + 'app/devices'); await X.waitForSelector('button:has-text("Test connection")', { timeout: 120000 });
  check(await waitText(X, /main #\d+/, 60000, 'main'), 'the computer’s info line (main chain height)');
  await X.click('button:has-text("Test connection")');
  check(await waitText(X, /through the network ✓/, 60000, 'main'), 'Test connection: ' + (await X.textContent('.test-result').catch(() => '')));
  check(await X.$('#earnings') != null && await waitText(X, /video served/, 5000, '#earnings'), 'Earned by your nodes panel');
  await X.click('button:has-text("⚡ Flash computers")');
  await X.waitForSelector('.flash select', { timeout: 60000 });
  await X.selectOption('.flash select', 'hetzner'); await X.fill('.flash textarea', 'test-token-123');
  await X.click('.flash button:has-text("Save (sealed for this computer)")');
  check(await waitText(X, /your login: Hetzner Cloud/, 60000, '.flash'), 'a flash login sealed in C# is opened by the computer');
  const fj = fs.readdirSync(W + '/node').filter(f => /flash/.test(f)).map(f => fs.readFileSync(W + '/node/' + f, 'utf8')).join();
  check(fj.includes('hetzner'), 'the computer keeps the login (provider hetzner)');
  check(await X.$('.flash button:has-text("⚡ Start a flash computer")') != null, 'Start a flash computer is offered');
  // notifications settings
  await X.goto(BASE + 'app/notifications/settings'); check(await waitText(X, /Per channel, group, list and organization/, 60000, 'main'), 'notification settings tab');
  // upload: tags and comment policy
  await X.goto(BASE + 'app/upload'); await X.waitForSelector('main', { timeout: 60000 });
  check(await waitText(X, /Comments on this video/, 60000, 'main') && await X.$('input[placeholder*="ags"]') != null, 'upload has tags and the comment policy');
  // You → Settings: add a not-interested tag
  await X.goto(BASE + 'app/you/settings'); await X.waitForSelector('input[placeholder="Add a tag…"]', { timeout: 60000 });
  await X.fill('input[placeholder="Add a tag…"]', 'spoilers'); await X.click('button:has-text("Add")');
  check(await waitText(X, /#spoilers/, 30000, 'main'), 'adding a not-interested tag');
  check(await X.$('button:has-text("Copy user ID")') != null, 'Copy user ID');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await cx.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
