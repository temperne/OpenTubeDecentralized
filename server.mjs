#!/usr/bin/env node
// OpenTube home node: serves index.html and runs a full node on the same address.
// Browsers that load the page from here connect to wss://<host>/ot automatically.
//
//   node server.mjs                       # http://localhost:8080
//   PORT=80 node server.mjs
//   PEERS=wss://other.example/ot node server.mjs   # also sync with other home nodes
//   MAIN_ONLY=1 node server.mjs           # keep only the main chain, not channel chains
//   MINE=0 node server.mjs                # don't spend CPU confirming registrations
//   NODE_NAME="My site" node server.mjs   # name used for this node's ranking list
//   RTMP_PORT=1935 node server.mjs        # receive OBS "Start Streaming" (rtmp://localhost/live)
//   RANKER=0 node server.mjs              # don't run a ranking list
//   PUBLIC=auto node server.mjs           # also HTTPS on 443 as <your-ip>.sslip.io with Let's Encrypt (needs ports 80/443)
//   PUBLIC=node.example.com node server.mjs   # the same with your own domain
//   VIDEO_DIR=./videos HOST_USER=<user ID> node server.mjs   # keep and share that user's channels' videos (needs: npm install webtorrent)
//
// Started by opentube.mjs (the copy-paste command on My node), it also gets NODE_GRANT (your signed grant: this node
// is yours), NODE_SITE (the site it belongs to) and NODE_ROLES, and announces itself to your My node page.
// No npm install needed: Node 20+ (Ed25519 WebCrypto, global WebSocket client on 22+).
// For https/wss, put it behind a TLS proxy such as Caddy:  caddy reverse-proxy --to :8080
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const DIR = path.dirname(fileURLToPath(import.meta.url));
const PORT = Number(process.env.PORT || 8080);
const DATA = path.resolve(process.env.DATA_DIR || path.join(DIR, 'data'));
const MAIN_ONLY = process.env.MAIN_ONLY === '1';
const MINE = process.env.MINE !== '0';
const PEERS = (process.env.PEERS || '').split(',').map(s => s.trim()).filter(Boolean);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);

// ---- load the exact same blockchain rules the browser runs, straight out of index.html
const html = fs.readFileSync(path.join(DIR, 'index.html'), 'utf8');
const coreSrc = /<script id="opentube-core">([\s\S]*?)<\/script>/.exec(html)?.[1];
if (!coreSrc) throw new Error('index.html has no opentube-core script');
new Function(coreSrc)();
const core = globalThis.OpenTubeCore;

// ---- identity + storage
fs.mkdirSync(DATA, { recursive: true });
const keyFile = path.join(DATA, 'key.json');
if (!fs.existsSync(keyFile)) fs.writeFileSync(keyFile, JSON.stringify(await core.newIdentity()), { mode: 0o600 });
const signer = await core.signer(JSON.parse(fs.readFileSync(keyFile, 'utf8')));
const blocksFile = path.join(DATA, 'blocks.jsonl');
const blockLog = fs.createWriteStream(blocksFile, { flags: 'a' });

// ---- proof of work with Node's native SHA-256 (much faster than a browser)
const lz = (d) => { let z = 0; for (const b of d) { if (b === 0) { z += 8; continue; } return z + Math.clz32(b) - 24; } return z; };
function mine(body, bits, onProgress) {
  let cancelled = false;
  const prefix = core.preimage(body);
  const promise = new Promise((resolve) => {
    let n = Math.floor(Math.random() * 1e9), count = 0;
    const step = () => {
      if (cancelled) return;
      for (let i = 0; i < 20000; i++, n++) {
        if (lz(crypto.createHash('sha256').update(prefix + n).digest()) >= bits) return resolve(n);
      }
      count += 20000; onProgress?.(count); setImmediate(step);
    };
    step();
  });
  return { promise, cancel: () => { cancelled = true; } };
}

