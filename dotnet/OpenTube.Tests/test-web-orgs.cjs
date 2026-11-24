// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-orgs.cjs <dir>/wwwroot
// Organizations in the C# pages (website and phone app), against the JavaScript page: classifications and petitions,
// reporting a video and flagging it, following an org (a flag that hides), filings, a case with its docket, roles (an
// arbitrator signs an order, the clerk dockets it), and a level of arbitration below.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-orgs-')), N = 8891, APP = path.resolve(process.argv[2]);
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
  // O: the JavaScript page (a channel with a video); OC: the same person in the C# pages, who runs the organization
  const co = await chromium.launchPersistentContext(W + '/o', { headless: true }); const O = co.pages()[0];
  await O.goto(BASE + '?folder=browser#/you/channels'); await O.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); await O.waitForTimeout(800);
  await O.fill('input[placeholder=Name]', 'Clip Channel'); await O.click('text=Create & register');
  await O.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await O.evaluate(() => location.hash.split('/')[2]);
  await until(O, (c) => opentube.engine.directory.has(c), chain, 180000);
  const video = await O.evaluate(async (c) => (await opentube.engine.append(c, { type: 'video', title: 'Spammy clip', desc: '', infohash: '12'.repeat(20), file: 'v.mp4', size: 10, thumb: '' })).hash, chain);
  const OC = await co.newPage(); OC.on('pageerror', e => errs.push(e.message));
  await OC.goto(BASE + 'app/orgs'); await OC.waitForSelector('input[placeholder=Name]', { timeout: 60000 });
  await OC.fill('input[placeholder=Name]', 'Fair Court');
  await OC.fill('.fields div:has(label:text-is("Rules it follows")) input', 'UNCITRAL'); await OC.fill('.fields div:has(label:text-is("Website")) input', 'https://court.example');
  const me0 = await O.evaluate(() => opentube.me.pub);
  await OC.fill('input[placeholder="Their user ID"]', me0); await OC.fill('input[placeholder^="Title, e.g."]', 'Chief arbitrator'); await OC.click('.panel button:has-text("Add")');
  await OC.click('button:has-text("Create")');
  await OC.waitForURL(/\/app\/c\//, { timeout: 180000 }); const org = OC.url().split('/c/')[1].split('/')[0];
  check(await until(O, (g) => opentube.engine.directory.get(g)?.genesis.payload.orgType === 'arbitration', org, 180000), 'an arbitration body created in C# is registered (the JavaScript page sees it)');
  check(await O.evaluate((g) => { const p = opentube.engine.directory.get(g).genesis.payload; return p.details?.rules === 'UNCITRAL' && p.details?.website === 'https://court.example' && p.people?.[0]?.role === 'Chief arbitrator'; }, org), 'with its details and people');
  await OC.goto(BASE + `app/c/${org}/about`); check(await OC.waitForSelector('text=Rules it follows', { timeout: 60000 }).then(() => true, () => false) && /Chief arbitrator/.test(await OC.textContent('main')), 'the About tab shows them');
  // classifications
  await OC.goto(BASE + `app/c/${org}/rules`); await OC.waitForSelector('input[placeholder^="short-id"]', { timeout: 120000 });
  await OC.fill('input[placeholder^="short-id"]', 'spam'); await OC.fill('input[placeholder^="Name, e.g. Adult"]', 'Spam'); await OC.fill('input[placeholder="What it covers"]', 'Junk and scams');
  await OC.click('button:has-text("Add classification")');
  await O.goto(BASE + `?folder=browser#/c/${org}/rules`); await O.waitForTimeout(1500);
  check(await until(O, (g) => [...opentube.engine.chains.get(g).blocks.values()].some(b => b.payload.type === 'class' && b.payload.id === 'spam'), org, 120000), 'adding a classification (the JavaScript page sees it)');
  // X: someone else on the C# pages
  const cx = await chromium.launchPersistentContext(W + '/x', { headless: true }); const X = cx.pages()[0]; X.on('pageerror', e => errs.push(e.message));
  await X.goto(BASE + 'app/you'); await X.waitForSelector('.mono', { timeout: 60000 }); const x = (await X.textContent('.panel .mono')).trim();
  await X.goto(BASE + `app/c/${org}/rules`); await X.waitForSelector('input[placeholder^="short-id"]', { timeout: 120000 });
  await X.fill('input[placeholder^="short-id"]', 'adult'); await X.fill('input[placeholder^="Name, e.g. Adult"]', 'Adult content'); await X.click('button:has-text("Petition for it")');
  check(await until(O, (g) => [...opentube.engine.chains.get(g).blocks.values()].some(b => b.payload.type === 'petition' && b.payload.id === 'adult'), org, 120000), 'a petition for a new classification');
  await OC.goto(BASE + `app/c/${org}/rules`); await OC.waitForSelector('button:has-text("Adopt")', { timeout: 60000 }); await OC.click('button:has-text("Adopt")');
  check(await until(O, (g) => [...opentube.engine.chains.get(g).blocks.values()].some(b => b.payload.type === 'class' && b.payload.id === 'adult'), org, 120000), 'the owner adopts it');
  // report a video and flag it
  await X.goto(BASE + `app/v/${chain}/${video}`); await X.waitForSelector('button:has-text("Report")', { timeout: 120000 });
  await X.click('button:has-text("Report")'); await X.waitForSelector('#report-box select', { timeout: 10000 });
  await X.waitForSelector('#report-box option[value="spam"]', { state: 'attached', timeout: 60000 });
  await X.selectOption('#report-box select >> nth=1', 'spam'); await X.fill('#report-box input', 'Obvious spam'); await X.click('#report-box button:has-text("Report")');
  check(await until(O, ({ g, v }) => [...opentube.engine.chains.get(g).blocks.values()].some(b => b.payload.type === 'report' && b.payload.ref?.block === v && b.payload.cls === 'spam'), { g: org, v: video }, 120000), 'reporting a video from its page (the org receives the report)');
  await OC.goto(BASE + `app/c/${org}/reports`); await OC.waitForSelector('button:has-text("Flag it")', { timeout: 60000 }); await OC.click('button:has-text("Flag it")');
  check(await until(O, ({ g, v }) => [...opentube.engine.chains.get(g).blocks.values()].some(b => b.payload.type === 'flag' && b.payload.ref?.block === v && b.payload.on), { g: org, v: video }, 120000), 'a moderator flags it');
  // follow the org and hide what it flags as spam
  await X.goto(BASE + `app/c/${org}/flags`); await X.click('button:has-text("Follow")');
  await X.waitForSelector('select:has(option[value="hide"])', { timeout: 30000 });
  await X.selectOption('.row-item:has-text("Spam") select', 'hide');
  await X.goto(BASE + `app/v/${chain}/${video}`);
  check(await waitText(X, /You chose not to open videos with this flag/, 60000), 'following the org: a video it flagged as spam is hidden (with “Show it anyway”)');
  await X.click('button:has-text("Show it anyway")'); check(await waitText(X, /Flagged as Spam \(Fair Court\)/, 10000), 'shown anyway, with the flag');
  // filings and a case
  await X.goto(BASE + `app/c/${org}/filings`); await X.waitForSelector('input[placeholder="Title"]', { timeout: 60000 });
  await X.fill('input[placeholder="Title"]', 'Unpaid order'); await X.fill('textarea', 'I paid and got nothing.'); await X.click('button:has-text("File")');
  check(await until(O, (g) => [...opentube.engine.chains.get(g).blocks.values()].some(b => b.payload.type === 'submit' && b.payload.title === 'Unpaid order'), org, 120000), 'filing a complaint with the clerk');
  await OC.goto(BASE + `app/c/${org}/filings`); await OC.waitForSelector('button:has-text("Open a case")', { timeout: 60000 }); await OC.click('button:has-text("Open a case")');
  await OC.waitForURL(/\/docket$/, { timeout: 180000 }).catch(() => {}); const kase = OC.url().split('/c/')[1]?.split('/')[0];
  check(/\/docket$/.test(OC.url()), 'the clerk opens a case from it (a child chain with its own key)');
  check(await until(O, ({ g, k }) => [...opentube.engine.chains.get(g).blocks.values()].some(b => b.payload.type === 'child' && b.payload.genesis.hash === k), { g: org, k: kase }, 120000), 'the JavaScript page sees the case registered on the court');
  check(await waitText(OC, /Unpaid order/, 60000) && await waitText(OC, /Status → open/, 30000), 'the docket: the filing, status open');
  await OC.selectOption('select:has(option[value="hearing"])', 'hearing'); await OC.click('button:has-text("Set status")');
  check(await waitText(OC, /Status → hearing/, 60000), 'the clerk sets the status (hearing)');
  // roles: X becomes an arbitrator and signs an order; the clerk dockets it
  await OC.goto(BASE + `app/c/${org}/people`); await OC.waitForSelector('input[placeholder="User ID"]', { timeout: 60000 });
  await OC.fill('input[placeholder="User ID"]', x); await OC.selectOption('select:has(option[value="judge"])', 'judge'); await OC.click('button:has-text("Add")');
  check(await until(O, ({ g, k }) => [...opentube.engine.chains.get(g).blocks.values()].some(b => b.payload.type === 'role' && b.payload.key === k && b.payload.role === 'judge' && b.payload.on), { g: org, k: x }, 120000), 'making someone an arbitrator');
  await X.goto(BASE + `app/c/${kase}/docket`); await X.waitForSelector('input[placeholder="Order title"]', { timeout: 120000 });
  await X.fill('input[placeholder="Order title"]', 'Refund ordered'); await X.fill('textarea', 'The seller refunds.'); await X.click('button:has-text("Sign order")');
  check(await waitText(X, /Signed and sent to the clerk/, 60000), 'the arbitrator signs an order');
  await OC.goto(BASE + `app/c/${kase}/docket`); await OC.waitForSelector('button:has-text("Put on the docket")', { timeout: 120000 }); await OC.click('button:has-text("Put on the docket")');
  check(await until(O, (k) => { const c = opentube.engine.chains.get(k) || (opentube.engine.requestChain?.(opentube.engine.openChain?.(k)), null); return c && [...c.blocks.values()].some(b => b.payload.type === 'order' && b.payload.title === 'Refund ordered'); }, kase, 120000) || await waitText(OC, /Refund ordered/, 30000), 'the clerk dockets the order');
  check(await waitText(OC, /signed by arbitrator/, 30000), 'the docket shows the signed order');
  // a level below
  await OC.goto(BASE + `app/c/${org}/courts`); await OC.waitForSelector('input[placeholder^="Name, e.g. Superior"]', { timeout: 60000 });
  await OC.fill('input[placeholder^="Name, e.g. Superior"]', 'Lower Court'); await OC.click('button:has-text("Add a level below")');
  check(await until(O, (g) => [...opentube.engine.chains.get(g).blocks.values()].some(b => b.payload.type === 'child' && b.payload.genesis.payload.name === 'Lower Court'), org, 120000), 'a level of arbitration below (a child org)');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await co.close(); await cx.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
