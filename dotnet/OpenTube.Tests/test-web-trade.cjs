// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-trade.cjs <dir>/wwwroot   (needs ffmpeg)
// Market, Exchange, Organizations and Devices in the C# pages, with another person on the JavaScript page: list / buy /
// bid; an offer and a swap claimed with its secret; an arbitration body and a role; your computer's status, files,
// sharing, log, and the command to add a computer.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os'), crypto = require('crypto');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-trade-'));
const N = 8883, RT = 1936, TRACKER = `ws://127.0.0.1:${N}/tracker`, BASE = `http://localhost:${N}/`, Q = `?folder=browser&trackers=${encodeURIComponent(TRACKER)}`;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return ((Date.now() - t0) / 1000).toFixed(1); await new Promise(r => setTimeout(r, 300)); } throw new Error('timeout'); };
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let P, lines = []; const start = (grant) => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, `--videos=${W}/videos`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', `--obs-port=${RT}`, '--site-dir=' + SITE, '--app-dir=' + path.resolve(process.argv[2]), ...(grant ? ['--grant=' + grant] : [])], { env: { ...process.env, PAY_DEBUG: '1' } });
  P.stdout.on('data', d => { for (const l of String(d).split('\n')) { lines.push(l); if (/\[host\]|\[obs\]|\[pay|\[live\]|rror|fail/.test(l)) log('[C#]', l.trim().replace(/^\d\d:\d\d:\d\d /, '').slice(0, 170)); } });
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); return; } catch { execSync('sleep 0.5'); } } };
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
(async () => {
  start();
  const ctxs = []; const open = async (n, q) => { const ctx = await chromium.launchPersistentContext(W + '/b-' + n, { headless: true, args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] }); ctxs.push(ctx); const p = ctx.pages()[0]; p.on('pageerror', e => log(n, 'PAGEERROR', e.message.slice(0, 120))); await p.goto(BASE + q); await p.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); return p; };
  const A = await open('A', Q + '#/you/channels'); await A.waitForTimeout(800);
  await A.fill('input[placeholder=Name]', 'Host Channel'); await A.click('text=Create & register');
  await A.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await A.evaluate(() => location.hash.split('/')[2]);
  await until(A, (c) => opentube.engine.directory.has(c), chain); log('channel registered');
  const user = await A.evaluate(() => opentube.me.pub);
  const grant = await A.evaluate(async () => { const g = { v: 1, kind: 'nodegrant', user: opentube.me.pub, at: Date.now(), label: '' }; g.sig = await opentube.me.sign(OpenTubeCore.nodeGrantPreimage(g)); return btoa(JSON.stringify(g)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); });
  const gone = new Promise(r => P.on('exit', r)); P.kill('SIGKILL'); await gone; execSync('sleep 1');
  fs.writeFileSync(W + '/node/obs.json', JSON.stringify({ profiles: { [user]: { key: 'host-key', channel: chain, title: 'Hosted show' } }, users: {} }));
  start(grant);
  const nodeKey = JSON.parse(execSync(`curl -s localhost:${N}/status`)).node;
  await until(A, (k) => opentube.engine.nodes.has(k), nodeKey, 90000);
  await A.evaluate(async ({ c, k }) => { await opentube.engine.append(c, { type: 'relay', key: k, on: true }); }, { c: chain, k: nodeKey });
  for (let i = 0; i < 120; i++) { const s = JSON.parse(execSync(`curl -s localhost:${N}/status`)); if ((s.obs?.linked || []).includes(chain)) break; await new Promise(r => setTimeout(r, 500)); }
  const waitText = async (p, re, ms = 120000, reload) => { const t0 = Date.now(); let last = t0; while (Date.now() - t0 < ms) { if (re.test(await p.textContent('main').catch(() => '') || '')) return true; await p.waitForTimeout(1000); if (reload && Date.now() - last > 15000) { last = Date.now(); await reload(); await p.waitForTimeout(2500); } } return false; };
  const untilJ = (p, fn, arg, ms = 120000) => until(p, fn, arg, ms).then(() => true).catch(() => false);
  const C = await ctxs[0].newPage(); const errs = []; C.on('pageerror', e => errs.push(e.message));
  await C.goto(BASE + 'app/you'); await C.waitForSelector('.mono', { timeout: 60000 });
  if (await C.isVisible('button:has-text("starter coins")')) await C.click('button:has-text("starter coins")');
  await waitText(C, /Coins: (?!0\.00)\d/, 300000, () => C.goto(BASE + 'app/you'));
  // B: another person, on the JavaScript page, with coins
  const B = await open('B', Q + '#/market'); const b = await B.evaluate(() => opentube.me.pub);
  await B.evaluate(() => opentube.engine.submitTx({ starter: true }, opentube.me));
  await untilJ(B, (k) => opentube.engine.ledger.bal.get(k) > 0, b, 300000);

  // ---- market: the C# side lists; B buys
  await C.goto(BASE + 'app/market/new'); await C.waitForSelector('text=List it', { timeout: 30000 });
  await C.fill('input[maxlength="120"]', 'Bike from C#'); await C.fill('input[inputmode="decimal"]', '2'); await C.click('button:has-text("List it")');
  const lid = await B.evaluate(async () => { for (let i = 0; i < 120; i++) { for (const [id, L] of opentube.engine.ledger.listings) if (L.title === 'Bike from C#') return id; await new Promise(r => setTimeout(r, 1000)); } return null; });
  check(!!lid, 'Market: the C# side lists something; the JavaScript side sees it');
  await B.evaluate((id) => opentube.engine.submitTx({ buy: { id } }, opentube.me), lid);
  await C.goto(BASE + 'app/market/item/' + lid);
  check(await waitText(C, /Sold to/, 120000, () => C.goto(BASE + 'app/market/item/' + lid)), 'B buys it; the C# listing page shows it sold');
  // a listing with its kind's own fields, a category and tags
  await C.goto(BASE + 'app/market/new'); await C.waitForSelector('text=List it', { timeout: 30000 });
  await C.selectOption('select >> nth=0', 'vehicle'); await C.waitForSelector('label:has-text("Make")', { timeout: 10000 });
  await C.fill('input[maxlength="120"]', 'Old Volvo'); await C.fill('.fields div:has(label:text-is("Make")) input', 'Volvo'); await C.fill('.fields div:has(label:text-is("Year")) input', '1999');
  await C.selectOption('.fields div:has(label:text-is("Fuel")) select', 'diesel');
  await C.fill('input[inputmode="decimal"]', '3'); await C.fill('input[placeholder^="e.g. bike"]', 'classic, swedish'); await C.click('button:has-text("List it")');
  const vid = await B.evaluate(async () => { for (let i = 0; i < 120; i++) { for (const [id, L] of opentube.engine.ledger.listings) if (L.title === 'Old Volvo') return id; await new Promise(r => setTimeout(r, 1000)); } return null; });
  const vl = vid && await B.evaluate((id) => opentube.engine.ledger.listings.get(id), vid);
  check(vl?.type === 'vehicle' && vl.details?.make === 'Volvo' && vl.details?.year === 1999 && vl.details?.fuel === 'diesel' && vl.tags?.includes('classic'), 'a vehicle listing with its fields and tags (the JavaScript side reads them): ' + JSON.stringify(vl && { type: vl.type, details: vl.details, tags: vl.tags }));
  await C.goto(BASE + 'app/market/item/' + vid); check(await waitText(C, /Make\s*Volvo/, 60000, () => C.goto(BASE + 'app/market/item/' + vid)), 'the C# listing page shows the vehicle’s details');
  await C.goto(BASE + 'app/market/mine'); check(await waitText(C, /Old Volvo/, 30000), 'Market → Mine lists it');
  // B lists an auction; the C# side bids
  await B.evaluate(() => opentube.engine.submitTx({ list: { kind: 'auction', type: 'goods', title: 'Lamp at auction', price: 100, ends: Date.now() + 86400000, fee: Math.max(1, Math.ceil(opentube.engine.ledger.prices.list)) } }, opentube.me));
  const aid = await B.evaluate(async () => { for (let i = 0; i < 120; i++) { for (const [id, L] of opentube.engine.ledger.listings) if (L.title === 'Lamp at auction') return id; await new Promise(r => setTimeout(r, 1000)); } return null; });
  await C.goto(BASE + 'app/market/item/' + aid); await waitText(C, /Lamp at auction/, 60000, () => C.goto(BASE + 'app/market/item/' + aid));
  await C.fill('input[placeholder="Your bid"]', '1.5'); await C.click('button:has-text("Bid")');
  check(await untilJ(B, (id) => opentube.engine.ledger.listings.get(id)?.high?.amt === 150, aid), 'the C# side bids on the JavaScript auction (the high bid there)');

  // ---- exchange: an offer, and a swap B claims with its secret
  await C.goto(BASE + 'app/exchange'); await C.waitForSelector('text=Post offer', { timeout: 30000 });
  await C.fill('input[placeholder="How many coins"]', '1'); await C.fill('input[placeholder="Price per coin"]', '0.00002'); await C.click('button:has-text("Post offer")');
  check(await untilJ(B, () => [...opentube.engine.ledger.offers.values()].some(o => o.rate === '0.00002' && o.state === 'open')), 'Exchange: the C# side posts an offer; the JavaScript side sees it');
  await C.goto(BASE + 'app/exchange/swaps'); await C.waitForSelector('text=Lock coins for a swap', { timeout: 30000 });
  await C.fill('input[placeholder="Who gets the coins (their user ID)"]', b); await C.fill('input[placeholder="Coins"]', '0.5');
  await C.click('button:has-text("Make a secret for me")'); await C.waitForSelector('text=Your secret for this hash', { timeout: 10000 });
  const secret = (await C.textContent('text=Your secret for this hash')).match(/[0-9a-f]{64}/)?.[0] || (await C.$$eval('.mono', els => els.map(e => e.textContent))).find(t => /^[0-9a-f]{64}$/.test(t.trim()))?.trim();
  await C.click('button:has-text("Lock coins")');
  const sid = await B.evaluate(async (k) => { for (let i = 0; i < 120; i++) { for (const [id, H] of opentube.engine.ledger.htlcs) if (H.to === k && H.state === 'locked') return id; await new Promise(r => setTimeout(r, 1000)); } return null; }, b);
  check(!!sid && !!secret, 'the C# side locks coins for B (a swap)');
  await B.evaluate(({ id, p }) => opentube.engine.submitTx({ unlock: { id, preimage: p } }, opentube.me), { id: sid, p: secret });
  check(await waitText(C, /unlocked/, 120000, () => C.goto(BASE + 'app/exchange/swaps')), 'B claims it with the secret; the C# side shows the swap unlocked');

  // ---- organizations
  await C.goto(BASE + 'app/orgs'); await C.waitForSelector('text=Create an organization', { timeout: 30000 });
  await C.fill('input[placeholder="Name"]', 'Fair Court'); await C.click('button:has-text("Create")');
  await C.waitForURL(/\/app\/c\//, { timeout: 120000 }).catch(() => {}); const org = C.url().split('/c/')[1];
  check(await untilJ(B, (o) => opentube.engine.directory.get(o)?.genesis.payload.orgType === 'arbitration', org, 180000), 'Orgs: the C# side creates an arbitration body; the JavaScript side sees it registered');
  await C.goto(BASE + `app/c/${org}/people`); await C.waitForSelector('input[placeholder="User ID"]', { timeout: 60000 });
  await C.fill('input[placeholder="User ID"]', b); await C.selectOption('select:has(option[value="judge"])', 'judge'); await C.click('button:has-text("Add")');
  check(await waitText(C, /arbitrator/, 60000), 'the owner gives B the arbitrator role');
  await B.goto(BASE + Q + '#/c/' + org); await B.waitForTimeout(1500);   // (opening its page loads the organization's chain)
  check(await untilJ(B, ({ o, k }) => { const c = opentube.engine.chains.get(o); return !!c && [...c.blocks.values()].some(x => x.payload.type === 'role' && x.payload.key === k && x.payload.role === 'judge' && x.payload.on); }, { o: org, k: b }, 60000), 'the JavaScript side sees the role on the organization’s chain');

  // ---- devices
  await C.goto(BASE + 'app/devices'); await C.waitForSelector('text=online', { timeout: 60000 }).catch(() => {});
  check(/online/.test(await C.textContent('main')) && /yours/.test(await C.textContent('main')), 'Devices: your computer, online');
  await C.click('button:has-text("Files")'); check(await waitText(C, /Host Channel/, 30000), 'its files (the channel it keeps)');
  await C.click('button:has-text("People")'); await C.waitForSelector('input[placeholder="Their user ID"]', { timeout: 30000 });
  await C.fill('input[placeholder="Their user ID"]', b); await C.fill('input[placeholder="Name"]', 'Bee'); await C.click('button:has-text("Share")');
  check(await waitText(C, /Bee[\s\S]*obs/, 30000), 'shared with B (OBS)');
  await C.click('button:has-text("Log")'); check(await waitText(C, /\[control\]/, 30000), 'its log');
  await C.fill('input[placeholder^="Its name"]', 'Laptop'); await C.click('button:has-text("Make the command")');
  const cmd = await C.textContent('.cmd'); const g = JSON.parse(Buffer.from(cmd.match(/--grant=([A-Za-z0-9_-]+)/)[1], 'base64url').toString());
  const okSig = await A.evaluate((g) => { const { sig, ...rest } = g; return OpenTubeCore.verify(g.user, 'opentube-nodegrant:' + OpenTubeCore.canon(rest), sig); }, g).catch(() => null);
  check(/curl -fsSL .*opentube-dotnet\.sh \| bash -s -- --site=.* --grant=/.test(cmd) && g.kind === 'nodegrant' && g.label === 'Laptop' && okSig !== false, 'the command to add a computer, with a grant signed by you');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  for (const c of ctxs) await c.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); P?.kill(); process.exit(1); });
