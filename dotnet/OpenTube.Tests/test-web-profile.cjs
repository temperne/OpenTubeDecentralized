// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-profile.cjs <dir>/wwwroot
// Profiles, You and the wallet in the C# pages (website and phone app), against the JavaScript page: start your
// profile, claim an @@name, edit it; someone's profile page (their channels, follow, add friend); the wallet (send coins
// by @@name, history, an account's public view); and "not interested" tags under Settings.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-profile-')), N = 8888, APP = path.resolve(process.argv[2]);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
let P; process.on('exit', () => { try { P?.kill(); } catch {} });
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return true; await new Promise(r => setTimeout(r, 1000)); } return false; };
const waitText = async (p, re, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (re.test(await p.textContent('main').catch(() => '') || '')) return true; await p.waitForTimeout(1000); } return false; };
(async () => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, '--no-host', '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', '--no-obs', '--site-dir=' + SITE, '--app-dir=' + APP]);
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); break; } catch { execSync('sleep 0.5'); } }
  const BASE = `http://localhost:${N}/`; const errs = [];
  // O: someone on the JavaScript page, with a channel
  const co = await chromium.launchPersistentContext(W + '/o', { headless: true }); const O = co.pages()[0];
  await O.goto(BASE + '?folder=browser#/you/channels'); await O.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); await O.waitForTimeout(800);
  await O.fill('input[placeholder=Name]', 'Their Channel'); await O.click('text=Create & register');
  await O.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await O.evaluate(() => location.hash.split('/')[2]);
  const o = await O.evaluate(() => opentube.me.pub);
  await until(O, (c) => opentube.engine.directory.has(c), chain, 180000);
  // X: the C# pages
  const cx = await chromium.launchPersistentContext(W + '/x', { headless: true }); const X = cx.pages()[0]; X.on('pageerror', e => errs.push(e.message));
  await X.goto(BASE + 'app/you'); await X.waitForSelector('button:has-text("Start my profile")', { timeout: 60000 });
  const x = (await X.textContent('.panel .mono')).trim();
  await X.click('button:has-text("starter coins")');
  await X.click('button:has-text("Start my profile")');
  check(await until(O, (k) => [...opentube.engine.directory.values()].some(e => e.genesis.payload.kind === 'circle' && e.genesis.author === k), x, 240000), 'starting your profile registers your page (the JavaScript page sees it)');
  await X.waitForSelector('#name-input', { timeout: 120000 });
  await X.fill('#name-input', 'xperson'); await X.click('button:has-text("Claim name")');
  check(await until(O, (k) => { const n = opentube.engine.ledger.names.get('@@xperson'); return n && opentube.engine.directory.get(n.chain)?.genesis.author === k; }, x, 240000), 'claiming @@xperson (the network records it)');
  await X.fill('.panel:has(> h3:text-is("Your public profile")) input >> nth=0', 'Xavier'); await X.fill('.panel:has(> h3:text-is("Your public profile")) textarea', 'I make **things**.');
  await X.click('.panel:has(> h3:text-is("Your public profile")) button:has-text("Save")');
  await O.goto(BASE + '?folder=browser#/u/' + x); await O.waitForTimeout(1500);
  check(await until(O, (k) => { const e = [...opentube.engine.directory.values()].find(e => e.genesis.payload.kind === 'circle' && e.genesis.author === k); const d = e && opentube.engine.derive(e.genesis.hash); return d?.profile.name === 'Xavier' && d.profile.about === 'I make **things**.'; }, x, 120000), 'editing your profile (name, about) reaches the JavaScript page');
  check(await waitText(X, /Xavier/, 30000) && await X.isVisible('.rich b:has-text("things")'), 'your profile, as others see it, shows it');
  // someone else's profile
  await X.goto(BASE + 'app/u/' + o); check(await waitText(X, /Their Channel/, 60000), 'their profile lists their channel');
  await X.click('button:has-text("Follow")'); check(await waitText(X, /Following ✓/, 10000), 'follow');
  await X.click('button:has-text("＋ Friend")'); check(await waitText(X, /friend/, 10000), 'add as a friend');
  // the wallet
  check(await until(O, (k) => opentube.engine.ledger.balance(k) > 0, x, 300000), 'starter coins arrive');
  const before = await O.evaluate((k) => opentube.engine.ledger.balance(k), o);
  await X.goto(BASE + 'app/wallet'); await X.waitForSelector('textarea', { timeout: 60000 });
  await X.fill('textarea', o); await X.fill('input[type=number]', '1.25'); await X.fill('input[placeholder^="Memo"]', 'thanks');
  await X.click('button:has-text("Send")');
  check(await waitText(X, /Sent/, 60000), 'sending coins');
  check(await until(O, ({ k, b }) => opentube.engine.ledger.balance(k) === b + 125, { k: o, b: before }, 180000), 'the recipient gets 1.25 coins (the JavaScript page counts them)');
  await X.goto(BASE + 'app/wallet'); check(await waitText(X, /−1\.25 to/, 60000) && await waitText(X, /starter coins/, 10000), 'your history: the payment and the starter coins');
  await X.goto(BASE + 'app/wallet/' + o); check(await waitText(X, /\+1\.25 from/, 60000), 'their account’s public view shows the payment');
  // an unknown name
  await X.goto(BASE + 'app/wallet'); await X.waitForSelector('textarea', { timeout: 60000 });
  await X.fill('textarea', '@@nobody-here'); await X.fill('input[type=number]', '1'); await X.click('button:has-text("Send")');
  check(await waitText(X, /Every recipient must be/, 10000), 'an unknown name is refused');
  // not interested
  await X.goto(BASE + 'app/you/settings'); check(await waitText(X, /Nothing\. Use ✕/, 30000), 'Settings: nothing muted yet');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await co.close(); await cx.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
