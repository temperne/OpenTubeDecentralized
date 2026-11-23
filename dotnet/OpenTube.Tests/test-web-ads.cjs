// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-ads.cjs <dir>/wwwroot   (needs ffmpeg)
// Ads in the C# pages (website and phone app), against the JavaScript page: a channel creates an ad (the file shared
// over BitTorrent, the terms signed by the channel's key); a viewer on the C# pages gets it before one of the
// channel's videos, watches it, and claims the view (an ad-view certificate for the viewer, coins for the creator);
// the JavaScript page's ledger agrees; the viewer cashes the certificate in.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-ads-')), N = 8892, APP = path.resolve(process.argv[2]);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
let P; process.on('exit', () => { try { P?.kill(); } catch {} });
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return true; await new Promise(r => setTimeout(r, 1000)); } return false; };
const waitText = async (p, re, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (re.test(await p.textContent('main').catch(() => '') || '')) return true; await p.waitForTimeout(1000); } return false; };
(async () => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, '--no-host', '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', '--no-obs', '--site-dir=' + SITE, '--app-dir=' + APP]);
  P.stdout.on('data', d => { for (const l of String(d).split('\n')) if (/claim|rror/.test(l)) log('[node]', l.trim().slice(0, 160)); });
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); break; } catch { execSync('sleep 0.5'); } }
  const BASE = `http://localhost:${N}/`; const errs = [];
  const adFile = W + '/ad.mp4';
  execSync(`ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc=size=320x180:rate=25 -f lavfi -i sine=frequency=660 -t 6 -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -movflags +faststart ${adFile}`);
  // O: the advertiser, on the JavaScript page: a channel with coins and a video; OC: the same person in the C# pages
  const co = await chromium.launchPersistentContext(W + '/o', { headless: true }); const O = co.pages()[0];
  await O.goto(BASE + '?folder=browser#/you/channels'); await O.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); await O.waitForTimeout(800);
  await O.evaluate(() => opentube.engine.submitTx({ starter: true }, opentube.me)).catch(() => {});
  await O.fill('input[placeholder=Name]', 'Shoe Co'); await O.click('text=Create & register');
  await O.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await O.evaluate(() => location.hash.split('/')[2]);
  await until(O, (c) => opentube.engine.directory.has(c), chain, 180000);
  const video = await O.evaluate(async (c) => (await opentube.engine.append(c, { type: 'video', title: 'Shoe review', desc: '', infohash: '34'.repeat(20), file: 'v.mp4', size: 10, thumb: '' })).hash, chain);
  const owner = await O.evaluate((c) => opentube.engine.derive(c).owner, chain), me = await O.evaluate(() => opentube.me.pub);
  check(await until(O, ({ k, m }) => opentube.engine.ledger.resolve(k) === m && opentube.engine.ledger.balance(k) >= 1000, { k: owner, m: me }, 300000), 'the advertiser’s channel is bound to them, with coins');
  const OC = await co.newPage(); OC.on('pageerror', e => errs.push(e.message));
  await OC.goto(BASE + `app/c/${chain}/advertise`); await OC.waitForSelector('text=Create an ad', { timeout: 120000 });
  await OC.setInputFiles('input[type=file]', adFile); await OC.waitForSelector('text=6 s long', { timeout: 20000 }).catch(() => {});
  await OC.fill('input[maxlength="140"]', 'Run faster'); await OC.selectOption('select:has(option[value="sports"])', 'sports');
  await OC.fill('input[inputmode="decimal"]', '0.20'); await OC.fill('input[type=number]', '10');
  await OC.click('button:has-text("Publish ad")');
  check(await until(O, (c) => opentube.engine.derive(c).ads.some(b => b.payload.title === 'Run faster' && b.payload.terms.duration === 6), chain, 120000), 'the C# side publishes an ad (the JavaScript page accepts its signed terms)');
  const tid = await O.evaluate(async (c) => opentube.engine.termsId(opentube.engine.derive(c).ads[0].payload.terms), chain);
  // X: a viewer on the C# pages
  const cx = await chromium.launchPersistentContext(W + '/x', { headless: true, args: ['--autoplay-policy=no-user-gesture-required'] }); const X = cx.pages()[0]; X.on('pageerror', e => errs.push(e.message));
  await X.goto(BASE + 'app/you'); await X.waitForSelector('.mono', { timeout: 60000 }); const x = (await X.textContent('.panel .mono')).trim();
  await X.goto(BASE + 'app/terms'); await X.click('button:has-text("I agree")').catch(() => {});
  await X.goto(BASE + `app/v/${chain}/${video}`);
  check(await X.waitForSelector('#ad-panel', { timeout: 60000 }).then(() => true, () => false), 'before the video, an ad: ' + (await X.textContent('#ad-panel').catch(() => '-')).replace(/\s+/g, ' ').slice(0, 100));
  check(await X.waitForSelector('#ad-video', { timeout: 60000 }).then(() => true, () => false), 'the ad file arrives over BitTorrent and plays: ' + (await X.textContent('#ad-status').catch(() => '-')));
  check(await X.waitForSelector('#ad-result', { timeout: 90000 }).then(() => true, () => false), 'the ad finishes: ' + (await X.textContent('#ad-result').catch(() => '-')));
  check(await until(O, ({ t, k }) => (opentube.engine.ledger.adViews.get(t) || 0) >= 1 && [...opentube.engine.ledger.certs.values()].some(c => c.owner === k), { t: tid, k: x }, 180000), 'the view is claimed: the JavaScript page’s ledger counts it and the viewer holds a certificate');
  await X.goto(BASE + 'app/market/adviews-mine'); check(await waitText(X, /1 × Shoe Co/, 60000), 'Market → Your ad views lists the certificate');
  const before = await O.evaluate((k) => opentube.engine.ledger.balance(k), x);
  await X.click('button:has-text("Cash in")');
  check(await until(O, ({ k, b }) => opentube.engine.ledger.balance(k) > b, { k: x, b: before }, 180000), 'cashing it in pays the viewer');
  await OC.goto(BASE + `app/c/${chain}/advertise`); check(await waitText(OC, /1 of 10 views/, 60000), 'the advertiser’s tab counts the view');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await co.close(); await cx.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
