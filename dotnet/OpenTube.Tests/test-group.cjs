// Run: dotnet build dotnet && node dotnet/OpenTube.Tests/test-group.cjs   (needs ffmpeg)
// The group stream on the .NET node: the host's two OBS cameras and an invited guest's OBS combined into one picture;
// the waiting room, the studio (spotlight, ticker), the viewer plays it, the host ends it.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-group-')), N = 8903, RT = 1951, BASE = `http://localhost:${N}/`;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return ((Date.now() - t0) / 1000).toFixed(1); await new Promise(r => setTimeout(r, 300)); } throw new Error('timeout'); };
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let P, lines = []; const start = (grant) => { P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, `--videos=${W}/videos`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', `--obs-port=${RT}`, '--site-dir=' + SITE, ...(grant ? ['--grant=' + grant] : [])]);
  P.stdout.on('data', d => { for (const l of String(d).split('\n')) { lines.push(l); if (/\[group\]|\[obs\] (live|stream from)|rror/.test(l)) log('[C#]', l.trim().replace(/^\d\d:\d\d:\d\d /, '').slice(0, 170)); } });
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); return; } catch { execSync('sleep 0.5'); } } };
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
const obs = (key, color, secs = 45) => spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-re', '-f', 'lavfi', '-i', `testsrc2=size=640x360:rate=30`, '-f', 'lavfi', '-i', `sine=frequency=${color}`, '-t', String(secs), '-vf', `drawbox=c=${['red', 'green', 'blue'][Math.floor(color / 300) % 3]}@0.5:t=fill`, '-c:v', 'libx264', '-preset', 'veryfast', '-g', '30', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-f', 'flv', `rtmp://localhost:${RT}/live/${key}`]);
(async () => {
  start();
  const ctxs = []; const open = async (n, q) => { const ctx = await chromium.launchPersistentContext(W + '/b-' + n, { headless: true, args: ['--autoplay-policy=no-user-gesture-required'] }); ctxs.push(ctx); const p = ctx.pages()[0]; p.on('pageerror', e => log(n, 'PAGEERROR', e.message.slice(0, 120))); await p.goto(BASE + q); await p.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); return p; };
  const A = await open('A', '?folder=browser#/you/channels'); await A.waitForTimeout(800);
  await A.fill('input[placeholder=Name]', 'Group Channel'); await A.click('text=Create & register');
  await A.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await A.evaluate(() => location.hash.split('/')[2]);
  await until(A, (c) => opentube.engine.directory.has(c), chain);
  const user = await A.evaluate(() => opentube.me.pub);
  const grant = await A.evaluate(async () => { const g = { v: 1, kind: 'nodegrant', user: opentube.me.pub, at: Date.now(), label: '' }; g.sig = await opentube.me.sign(OpenTubeCore.nodeGrantPreimage(g)); return btoa(JSON.stringify(g)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); });
  const g0 = new Promise(r => P.on('exit', r)); P.kill('SIGKILL'); await g0; execSync('sleep 1');
  fs.writeFileSync(W + '/node/obs.json', JSON.stringify({ profiles: { [user]: { key: 'grp-key', channel: chain, title: 'Group show', tile: { on: true, n: 4 } } }, users: {} }));
  start(grant);
  const nodeKey = JSON.parse(execSync(`curl -s localhost:${N}/status`)).node;
  await until(A, (k) => opentube.engine.nodes.has(k), nodeKey, 90000);
  await A.evaluate(async ({ c, k }) => { await opentube.engine.append(c, { type: 'relay', key: k, on: true }); }, { c: chain, k: nodeKey });
  for (let i = 0; i < 120; i++) { const s = JSON.parse(execSync(`curl -s localhost:${N}/status`)); if ((s.obs?.linked || []).includes(chain)) break; await new Promise(r => setTimeout(r, 500)); }
  const ctl = (cmd, args) => A.evaluate(async ({ k, cmd, args }) => { try { return (await opentube.nodeCtl(opentube.engine.nodes.get(k), cmd, args)).data; } catch (e) { return 'refused: ' + e.message; } }, { k: nodeKey, cmd, args });
  // a guest, invited from the site; their OBS key is theirs on this computer
  const guest = 'G'.repeat(42) + 'A';
  log('site → group.invite:', JSON.stringify(await ctl('group.invite', { key: guest, name: 'Guest', hours: 1 })).slice(0, 120));
  const guestKey = JSON.parse(fs.readFileSync(W + '/node/obs.json', 'utf8')).profiles[guest]?.key;
  check(!!guestKey, 'the guest got their own stream key on this computer');
  const f1 = obs('grp-key', 300), f2 = obs('grp-key-2', 650), f3 = obs(guestKey, 900);
  log('three OBS streams: the host, the host\'s camera 2, the guest');
  log('the group stream is live after', await until(A, (c) => opentube.engine.derive(c)?.liveNow.length > 0, chain, 60000).catch(() => 'NEVER'), 's');
  await new Promise(r => setTimeout(r, 4000));
  let st = await ctl('group', { thumbs: true });
  const gIn = st.inputs?.find(x => x.user === guest);
  check(st.inputs?.length === 3, `three inputs: ${st.inputs?.map(x => `${x.label}${x.onAir ? '' : ' (waiting)'}`).join(', ')}`);
  check(gIn && !gIn.onAir && st.inputs.filter(x => x.onAir).length === 2, 'the guest waits in the waiting room; the host\'s two cameras are on air');
  check(st.inputs?.every(x => /^data:image\/jpeg/.test(x.thumb || '')), 'the host sees a preview of each input');
  st = await ctl('group.air', { id: gIn?.id, on: true }); check(st.inputs?.find(x => x.user === guest)?.onAir, 'the host puts the guest on air');
  st = await ctl('group.studio', { layout: 'spotlight', ticker: { on: true, text: 'Welcome to the group show!', speed: 4 } });
  check(st.studio?.layout === 'spotlight' && st.studio?.ticker?.on, 'spotlight layout and a ticker');
  const lb = await A.evaluate((c) => opentube.engine.derive(c).liveNow[0], chain);
  const V = await open('V', `#/live/${chain}/${lb.hash}`);
  log('viewer playing after', await until(V, () => { const v = document.querySelector('video'); return v && v.currentTime > 1; }, null, 60000).catch(() => 'NO'), 's');
  await new Promise(r => setTimeout(r, 6000));
  const dims = await V.evaluate(() => { const v = document.querySelector('video'); return { w: v?.videoWidth, h: v?.videoHeight, t: v?.currentTime }; });
  check(dims.w === 1280 && dims.h === 720 && dims.t > 3, `the viewer plays the group picture: ${dims.w}×${dims.h} at ${dims.t?.toFixed(1)} s`);
  const shot = W + '/group.png'; await V.screenshot({ path: shot }); log('screenshot:', shot);
  st = await ctl('group.kick.all', {}); log('the host ended it for everyone');
  log('stream ended on the channel after', await until(A, (c) => opentube.engine.derive(c)?.liveNow.length === 0, chain, 60000).catch(() => 'NEVER'), 's');
  for (const f of [f1, f2, f3]) try { f.kill(); } catch {}
  check(lines.some(l => /\[group\] ended/.test(l)), 'the group stream ended');
  for (const c of ctxs) await c.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); try { P.kill(); } catch {} process.exit(1); });
