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
  await X.click('label.check:has-text("Also send it to YouTube") input'); await X.fill('input[placeholder="YouTube stream key"]', 'yt-test-key'); await X.click('button:has-text("Save")');
  check(await waitText(X, /Saved: your streams also go to YouTube/, 30000), 'YouTube forwarding saved');
  const ob = JSON.parse(fs.readFileSync(W + '/node/obs.json', 'utf8')); check(JSON.stringify(ob).includes('yt-test-key') && JSON.stringify(ob).includes('"on":true'), 'the computer keeps the YouTube key, switched on');
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
  // a real guest: Y (C# pages, own channel) is invited, sees "Join on camera" on the host's stream, and joins from Go live
  const cy = await chromium.launchPersistentContext(W + '/y', { headless: true, args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] }); const YJ = cy.pages()[0];
  await YJ.goto(BASE + '?folder=browser#/you/channels'); await YJ.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); await YJ.waitForTimeout(800);
  await YJ.fill('input[placeholder=Name]', 'Guest Channel'); await YJ.click('text=Create & register');
  await YJ.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const ychain = await YJ.evaluate(() => location.hash.split('/')[2]);
  await YJ.waitForFunction((c) => opentube.engine.directory.has(c), ychain, { timeout: 180000 });
  const y = await YJ.evaluate(() => opentube.me.pub);
  await X.fill('.group-panel input[placeholder^="Their"]', y); await X.click('.group-panel button:has-text("Invite")');
  check(await X.waitForSelector(`.group-panel a[href="u/${y}"]`, { timeout: 30000 }).then(() => true, () => false), 'the host invites a real guest');
  const Y = await cy.newPage(); Y.on('pageerror', e => errs.push(e.message));
  await Y.goto(BASE + 'app/devices'); await Y.waitForTimeout(3000);
  const nodeKey = JSON.parse(execSync(`curl -s localhost:${N}/status`)).node;
  await Y.goto(BASE + `app/golive/${ychain}?group=${nodeKey}`); await Y.waitForSelector('text=Joining a group stream on camera', { timeout: 60000 });
  await Y.fill('input[maxlength="140"]', 'Guest camera'); await Y.click('button:has-text("Go live")');
  check(await waitText(Y, /You’re in the group stream|Live/, 60000), 'the guest goes live into the group: ' + (await Y.textContent('#golive-status').catch(() => '')));
  check(await X.waitForSelector('.group-panel .studio-person:has-text("🌐")', { timeout: 90000 }).then(() => true, () => false), 'the host’s studio lists the guest’s camera (waiting room or on air)');
  await cy.close();
  // a guest with no channel at all (C# pages only): invited, joins from Go live; their profile carries the stream
  const cz = await chromium.launchPersistentContext(W + '/z', { headless: true, args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] }); const Z = cz.pages()[0]; Z.on('pageerror', e => errs.push(e.message));
  await Z.goto(BASE + 'app/you/settings'); await Z.waitForSelector('button:has-text("Copy user ID")', { timeout: 120000 });
  const z = (await Z.textContent('.row-actions:has(button:has-text("Copy user ID")) .mono')).trim(); log('guest', z.slice(0, 12));
  await X.fill('.group-panel input[placeholder^="Their"]', z); await X.click('.group-panel button:text-is("Invite")');
  check(await X.waitForSelector(`.group-panel a[href="u/${z}"]`, { timeout: 30000 }).then(() => true, () => false), 'the host invites a guest who has no channel');
  await Z.goto(BASE + `app/golive?group=${nodeKey}`); await Z.waitForSelector('text=Joining a group stream on camera', { timeout: 60000 });
  check(!/don’t have a channel/.test(await Z.textContent('main')), 'Go live doesn’t ask the guest for a channel');
  await Z.fill('input[maxlength="140"]', 'Guest without channel'); await Z.click('button:has-text("Go live")');
  check(await waitText(Z, /You’re in the group stream/, 240000, ), 'the channel-less guest goes live into the group: ' + (await Z.textContent('#golive-status').catch(() => '')));
  check(await X.waitForFunction(() => document.querySelectorAll('.group-panel .studio-person').length >= 2 || /Guest without|\(browser\)/.test(document.querySelector('.group-panel')?.textContent || ''), null, { timeout: 90000 }).then(() => true, () => false), 'the host’s studio lists their camera');
  await cz.close();
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await cx.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
