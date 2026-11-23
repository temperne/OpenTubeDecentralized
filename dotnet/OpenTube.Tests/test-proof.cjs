// Run: dotnet build dotnet && node dotnet/OpenTube.Tests/test-proof.cjs
// Storage proofs between two .NET hosts that hold the same video: each challenges the other on a random piece with a
// fresh nonce (routed over the network) and checks the signed answer against its own copy.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-proof-'));
const P1 = 8900, P2 = 8901, TRACKER = `ws://127.0.0.1:${P1}/tracker`, Q = `?folder=browser&trackers=${encodeURIComponent(TRACKER)}`;
const VIDEO = '/tmp/claude-0/-home-user-OpenTubeDecentralized/a82132eb-7c72-5513-9bf1-91c1c8d3383e/scratchpad/work/small.mp4';
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return true; await new Promise(r => setTimeout(r, 300)); } throw new Error('timeout'); };
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
const procs = {}, lines = { N1: [], N2: [] };
const run = (name, port, extra) => {
  const p = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${port}`, `--data=${W}/${name}`, `--videos=${W}/${name}-videos`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', '--no-obs', '--site-dir=' + SITE, `--mesh-trackers=${TRACKER}`, ...extra], { env: { ...process.env, PROOF_EVERY_MS: '4000' } });
  p.stdout.on('data', d => { for (const l of String(d).split('\n')) { lines[name].push(l); if (/\[proof\]|rror|fail/.test(l)) log(`[${name}]`, l.trim().replace(/^\d\d:\d\d:\d\d /, '').slice(0, 170)); } });
  procs[name] = p; for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${port}/status`); return; } catch { execSync('sleep 0.5'); } } };
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
(async () => {
  run('N1', P1, []);
  const ctx = await chromium.launchPersistentContext(W + '/browser', { headless: true }); const A = ctx.pages()[0];
  await A.goto(`http://localhost:${P1}/${Q}#/you/channels`); await A.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); await A.waitForTimeout(800);
  await A.fill('input[placeholder=Name]', 'Proof Channel'); await A.click('text=Create & register');
  await A.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await A.evaluate(() => location.hash.split('/')[2]);
  await until(A, (c) => opentube.engine.directory.has(c), chain);
  const grant = await A.evaluate(async () => { const g = { v: 1, kind: 'nodegrant', user: opentube.me.pub, at: Date.now(), label: '' }; g.sig = await opentube.me.sign(OpenTubeCore.nodeGrantPreimage(g)); return btoa(JSON.stringify(g)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); });
  const g1 = new Promise(r => procs.N1.on('exit', r)); procs.N1.kill('SIGKILL'); await g1; execSync('sleep 1');
  run('N1', P1, ['--grant=' + grant]); run('N2', P2, ['--grant=' + grant, `--peers=127.0.0.1:${P1}`]);
  const keys = [P1, P2].map(p => JSON.parse(execSync(`curl -s localhost:${p}/status`)).node);
  await until(A, (ks) => ks.every(k => opentube.engine.nodes.has(k)), keys, 90000); log('both computers are on the owner\'s Devices');
  await A.goto(`http://localhost:${P1}/${Q}#/c/${chain}/upload`); await A.waitForTimeout(1500);
  await A.setInputFiles('input[type=file][accept="video/*"]', VIDEO); await A.waitForTimeout(800);
  await A.click('text=Publish to channel'); await A.waitForFunction(() => location.hash.startsWith('#/v/'), null, { timeout: 60000 });
  const ih = await A.evaluate((c) => [...opentube.engine.chains.get(c).blocks.values()].find(b => b.payload.infohash)?.payload.infohash, chain);
  const ctl = (k, cmd, args) => A.evaluate(async ({ k, cmd, args }) => { try { return (await opentube.nodeCtl(opentube.engine.nodes.get(k), cmd, args)).data; } catch (e) { return 'refused: ' + e.message; } }, { k, cmd, args });
  for (const k of keys) log('store on', k.slice(0, 8), JSON.stringify(await ctl(k, 'store', { chain, ih })));
  for (const k of keys) { let pr = null; for (let i = 0; i < 90 && !pr?.done; i++) { pr = await ctl(k, 'store.status', { ih }); if (!pr?.done) await new Promise(r => setTimeout(r, 1000)); } check(pr?.done, `${k.slice(0, 8)} holds the video`); }
  const t0 = Date.now(); while (Date.now() - t0 < 120000 && !(lines.N1.some(l => /\[proof\] .* proved it holds/.test(l)) && lines.N2.some(l => /\[proof\] .* proved it holds/.test(l)))) await new Promise(r => setTimeout(r, 1000));
  check(lines.N1.some(l => new RegExp(`\\[proof\\] ${keys[1].slice(0, 8)} proved it holds ${ih.slice(0, 8)}`).test(l)), 'N1 challenged N2 and N2 proved it holds the video');
  check(lines.N2.some(l => new RegExp(`\\[proof\\] ${keys[0].slice(0, 8)} proved it holds ${ih.slice(0, 8)}`).test(l)), 'N2 challenged N1 and N1 proved it holds the video');
  check(![...lines.N1, ...lines.N2].some(l => /failed a storage proof/.test(l)), 'no failed proofs');
  await ctx.close(); for (const p of Object.values(procs)) p.kill(); process.exit(fail);
})().catch(e => { console.error(e); for (const p of Object.values(procs)) try { p.kill(); } catch {} process.exit(1); });
