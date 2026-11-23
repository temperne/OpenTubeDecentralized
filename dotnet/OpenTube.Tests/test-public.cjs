// Run: dotnet build dotnet && node dotnet/OpenTube.Tests/test-public.cjs
// The site's Devices list shows how the .NET node is reachable, and switches it: Off, Automatic, Always a tunnel.
// (Run where the node is behind a router: Automatic then means a Cloudflare tunnel; if Cloudflare can't be reached,
// the page shows the tunnel's error.)
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-pub-')), PORT = 8891, BASE = `http://localhost:${PORT}/`, D = W + '/node';
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const until = async (fn, ms = 90000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { const v = await fn().catch(() => null); if (v) return v; await new Promise(r => setTimeout(r, 500)); } throw new Error('timeout'); };
let N; const start = (grant) => {
  N = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${PORT}`, '--data=' + D, '--mine=all', '--no-ranking', '--no-obs', '--site-dir=' + REPO, ...(grant ? ['--grant=' + grant] : [])]);
  N.stdout.on('data', d => { for (const l of String(d).split('\n')) if (/internet|https|tunnel|cloudflared|public/.test(l)) log('[C#]', l.trim().replace(/^\d\d:\d\d:\d\d /, '').slice(0, 170)); });
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${PORT}/status`); return; } catch { execSync('sleep 0.5'); } } };
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
(async () => {
  start();
  const ctx = await chromium.launchPersistentContext(W + '/browser', { headless: true }); const A = ctx.pages()[0];
  A.on('pageerror', e => log('PAGEERROR', e.message.slice(0, 120)));
  await A.goto(BASE + '#/me'); await A.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 });
  const grant = await A.evaluate(async () => { const g = { v: 1, kind: 'nodegrant', user: opentube.me.pub, at: Date.now(), label: '' }; g.sig = await opentube.me.sign(OpenTubeCore.nodeGrantPreimage(g)); return btoa(JSON.stringify(g)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); });
  const gone = new Promise(r => N.on('exit', r)); N.kill('SIGKILL'); await gone; execSync('sleep 1');
  start(grant);
  const nodeKey = JSON.parse(execSync(`curl -s localhost:${PORT}/status`)).node;
  const reach = () => A.evaluate((k) => opentube.engine.nodes.get(k)?.info?.reach || null, nodeKey);
  const r0 = await until(reach); log('the site sees:', r0.text);
  check(['tunnel', 'direct', 'none'].includes(r0.mode) && r0.setting === 'auto', `reported: mode ${r0.mode}, setting ${r0.setting}`);
  const ctl = (cmd, args) => A.evaluate(async ({ k, cmd, args }) => { try { return (await opentube.nodeCtl(opentube.engine.nodes.get(k), cmd, args)).data; } catch (e) { return 'refused: ' + e.message; } }, { k: nodeKey, cmd, args });
  // the page's own Devices row
  await A.goto(BASE + '#/me'); const sel = await until(() => A.$('select:has(option[value=tunnel])'), 60000);
  const row = await sel.evaluate(s => s.closest('.item')?.innerText || s.parentElement.parentElement.innerText);
  check(/Public:/.test(row) && /(Not public|Public at|Starting a Cloudflare tunnel)/.test(row), 'the Devices row shows reachability: ' + row.split('\n').filter(l => /public|tunnel|Public/i.test(l)).join(' | ').slice(0, 200));
  // Off, from the page's own select
  await sel.selectOption('off');
  const r1 = await until(async () => { const r = await reach(); return r?.setting === 'off' && r.mode === 'none' ? r : null; }); check(true, 'Off from the page: ' + r1.text);
  const t1 = Date.now();
  check(JSON.parse(fs.readFileSync(D + '/public.json', 'utf8')).setting === 'off', 'saved for the next start');
  // Always a tunnel (from nodeCtl), then Automatic
  log('site → public.set tunnel:', JSON.stringify(await ctl('public.set', { mode: 'tunnel' })));
  const r2 = await until(async () => { const r = await reach(); return r?.setting === 'tunnel' && r.mode !== 'none' && r.mode !== 'checking' ? r : null; }); check(Date.now() - t1 < 20000, `Always a tunnel, in ${((Date.now() - t1) / 1000).toFixed(1)} s: ` + r2.text);
  log('site → public.set auto:', JSON.stringify(await ctl('public.set', { mode: 'auto' })));
  const r3 = await until(async () => { const r = await reach(); return r?.setting === 'auto' && r.mode !== 'checking' ? r : null; }); check(true, 'Automatic: ' + r3.text);
  check(/refused/.test(await ctl('public.set', { mode: 'nonsense' })), 'a bad mode is refused');
  check(await until(() => A.evaluate(() => fetch('/status').then(r => r.ok)), 20000), 'the website still answers after the switches');
  // restart: the saved setting holds
  await ctl('public.set', { mode: 'off' }); await until(async () => (await reach())?.setting === 'off');
  const gone2 = new Promise(r => N.on('exit', r)); N.kill('SIGKILL'); await gone2; execSync('sleep 1'); start(grant);
  check(JSON.parse(execSync(`curl -s localhost:${PORT}/status`)).public == null, 'after a restart it is still off');
  await ctx.close(); N.kill(); process.exit(fail);
})().catch(e => { console.error(e); try { N.kill(); } catch {} process.exit(1); });
