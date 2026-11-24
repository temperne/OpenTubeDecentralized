// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-maps.cjs <dir>/wwwroot
// Places in the C# pages (website and phone app): a channel's place set with the place picker ("Use my location"), a
// listing with a place; the JavaScript page reads both; the Market's Map tab lists them. (The map itself comes from
// OpenStreetMap: shown when this machine can reach it.)
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-maps-')), N = 8897, APP = path.resolve(process.argv[2]);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
let P; process.on('exit', () => { try { P?.kill(); } catch {} });
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return true; await new Promise(r => setTimeout(r, 1000)); } return false; };
const waitText = async (p, re, ms = 60000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (re.test(await p.textContent('main').catch(() => '') || '')) return true; await p.waitForTimeout(1000); } return false; };
(async () => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, '--no-host', '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', '--no-obs', '--site-dir=' + SITE, '--app-dir=' + APP]);
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); break; } catch { execSync('sleep 0.5'); } }
  const BASE = `http://localhost:${N}/`; const errs = [];
  const co = await chromium.launchPersistentContext(W + '/o', { headless: true, geolocation: { latitude: 38.72225, longitude: -9.13934 }, permissions: ['geolocation'] }); const O = co.pages()[0];
  await O.goto(BASE + '?folder=browser#/you/channels'); await O.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); await O.waitForTimeout(800);
  await O.evaluate(() => opentube.engine.submitTx({ starter: true }, opentube.me)).catch(() => {});
  await O.fill('input[placeholder=Name]', 'Cafe Lisboa'); await O.click('text=Create & register');
  await O.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await O.evaluate(() => location.hash.split('/')[2]);
  await until(O, (c) => opentube.engine.directory.has(c), chain, 180000);
  const C = await co.newPage(); C.on('pageerror', e => errs.push(e.message));
  await C.goto(BASE + `app/c/${chain}/manage`); await C.waitForSelector('.place-picker', { timeout: 120000 });
  await C.click('.place-picker button:has-text("Use my location")');
  check(await waitText(C, /📍 38\.72225, -9\.13934/, 20000), 'the place picker takes your location');
  await C.fill('.place-picker input[placeholder^="Place name"]', 'Rua Augusta, Lisbon');
  await C.click('.panel:has(h3:text-is("Profile")) button:has-text("Save")');
  check(await until(O, (c) => { const p = opentube.engine.derive(c).profile.place; return p?.lat === 38.72225 && p?.lon === -9.13934 && p.label === 'Rua Augusta, Lisbon'; }, chain, 120000), 'the channel’s place is on its chain (the JavaScript page reads it)');
  // a listing with a place
  check(await until(O, () => opentube.engine.ledger.balance(opentube.me.pub) >= 1000, null, 300000), 'coins for a listing');
  await C.goto(BASE + 'app/market/new'); await C.waitForSelector('text=List it', { timeout: 60000 });
  await C.fill('input[maxlength="120"]', 'Pastel de nata'); await C.fill('input[inputmode="decimal"]', '1');
  await C.click('.place-picker button:has-text("Use my location")'); await C.waitForTimeout(500);
  await C.click('button:has-text("List it")');
  check(await until(O, () => [...opentube.engine.ledger.listings.values()].some(L => L.title === 'Pastel de nata' && L.place?.lat === 38.72225), null, 180000), 'a listing with a place');
  await C.goto(BASE + 'app/market/map'); check(await waitText(C, /2 place\(s\) known/, 60000), 'the Market’s Map tab knows both places');
  const mapLoaded = await C.waitForSelector('.osm .leaflet-marker-icon', { timeout: 20000 }).then(() => true, () => false);
  log(mapLoaded ? 'the map (OpenStreetMap) shows the pins' : '(the map library isn’t reachable from here: pins not drawn)');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await co.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
