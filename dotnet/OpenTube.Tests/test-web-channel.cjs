// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-channel.cjs <dir>/wwwroot
// The channel page in the C# pages (website and phone app), against the JavaScript page: the owner claims a username,
// edits the profile (about, location, a link), posts; a viewer replies and likes; a group's discussion; a list's
// channels and feed; blocking a commenter; and @name links.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-channel-')), N = 8887, APP = path.resolve(process.argv[2]);
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
  // O: the owner, on the JavaScript page: a channel with a video, a group and a list
  const co = await chromium.launchPersistentContext(W + '/o', { headless: true }); const O = co.pages()[0];
  await O.goto(BASE + '?folder=browser#/you/channels'); await O.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); await O.waitForTimeout(800);
  const create = async (kind, name) => {
    await O.goto(BASE + '?folder=browser#/you/channels'); await O.waitForTimeout(800);
    if (kind !== 'channel') await O.selectOption('select', kind);
    await O.fill('input[placeholder=Name]', name); await O.click('text=Create & register');
    await O.waitForFunction((n) => location.hash.startsWith('#/c/') && opentube.engine.derive(location.hash.split('/')[2])?.profile.name === n, name, { timeout: 180000 });
    const id = await O.evaluate(() => location.hash.split('/')[2]); await until(O, (c) => opentube.engine.directory.has(c), id, 180000); return id;
  };
  const chain = await create('channel', 'Tab Channel');
  await O.evaluate(async (c) => opentube.engine.append(c, { type: 'video', title: 'Listed video', desc: '', infohash: 'cd'.repeat(20), file: 'v.mp4', size: 10, thumb: '' }), chain);
  const group = await create('group', 'Tab Group');
  const list = await create('list', 'Tab List');
  log('channel, group and list created');
  // the owner in the C# pages (same browser: same keys)
  const OC = await co.newPage(); OC.on('pageerror', e => errs.push(e.message));
  await OC.goto(BASE + `app/c/${chain}/manage`); await OC.waitForSelector('#name-input', { timeout: 120000 });
  await OC.fill('#name-input', 'tabchannel'); check(await waitText(OC, /@tabchannel is available/, 10000), 'username: the page says it’s available');
  await OC.click('button:has-text("Claim name")');
  check(await until(O, (c) => opentube.engine.ledger.nameOf?.get?.(c) === '@tabchannel' || opentube.engine.ledger.names.get('@tabchannel')?.chain === c, chain, 240000), 'claiming @tabchannel: the network records it');
  await OC.fill('textarea', 'All about **tabs**.'); await OC.fill('input[placeholder^="City"]', 'Lisbon, Portugal');
  await OC.fill('input[placeholder^="Label"]', 'Site'); await OC.fill('input[placeholder="https://…"]', 'https://example.org'); await OC.click('button:has-text("Add link")');
  await OC.click('.panel:has(h3:text-is("Profile")) button:has-text("Save")');
  check(await until(O, (c) => { const p = opentube.engine.derive(c).profile; return p.about === 'All about **tabs**.' && p.location === 'Lisbon, Portugal' && p.links?.[0]?.url === 'https://example.org'; }, chain, 120000), 'profile edits (about, location, a link) reach the JavaScript page');
  await OC.goto(BASE + `app/c/${chain}/about`);
  check(await waitText(OC, /Lisbon, Portugal/, 60000) && await OC.isVisible('.rich b:has-text("tabs")') && await OC.isVisible('a.tagbox[href="https://example.org"]'), 'the About tab shows them (formatted)');
  check(await OC.isVisible('a:has-text("@tabchannel")'), 'and the channel’s @name');
  await OC.goto(BASE + `app/c/${chain}/posts`); await OC.fill('textarea', 'First channel post'); await OC.click('button:has-text("Post")');
  check(await until(O, (c) => opentube.engine.derive(c).posts.some(b => b.payload.text === 'First channel post'), chain, 120000), 'a channel post from C# reaches the JavaScript page');
  // X: a viewer on the C# pages
  const cx = await chromium.launchPersistentContext(W + '/x', { headless: true }); const X = cx.pages()[0]; X.on('pageerror', e => errs.push(e.message));
  await X.goto(BASE + 'app/you'); await X.waitForSelector('.mono', { timeout: 60000 }); const x = (await X.textContent('.mono')).trim();
  await X.click('button:has-text("starter coins")');
  await X.goto(BASE + 'app/n/' + encodeURIComponent('@tabchannel'));
  await X.waitForURL(new RegExp(`/app/c/${chain}`), { timeout: 120000 }).catch(() => {});
  check(X.url().includes(`/app/c/${chain}`), '@tabchannel opens the channel');
  check(await waitText(X, /Listed video/, 60000), 'its Videos tab lists the video');
  check(!(await X.isVisible('.tabs a:has-text("Manage")')), 'a viewer has no Manage tab');
  await X.goto(BASE + `app/c/${chain}/posts`); check(await waitText(X, /First channel post/, 60000), 'the viewer reads the channel’s post');
  // group discussion
  await X.goto(BASE + `app/c/${group}`); await X.waitForSelector('textarea', { timeout: 120000 });
  await X.fill('textarea', 'Hello group'); await X.click('button:has-text("Post")');
  check(await until(O, (g) => opentube.engine.derive(g).posts.some(b => b.payload.text === 'Hello group' && b.payload.name), group, 120000), 'a member posts in the group’s discussion (with their name)');
  await O.evaluate(async (g) => opentube.engine.append(g, { type: 'post', text: 'Owner hello' }), group);
  await X.waitForSelector('.post:has-text("Owner hello") input[placeholder="Reply…"]', { timeout: 60000 });
  await X.fill('.post:has-text("Owner hello") input[placeholder="Reply…"]', 'Nice post'); await X.click('.post:has-text("Owner hello") button:has-text("Reply")');
  check(await until(O, (g) => { const d = opentube.engine.derive(g); const p = d.posts.find(b => b.payload.text === 'Owner hello'); return p && d.commentsFor(p.hash).some(b => b.payload.text === 'Nice post'); }, group, 120000), 'a member’s reply in the group reaches the JavaScript page');
  check(await waitText(X, /Nice post/, 60000), 'and shows under the post');
  await X.click('.post:has-text("Owner hello") button:has-text("👍")');
  check(await until(O, (g) => { const d = opentube.engine.derive(g); const p = d.posts.find(b => b.payload.text === 'Owner hello'); return (d.votes.get(p.hash)?.blocks || []).length > 0; }, group, 120000), 'liking a group post');
  await X.click('.tabs a:has-text("Members")'); check(await waitText(X, /Have posted/, 30000), 'the Members tab');
  // list: channels and feed
  await OC.goto(BASE + `app/c/${list}/channels`); await OC.waitForSelector('select', { timeout: 120000 });
  await OC.selectOption('select', chain); await OC.fill('input[placeholder^="Why"]', 'Great tabs'); await OC.click('button:has-text("Add")');
  check(await until(O, ({ l, c }) => opentube.engine.derive(l).adds.some(b => b.payload.target === c && b.payload.note === 'Great tabs'), { l: list, c: chain }, 120000), 'adding a channel to the list (with a note)');
  await X.goto(BASE + `app/c/${list}/channels`); check(await waitText(X, /Tab Channel.*Great tabs|Great tabs/, 60000), 'the list shows the channel and the note');
  // block a commenter
  await OC.goto(BASE + `app/c/${chain}/manage`); await OC.waitForSelector('input[placeholder^="User ID"]', { timeout: 60000 });
  await OC.fill('input[placeholder^="User ID"]', x); await OC.click('button:has-text("Block")');
  check(await until(O, ({ c, k }) => opentube.engine.derive(c).revoked.has(k), { c: chain, k: x }, 120000), 'blocking a commenter (the network records it)');
  check(await waitText(OC, /Unblock/, 30000), 'they are listed under Blocked');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await co.close(); await cx.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
