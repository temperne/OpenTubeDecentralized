// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-join.cjs <dir>/wwwroot
// Joining a channel from the C# pages (website and phone app): with a computer of yours that hosts, Join publishes your
// signed membership (joined channels and your own), which the network (the JavaScript page) and your computer read;
// leaving publishes it again without the channel.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-join-')), N = 8894, APP = path.resolve(process.argv[2]);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
let P, lines = []; process.on('exit', () => { try { P?.kill(); } catch {} });
const start = (grant) => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, `--videos=${W}/videos`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', '--no-obs', '--site-dir=' + SITE, '--app-dir=' + APP, ...(grant ? ['--grant=' + grant] : [])]);
  P.stdout.on('data', d => { for (const l of String(d).split('\n')) { lines.push(l); if (/\[host\]|member|rror/.test(l)) log('[C#]', l.trim().slice(0, 160)); } });
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); return; } catch { execSync('sleep 0.5'); } }
};
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return true; await new Promise(r => setTimeout(r, 1000)); } return false; };
(async () => {
  start();
  const BASE = `http://localhost:${N}/`; const errs = [];
  // O: a channel on the JavaScript page
  const co = await chromium.launchPersistentContext(W + '/o', { headless: true }); const O = co.pages()[0];
  await O.goto(BASE + '?folder=browser#/you/channels'); await O.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); await O.waitForTimeout(800);
  await O.evaluate(() => opentube.engine.submitTx({ starter: true }, opentube.me)).catch(() => {});
  await O.fill('input[placeholder=Name]', 'Joinable'); await O.click('text=Create & register');
  await O.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await O.evaluate(() => location.hash.split('/')[2]);
  await until(O, (c) => opentube.engine.directory.has(c), chain, 180000);
  await until(O, () => opentube.engine.ledger.balance(opentube.me.pub) >= 1000, null, 300000);
  await O.evaluate(async (c) => { const vb = await opentube.engine.append(c, { type: 'video', title: 'Hosted clip', desc: '', infohash: '56'.repeat(20), file: 'v.mp4', size: 10, thumb: '' });
    await opentube.engine.submitTx({ post: { chain: c, video: vb.hash, size: 10, amt: OpenTubeCore.postCost(opentube.engine.ledger.prices.post, 10) } }, opentube.me); }, chain);
  // X: the C# pages; the computer is X's (a grant signed with X's key, made on the JavaScript page in X's browser)
  const cx = await chromium.launchPersistentContext(W + '/x', { headless: true }); const XJ = cx.pages()[0];
  await XJ.goto(BASE + '?folder=browser#/'); await XJ.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 });
  const x = await XJ.evaluate(() => opentube.me.pub);
  const grant = await XJ.evaluate(async () => { const g = { v: 1, kind: 'nodegrant', user: opentube.me.pub, at: Date.now(), label: '' }; g.sig = await opentube.me.sign(OpenTubeCore.nodeGrantPreimage(g)); return btoa(JSON.stringify(g)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); });
  const gone = new Promise(r => P.on('exit', r)); P.kill('SIGKILL'); await gone; execSync('sleep 1'); start(grant);
  const X = await cx.newPage(); X.on('pageerror', e => errs.push(e.message));
  await X.goto(BASE + `app/c/${chain}`); await X.waitForSelector('button:has-text("Join")', { timeout: 120000 });
  const t0 = Date.now(); while (Date.now() - t0 < 120000 && await X.isDisabled('button:has-text("Join")')) await X.waitForTimeout(1000);
  check(!(await X.isDisabled('button:has-text("Join")')), 'Join is offered: your computer hosts');
  await X.click('button:has-text("Join")');
  check(await X.waitForSelector('button:has-text("Joined ✓")', { timeout: 30000 }).then(() => true, () => false), 'joined');
  await X.waitForTimeout(3000);
  check(await until(O, ({ k, c }) => { opentube.engine.requestMembership(k); return opentube.engine.memberships.get(k)?.joined.includes(c); }, { k: x, c: chain }, 60000), 'your signed membership lists the channel (the JavaScript page reads it)');
  check(await until(O, () => true, null, 1) && await (async () => { for (let i = 0; i < 90; i++) { if (lines.some(l => /\[host\].*Hosted clip/.test(l))) return true; await new Promise(r => setTimeout(r, 1000)); } return false; })(), 'your computer starts hosting it');
  await X.click('button:has-text("Joined ✓")');
  check(await until(O, ({ k, c }) => { opentube.engine.requestMembership(k); return !opentube.engine.memberships.get(k)?.joined.includes(c); }, { k: x, c: chain }, 120000), 'leaving publishes the membership without it');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await co.close(); await cx.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
