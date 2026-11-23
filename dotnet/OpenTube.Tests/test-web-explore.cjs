// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-explore.cjs <dir>/wwwroot
// Home, Explore, tags and the terms banner in the C# pages (website and phone app), against the JavaScript page: a paid
// video ranks in Trending; New, Channels and search; a tag page and "not interested"; subscribing fills Home (videos and
// the timeline); a public post from Home; the terms banner.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-explore-')), N = 8890, APP = path.resolve(process.argv[2]);
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
  // O: a creator on the JavaScript page: a channel and a paid video tagged #cooking
  const co = await chromium.launchPersistentContext(W + '/o', { headless: true }); const O = co.pages()[0];
  await O.goto(BASE + '?folder=browser#/you/channels'); await O.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); await O.waitForTimeout(800);
  await O.evaluate(() => opentube.engine.submitTx({ starter: true }, opentube.me)).catch(() => {});
  await O.fill('input[placeholder=Name]', 'Kitchen Channel'); await O.click('text=Create & register');
  await O.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await O.evaluate(() => location.hash.split('/')[2]);
  await until(O, (c) => opentube.engine.directory.has(c), chain, 180000);
  const me = await O.evaluate(() => opentube.me.pub);
  check(await until(O, (k) => opentube.engine.ledger.balance(k) >= 1000, me, 300000), 'the creator has coins to post');
  const video = await O.evaluate(async (c) => {
    const vb = await opentube.engine.append(c, { type: 'video', title: 'Pasta night', desc: 'How to cook pasta', infohash: 'ef'.repeat(20), file: 'v.mp4', size: 1000, thumb: '', tags: ['cooking'] });
    const amt = OpenTubeCore.postCost(opentube.engine.ledger.prices.post, 1000);
    await opentube.engine.submitTx({ post: { chain: c, video: vb.hash, size: 1000, amt } }, opentube.me); return vb.hash;
  }, chain);
  check(await until(O, (v) => { const b = [...opentube.engine.chains.values()].flatMap(c => [...c.blocks.values()]).find(b => b.hash === v); return b && opentube.engine.postPaid(b); }, video, 180000), 'the video’s posting fee is paid');
  // X: a viewer on the C# pages
  const cx = await chromium.launchPersistentContext(W + '/x', { headless: true }); const X = cx.pages()[0]; X.on('pageerror', e => errs.push(e.message));
  await X.goto(BASE + 'app/'); await X.waitForSelector('main h2', { timeout: 60000 });
  check(await X.isVisible('.terms-banner'), 'the terms banner shows');
  await X.click('.terms-banner button:has-text("I agree")'); check(await X.waitForSelector('.terms-banner', { state: 'detached', timeout: 10000 }).then(() => true, () => false), 'agreeing hides it');
  check(await waitText(X, /You don’t follow anything yet/, 30000), 'Home without subscriptions suggests Explore');
  check(await waitText(X, /Pasta night/, 120000), 'and shows trending videos');
  await X.goto(BASE + 'app/explore'); check(await waitText(X, /Pasta night/, 120000), 'Explore → Trending lists the video');
  check(await X.isVisible('.rank-n:has-text("#1")'), 'ranked #1');
  await X.click('.row-actions button:has-text("This week")'); check(await waitText(X, /Pasta night/, 10000), 'the “This week” view');
  await X.fill('input.search', 'nothing-like-this'); check(await waitText(X, /No videos match/, 10000), 'search filters videos');
  await X.fill('input.search', 'pasta'); check(await waitText(X, /Pasta night/, 10000), 'search finds it');
  await X.click('.tabs a:has-text("New")'); check(await waitText(X, /Pasta night/, 30000), 'New lists it');
  await X.click('.tabs a:has-text("Channels")'); await X.fill('input.search', ''); check(await waitText(X, /Kitchen Channel/, 30000), 'Channels lists the channel');
  check(!(await X.textContent('.terms-banner').catch(() => null)), 'the terms banner stays away');
  // tags
  await X.goto(BASE + 'app/tag/cooking'); check(await waitText(X, /Pasta night/, 60000), 'the #cooking tag page lists the video');
  await X.click('button:has-text("Not interested")'); check(await waitText(X, /Show this tag again/, 10000), '“Not interested” in #cooking');
  await X.goto(BASE + 'app/explore'); await X.waitForTimeout(3000); check(!(await waitText(X, /Pasta night/, 5000)), 'Explore hides videos tagged #cooking now');
  await X.goto(BASE + 'app/tag/cooking'); await X.click('button:has-text("Show this tag again")'); await X.waitForTimeout(500);
  // subscribe: Home fills
  await X.goto(BASE + `app/c/${chain}`); await X.click('button:has-text("Subscribe")');
  await X.goto(BASE + 'app/'); check(await waitText(X, /Kitchen Channel posted a video/, 60000), 'after subscribing, Home’s timeline shows the channel’s video');
  await X.click('button:has-text("Videos")'); check(await waitText(X, /Pasta night/, 30000), 'and the Videos view lists it');
  // a public post from Home
  await X.click('button:has-text("Everything")'); await X.fill('textarea', 'Hello from Home #cooking');
  await X.click('.panel button:has-text("Post")');
  await O.goto(BASE + '?folder=browser#/square'); await O.waitForTimeout(2000);
  check(await until(O, () => [...opentube.engine.chains.values()].filter(c => c.genesis.payload.kind === 'square').some(c => opentube.engine.derive(c.id).posts.some(b => b.payload.text === 'Hello from Home #cooking')), null, 120000), 'a public post from Home reaches the JavaScript page');
  check(await waitText(X, /Hello from Home/, 60000), 'and shows in your timeline');
  await X.goto(BASE + 'app/tag/cooking'); check(await waitText(X, /Hello from Home/, 60000), 'the tag page lists posts mentioning #cooking');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await co.close(); await cx.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
