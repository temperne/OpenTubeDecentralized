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
//
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
setInterval(() => engine.gossip(), 15000);

if (!ranker.genesis) {
  const name = (process.env.NODE_NAME || `Trending on ${(await import('node:os')).hostname()}`).slice(0, 60);
  ranker.genesis = await engine.createGenesis({ type: 'genesis', kind: 'list', name, about: 'Videos ranked by votes and freshness, computed by this home node.', ranking: true }, rankerSigner);
  fs.writeFileSync(rankerFile, JSON.stringify(ranker), { mode: 0o600 });
  log(`created ranking list "${name}"`);
} else if (!engine.directory.has(ranker.genesis.hash)) {
  await engine.addToMempool(ranker.genesis); engine.openChain(ranker.genesis.hash); engine.maybeMine();
}
const RANKER_ID = ranker.genesis.hash;
async function publishRanking() {
  if (!engine.directory.has(RANKER_ID)) return;   // wait until the list is registered on the main chain
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

// ---- HTTP: the page itself, a status endpoint, and the WebSocket upgrade
const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://x');
  if (url.pathname === '/' || url.pathname === '/index.html' || /^\/[@~][A-Za-z0-9._-]{3,30}\/?$/.test(url.pathname)) {  // /@channel and /~list links open the page
    res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-cache' });
    return res.end(fs.readFileSync(path.join(DIR, 'index.html')));
  }
  if (url.pathname === '/sw.js') {  // WebTorrent's streaming service worker: lets videos play while downloading
    res.writeHead(200, { 'content-type': 'text/javascript; charset=utf-8', 'cache-control': 'no-cache' });
    return res.end(fs.readFileSync(path.join(DIR, 'sw.js')));
  }
  if (url.pathname.startsWith('/ingest/') && (req.method === 'POST' || req.method === 'PUT')) return ingest(req, res, url.pathname.slice(8));
  if (url.pathname === '/status') {
    res.writeHead(200, { 'content-type': 'application/json', 'access-control-allow-origin': '*' });
    return res.end(JSON.stringify({ network: engine.MAIN_ID, height: engine.main.best.height, tip: engine.main.best.hash, registered: engine.directory.size, pending: engine.mempool.size, chains: engine.chains.size, peers: peers.size, mining: !!engine.miner, node: signer.pub, now: Date.now(), ranker: RANKER_ID, tracker: tracker.stats(), live: liveHub.stats(), ingest: true, memberships: engine.memberships.size }));
  }
  res.writeHead(404); res.end('not found');
});
server.on('upgrade', (req, socket) => {
  const p = new URL(req.url, 'http://x').pathname;
  if (p === '/ot') acceptSocket(req, socket, 'node');
  else if (p === '/tracker') acceptSocket(req, socket, 'tracker');
  else if (p === '/live') acceptSocket(req, socket, 'live');
  else socket.destroy();
});
server.listen(PORT, () => log(`OpenTube node on http://localhost:${PORT}  (node /ot, video tracker /tracker, live hub /live, status /status)`));
