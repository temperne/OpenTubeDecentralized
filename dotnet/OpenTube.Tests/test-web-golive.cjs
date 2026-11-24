// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-golive.cjs <dir>/wwwroot
// Going live from the camera in the C# pages and ending it: the Where choice (this channel, or into a group stream on
// your computer), a hub on your computer named in the live block, still live after 30 s, and on ending, "Your stream
// has ended" with the recording to publish right on the Go live page (published as a video of that stream).
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-golive-')), N = 8899, RT = 1941, APP = path.resolve(process.argv[2]);
const BASE = `http://localhost:${N}/`;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
let P, lines = []; process.on('exit', () => { try { P?.kill(); } catch {} });
const start = (grant) => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, `--videos=${W}/videos`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', `--obs-port=${RT}`, '--site-dir=' + SITE, '--app-dir=' + APP, ...(grant ? ['--grant=' + grant] : [])]);
  P.stdout.on('data', d => { for (const l of String(d).split('\n')) { lines.push(l); if (/\[live\]|rror/.test(l)) log('[C#]', l.trim().slice(0, 160)); } });
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); return; } catch { execSync('sleep 0.5'); } }
};
const waitText = async (p, re, ms = 60000, sel = 'main') => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (re.test(await p.textContent(sel).catch(() => '') || '')) return true; await p.waitForTimeout(1000); } return false; };
(async () => {
  start();
  const cx = await chromium.launchPersistentContext(W + '/x', { headless: true, args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] }); const XJ = cx.pages()[0];
  await XJ.goto(BASE + '?folder=browser#/you/channels'); await XJ.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); await XJ.waitForTimeout(800);
  await XJ.fill('input[placeholder=Name]', 'Camera Channel'); await XJ.click('text=Create & register');
  await XJ.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await XJ.evaluate(() => location.hash.split('/')[2]);
  await XJ.waitForFunction((c) => opentube.engine.directory.has(c), chain, { timeout: 180000 });
  const grant = await XJ.evaluate(async () => { const g = { v: 1, kind: 'nodegrant', user: opentube.me.pub, at: Date.now(), label: '' }; g.sig = await opentube.me.sign(OpenTubeCore.nodeGrantPreimage(g)); return btoa(JSON.stringify(g)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); });
  const gone = new Promise(r => P.on('exit', r)); P.kill('SIGKILL'); await gone; execSync('sleep 1'); start(grant);
  const nodeKey = JSON.parse(execSync(`curl -s localhost:${N}/status`)).node;
  await XJ.waitForFunction((k) => opentube.engine.nodes.has(k), nodeKey, { timeout: 90000 });
  await XJ.evaluate(async ({ c, k }) => { await opentube.engine.append(c, { type: 'relay', key: k, on: true }); }, { c: chain, k: nodeKey });   // the computer may end the stream and offer the recording
  const errs = []; const X = await cx.newPage(); X.on('pageerror', e => errs.push(e.message));
  // the Network page lists the computer's tracker
  await X.goto(BASE + 'app/network'); await X.waitForSelector('#net-checks', { timeout: 120000 });
  check(await waitText(X, new RegExp(`localhost:${N}/tracker|127.0.0.1:${N}/tracker|:${N}/tracker`), 30000), 'the Network page lists your computer’s tracker');
  check(await waitText(X, /Last message in/, 5000) && await waitText(X, /Recent events/, 5000) && await waitText(X, /Last heard from peers/, 5000), 'the Network page has the connection details, chain table and events');
  await X.goto(BASE + 'app/golive/' + chain); await X.waitForSelector('button:has-text("Go live")', { timeout: 60000 });
  check(await X.waitForSelector('#golive-where option:has-text("Into my group stream")', { state: 'attached', timeout: 60000 }).then(() => true, () => false), 'Where: into a group stream on your computer is offered');
  await X.fill('input[maxlength="140"]', 'Long camera show'); await X.click('button:has-text("● Go live")');
  check(await waitText(X, /Live · .* sent/, 60000), 'live: ' + (await X.textContent('#golive-status').catch(() => '')));
  const lb = await XJ.evaluate(async (c) => { for (let i = 0; i < 60; i++) { const b = [...opentube.engine.chains.get(c).blocks.values()].find(b => b.payload.type === 'live' && b.payload.title === 'Long camera show'); if (b) return b.payload; await new Promise(r => setTimeout(r, 500)); } return null; }, chain);
  check(lb && (lb.hub === nodeKey || lb.hub === undefined), 'the live block names its hub (your computer; here it is also the site): ' + lb?.hub);
  const sent = async () => Number((/· ([\d.]+) (KB|MB) sent/.exec(await X.textContent('#golive-status')) || [])[1] || 0) * (/MB sent/.test(await X.textContent('#golive-status')) ? 1000 : 1);
  await X.waitForTimeout(12000); const a = await sent(); await X.waitForTimeout(20000); const b = await sent();
  check(/Live ·/.test(await X.textContent('#golive-status')) && b > a, `still live after 30 s, still sending (${a} → ${b} KB): ` + (await X.textContent('#golive-status')));
  await X.click('button:has-text("End stream")');
  check(await waitText(X, /Your stream has ended/, 30000), 'Your stream has ended');
  check(await X.waitForSelector('#stream-ended button:has-text("Publish recording")', { timeout: 90000 }).then(() => true, () => false), 'the recording is offered to publish on the Go live page');
  await X.click('#stream-ended button:has-text("Publish recording")');
  check(await waitText(X, /published as a video/, 120000), 'publishing it');
  const vid = await XJ.evaluate(async ({ c, s }) => { for (let i = 0; i < 60; i++) { const v = opentube.engine.derive(c).videos.find(v => v.payload.stream === s); if (v) return v.payload.title; await new Promise(r => setTimeout(r, 1000)); } return null; }, { c: chain, s: lb?.stream });
  check(!!vid, 'the channel has a video of the stream: ' + vid);
  // into your own computer's group stream, from a channel not linked to it yet: the computer's combined stream goes live on it
  await XJ.goto(BASE + '?folder=browser#/you/channels'); await XJ.waitForTimeout(800);
  await XJ.fill('input[placeholder=Name]', 'Group Channel'); await XJ.click('text=Create & register');
  await XJ.waitForFunction(() => location.hash.startsWith('#/c/') && location.hash.split('/')[2] !== window.__c1, null, { timeout: 180000 });
  const chain2 = await XJ.evaluate(() => location.hash.split('/')[2]); await XJ.waitForFunction((c) => opentube.engine.directory.has(c), chain2, { timeout: 180000 });
  await X.goto(BASE + 'app/golive/' + chain2); await X.waitForSelector('#golive-where', { timeout: 60000 });
  await X.selectOption('#golive-where', nodeKey); await X.fill('input[maxlength="140"]', 'Group show'); await X.click('button:has-text("● Go live")');
  check(await waitText(X, /You’re in the group stream|Live ·/, 60000), 'into my group stream: ' + (await X.textContent('#golive-status').catch(() => '')));
  const glb = await XJ.evaluate(async ({ c, k }) => { for (let i = 0; i < 120; i++) { const d = opentube.engine.derive(c); const b = (d.liveNow || []).find(b => b.author === k); if (b) return b.payload; await new Promise(r => setTimeout(r, 1000)); } return null; }, { c: chain2, k: nodeKey });
  check(!!glb, 'the computer’s group stream shows as live on the channel: ' + JSON.stringify(glb && { title: glb.title }));
  check(glb?.title === 'Group show', 'with the title given on Go live');
  await X.goto(BASE + 'app/c/' + chain2); check(await waitText(X, /LIVE|Live now|● Live/i, 30000), 'the channel page shows it live');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await cx.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