// ---- peers: browsers connected to us, plus other home nodes we dial
const peers = new Map();  // id -> { send(obj), label }
let nextId = 1;
function send(msg, to) {
  const data = JSON.stringify(msg);
  if (to) peers.get(to)?.send(data);
  else for (const p of peers.values()) p.send(data);
}

// ---- this node's ranking list ("Trending on <name>"): its own key, created once
const rankerFile = path.join(DATA, 'ranker.json');
let ranker = fs.existsSync(rankerFile) ? JSON.parse(fs.readFileSync(rankerFile, 'utf8')) : null;
if (!ranker) { ranker = { key: await core.newIdentity() }; fs.writeFileSync(rankerFile, JSON.stringify(ranker), { mode: 0o600 }); }
const rankerSigner = await core.signer(ranker.key);

// Rerank ~20 s after new videos/votes arrive (publishRanking is defined further down).
let rankTimer = null;
function soonRank() { if (!rankTimer) rankTimer = setTimeout(() => { rankTimer = null; publishRanking(); }, 20000); }

// Everyone's latest joined-channel list, so headless hosts can follow their owner while the owner is offline.
const memFile = path.join(DATA, 'memberships.json');
let memTimer = null;
const saveMemberships = () => { memTimer ||= setTimeout(() => { memTimer = null; fs.writeFileSync(memFile, JSON.stringify([...engine.memberships.values()])); }, 2000); };

const engine = await core.createEngine({
  signer, send,
  onMembership: () => saveMemberships(),
  keyFor: (pub) => pub === rankerSigner.pub ? rankerSigner : null,
  mine: MINE ? mine : null,
  shouldMine: () => true,          // a home node confirms everyone's registrations
  trackAll: !MAIN_ONLY,
  relayWatch: true,                // pass ad watch-group messages between browsers
  persist: (c, b) => blockLog.write(JSON.stringify({ c, b }) + '\n'),
  onBlock: (chain, b) => {
    if (chain === engine.main) log(`main #${b.height} ${b.hash.slice(0, 12)} (${b.payload.entries.length} registration(s))`);
    else if (b.payload.type === 'video' || b.payload.type === 'vote' || b.payload.type === 'hide') soonRank();
  }
});

// ---- replay stored blocks
if (fs.existsSync(blocksFile)) {
  const byChain = new Map();
  for (const line of fs.readFileSync(blocksFile, 'utf8').split('\n')) {
    if (!line) continue;
    try { const { c, b } = JSON.parse(line); (byChain.get(c) || byChain.set(c, []).get(c)).push(b); } catch {}
  }
  await engine.insert(engine.MAIN_ID, byChain.get(engine.MAIN_ID) || [], null, false);
  for (const [c, blocks] of byChain) if (c !== engine.MAIN_ID && engine.openChain(c)) await engine.insert(c, blocks, null, false);
}
if (fs.existsSync(memFile)) for (const m of JSON.parse(fs.readFileSync(memFile, 'utf8'))) await engine.acceptMembership(m);
log(`network ${engine.MAIN_ID.slice(0, 16)}… · main height ${engine.main.best.height} · ${engine.directory.size} registered · ${engine.chains.size} chains loaded`);
setInterval(() => engine.gossip(), 60000);   // repair pass; new blocks are pushed right away

