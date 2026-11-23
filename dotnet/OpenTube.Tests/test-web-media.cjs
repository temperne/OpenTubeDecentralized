// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-media.cjs <dir>/wwwroot   (needs ffmpeg)
// Your media library in the C# pages (website and phone app): add a picture (private, kept on this device), attach it
// to a public post (shared over BitTorrent from this device), and another person sees it: in the C# pages and on the
// JavaScript page (same torrent). Kept after a reload, and deleted from the library.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-media-')), N = 8893, APP = path.resolve(process.argv[2]);
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
  const pic = W + '/sunset.png'; execSync(`ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc=size=320x200 -frames:v 1 ${pic}`);
  // X: the C# pages
  const cx = await chromium.launchPersistentContext(W + '/x', { headless: true }); const X = cx.pages()[0]; X.on('pageerror', e => errs.push(e.message));
  await X.goto(BASE + 'app/you/media'); await X.waitForSelector('text=Add pictures or videos', { timeout: 60000 });
  await X.setInputFiles('input[type=file]', pic);
  check(await X.waitForSelector('.media-card:has-text("sunset.png") img', { timeout: 30000 }).then(() => true, () => false), 'adding a picture to My media (with a thumbnail): private');
  check(await waitText(X, /private/, 5000), 'marked private');
  await X.reload(); check(await X.waitForSelector('.media-card:has-text("sunset.png")', { timeout: 60000 }).then(() => true, () => false), 'it’s still there after a reload (kept on this device)');
  // attach it to a public post
  await X.goto(BASE + 'app/posts'); await X.waitForSelector('textarea', { timeout: 60000 });
  await X.fill('textarea', 'Look at this sunset'); await X.click('button:has-text("📎 Attach")');
  await X.click('.media-pick'); await X.click('button:has-text("Post")');
  // J: the JavaScript page
  const cj = await chromium.launchPersistentContext(W + '/j', { headless: true }); const J = cj.pages()[0];
  await J.goto(BASE + '?folder=browser#/square'); await J.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 });
  check(await until(J, () => [...opentube.engine.chains.values()].filter(c => c.genesis.payload.kind === 'square').some(c => opentube.engine.derive(c.id).posts.some(b => b.payload.text === 'Look at this sunset' && b.payload.media?.[0]?.mime === 'image/png')), null, 120000), 'the post carries the picture’s reference (the JavaScript page reads it)');
  await J.evaluate(() => views.square?.refresh?.()).catch(() => {}); await J.goto(BASE + '?folder=browser#/square');
  check(await J.waitForSelector('.media-item img[src^="blob:"]', { timeout: 120000 }).then(() => true, () => false), 'the JavaScript page downloads the picture from the C# side over BitTorrent and shows it');
  // Y: someone else in the C# pages
  const cy = await chromium.launchPersistentContext(W + '/y', { headless: true }); const Y = cy.pages()[0]; Y.on('pageerror', e => errs.push(e.message));
  await Y.goto(BASE + 'app/posts'); await Y.waitForSelector('text=Look at this sunset', { timeout: 120000 });
  check(await Y.waitForSelector('.post:has-text("Look at this sunset") .media-item img[src^="blob:"]', { timeout: 120000 }).then(() => true, () => false), 'another person in the C# pages sees the picture (fetched over BitTorrent)');
  await X.goto(BASE + 'app/you/media'); check(await waitText(X, /shared 1×/, 30000), 'the library says it was shared once');
  await X.click('.media-card button:has-text("Delete")'); await X.click('.media-card button:has-text("Really delete?")');
  check(await waitText(X, /No pictures or videos yet/, 30000), 'deleting it from the library');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await cx.close(); await cy.close(); await cj.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
