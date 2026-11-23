// Run: dotnet build dotnet && node dotnet/OpenTube.Tests/test-host.cjs   (needs ffmpeg)
// Video hosting on the .NET node: an OBS recording is kept in the channel's folder (channel.json), shared over
// BitTorrent and offered on the channel (liveend rec); a browser downloads it from the node over WebRTC; the owner's
// file manager works from the site; "store" makes the node download a browser's upload and keep it.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os'), crypto = require('crypto');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-host-'));
const N = 8899, RT = 1948, TRACKER = `ws://127.0.0.1:${N}/tracker`, BASE = `http://localhost:${N}/`, Q = `?folder=browser&trackers=${encodeURIComponent(TRACKER)}`;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return ((Date.now() - t0) / 1000).toFixed(1); await new Promise(r => setTimeout(r, 300)); } throw new Error('timeout'); };
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let P, lines = []; const start = (grant) => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, `--videos=${W}/videos`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', `--obs-port=${RT}`, '--site-dir=' + SITE, ...(grant ? ['--grant=' + grant] : [])], { env: { ...process.env, PAY_DEBUG: '1' } });
  P.stdout.on('data', d => { for (const l of String(d).split('\n')) { lines.push(l); if (/\[host\]|\[obs\]|\[pay|rror|fail/.test(l)) log('[C#]', l.trim().replace(/^\d\d:\d\d:\d\d /, '').slice(0, 170)); } });
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
  // 1. OBS → recording in the channel's folder, shared, offered
  execSync(`ffmpeg -hide_banner -loglevel error -re -f lavfi -i testsrc=size=320x240:rate=30 -f lavfi -i sine=frequency=440 -t 12 -c:v libx264 -preset veryfast -g 60 -pix_fmt yuv420p -c:a aac -f flv rtmp://localhost:${RT}/live/host-key`);
  log('OBS streamed 12 s');
  const rec = await A.evaluate(async (c) => { for (let i = 0; i < 60; i++) { const d = opentube.engine.derive(c); const b = [...(d.chain?.blocks?.values?.() || [])].concat([]).find?.(() => false); const ends = [...opentube.engine.chains.get(c).blocks.values()].filter(b => b.payload.type === 'liveend' && b.payload.rec); if (ends.length) return ends[0].payload.rec; await new Promise(r => setTimeout(r, 500)); } return null; }, chain);
  check(!!rec?.infohash, `the stream's end offers the recording: ${rec ? `${rec.file}, ${rec.size} bytes, ${rec.infohash.slice(0, 12)}…, thumb ${rec.thumb ? 'yes' : 'no'}` : 'none'}`);
  const dirs = fs.readdirSync(W + '/videos').filter(d => !d.startsWith('.')); const folder = W + '/videos/' + dirs[0];
  const meta = JSON.parse(fs.readFileSync(folder + '/channel.json', 'utf8'));
  check(meta.chain === chain && meta.videos[rec?.infohash]?.complete && fs.existsSync(folder + '/' + rec?.file), `kept in ${dirs[0]}/ with channel.json (${Object.keys(meta.videos).length} video)`);
  check(fs.existsSync(`${folder}/.opentube/${rec?.infohash}.torrent`), 'its .torrent is saved for the next start');
  // 2. a browser downloads it from the node over WebRTC
  const V = await open('V', Q + '#/');
  const t0 = Date.now();
  const got = await V.evaluate(async ({ ih, tr }) => new Promise((res) => {
    const t = opentube.wt.add(`magnet:?xt=urn:btih:${ih}&tr=${encodeURIComponent(tr)}`, { announce: [tr] });
    const to = setTimeout(() => res({ error: 'timeout', progress: t.progress }), 90000);
    t.on('done', async () => { clearTimeout(to); const b = new Uint8Array(await t.files[0].arrayBuffer()); res({ h: [...new Uint8Array(await crypto.subtle.digest('SHA-256', b))].map(x => x.toString(16).padStart(2, '0')).join('') }); });
  }), { ih: rec.infohash, tr: TRACKER });
  check(got.h === crypto.createHash('sha256').update(fs.readFileSync(folder + '/' + rec.file)).digest('hex'), `a viewer's browser downloaded it from the node over WebRTC: ${got.error || 'identical'} (${((Date.now() - t0) / 1000).toFixed(1)} s)`);
  // 3. the owner's file manager, from the site
  const ctl = (cmd, args) => A.evaluate(async ({ k, cmd, args }) => { try { return (await opentube.nodeCtl(opentube.engine.nodes.get(k), cmd, args)).data; } catch (e) { return 'refused: ' + e.message; } }, { k: nodeKey, cmd, args });
  const files = await ctl('files', {});
  const ch = files.channels?.find(c => c.chain === chain);
  check(ch?.videos?.some(v => v.ih === rec.infohash && v.complete && v.pinned), `Manage files lists it (pinned until published): ${JSON.stringify(ch?.videos?.map(v => ({ title: v.title, complete: v.complete, pinned: v.pinned })))}`);
  const capped = await ctl('cap', { gb: 3 }); check(capped.capGB === 3, 'the size limit changes: ' + capped.capGB + ' GB');
  // 4. store: the owner uploads from a browser with no video folder; the node downloads it and keeps it
  await A.goto(BASE + Q + '#/c/' + chain + '/upload'); await A.waitForTimeout(1500);
  await A.setInputFiles('input[type=file][accept="video/*"]', '/tmp/claude-0/-home-user-OpenTubeDecentralized/a82132eb-7c72-5513-9bf1-91c1c8d3383e/scratchpad/work/small.mp4'); await A.waitForTimeout(800);
  await A.click('text=Publish to channel'); await A.waitForFunction(() => location.hash.startsWith('#/v/'), null, { timeout: 60000 });
  const vih = await A.evaluate((c) => opentube.engine.derive(c).videos.at(-1)?.payload.infohash, chain);
  const st = await ctl('store', { chain, ih: vih }); log('site → store:', JSON.stringify(st));
  let prog = null; for (let i = 0; i < 90 && !prog?.done; i++) { prog = await ctl('store.status', { ih: vih }); if (!prog?.done) await new Promise(r => setTimeout(r, 1000)); }
  const kept = fs.readdirSync(folder).find(f => f.endsWith('.mp4') && f !== rec.file);
  check(prog?.done && kept && crypto.createHash('sha256').update(fs.readFileSync(folder + '/' + kept)).digest('hex') === crypto.createHash('sha256').update(fs.readFileSync('/tmp/claude-0/-home-user-OpenTubeDecentralized/a82132eb-7c72-5513-9bf1-91c1c8d3383e/scratchpad/work/small.mp4')).digest('hex'), `store: the node downloaded the upload from the browser and keeps it: ${kept || 'none'} ${JSON.stringify(prog)}`);
  // 5. pay per piece (ot_pay): a viewer with no coins downloads that video from the node alone; the node gets its
  //    signed tabs and stops sending once it can't pay (as the JavaScript node does)
  await ctxs[0].close();   // the uploader's browser is gone: only the node has it
  const V2 = await open('V2', Q + '#/c/' + chain);
  await until(V2, ({ c, ih }) => [...(opentube.engine.chains.get(c)?.blocks.values() || [])].some(b => b.payload.infohash === ih), { c: chain, ih: vih }, 60000).catch(() => log('V2 never got the video block'));
  const r2 = await V2.evaluate(async ({ ih, tr }) => new Promise((res) => {
    const t = opentube.wt.add(`magnet:?xt=urn:btih:${ih}&tr=${encodeURIComponent(tr)}`, { announce: [tr] });
    setTimeout(() => res({ progress: t.progress, peers: t.numPeers, done: t.done }), 40000);
  }), { ih: vih, tr: TRACKER });
  const stopped = lines.find(l => /\[pay\] stopped sending to/.test(l)), gotTab = lines.some(l => /pay-debug\] got \{"tab"/.test(l));
  check(gotTab, 'the node received the viewer\'s signed tabs over ot_pay');
  check(!!stopped && !r2.done, `pay per piece: a viewer who can't pay is stopped (${stopped ? stopped.replace(/^.*\[pay\] /, '') : 'never stopped'}; it got ${Math.round(r2.progress * 100)}%)`);
  for (const c of ctxs.slice(1)) await c.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); try { P.kill(); } catch {} process.exit(1); });