// ---- OBS "Start Streaming" (RTMP_PORT): OBS → rtmp://localhost/live → this node → the live hub. The owner first clicks
// Go live → Stream from OBS on the site and picks this computer; that live block names this node's key as `relay`.
function liveSink(chain, stream, mime) {
  let n = 0, o = 0, queue = Promise.resolve(), ended = false;
  const seg = (bytes, k) => (queue = queue.then(async () => {
    const g = { stream, n: n++, o, h: crypto.createHash('sha256').update(bytes).digest('hex') };
    o += bytes.length;
    g.sig = await signer.sign(core.liveSegPreimage(g)); g.d = bytes.toString('base64'); if (k) g.k = 1;
    send(g);
  }).catch(e => log('[obs] segment failed:', e.message)));
  let send, finish;
  const site = process.env.NODE_SITE;
  if (!site) {   // this node is the site: feed its own hub
    const src = liveHub.local(stream, chain, mime);
    send = (g) => src.seg(g); finish = () => src.end();
  } else {       // relay to the site's hub as the broadcaster (segments only: this node has no WebRTC)
    let ws = null, ready = false; const pending = [];
    const flush = () => { while (ready && pending.length) ws.send(JSON.stringify({ t: 'seg', seg: pending.shift() })); };
    const connect = () => {
      ready = false; ws = new WebSocket(site.replace(/^http/, 'ws') + '/live');
      ws.onmessage = async (e) => {
        let m; try { m = JSON.parse(e.data); } catch { return; }
        if (m.t === 'hi') { ws.send(JSON.stringify({ t: 'auth', key: signer.pub, sig: await signer.sign(core.liveAuthPreimage(m.nonce)) })); ws.send(JSON.stringify({ t: 'broadcast', stream, chain, mime, rtc: false })); }
        else if (m.t === 'broadcasting') { ready = true; flush(); log('[obs] relaying to ' + site); }
        else if (m.t === 'error') log('[obs] the site refused the stream:', m.error);
      };
      ws.onclose = () => { ready = false; if (!ended) setTimeout(connect, 2000); };
      ws.onerror = () => {};
    };
    connect();
    send = (g) => { pending.push(g); flush(); };
    finish = () => { const done = () => { try { ws.send(JSON.stringify({ t: 'leave', stream })); } catch {} ended = true; setTimeout(() => ws.close(), 500); };
      const wait = () => (pending.length && Date.now() - t0 < 15000 ? setTimeout(wait, 200) : done()); const t0 = Date.now(); wait(); };
  }
  return { init: (bytes) => seg(bytes, false), fragment: (bytes, k) => seg(bytes, k), end: () => { queue.then(() => finish()); } };
}
if (process.env.RTMP_PORT) {
  try {
    const { startRtmp } = await import('./rtmp.mjs');
    const armed = () => {   // the newest live stream, not ended, whose owner named this node as its relay
      let best = null;
      for (const c of engine.chains.values()) {
        if (c === engine.main) continue;
        const d = engine.derive(c.id); if (!d || d.kind !== 'channel') continue;
        for (const b of d.liveNow) if (b.payload.relay === signer.pub && (!best || b.ts > best.b.ts)) best = { chain: c.id, b };
      }
      return best;
    };
    const rtmp = startRtmp({ port: Number(process.env.RTMP_PORT), host: process.env.RTMP_HOST || '127.0.0.1', log: (...a) => log('[obs]', ...a),
      onPublish: () => { const a = armed(); return a ? liveSink(a.chain, a.b.payload.stream, a.b.payload.mime) : 'Not live yet. On the site: Go live → Stream from OBS, pick this computer, then Start Streaming again.'; } });
    globalThis.openTubeRtmp = { status: () => ({ ...rtmp.status(), armed: !!armed() }) };
  } catch (e) { log('OBS receiver unavailable:', e.message); }
}

// ---- video hosting without a browser (VIDEO_DIR + HOST_USER, set by opentube.mjs): keep and share the videos of
// HOST_USER's joined and own channels (host.mjs, needs the webtorrent package)
if (process.env.VIDEO_DIR && process.env.HOST_USER) {
  try {
    const { startHost } = await import('./host.mjs');
    globalThis.openTubeHost = await startHost({ engine, user: process.env.HOST_USER, videoDir: path.resolve(process.env.VIDEO_DIR), site: process.env.NODE_SITE || `http://localhost:${PORT}`, capGB: Number(process.env.CAP_GB) || 5, log });
  } catch (e) { log('video hosting unavailable:', e.message, '(npm install webtorrent next to server.mjs)'); }
}

