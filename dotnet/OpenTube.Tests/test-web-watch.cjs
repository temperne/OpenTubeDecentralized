// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-watch.cjs <dir>/wwwroot
// The watch page in the C# pages (website and phone app), against the JavaScript page: the description formatted,
// tags, comments (posted and read both ways), likes paid to the creator and to a commenter, a tip, the owner's tools
// (hiding a comment, turning comments off) and adding the video to a list.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-watch-')), N = 8886, APP = path.resolve(process.argv[2]);
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
  // O: the creator, on the JavaScript page: a channel and a video (its file needn't exist for this test)
  const co = await chromium.launchPersistentContext(W + '/o', { headless: true }); const O = co.pages()[0];
  await O.goto(BASE + '?folder=browser#/you/channels'); await O.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); await O.waitForTimeout(800);
  await O.fill('input[placeholder=Name]', 'Watch Channel'); await O.click('text=Create & register');
  await O.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await O.evaluate(() => location.hash.split('/')[2]);
  await until(O, (c) => opentube.engine.directory.has(c), chain, 180000);
  const video = await O.evaluate(async (c) => (await opentube.engine.append(c, { type: 'video', title: 'Formatted video', desc: 'This is **bold** and *italic*.\n- one\n- two\nSee https://example.org/page #cooking', infohash: 'ab'.repeat(20), file: 'v.mp4', size: 1234, thumb: '', tags: ['cooking', 'travel'] })).hash, chain);
  // X: a viewer on the C# pages
  const cx = await chromium.launchPersistentContext(W + '/x', { headless: true }); const X = cx.pages()[0]; X.on('pageerror', e => errs.push(e.message));
  await X.goto(BASE + 'app/you'); await X.waitForSelector('.mono', { timeout: 60000 }); const x = (await X.textContent('.mono')).trim();
  await X.click('button:has-text("starter coins")');
  await X.goto(BASE + `app/v/${chain}/${video}`);
  check(await waitText(X, /Formatted video/, 120000), 'the C# watch page shows the video');
  check(await X.isVisible('.rich b:has-text("bold")') && await X.isVisible('.rich i:has-text("italic")') && (await X.$$('.rich li')).length === 2 && await X.isVisible('.rich a.ext[href="https://example.org/page"]'), 'the description is formatted (bold, italic, a list, a link)');
  check(await X.isVisible('a.tag[href="tag/cooking"]') && await X.isVisible('a.tag[href="tag/travel"]'), 'its tags link to tag pages');
  // a comment from C#, read on the JavaScript page
  await X.fill('#comment-box', 'Hello from **C#**'); await X.click('button:has-text("Comment")');
  check(await until(O, ({ c, v }) => opentube.engine.derive(c).commentsFor(v).some(b => b.payload.text === 'Hello from **C#**'), { c: chain, v: video }, 120000), 'a comment from the C# page reaches the JavaScript page');
  // a comment from the JavaScript page, read in C#
  await O.evaluate(async ({ c, v }) => opentube.engine.append(c, { type: 'comment', target: v, text: 'Owner reply', name: 'Owner' }), { c: chain, v: video });
  check(await waitText(X, /Owner reply/, 60000), 'the owner’s comment shows in C#');
  check(await X.isVisible('.comment:has-text("Owner reply") .badge:has-text("owner")'), 'marked as the owner');
  check(await X.isVisible('.comment b:has-text("C#")'), 'comments are formatted too');
  // likes, paid
  check(await until(O, (me) => opentube.engine.ledger.balance(me) > 0, x, 240000), 'the viewer has starter coins');
  await X.click('.watch-main > .row-actions button:has-text("👍")');
  check(await until(O, ({ c, v, me }) => (opentube.engine.derive(c).votes.get(v)?.blocks || []).some(b => b.author === me && opentube.engine.isPaid('vote:' + b.hash, b.author)), { c: chain, v: video, me: x }, 180000), 'liking the video: a vote, paid to the creator (the JavaScript page counts it)');
  check(await waitText(X, /👍 1/, 120000), 'and the C# page shows 👍 1');
  const owner = await O.evaluate((c) => opentube.engine.derive(c).owner, chain);
  const ownerComment = await O.evaluate(({ c, v }) => opentube.engine.derive(c).commentsFor(v).find(b => b.payload.text === 'Owner reply').hash, { c: chain, v: video });
  await X.click('.comment:has-text("Owner reply") button:has-text("👍")');
  check(await until(O, ({ c, t }) => (opentube.engine.derive(c).votes.get(t)?.blocks || []).some(b => opentube.engine.isPaid('vote:' + b.hash, b.author)), { c: chain, t: ownerComment }, 180000), 'liking a comment pays its author');
  // tip
  const before = await O.evaluate((k) => opentube.engine.ledger.balance(k), owner);
  await X.click('button:has-text("Tip")'); await X.fill('.panel input[type=number]', '0.5'); await X.click('.panel button:has-text("Send")');
  check(await waitText(X, /Tipped 0.5 coins/, 60000), 'a tip is sent');
  check(await until(O, ({ k, b }) => opentube.engine.ledger.balance(k) >= b + 50, { k: owner, b: before }, 180000), 'the channel’s owner receives it');
  // the owner, in the C# pages (same browser as the JavaScript page: same keys)
  const OC = await co.newPage(); OC.on('pageerror', e => errs.push(e.message));
  await OC.goto(BASE + `app/v/${chain}/${video}`); check(await waitText(OC, /Hello from/, 120000), 'the owner opens the C# watch page');
  check(!(await OC.isVisible('button:has-text("Tip")')) && await OC.isVisible('button:has-text("Hide video")'), 'the owner sees owner tools (no tip, “Hide video”)');
  await OC.click('.comment:has-text("Hello from") button:has-text("Hide")');
  check(await until(O, ({ c, v }) => { const d = opentube.engine.derive(c); const b = d.commentsFor(v).find(b => b.payload.text === 'Hello from **C#**'); return b && d.hidden.has(b.hash); }, { c: chain, v: video }, 120000), 'the owner hides the viewer’s comment (on the chain)');
  check(await until(X, () => !!document.querySelector('.comment.hidden-item'), null, 60000), 'the viewer sees it marked hidden');
  await OC.selectOption('select[title="Who can comment on this video"]', 'off');
  check(await until(O, ({ c, v }) => opentube.engine.derive(c).policyFor(v) === 'off', { c: chain, v: video }, 120000), 'the owner turns comments off for this video');
  check(await until(X, () => document.querySelector('#comment-box')?.disabled && /Comments are off/.test(document.querySelector('#comment-box').placeholder), null, 60000), 'the viewer can no longer comment (“Comments are off”)');
  // add to a list (the owner makes one on the JavaScript page)
  await O.goto(BASE + '?folder=browser#/you/channels'); await O.waitForTimeout(800);
  await O.selectOption('select', 'list').catch(() => {}); await O.fill('input[placeholder=Name]', 'Best of'); await O.click('text=Create & register');
  await O.waitForFunction((c) => location.hash.startsWith('#/c/') && !location.hash.includes(c), chain, { timeout: 180000 }); const list = await O.evaluate(() => location.hash.split('/')[2]);
  await until(O, (l) => opentube.engine.directory.has(l), list, 180000);
  await OC.goto(BASE + `app/v/${chain}/${video}`); await OC.waitForSelector('select:has(option:has-text("Add to list"))', { timeout: 120000 });
  await OC.selectOption('select:has(option:has-text("Add to list"))', list);
  check(await until(O, ({ l, v }) => [...(opentube.engine.chains.get(l)?.blocks.values() || [])].some(b => b.payload.type === 'add' && b.payload.video === v), { l: list, v: video }, 120000), 'adding the video to your list (an “add” block on the list)');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await co.close(); await cx.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
