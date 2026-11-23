// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-mail.cjs <dir>/wwwroot
// Private messages in the C# pages (website and phone app), with the JavaScript page on the other side: the C# side
// publishes its messaging key, writes to a JavaScript user (who decrypts it and checks its signature), and reads their
// reply; the message on the chain shows nothing readable.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-mail-')), N = 8889, APP = path.resolve(process.argv[2]);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
let P; process.on('exit', () => { try { P?.kill(); } catch {} });
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return true; await new Promise(r => setTimeout(r, 500)); } return false; };
(async () => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, '--no-host', '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', '--no-obs', '--site-dir=' + SITE, '--app-dir=' + APP]);
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); break; } catch { execSync('sleep 0.5'); } }
  // A: the JavaScript page, with a messaging key
  const ca = await chromium.launchPersistentContext(W + '/a', { headless: true }); const A = ca.pages()[0];
  await A.goto(`http://localhost:${N}/?folder=browser#/messages`); await A.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 });
  await A.evaluate(() => opentube.mail.publishKeys()); const a = await A.evaluate(() => opentube.me.pub);
  // C: the C# site, a different person
  const cc = await chromium.launchPersistentContext(W + '/c', { headless: true, viewport: { width: 412, height: 860 }, isMobile: true }); const C = cc.pages()[0]; const errs = [];
  C.on('pageerror', e => errs.push(e.message));
  await C.goto(`http://localhost:${N}/app/messages`); await C.waitForSelector('text=New message', { timeout: 60000 });
  await C.click('nav >> text=You'); await C.waitForSelector('.mono'); const c = (await C.textContent('.mono')).trim();
  check(await until(A, (k) => (opentube.engine.ledger.msgkeys.get(k) || []).length > 0, c) && await until(A, (k) => true, null), 'the C# side published its messaging key (the network knows it)');
  await until(C, () => true, null);
  // C writes to A
  await C.goto(`http://localhost:${N}/app/messages/${a}`); await C.waitForSelector('textarea', { timeout: 30000 });
  await C.waitForFunction(() => !document.querySelector('.compose button').disabled || document.querySelector('textarea').value === '', null, { timeout: 120000 }).catch(() => {});
  for (let i = 0; i < 120 && /haven’t published/.test(await C.textContent('main')); i++) await C.waitForTimeout(1000);
  await C.fill('textarea', 'Hi from C# — ünïcödé ✓ "quotes"'); await C.click('.compose button');
  await C.waitForSelector('.bubble.out', { timeout: 60000 }).catch(() => {});
  check(await C.isVisible('.bubble.out'), 'the C# side sent it (it shows in the conversation)');
  const gotA = await until(A, async () => (await opentube.mail.inbox()).some(m => m.text === 'Hi from C# — ünïcödé ✓ "quotes"'), null, 90000);
  check(gotA, 'the JavaScript page decrypts it and its signature checks (same keys, tag, cipher and signature)');
  const blk = await A.evaluate(() => [...opentube.engine.chains.values()].filter(c => c.genesis.payload.kind === 'mail').flatMap(c => [...c.blocks.values()]).find(b => b.payload.type === 'msg')?.payload);
  check(blk && blk.hints?.length === 3 && !/Hi from|ünïcödé/.test(JSON.stringify(blk)), 'on the chain: nothing readable, 3 hints');
  // A replies from the JavaScript page; the C# side reads it
  await A.evaluate((k) => opentube.mail.send(k, 'Reply from JavaScript 👋'), c);
  await C.waitForSelector('.bubble.in', { timeout: 90000 }).catch(() => {});
  check(/Reply from JavaScript 👋/.test(await C.textContent('.thread').catch(() => '')), 'the C# side decrypts the JavaScript page’s reply and shows the conversation');
  await C.goto(`http://localhost:${N}/app/messages`); await C.waitForSelector('a.convo', { timeout: 30000 }).catch(() => {});
  check(/Reply from JavaScript/.test(await C.textContent('main')), 'Messages lists the conversation with its newest message');
  await C.screenshot({ path: W + '/messages.png' }); log('screenshot', W + '/messages.png');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await ca.close(); await cc.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