// ---- "your node": started from someone's My node command, it announces itself to their My node page
const GRANT = (() => { try { return process.env.NODE_GRANT ? JSON.parse(Buffer.from(process.env.NODE_GRANT, 'base64url').toString('utf8')) : null; } catch { return null; } })();
if (process.env.NODE_GRANT && !(GRANT && core.validNodeGrant(GRANT))) log('NODE_GRANT is not a valid grant; not announcing this node');
else if (GRANT) {
  const os = await import('node:os');
  const roles = (process.env.NODE_ROLES || 'node').split(',').filter(Boolean).slice(0, 8);
  const addrs = () => [...(process.env.PUBLIC_URL ? [process.env.PUBLIC_URL] : []), ...Object.values(os.networkInterfaces()).flat()
    .filter(i => i && i.family === 'IPv4' && !i.internal).map(i => `http://${i.address}:${PORT}`), `http://localhost:${PORT}`].slice(0, 8);
  const announce = async () => {
    let host = globalThis.openTubeHost?.status() || null;
    if (!host) try { host = JSON.parse(fs.readFileSync(process.env.HOST_STATUS_FILE, 'utf8')); if (Date.now() - host.at > 120000) host = { ...host, stale: true }; } catch {}
    const info = { name: (process.env.NODE_LABEL || os.hostname()).slice(0, 60), os: `${os.platform()} ${os.arch()}`, node: process.version, height: engine.main.best.height,
      peers: peers.size, chains: engine.chains.size, site: process.env.NODE_SITE || null, host, obs: globalThis.openTubeRtmp?.status?.() || null };
    try { await engine.publishNodeInfo({ grant: GRANT, roles, addrs: addrs(), info }); } catch (e) { log('announce failed:', e.message); }
  };
  log(`this node belongs to user ${GRANT.user.slice(0, 8)}… (${roles.join(', ')}); it will show up on their My node page`);
  setTimeout(announce, 4000); setInterval(announce, 30000);
}

const RANKS = process.env.RANKER !== '0';   // RANKER=0: no ranking list (personal nodes started by opentube.mjs)
if (!RANKS) {}
else if (!ranker.genesis) {
  const name = (process.env.NODE_NAME || `Trending on ${(await import('node:os')).hostname()}`).slice(0, 60);
  ranker.genesis = await engine.createGenesis({ type: 'genesis', kind: 'list', name, about: 'Videos ranked by votes and freshness, computed by this home node.', ranking: true }, rankerSigner);
  fs.writeFileSync(rankerFile, JSON.stringify(ranker), { mode: 0o600 });
  log(`created ranking list "${name}"`);
} else if (!engine.directory.has(ranker.genesis.hash)) {
  await engine.addToMempool(ranker.genesis); engine.openChain(ranker.genesis.hash); engine.maybeMine();
}
const RANKER_ID = RANKS ? ranker.genesis.hash : null;
async function publishRanking() {
  if (!RANKER_ID || !engine.directory.has(RANKER_ID)) return;   // wait until the list is registered on the main chain
  const items = engine.rank();
  try { await engine.publishRanking(RANKER_ID, items); } catch (e) { log('ranking failed', e.message); }
}
// Also rerank every RANK_EVERY_SECONDS (default 5 minutes) regardless.
setTimeout(publishRanking, 5000);
setInterval(publishRanking, Math.max(10, Number(process.env.RANK_EVERY_SECONDS) || 300) * 1000);

// ---- video tracker at /tracker: introduces browsers that hold the same video
const tracker = core.createTracker();
// ---- live hub at /live: introduces live viewers to each other (members first) and relays signed segments
const liveHub = core.createLiveHub({
  ownerOf: (c) => engine.directory.get(c)?.genesis.author || engine.chains.get(c)?.owner || null,
  signerOf: (c, s) => engine.chains.has(c) ? engine.derive(c)?.streams.find(b => b.payload.stream === s)?.payload.relay || null : null,
  isJoined: (k, c) => !!engine.memberships.get(k)?.joined?.includes(c),
  ingest: { ticket: (t) => { const key = crypto.randomBytes(18).toString('base64url'); ingestTickets.set(key, { ...t, exp: Date.now() + 12 * 3600000 }); return key; } }
});

