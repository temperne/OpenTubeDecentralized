// Run: dotnet build dotnet && node dotnet/OpenTube.Tests/compare-hub.cjs  (starts a JavaScript node and the C# node in turn)
// The live hub and tracker at protocol level, against a JavaScript node and the C# node: the same script of messages,
// and what each participant receives, compared.
const { spawn, execSync } = require('child_process'); const fs = require('fs'); const crypto = require('crypto');
const REPO = require('path').resolve(__dirname, '../..'), W = require('os').tmpdir();
new Function(/<script id="opentube-core">([\s\S]*?)<\/script>/.exec(fs.readFileSync(REPO + '/index.html', 'utf8'))[1])();
const C = globalThis.OpenTubeCore, sleep = (ms) => new Promise(r => setTimeout(r, ms));
const st = (port) => { try { return JSON.parse(execSync(`curl -sf localhost:${port}/status`)); } catch { return null; } };
async function scenario(port) {
  const owner = await C.newIdentity(), viewer = await C.newIdentity(), other = await C.newIdentity();
  const so = await C.signer(owner), sv = await C.signer(viewer), sx = await C.signer(other);
  const g = { v: 1, chain: null, parents: [], height: 0, ts: Date.now(), author: owner.pub, payload: { type: 'genesis', kind: 'channel', name: 'Hub test', about: '', avatar: '', comments: 'anyone' }, nonce: 0 };
  g.hash = await C.hashBlock(g); g.sig = await so.sign(g.hash);
  const ot = new WebSocket(`ws://localhost:${port}/ot`); await new Promise(r => ot.onopen = r); ot.send(JSON.stringify({ t: 'mp', e: [g] }));
  for (let i = 0; i < 120 && !(st(port)?.registered >= 1); i++) await sleep(500);
  const client = async (name) => { const ws = new WebSocket(`ws://localhost:${port}/live`), got = []; let nonce; ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.t === 'hi') nonce = m.nonce; got.push(m); }; await new Promise(r => ws.onopen = r); for (let i = 0; i < 50 && !nonce; i++) await sleep(50); return { ws, got, nonce, send: (m) => ws.send(JSON.stringify(m)) }; };
  const stream = crypto.randomBytes(32).toString('hex');
  const B = await client('B'), V = await client('V'), X = await client('X'), P = await client('P');
  B.send({ t: 'auth', key: owner.pub, sig: await so.sign(C.liveAuthPreimage(B.nonce)) });
  V.send({ t: 'auth', key: viewer.pub, sig: await sv.sign(C.liveAuthPreimage(V.nonce)) });
  X.send({ t: 'auth', key: other.pub, sig: await sx.sign(C.liveAuthPreimage(X.nonce)) });
  await sleep(300);
  X.send({ t: 'broadcast', stream, chain: g.hash, mime: 'video/webm', rtc: false });   // not the owner: refused
  B.send({ t: 'broadcast', stream, chain: g.hash, mime: 'video/webm', rtc: false, dcSlots: 0 }); await sleep(300);
  V.send({ t: 'watch', stream, slots: 2, dcSlots: 0 }); await sleep(300);
  const seg = { stream, n: 0, o: 0, h: 'ab'.repeat(32), sig: 's', d: Buffer.from('hello').toString('base64') };
  B.send({ t: 'seg', seg }); await sleep(300);
  V.send({ t: 'alive', stream, q: { kbps: 812.4, stalls: 1, buf: 3.27 } }); await sleep(200);
  P.send({ t: 'probe', stream }); P.send({ t: 'probe', stream: 'cd'.repeat(32) }); await sleep(300);
  V.send({ t: 'signal', stream, to: 'nobody', data: { x: 1 } }); await sleep(200);   // not its parent: dropped
  B.send({ t: 'leave', stream }); await sleep(400);
  // tracker: two peers in one swarm, an offer passed on, then a scrape
  const tr = async () => { const ws = new WebSocket(`ws://localhost:${port}/tracker`), got = []; ws.onmessage = (e) => got.push(JSON.parse(e.data)); await new Promise(r => ws.onopen = r); return { ws, got, send: (m) => ws.send(JSON.stringify(m)) }; };
  const T1 = await tr(), T2 = await tr(), ih = 'ab'.repeat(20);
  T1.send({ action: 'announce', info_hash: ih, peer_id: 'peer-one', numwant: 5, offers: [] }); await sleep(200);
  T2.send({ action: 'announce', info_hash: ih, peer_id: 'peer-two', numwant: 5, offers: [{ offer: { type: 'offer', sdp: 'x' }, offer_id: 'o1' }] }); await sleep(200);
  T1.send({ action: 'announce', info_hash: ih, peer_id: 'peer-one', to_peer_id: 'peer-two', answer: { type: 'answer', sdp: 'y' }, offer_id: 'o1' }); await sleep(200);
  T1.send({ action: 'scrape', info_hash: [ih, 'cd'.repeat(20)] }); await sleep(300);
  for (const c of [B, V, X, P]) c.ws.close(); T1.ws.close(); T2.ws.close(); ot.close();
  // what each received, without the parts that differ by run (nonces, keys, stream ids, times)
  const clean = (m) => JSON.parse(JSON.stringify(m, (k, v) => ['nonce', 'stream', 'at', 'first', 'last', 'k', 'now', 'from', 'peer'].includes(k) ? '·' : v));
  const keep = (got) => got.filter(m => m.t !== 'viewers').map(clean);   // (viewer reports are timed; checked separately below)
  const viewersLast = (got) => { const r = [...got].reverse().find(m => m.t === 'viewers'); return r ? clean({ viewers: r.viewers, peak: r.peak, quality: r.quality, members: r.members, seg: r.seg, seenCount: r.seenCount }) : null; };
  return { B: keep(B.got), V: keep(V.got), X: keep(X.got), P: keep(P.got), Bviewers: viewersLast(B.got), T1: T1.got, T2: T2.got };
}
(async () => {
  const out = {};
  for (const [name, port, cmd, args, env] of [
    ['JavaScript node', 8795, process.execPath, [REPO + '/server.mjs'], { PORT: '8795', DATA_DIR: W + '/hub-js', MINE: '1', RANKER: '0', MESH: '0' }],
    ['C# node', 8796, 'dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', '--port=8796', '--data=' + W + '/hub-cs', '--mine=all', '--no-tunnel', '--no-ranking', '--site-dir=' + REPO], {}]]) {
    fs.rmSync(W + (port === 8795 ? '/hub-js' : '/hub-cs'), { recursive: true, force: true });
    const p = spawn(cmd, args, { env: { ...process.env, ...env } });
    for (let i = 0; i < 60 && !st(port); i++) await sleep(500);
    out[name] = await scenario(port); p.kill();
  }
  const [a, b] = Object.values(out);
  for (const k of Object.keys(a)) {
    const same = JSON.stringify(a[k]) === JSON.stringify(b[k]);
    console.log(`${k}: ${same ? 'same' : 'DIFFERENT'} — ${JSON.stringify(a[k]).slice(0, 220)}`);
    if (!same) console.log(`   C#: ${JSON.stringify(b[k]).slice(0, 220)}`);
  }
  process.exit(0);
})().catch(e => { console.error(e); process.exit(1); });
