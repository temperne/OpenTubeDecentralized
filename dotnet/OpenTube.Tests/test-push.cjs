// Run: dotnet build dotnet && ECE_DIR=<folder with node_modules/http_ece> node dotnet/OpenTube.Tests/test-push.cjs   (needs ffmpeg)
// Your computer notifies your devices while they sleep: a device registers (push address, keys, subscriptions);
// OBS goes live on the subscribed channel; the computer sends an encrypted Web Push (RFC 8291) signed with VAPID
// (RFC 8292) to the push address, which the reference implementation decrypts. A push address that's gone (410) is
// forgotten.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os'), crypto = require('crypto'), http = require('http'); const ece = require(path.join(process.env.ECE_DIR, 'node_modules/http_ece'));
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-push-'));
const N = 8891, RT = 1940, TRACKER = `ws://127.0.0.1:${N}/tracker`, BASE = `http://localhost:${N}/`, Q = `?folder=browser&trackers=${encodeURIComponent(TRACKER)}`;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return ((Date.now() - t0) / 1000).toFixed(1); await new Promise(r => setTimeout(r, 300)); } throw new Error('timeout'); };
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let P, lines = []; const start = (grant) => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, `--videos=${W}/videos`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', `--obs-port=${RT}`, '--site-dir=' + SITE, ...(grant ? ['--grant=' + grant] : [])], { env: { ...process.env, PAY_DEBUG: '1' } });
  P.stdout.on('data', d => { for (const l of String(d).split('\n')) { lines.push(l); if (/\[host\]|\[obs\]|\[pay|\[live\]|\[push|rror|fail/.test(l)) log('[C#]', l.trim().replace(/^\d\d:\d\d:\d\d /, '').slice(0, 170)); } });
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
  // a push service: records what arrives; /gone answers 410
  const got = []; const ps = http.createServer((req, res) => { const parts = []; req.on('data', d => parts.push(d)); req.on('end', () => { got.push({ path: req.url, headers: req.headers, body: Buffer.concat(parts) }); res.writeHead(req.url === '/gone' ? 410 : 201); res.end(); }); });
  await new Promise(r => ps.listen(0, '127.0.0.1', r)); const PS = `http://127.0.0.1:${ps.address().port}`;
  const dev = crypto.createECDH('prime256v1'); dev.generateKeys(); const auth = crypto.randomBytes(16);
  const ctl = (cmd, args) => A.evaluate(async ({ k, cmd, args }) => { try { return (await opentube.nodeCtl(opentube.engine.nodes.get(k), cmd, args)).data; } catch (e) { return 'refused: ' + e.message; } }, { k: nodeKey, cmd, args });
  const reg = (device, endpoint) => ctl('push.register', { device, endpoint, p256dh: dev.getPublicKey().toString('base64url'), auth: auth.toString('base64url'), label: 'test phone', mine: [], prefs: { subs: { [chain]: 'sub' }, notif: { types: {}, objects: {}, typeObj: {} } } });
  const r1 = await reg('phone', PS + '/push/abc'); const r2 = await reg('old-phone', PS + '/gone');
  check(r1?.registered && r1.subscriptions === 1 && r2?.registered, `a device registers with its computer (signed by you): ${JSON.stringify(r1)}`);
  const vapidKey = (await ctl('push.key', {})).key;
  // OBS goes live on the subscribed channel
  const ff = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-re', '-f', 'lavfi', '-i', 'testsrc=size=320x240:rate=30', '-f', 'lavfi', '-i', 'sine=frequency=440', '-t', '12', '-c:v', 'libx264', '-preset', 'veryfast', '-g', '60', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-f', 'flv', `rtmp://localhost:${RT}/live/host-key`]);
  for (let i = 0; i < 60 && !got.some(g => g.path === '/push/abc'); i++) await new Promise(r => setTimeout(r, 500));
  const p = got.find(g => g.path === '/push/abc');
  check(!!p, 'the computer sent a push when the channel went live');
  if (p) {
    check(p.headers['content-encoding'] === 'aes128gcm' && /^vapid t=.+, k=/.test(p.headers.authorization || '') && p.headers.ttl && p.headers.urgency === 'high', `Web Push headers: ${p.headers['content-encoding']}, TTL ${p.headers.ttl}, urgency ${p.headers.urgency}, VAPID`);
    const [t, k] = p.headers.authorization.replace('vapid ', '').split(', '); const [h_, c_, s_] = t.slice(2).split('.');
    const pub = Buffer.from(k.slice(2), 'base64url');
    const key = crypto.createPublicKey({ key: { kty: 'EC', crv: 'P-256', x: pub.subarray(1, 33).toString('base64url'), y: pub.subarray(33).toString('base64url') }, format: 'jwk' });
    check(k.slice(2) === vapidKey && crypto.verify('sha256', Buffer.from(h_ + '.' + c_), { key, dsaEncoding: 'ieee-p1363' }, Buffer.from(s_, 'base64url')) && JSON.parse(Buffer.from(c_, 'base64url')).aud === PS, 'signed by the computer’s VAPID key, for that push service');
    let msg = null; try { msg = JSON.parse(ece.decrypt(p.body, { version: 'aes128gcm', privateKey: dev, authSecret: auth })); } catch (e) { log('decrypt failed:', e.message); }
    check(msg?.type === 'live' && /Host Channel is live: Hosted show/.test(msg.text) && msg.href.startsWith('#/live/' + chain), `the reference implementation decrypts it: ${JSON.stringify(msg)}`);
  }
  await new Promise(r => ff.on('exit', r)); await new Promise(r => setTimeout(r, 1500));
  check(lines.some(l => /old-phone|push address is gone/.test(l)) || lines.some(l => /test phone’s push address is gone/.test(l)), 'a push address that’s gone (410) is forgotten');
  const again = await reg('phone', PS + '/push/abc'); check(again?.registered, 'registering again (new settings) replaces it');
  // a private message: someone else writes to you; your computer recognizes it by its hint and pushes, without reading it
  await A.evaluate(() => opentube.mail.publishKeys());
  const B = await open('B', Q + '#/');
  await until(B, (u) => (opentube.engine.ledger.msgkeys.get(u) || []).length > 0 && [...opentube.engine.nodes.values()].some(n => n.grant?.user === u && n.info?.notify), user, 120000).catch(() => log('B never saw the messaging key or the notify key'));
  const before = got.length;
  const ts0 = Date.now(); await B.evaluate((u) => opentube.mail.send(u, 'hello, this is private'), user); log('sending took', ((Date.now() - ts0) / 1000).toFixed(1), 's');
  const tm = Date.now(); for (let i = 0; i < 300 && !got.slice(before).some(g => g.path === '/push/abc'); i++) await new Promise(r => setTimeout(r, 500));
  log('the message push came after', ((Date.now() - tm) / 1000).toFixed(1), 's');
  const mp = got.slice(before).find(g => g.path === '/push/abc');
  let mm = null; try { mm = mp && JSON.parse(ece.decrypt(mp.body, { version: 'aes128gcm', privateKey: dev, authSecret: auth })); } catch {}
  check(mm?.type === 'message' && mm.text === 'You have a new private message' && mm.href === '#/messages', `a private message to you: your computer pushes “${mm?.text}” (it found its hint; it can’t read the message)`);
  const blk = await B.evaluate(() => [...opentube.engine.chains.values()].filter(c => c.genesis.payload.kind === 'mail').flatMap(c => [...c.blocks.values()]).find(b => b.payload.type === 'msg')?.payload);
  check(blk && blk.hints?.length === 3 && !JSON.stringify(blk).includes('hello') && !JSON.stringify(blk).includes(user), 'on the chain: 3 hints (padded), no text and no recipient in the clear');
  await A.evaluate(() => opentube.mail.inbox());   // (opens the month's message chain: it loads from the network)
  for (let i = 0; i < 40 && !(await A.evaluate(async () => (await opentube.mail.inbox()).length)); i++) await new Promise(r => setTimeout(r, 500));
  const inbox = await A.evaluate(async () => (await opentube.mail.inbox()).map(m => m.text));
  check(inbox.includes('hello, this is private'), 'you can read it (with your messaging key, on your own device)');
  ps.close(); for (const c of ctxs) await c.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); P?.kill(); process.exit(1); });