// ---- OBS ingest: OBS (Recording → Custom Output (FFmpeg) → Output to URL, WebM) sends the stream to
// http(s)://this-node/ingest/<stream key>. The owner's browser got that key from the live hub and named this node as
// the stream's `relay` in its signed live block, so this node cuts the bytes into ~1 s chunks, signs them with its
// own key and feeds them to the hub like any broadcaster's segments. Members (and the owner's browser) save them.
const ingestTickets = new Map();   // stream key -> { stream, chain, mime, exp }
function ingest(req, res, key) {
  const t = ingestTickets.get(key);
  if (!t || t.exp < Date.now()) { res.writeHead(403); return res.end('unknown or expired stream key'); }
  ingestTickets.delete(key);   // one stream per key
  const src = liveHub.local(t.stream, t.chain, t.mime);
  log(`live: OBS stream started (${t.stream.slice(0, 8)}…)`);
  let n = 0, o = 0, parts = [], size = 0, last = Date.now(), queue = Promise.resolve(), done = false;
  const flush = async () => {
    if (!size) return;
    const bytes = Buffer.concat(parts); parts = []; size = 0;
    const g = { stream: t.stream, n: n++, o, h: crypto.createHash('sha256').update(bytes).digest('hex') };
    o += bytes.length;
    g.sig = await signer.sign(core.liveSegPreimage(g));
    g.d = bytes.toString('base64');
    src.seg(g);
  };
  const tick = setInterval(() => { if (Date.now() - last >= 1000) { last = Date.now(); queue = queue.then(flush); } }, 250);
  req.on('data', (chunk) => {
    parts.push(chunk); size += chunk.length;
    if (size >= 512 * 1024) { last = Date.now(); queue = queue.then(flush); }
  });
  const end = () => {
    if (done) return; done = true; clearInterval(tick);
    queue.then(flush).then(() => setTimeout(() => { src.end(); log(`live: OBS stream ended (${(o / 1e6).toFixed(1)} MB)`); }, 2000));
    if (!res.headersSent) { res.writeHead(200); res.end('ok'); }
  };
  req.on('end', end); req.on('close', end); req.on('error', end);
}

// ---- minimal WebSocket server (RFC 6455: text frames, ping/pong, close), so no dependencies
const MAX_MSG = 16 * 1024 * 1024;
function acceptSocket(req, socket, role = 'node') {
  const key = req.headers['sec-websocket-key'];
  if (!key) return socket.destroy();
  const accept = crypto.createHash('sha1').update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
  socket.setNoDelay(true);
  const id = 'ws' + nextId++;
  const frame = (op, payload) => {
    const len = payload.length;
    const head = len < 126 ? Buffer.from([0x80 | op, len])
      : len < 65536 ? Buffer.from([0x80 | op, 126, len >> 8, len & 255])
      : (() => { const b = Buffer.alloc(10); b[0] = 0x80 | op; b[1] = 127; b.writeBigUInt64BE(BigInt(len), 2); return b; })();
    if (!socket.destroyed) socket.write(Buffer.concat([head, payload]));
  };
  const sendText = (s) => frame(1, Buffer.from(s));
  if (role === 'tracker') tracker.attach(id, sendText);
  else if (role === 'live') liveHub.attach(id, sendText);
  else peers.set(id, { send: sendText, label: req.socket.remoteAddress });
  let buf = Buffer.alloc(0), parts = [];
  socket.on('data', (chunk) => {
    buf = Buffer.concat([buf, chunk]);
    for (;;) {
      if (buf.length < 2) return;
      const fin = buf[0] & 0x80, op = buf[0] & 0x0f, masked = buf[1] & 0x80;
      let len = buf[1] & 0x7f, off = 2;
      if (len === 126) { if (buf.length < 4) return; len = buf.readUInt16BE(2); off = 4; }
      else if (len === 127) { if (buf.length < 10) return; len = Number(buf.readBigUInt64BE(2)); off = 10; }
      if (len > MAX_MSG) return socket.destroy();
      const need = off + (masked ? 4 : 0) + len;
      if (buf.length < need) return;
      let payload = buf.subarray(off + (masked ? 4 : 0), need);
      if (masked) { const m = buf.subarray(off, off + 4); payload = Buffer.from(payload); for (let i = 0; i < payload.length; i++) payload[i] ^= m[i & 3]; }
      buf = buf.subarray(need);
      if (op === 8) { frame(8, Buffer.alloc(0)); return socket.end(); }
      if (op === 9) { frame(10, payload); continue; }
      if (op === 1 || op === 0) {
        parts.push(payload);
        if (parts.reduce((s, p) => s + p.length, 0) > MAX_MSG) return socket.destroy();
        if (fin) {
          const text = Buffer.concat(parts).toString('utf8'); parts = [];
          let msg; try { msg = JSON.parse(text); } catch { continue; }
          if (role === 'tracker') tracker.message(id, msg); else if (role === 'live') liveHub.message(id, msg).catch(() => {}); else engine.handle(msg, id).catch(() => {});
        }
      }
    }
  });
  const gone = () => { if (role === 'tracker') tracker.detach(id); else if (role === 'live') liveHub.detach(id); else if (peers.delete(id)) log(`- browser ${id} (${peers.size} connected)`); };
  socket.on('close', gone); socket.on('error', gone);
  if (role === 'tracker' || role === 'live') return;
  log(`+ browser ${id} (${peers.size} connected)`);
  engine.peerJoined(id);
}

