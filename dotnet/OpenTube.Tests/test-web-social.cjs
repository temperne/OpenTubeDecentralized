// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-social.cjs <dir>/wwwroot
// Posts and friends in the C# pages (website and phone app), with the JavaScript page on the other side: public posts,
// replies and a paid like both ways; friends pages, sealed posts each side decrypts from the other, a sealed reply.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-social-')), N = 8887, APP = path.resolve(process.argv[2]);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
let P; process.on('exit', () => { try { P?.kill(); } catch {} });
// (reload, when given, at most every 15 s: reloading restarts the C# app)
const waitText = async (p, sel, re, ms = 120000, reload = null) => { const t0 = Date.now(); let last = t0; while (Date.now() - t0 < ms) { const t = await p.textContent(sel).catch(() => ''); if (re.test(t || '')) return true; await p.waitForTimeout(1500); if (reload && Date.now() - last > 15000) { last = Date.now(); await reload(); await p.waitForTimeout(3000); } } return false; };
(async () => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, '--no-host', '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', '--no-obs', '--site-dir=' + SITE, '--app-dir=' + APP]);
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); break; } catch { execSync('sleep 0.5'); } }
  const BASE = `http://localhost:${N}`;
  const ca = await chromium.launchPersistentContext(W + '/a', { headless: true }); const A = ca.pages()[0];
  await A.goto(BASE + '/?folder=browser#/square'); await A.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); await A.waitForTimeout(1500);
  const a = await A.evaluate(() => opentube.me.pub);
  const cc = await chromium.launchPersistentContext(W + '/c', { headless: true, viewport: { width: 412, height: 860 }, isMobile: true }); const C = cc.pages()[0]; const errs = [];
  C.on('pageerror', e => errs.push(e.message));
  await C.goto(BASE + '/app/you'); await C.waitForSelector('.mono', { timeout: 60000 }); const c = (await C.textContent('.mono')).trim();

  // ---- posts
  await C.goto(BASE + '/app/posts'); await C.waitForSelector('textarea', { timeout: 30000 });
  await C.fill('textarea', 'A public post from C# ✓'); await C.click('button:has-text("Post")');
  check(await waitText(C, 'main', /A public post from C# ✓/, 60000), 'the C# side posts publicly (it shows in Posts)');
  check(await waitText(A, 'main', /A public post from C# ✓/, 90000, () => A.evaluate(() => views.square?.refresh?.()).catch(() => {})), 'the JavaScript page sees it');
  await A.fill('textarea[placeholder="What’s happening?"]', 'A public post from JavaScript'); await A.click('button:has-text("Post")');
  check(await waitText(C, 'main', /A public post from JavaScript/, 90000), 'the C# side sees the JavaScript page’s post');
  // the JavaScript side replies to the C# post; the C# thread shows it
  const href = await A.evaluate(() => [...document.querySelectorAll('a')].find(x => /repl/.test(x.textContent) && x.closest('.item')?.textContent.includes('A public post from C#'))?.getAttribute('href'));
  await A.goto(BASE + '/?folder=browser' + href); await A.waitForSelector('input[placeholder="Reply…"]', { timeout: 30000 });
  await A.fill('input[placeholder="Reply…"]', 'Reply from JavaScript'); await A.click('button:has-text("Reply")');
  await C.click('a.sub:near(:text("A public post from C# ✓"))').catch(async () => { await C.goto(BASE + '/app/posts'); });
  const thread = href.replace('#/square/', '/app/posts/');
  await C.goto(BASE + thread);
  check(await waitText(C, 'main', /Reply from JavaScript/, 90000), 'the C# thread shows the reply');
  await C.fill('input[placeholder="Reply…"]', 'Reply from C#'); await C.click('button:has-text("Reply")');
  check(await waitText(A, 'main', /Reply from C#/, 90000, () => A.evaluate(() => views.square?.refresh?.()).catch(() => {})), 'the JavaScript page sees the C# reply');
  // a paid like from the C# side on the JavaScript post (first its starter coins)
  await C.goto(BASE + '/app/you'); await C.click('button:has-text("starter coins")', { timeout: 30000 });
  check(await waitText(C, 'main', /Coins: (?!0\.00)\d/, 300000, () => C.goto(BASE + '/app/you')), 'the C# side earns its starter coins');
  await C.goto(BASE + '/app/posts'); await C.waitForSelector('text=A public post from JavaScript');
  await C.click('.post:has-text("A public post from JavaScript") button:has-text("👍")');
  await C.waitForTimeout(4000); log('C# like status:', (await C.textContent('#post-status').catch(() => 'none')), '| C# shows', await C.textContent('.post:has-text("A public post from JavaScript") button:has-text("👍")'));
  await A.goto(BASE + '/?folder=browser#/square');
  check(await waitText(A, 'main', /👍 1/, 120000, () => A.evaluate(() => views.square?.refresh?.()).catch(() => {})), 'a like from the C# side counts on the JavaScript page (paid)');

  // ---- friends
  await A.goto(BASE + '/?folder=browser#/friends'); await A.click('text=Start my friends page', { timeout: 60000 });
  await C.goto(BASE + '/app/friends'); await C.click('text=Start my friends page', { timeout: 60000 });
  await A.goto(BASE + '/?folder=browser#/you/friends');   // (once the page exists, the friend list is under You → Friends)
  await A.waitForSelector('input[placeholder="A friend’s user ID"]', { timeout: 60000 }); await A.fill('input[placeholder="A friend’s user ID"]', c); await A.fill('input[placeholder="Their name (for you)"]', 'Cee'); await A.click('button:has-text("Add friend")');
  await C.fill('input[placeholder="A friend’s user ID"]', a); await C.fill('input[placeholder="Their name (for you)"]', 'Ay'); await C.click('button:has-text("Add friend")');
  // both pages registered (mined), then a sealed post each way
  check(await waitText(C, 'main', /Post to friends/, 120000, () => C.goto(BASE + '/app/friends')) && await waitText(C, 'main', /^(?![\s\S]*hasn’t started)[\s\S]*Ay/, 120000, () => C.goto(BASE + '/app/friends')), 'both friends pages registered; the C# side has A as a friend');
  await A.goto(BASE + '/?folder=browser#/friends'); await A.waitForTimeout(2000);
  await A.waitForSelector('textarea:not([disabled])', { timeout: 60000 });
  await A.fill('textarea', 'Sealed from JavaScript'); await A.click('button:has-text("Post to friends")');
  check(await waitText(C, 'main', /Sealed from JavaScript/, 120000, () => C.goto(BASE + '/app/friends')), 'the C# side decrypts the JavaScript friend’s sealed post');
  await C.fill('textarea', 'Sealed from C# ✓'); await C.click('button:has-text("Post to friends")');
  check(await waitText(A, 'main', /Sealed from C# ✓/, 120000, () => A.goto(BASE + '/?folder=browser#/friends')), 'the JavaScript page decrypts the C# side’s sealed post');
  await C.fill('.post:has-text("Sealed from JavaScript") input', 'Sealed reply from C#'); await C.click('.post:has-text("Sealed from JavaScript") button:has-text("Reply")');
  check(await waitText(A, 'main', /Sealed reply from C#/, 120000, () => A.goto(BASE + '/?folder=browser#/friends')), 'a sealed reply from the C# side reaches the JavaScript friend');
  const onChain = await A.evaluate(() => JSON.stringify([...opentube.engine.chains.values()].filter(c => c.genesis.payload.kind === 'circle').flatMap(c => [...c.blocks.values()].map(b => b.payload))));
  check(!/Sealed from|Sealed reply/.test(onChain), 'on the chain the friends posts are unreadable');
  await C.screenshot({ path: W + '/friends.png' }); log('screenshot', W + '/friends.png');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await ca.close(); await cc.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
