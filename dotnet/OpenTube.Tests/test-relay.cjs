// Run: dotnet build dotnet && node dotnet/OpenTube.Tests/test-relay.cjs
// Relaying: the owner goes live from a browser (fake camera) on hub H; the owner's .NET computer R watches the stream
// as a member, serves viewers itself over WebRTC data channels, and keeps a recording (its owner's channel).
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-relay-'));
const H = 8896, R = 8897, BASE = `http://localhost:${H}/`;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return ((Date.now() - t0) / 1000).toFixed(1); await new Promise(r => setTimeout(r, 300)); } throw new Error('timeout'); };
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
const procs = {}, lines = { H: [], R: [] };
const run = (name, port, extra) => {
  const p = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${port}`, `--data=${W}/${name}`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-obs', '--no-mesh', '--site-dir=' + SITE, ...extra]);
  p.stdout.on('data', d => { for (const l of String(d).split('\n')) { lines[name].push(l); if (/\[live\]|\[control\]|rror|fail/.test(l)) log(`[${name}]`, l.trim().replace(/^\d\d:\d\d:\d\d /, '').slice(0, 170)); } });
  procs[name] = p; for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${port}/status`); return; } catch { execSync('sleep 0.5'); } } };
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
(async () => {
  run('H', H, []);
  const ctxs = [];
  const open = async (n, q) => { const ctx = await chromium.launchPersistentContext(W + '/b-' + n, { headless: true, args: ['--use-fake-ui-for-media-stream', '--use-fake-device-for-media-stream', '--autoplay-policy=no-user-gesture-required'], permissions: ['camera', 'microphone'] }); ctxs.push(ctx); const p = ctx.pages()[0]; p.on('pageerror', e => log(n, 'PAGEERROR', e.message.slice(0, 120))); await p.goto(BASE + q); await p.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); return p; };
  const A = await open('A', '?folder=browser#/you/channels'); await A.waitForTimeout(800);
  await A.fill('input[placeholder=Name]', 'Relay Channel'); await A.click('text=Create & register');
  await A.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await A.evaluate(() => location.hash.split('/')[2]);
  log('channel registered after', await until(A, (c) => opentube.engine.directory.has(c), chain), 's');
  const grant = await A.evaluate(async () => { const g = { v: 1, kind: 'nodegrant', user: opentube.me.pub, at: Date.now(), label: '' }; g.sig = await opentube.me.sign(OpenTubeCore.nodeGrantPreimage(g)); return btoa(JSON.stringify(g)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); });
  run('R', R, ['--grant=' + grant, `--site=http://localhost:${H}`]);
  const rKey = JSON.parse(execSync(`curl -s localhost:${R}/status`)).node;
  await until(A, (k) => opentube.engine.nodes.has(k), rKey, 90000); log('A sees its computer R');
  // go live from the browser
  await A.goto(BASE + `?folder=browser#/c/${chain}/golive`); await A.waitForTimeout(2000);
  await A.click('button:has-text("Allow camera")').catch(() => {}); await A.waitForTimeout(1000);
  const camSel = 'select:has(option:has-text("Choose a camera"))';
  const cam = await A.$$eval(camSel + ' option', os => os.map(o => [o.value, o.textContent])); await A.selectOption(camSel, cam[1][0]); await A.waitForTimeout(1500);
  await A.locator('.live-studio input[placeholder="Stream title"]:visible').last().fill('Relay show');
  await A.click('button:has-text("Start streaming")');
  log('live on the channel after', await until(A, (c) => opentube.engine.derive(c)?.liveNow.length > 0, chain, 60000), 's');
  const lb = await A.evaluate((c) => opentube.engine.derive(c).liveNow[0], chain);
  const t0 = Date.now(); while (Date.now() - t0 < 60000 && !lines.R.some(l => /relaying “/.test(l))) await new Promise(r => setTimeout(r, 500));
  check(lines.R.some(l => /relaying “/.test(l)), 'R relays the stream (it gets the segments as a member)');
  const viewers = []; for (const n of ['V1', 'V2', 'V3']) viewers.push(await open(n, `?live=dc#/live/${chain}/${lb.hash}`));   // segment viewers (no WebRTC video): the tree
  for (const [i, V] of viewers.entries()) log(`viewer ${i + 1} playing after`, await until(V, () => { const v = document.querySelector('video'); return v && v.currentTime > 1; }, null, 60000).catch(() => 'NO'), 's');
  await new Promise(r => setTimeout(r, 8000));
  const fed = lines.R.filter(l => /gets the stream from this computer over WebRTC/.test(l)).length;
  check(fed >= 1, `viewers fed by R over WebRTC: ${fed}`);
  for (const [i, V] of viewers.entries()) log(`viewer ${i + 1} at`, await V.evaluate(() => document.querySelector('video')?.currentTime.toFixed(1)), 's');
  A.once('dialog', d => d.accept()); await A.click('button:has-text("End stream")').catch(e => log('no End stream button:', e.message)); log('the owner ended the stream');
  const t1 = Date.now(); while (Date.now() - t1 < 60000 && !lines.R.some(l => /recording saved/.test(l))) await new Promise(r => setTimeout(r, 500));
  const recs = fs.existsSync(W + '/R/recordings') ? fs.readdirSync(W + '/R/recordings') : [];
  const f = recs[0] && W + '/R/recordings/' + recs[0];
  const dur = f ? execSync(`ffprobe -v error -show_entries format=duration -of csv=p=0 "${f}" 2>/dev/null || true`).toString().trim() : '';
  check(lines.R.some(l => /recording saved/.test(l)), `R closed the recording when the stream ended (${((Date.now() - t1) / 1000).toFixed(1)} s)`);
  check(!!f && fs.statSync(f).size > 10000, `R kept a recording: ${recs[0] || 'none'} ${f ? fs.statSync(f).size : 0} bytes, ${dur || '?'} s`);
  for (const c of ctxs) await c.close(); for (const p of Object.values(procs)) p.kill(); process.exit(fail);
})().catch(e => { console.error(e); for (const p of Object.values(procs)) try { p.kill(); } catch {} process.exit(1); });
