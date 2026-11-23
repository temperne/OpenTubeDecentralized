// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web.cjs <dir>/wwwroot
// The C# website (WebAssembly) served by a .NET node at /app/: it starts, links to the node, gets the chains,
// lists channels, opens one, creates a channel (proof of work in WebAssembly), and keeps the browser's key.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-web-')), N = 8909, APP = path.resolve(process.argv[2]);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let P; process.on('exit', () => { try { P?.kill(); } catch {} });
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
(async () => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, '--no-host', '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', '--no-obs', '--site-dir=' + SITE, '--app-dir=' + APP]);
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); break; } catch { execSync('sleep 0.5'); } }
  const ctx = await chromium.launchPersistentContext(W + '/b', { headless: true }); const J = ctx.pages()[0], errs = [];
  // first the JavaScript page makes a channel, so there's something to see (and a key in this browser)
  await J.goto(`http://localhost:${N}/?folder=browser#/you/channels`); await J.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); await J.waitForTimeout(800);
  await J.fill('input[placeholder=Name]', 'Made in JavaScript'); await J.click('text=Create & register');
  await J.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await J.evaluate(() => location.hash.split('/')[2]);
  await J.waitForFunction((c) => opentube.engine.directory.has(c), chain, { timeout: 120000 });
  const jsKey = await J.evaluate(() => opentube.me.pub); log('the JavaScript page registered', chain.slice(0, 8), 'as', jsKey.slice(0, 8));
  const P2 = await ctx.newPage(); const allLog = []; P2.on('console', m => allLog.push(m.type() + ': ' + m.text())); P2.on('pageerror', e => errs.push(e.message)); P2.on('console', m => { if (m.type() === 'error') errs.push(m.text()); });
  const t0 = Date.now(); await P2.goto(`http://localhost:${N}/app/`);
  await P2.waitForSelector('header.top', { timeout: 120000 }); log('the C# site started in', ((Date.now() - t0) / 1000).toFixed(1), 's');
  await P2.waitForFunction(() => /[1-9]\d* nodes?/.test(document.querySelector('.net')?.textContent || ''), null, { timeout: 60000 }).catch(() => {});
  check(/1 node/.test(await P2.textContent('.net')), 'it links to the node: ' + (await P2.textContent('.net')).replace(/\s+/g, ' ').trim());
  await P2.click('nav >> text=You'); await P2.waitForSelector('.mono');
  check((await P2.textContent('.mono')).trim() === jsKey, 'it uses the key this browser already had (shared with the JavaScript page)');
  await P2.click('nav >> text=Explore');
  await P2.waitForFunction(() => /Made in JavaScript/.test(document.body.textContent), null, { timeout: 60000 }).catch(() => {});
  check(/Made in JavaScript/.test(await P2.textContent('main')), 'Explore lists the channel registered on the network');
  await P2.click('text=Made in JavaScript'); await P2.waitForFunction(() => document.querySelector('.channel-head h2')?.textContent === 'Made in JavaScript', null, { timeout: 60000 }).catch(() => {});
  check((await P2.textContent('main')).includes('videos'), 'its channel page loads from the network');
  await P2.click('nav >> text=You'); await P2.click('.tabs >> text=Channels'); await P2.fill('input[placeholder=Name]', 'Made in C#'); const t1 = Date.now(); await P2.click('text=Create & register');
  await P2.waitForURL(/\/app\/c\/[0-9a-f]{64}/, { timeout: 180000 }).catch(async e => { log('page says:', (await P2.innerHTML('body')).slice(0, 3000), errs.join(' | '), allLog.join('\n')); throw e; }); const cs = P2.url().split('/c/')[1];
  log('created in C# after', ((Date.now() - t1) / 1000).toFixed(1), 's:', cs.slice(0, 8));
  await J.waitForFunction((c) => opentube.engine.directory.has(c), cs, { timeout: 180000 }).then(() => check(true, `the JavaScript page sees the channel made in C# registered (${((Date.now() - t1) / 1000).toFixed(1)} s)`)).catch(() => check(false, 'the channel made in C# got registered'));
  await P2.click('nav >> text=You'); await P2.waitForFunction(() => /Made in JavaScript/.test(document.body.textContent) && /Made in C#/.test(document.body.textContent), null, { timeout: 30000 }).catch(() => {});
  check(/Made in JavaScript[\s\S]*Made in C#|Made in C#[\s\S]*Made in JavaScript/.test(await P2.textContent('main')), 'Your channels lists both (the channel keys are shared with the JavaScript page)');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await P2.screenshot({ path: W + '/web.png' }); log('screenshot:', W + '/web.png');
  await ctx.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