// ---- outgoing links to other home nodes (federation)
function dial(url) {
  if (typeof WebSocket !== 'function') return log('PEERS needs Node 22+ (global WebSocket)');
  const id = 'node:' + url;
  const ws = new WebSocket(url);
  ws.onopen = () => { peers.set(id, { send: (s) => ws.readyState === 1 && ws.send(s), label: url }); log(`linked to ${url}`); engine.peerJoined(id); };
  ws.onmessage = (e) => { try { engine.handle(JSON.parse(e.data), id); } catch {} };
  ws.onclose = () => { peers.delete(id); setTimeout(() => dial(url), 10000); };
  ws.onerror = () => {};
}
PEERS.forEach(dial);

// ---- the peer mesh: join the network's WebRTC room like every browser does, so browsers and other nodes connect to
// this node directly (no server in between), introduced by any tracker: the site's, this node's own, public ones.
// Needs the @trystero-p2p/torrent and webtorrent packages (opentube.mjs installs both). MESH=0 turns it off.
async function joinMesh() {   // called once the HTTP server listens (its own tracker must be up)
  try {
    const [{ joinRoom }, { RTCPeerConnection }] = await Promise.all([import('@trystero-p2p/torrent'), import('webrtc-polyfill')]);
    const site = process.env.NODE_SITE;
    let listed = []; try { listed = (await (await fetch('https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_all_ws.txt', { signal: AbortSignal.timeout(5000) })).text()).split('\n').map(x => x.trim()).filter(x => x.startsWith('wss://')).slice(0, 20); } catch {}
    const urls = [...new Set([site && site.replace(/^http/, 'ws') + '/tracker', `ws://localhost:${PORT}/tracker`, ...(engine.CFG.trackers || []), ...listed].filter(Boolean))];
    const room = joinRoom({ appId: 'opentube-' + engine.MAIN_ID.slice(0, 16), relayConfig: { urls }, rtcPolyfill: RTCPeerConnection, maxReceiveBytes: 16 * 1024 ** 2 }, 'opentube/' + engine.MAIN_ID.slice(0, 32));
    const action = room.makeAction('ot', { onMessage: (data, ctx) => engine.handle(data, 'rtc:' + ctx.peerId).catch(() => {}) });
    room.onPeerJoin = (pid) => {
      const id = 'rtc:' + pid;
      peers.set(id, { send: (s) => action.send(JSON.parse(s), { target: pid }).catch(() => {}), label: 'webrtc ' + pid.slice(0, 6) });
      log(`+ peer ${pid.slice(0, 8)} over WebRTC (${peers.size} connected)`); engine.peerJoined(id);
    };
    room.onPeerLeave = (pid) => { if (peers.delete('rtc:' + pid)) log(`- peer ${pid.slice(0, 8)} (${peers.size} connected)`); };
    log(`peer mesh: joined the network's room via ${urls.length} tracker(s)`);
  } catch (e) { log('peer mesh unavailable:', e.message, '(npm install @trystero-p2p/torrent webtorrent next to server.mjs)'); }
}

