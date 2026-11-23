// Run: dotnet build dotnet && node dotnet/OpenTube.Tests/test-control.cjs   (the site controls the .NET node, then OBS streams; needs ffmpeg)
// OBS (played by ffmpeg) → the C# node → viewers: live block, playback in a browser, recording, end of stream.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs');
const REPO = require('path').resolve(__dirname, '../..'), W = require('fs').mkdtempSync(require('path').join(require('os').tmpdir(), 'opentube-ctl-')), PORT = 8890, RT = 1946, BASE = `http://localhost:${PORT}/`, D = W + '/csobs-N';
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return ((Date.now() - t0) / 1000).toFixed(1); await new Promise(r => setTimeout(r, 300)); } throw new Error('timeout'); };
let N, streamKey; const start = (grant) => { N = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${PORT}`, '--data=' + D, '--mine=all', '--no-tunnel', '--no-ranking', `--obs-port=${RT}`, '--site-dir=' + REPO, ...(grant ? ['--grant=' + grant] : [])]);
  N.stdout.on('data', d => { for (const l of String(d).split('\n')) if (/\[obs\]|rror|fail/.test(l)) log('[C#]', l.trim().replace(/^\d\d:\d\d:\d\d /, '').slice(0, 170)); });
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${PORT}/status`); return; } catch { execSync('sleep 0.5'); } } };
(async () => {
  fs.rmSync(D, { recursive: true, force: true }); start();
  const ctxs = [];
  const open = async (n, q) => { fs.rmSync(W + '/csobs-' + n, { recursive: true, force: true }); const ctx = await chromium.launchPersistentContext(W + '/csobs-' + n, { headless: true, args: ['--autoplay-policy=no-user-gesture-required'] }); ctxs.push(ctx); const p = ctx.pages()[0]; p.on('pageerror', e => log(n, 'PAGEERROR', e.message.slice(0, 120))); await p.goto(BASE + q); await p.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 }); return p; };
  const A = await open('A', '?folder=browser#/you/channels'); await A.waitForTimeout(800);
  await A.fill('input[placeholder=Name]', 'OBS Channel'); await A.click('text=Create & register');
  await A.waitForFunction(() => location.hash.startsWith('#/c/'), null, { timeout: 180000 }); const chain = await A.evaluate(() => location.hash.split('/')[2]);
  log('channel registered after', await until(A, (c) => opentube.engine.directory.has(c), chain), 's');
  const user = await A.evaluate(() => opentube.me.pub);
  const grant = await A.evaluate(async () => { const g = { v: 1, kind: 'nodegrant', user: opentube.me.pub, at: Date.now(), label: '' }; g.sig = await opentube.me.sign(OpenTubeCore.nodeGrantPreimage(g)); return btoa(JSON.stringify(g)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); });
  // the stream key, as the site would set it, then the node again with the owner's grant
  const gone = new Promise(r => N.on('exit', r)); N.kill('SIGKILL'); await gone;
  for (let i = 0; i < 30; i++) { try { execSync(`fuser ${RT}/tcp ${PORT}/tcp 2>/dev/null`); execSync('sleep 0.3'); } catch { break; } }
  start(grant);
  const nodeKey = JSON.parse(execSync(`curl -s localhost:${PORT}/status`)).node;
  await until(A, (c) => opentube.engine.chains.get(c)?.blocks.size >= 1, chain);
  // the site controls the node: its own nodeCtl (signed commands routed to the node, signed answers)
  await until(A, (k) => opentube.engine.nodes.has(k), nodeKey, 60000);
  const ctl = (cmd, args) => A.evaluate(async ({ k, cmd, args }) => { try { const r = await opentube.nodeCtl(opentube.engine.nodes.get(k), cmd, args); return r.data; } catch (e) { return 'refused: ' + e.message; } }, { k: nodeKey, cmd, args });
  let o = await ctl('obs', {}); log('site → obs:', JSON.stringify({ key: o.key?.slice(0, 6) + '…', port: o.port, channel: o.channel, linked: o.linked, impl: o.version?.impl }));
  log('site → obs.set without proof:', await ctl('obs.set', { channel: chain }));
  const channelSig = await A.evaluate(async ({ k, c }) => { const owner = opentube.engine.derive(c).owner; return opentube.signerFor(owner).sign(OpenTubeCore.obsChannelPreimage(k, opentube.me.pub, c)); }, { k: nodeKey, c: chain });
  o = await ctl('obs.set', { channel: chain, channelSig, title: 'Set from the site' }); log('site → obs.set:', typeof o === 'string' ? o : JSON.stringify({ channel: o.channel?.slice(0, 8), title: o.title, linked: o.linked }));
  const lg = await ctl('logs', { n: 5 }); log('site → logs:', lg.lines?.length, 'lines, last:', lg.lines?.at(-1)?.slice(0, 80));
  const us = await ctl('users.set', { key: 'B'.repeat(43), can: ['obs'], name: 'Bob' }); log('site → users.set:', JSON.stringify(us.users));
  log('site → group:', await ctl('group', {}));
  const forged = await A.evaluate(async (k) => { const id = await OpenTubeCore.newIdentity(), s = await OpenTubeCore.signer(id); const c = { v: 1, kind: 'nodectl', node: k, user: id.pub, at: Date.now(), nonce: 'f'.repeat(32), cmd: 'logs', args: {} }; c.sig = await s.sign(OpenTubeCore.ctlPreimage(c)); const r = await opentube.engine.route(k, c, 15000); return r ? (r.ok ? 'ANSWERED' : 'refused: ' + r.error) : 'no answer'; }, nodeKey);
  log('a stranger’s command:', forged);
  streamKey = o.key;
  await A.evaluate(async ({ c, k }) => { await opentube.engine.append(c, { type: 'relay', key: k, on: true }); }, { c: chain, k: nodeKey });
  log('the owner linked this computer to the channel');
  for (let i = 0; i < 120; i++) { const s = JSON.parse(execSync(`curl -s localhost:${PORT}/status`)); if ((s.obs?.linked || []).includes(chain)) { log('the node sees the link after', (i / 2).toFixed(1), 's'); break; } await new Promise(r => setTimeout(r, 500)); }
  const ff = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-re', '-f', 'lavfi', '-i', 'testsrc=size=640x360:rate=30', '-f', 'lavfi', '-i', 'sine=frequency=440', '-t', '30', '-c:v', 'libx264', '-preset', 'veryfast', '-g', '60', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-f', 'flv', `rtmp://localhost:${RT}/live/${streamKey}`]);
  log('OBS (ffmpeg) streaming');
  log('live on the channel (in A\'s browser) after', await until(A, (c) => opentube.engine.derive(c)?.liveNow.length > 0, chain, 60000).catch(() => 'NEVER'), 's');
  const lb = await A.evaluate((c) => opentube.engine.derive(c).liveNow[0], chain);
  if (lb) {
    const V = await open('V', `#/live/${chain}/${lb.hash}`);
    log('viewer playing after', await until(V, () => { const v = document.querySelector('video'); return v && v.currentTime > 1; }, null, 60000).catch(() => 'NO'), 's');
    await new Promise(r => setTimeout(r, 8000));
    log('viewer at', await V.evaluate(() => document.querySelector('video')?.currentTime.toFixed(1)), 's of video');
  }
  await new Promise(r => ff.on('exit', r));
  log('OBS stopped; stream ended on the channel after', await until(A, (c) => opentube.engine.derive(c)?.liveNow.length === 0 && opentube.engine.derive(c)?.liveEnds.size > 0, chain, 60000).catch(() => 'NEVER'), 's');
  const recs = fs.readdirSync(D + '/recordings'); const f = D + '/recordings/' + recs[0];
  log('recording:', recs[0], fs.statSync(f).size, 'bytes,', execSync(`ffprobe -v error -show_entries format=duration -of csv=p=0 "${f}"`).toString().trim(), 's');
  for (const c of ctxs) await c.close(); N.kill(); process.exit(0);
})().catch(e => { console.error(e); try { N.kill(); } catch {} process.exit(1); });
