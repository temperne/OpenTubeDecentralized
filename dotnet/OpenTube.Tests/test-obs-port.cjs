// Run: dotnet build dotnet && node dotnet/OpenTube.Tests/test-obs-port.cjs
// The OBS port from the site: Devices → OBS shows the port and its owner changes it; the computer listens there at
// once. When another program has the chosen port, the computer takes the next free one and the page says so.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os'), crypto = require('crypto'), net = require('net');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-port-'));
const N = 8895, RT = 1942, TRACKER = `ws://127.0.0.1:${N}/tracker`, BASE = `http://localhost:${N}/`, Q = `?folder=browser&trackers=${encodeURIComponent(TRACKER)}`;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return ((Date.now() - t0) / 1000).toFixed(1); await new Promise(r => setTimeout(r, 300)); } throw new Error('timeout'); };
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let P, lines = []; const start = (grant) => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, `--videos=${W}/videos`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', `--obs-port=${RT}`, '--site-dir=' + SITE, ...(grant ? ['--grant=' + grant] : [])], { env: { ...process.env, PAY_DEBUG: '1' } });
  P.stdout.on('data', d => { for (const l of String(d).split('\n')) { lines.push(l); if (/\[host\]|\[obs\]|\[pay|\[live\]|rror|fail/.test(l)) log('[C#]', l.trim().replace(/^\d\d:\d\d:\d\d /, '').slice(0, 170)); } });
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); return; } catch { execSync('sleep 0.5'); } } };
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
(async () => {
  start();
  const ctxs = []; const open = async (n, q) => { const ctx = await chromium.launchPersistentContext(W + '/b-' + n, { headless: true }); ctxs.push(ctx); const p = ctx.pages()[0]; p.on('pageerror', e => log(n, 'PAGEERROR', e.message.slice(0, 120))); await p.goto(BASE + q); await p.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); return p; };
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
  const portOpen = (p) => new Promise(r => { const s = net.connect(p, '127.0.0.1', () => { s.destroy(); r(true); }); s.on('error', () => r(false)); });
  const openPanel = async () => {
    await A.goto(BASE + Q + '#/me'); await A.waitForTimeout(1500);
    await A.click('text=🎥 OBS & streaming', { timeout: 60000 });
    await A.waitForSelector('input[type=number]', { timeout: 60000 }).catch(async () => { await A.screenshot({ path: W + '/panel.png', fullPage: true }); log('screenshot', W + '/panel.png'); log((await A.textContent('body')).replace(/\s+/g, ' ').slice(0, 1500)); });
  };
  await openPanel();
  const servers = async () => (await A.$$eval('code.mono', els => els.map(e => e.textContent))).filter(t => t.startsWith('rtmp://'));
  check((await servers()).some(u => u.includes(`:${RT}/live`)), `Devices → OBS shows the port in the server addresses: ${(await servers())[0]}`);
  // change it to a free port
  const NEW = 1957; await A.fill('input[type=number]', String(NEW)); await A.click('button:has-text("Change")');
  await A.waitForFunction((p) => [...document.querySelectorAll('code.mono')].some(e => e.textContent.includes(':' + p + '/live')), NEW, { timeout: 20000 }).catch(() => {});
  check(await portOpen(NEW) && !(await portOpen(RT)), `changing the port: the computer listens on ${NEW} at once (and no longer on ${RT})`);
  check((await servers()).some(u => u.includes(`:${NEW}/live`)), 'the server addresses show the new port');
  // another program has the chosen port: the next free one, and the page says why
  const BUSY = 1961; const other = net.createServer(() => {}).listen(BUSY); await new Promise(r => other.once('listening', r));
  await A.fill('input[type=number]', String(BUSY)); await A.click('button:has-text("Change")');
  await A.waitForSelector('text=is used by another program', { timeout: 20000 }).catch(() => {});
  const note = await A.textContent('body');
  check(/port 1961 is used by another program, so OBS streams to port 1962/i.test(note) && await portOpen(BUSY + 1), 'a port another program has: the computer takes the next free one (1962) and the page says so');
  check((await servers()).some(u => u.includes(':1962/live')), 'the server addresses use 1962');
  // the choice survives a restart
  { const gone = new Promise(r => P.on('exit', r)); P.kill('SIGKILL'); await gone; } other.close(); start(grant);
  for (let i = 0; i < 40 && !(await portOpen(BUSY)); i++) await new Promise(r => setTimeout(r, 250));
  check(await portOpen(BUSY), 'after a restart (the port free again) it listens on the chosen port, 1961');
  for (const c of ctxs) await c.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); P?.kill(); process.exit(1); });