// ---- HTTP: the page itself, a status endpoint, and the WebSocket upgrade
function handle(req, res) {
  const url = new URL(req.url, 'http://x');
  if (url.pathname === '/' || url.pathname === '/index.html' || /^\/[@~][A-Za-z0-9._-]{3,30}\/?$/.test(url.pathname)) {  // /@channel and /~list links open the page
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-cache' });
    return res.end(fs.readFileSync(path.join(DIR, 'index.html')));
  }
  if (['/opentube.mjs', '/server.mjs', '/headless.mjs', '/rtmp.mjs', '/host.mjs', '/tls.mjs'].includes(url.pathname) && fs.existsSync(path.join(DIR, url.pathname.slice(1)))) {   // for the My node command
    res.writeHead(200, { 'content-type': 'text/javascript; charset=utf-8', 'cache-control': 'no-cache', 'access-control-allow-origin': '*' });
    return res.end(fs.readFileSync(path.join(DIR, url.pathname.slice(1))));
  }
  if (url.pathname === '/sw.js') {  // WebTorrent's streaming service worker: lets videos play while downloading
    res.writeHead(200, { 'content-type': 'text/javascript; charset=utf-8', 'cache-control': 'no-cache' });
    return res.end(fs.readFileSync(path.join(DIR, 'sw.js')));
  }
  if (url.pathname.startsWith('/ingest/') && (req.method === 'POST' || req.method === 'PUT')) return ingest(req, res, url.pathname.slice(8));
  if (url.pathname === '/status') {
    res.writeHead(200, { 'content-type': 'application/json', 'access-control-allow-origin': '*' });
    return res.end(JSON.stringify({ network: engine.MAIN_ID, height: engine.main.best.height, tip: engine.main.best.hash, registered: engine.directory.size, pending: engine.mempool.size, chains: engine.chains.size, peers: peers.size, mining: !!engine.miner, node: signer.pub, now: Date.now(), ranker: RANKER_ID, tracker: tracker.stats(), live: liveHub.stats(), liveStreams: liveHub.live().slice(0, 50), ingest: true, memberships: engine.memberships.size }));
  }
  res.writeHead(404); res.end('not found');
}
function upgrade(req, socket) {
  const p = new URL(req.url, 'http://x').pathname;
  if (p === '/ot') acceptSocket(req, socket, 'node');
  else if (p === '/tracker') acceptSocket(req, socket, 'tracker');
  else if (p === '/live') acceptSocket(req, socket, 'live');
  else socket.destroy();
}
const server = http.createServer(handle);
server.on('upgrade', upgrade);
server.listen(PORT, async () => {
  log(`OpenTube node on http://localhost:${PORT}  (node /ot, video tracker /tracker, live hub /live, status /status)`);
  // PUBLIC=auto (or PUBLIC=your.domain): also serve HTTPS on 443 with a Let's Encrypt certificate, so browsers can use
  // this node and its tracker. auto = <public IP, dashed>.sslip.io, no domain needed (tls.mjs).
  if (process.env.PUBLIC) {
    try {
      const { autoHttps } = await import('./tls.mjs');
      const pub = await autoHttps({ handler: handle, upgrade, dataDir: DATA, domain: process.env.PUBLIC === 'auto' ? null : process.env.PUBLIC, email: process.env.PUBLIC_EMAIL || null, log });
      if (pub) process.env.PUBLIC_URL = pub.url;
    } catch (e) { log('[https] off:', e.message); }
  }
  if (process.env.MESH !== '0') joinMesh();
});
