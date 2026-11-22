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
import net from 'node:net';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const DIR = path.dirname(fileURLToPath(import.meta.url));
const PORT = Number(process.env.PORT || 8080);
const DATA = path.resolve(process.env.DATA_DIR || path.join(DIR, 'data'));
const MAIN_ONLY = process.env.MAIN_ONLY === '1';
const MINE = process.env.MINE !== '0' && process.env.MINE !== 'own';
const MINE_OWN = process.env.MINE === 'own';   // personal nodes: confirm only their own transactions (receipts, etc.)
const PEERS = (process.env.PEERS || '').split(',').map(s => s.trim()).filter(Boolean);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);

// ---- load the exact same blockchain rules the browser runs, straight out of index.html
const html = fs.readFileSync(path.join(DIR, 'index.html'), 'utf8');
const coreSrc = /<script id="opentube-core">([\s\S]*?)<\/script>/.exec(html)?.[1];
const BUILD = crypto.createHash('sha256').update(html).digest('hex').slice(0, 12);   // the same fingerprint browsers compute for the page
// a fingerprint of every program file this node runs, so the site can check each one against its own copy (a stale
// rtmp.mjs once bound OBS to localhost while index.html, and so the version, looked current)
const FILES = Object.fromEntries(['server.mjs', 'rtmp.mjs', 'host.mjs', 'tls.mjs', 'headless.mjs'].filter(f => fs.existsSync(path.join(DIR, f)))
  .map(f => [f, crypto.createHash('sha256').update(fs.readFileSync(path.join(DIR, f))).digest('hex').slice(0, 12)]));
if (!coreSrc) throw new Error('index.html has no opentube-core script');
new Function(coreSrc)();
const core = globalThis.OpenTubeCore;

// ---- identity + storage
fs.mkdirSync(DATA, { recursive: true });
const keyFile = path.join(DATA, 'key.json');
if (!fs.existsSync(keyFile)) fs.writeFileSync(keyFile, JSON.stringify(await core.newIdentity()), { mode: 0o600 });
const signer = await core.signer(JSON.parse(fs.readFileSync(keyFile, 'utf8')));
const nodePeerId = (pub) => crypto.createHash('sha1').update('opentube-node:' + pub).digest('hex');   // this node's WebTorrent peer ID
// The owner's grant (from the My node command): this node is theirs.
const GRANT = (() => { try { return process.env.NODE_GRANT ? JSON.parse(Buffer.from(process.env.NODE_GRANT, 'base64url').toString('utf8')) : null; } catch { return null; } })();
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
  signer, send, peers: () => [...peers.keys()],
  onMembership: () => saveMemberships(),
  keyFor: (pub) => pub === rankerSigner.pub ? rankerSigner : null,
  mine: MINE || MINE_OWN ? mine : null,
  shouldMine: MINE ? () => true : (g) => g.author === signer.pub,   // a home node confirms everyone's registrations; MINE=own: only its own transactions
  onReceipt: (r, from) => receipts?.take(r, from),
  onRoute: (body, dst, reply) => onRoute(body, reply),
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
log(`program files: ${Object.entries(FILES).map(([f, h]) => f + ' ' + h).join(', ')} (in ${DIR})`);
log(`OpenTube ${core.VERSION} (build ${BUILD}) · network ${engine.MAIN_ID.slice(0, 16)}… · main height ${engine.main.best.height} · ${engine.directory.size} registered · ${engine.chains.size} chains loaded`);
setInterval(() => engine.gossip(), 60000);   // repair pass; new blocks are pushed right away
// The public feed (core: SQUARE): keep this month's chain from the start (older months open as peers mention them).
const openSquare = () => { for (const kind of ['square', 'mail']) { const id = engine.squareId(core.monthKey(Date.now()), kind); if (id && !engine.chains.has(id)) engine.requestChain(engine.openChain(id)); } };   // and this month's private messages
if (!MAIN_ONLY) { openSquare(); setInterval(openSquare, 3600000); }

// ---- OBS "Start Streaming" (RTMP_PORT): OBS → rtmp://localhost/live → this node → the live hub. The owner first clicks
// Go live → Stream from OBS on the site and picks this computer; that live block names this node's key as `relay`.
// ---- the segment tree on this node (see dcSend in the core): WebRTC data channels to viewers, who pass the segments
// on to others. Needs webrtc-polyfill (installed with the peer mesh); without it the site's hub feeds the viewers.
let RTCPC = null;
const liveStats = new Map();   // stream -> the hub's latest viewer counts
const rtcReady = import('webrtc-polyfill').then(m => { RTCPC = m.RTCPeerConnection; }).catch(() => {});
function liveKids(stream, sendWs) {
  const kids = new Map(); let seg0 = null; const recent = [];
  const cand = (c) => ({ candidate: c.candidate, sdpMid: c.sdpMid, sdpMLineIndex: c.sdpMLineIndex });
  return {
    child(peer) {
      if (!RTCPC) return;
      kids.get(peer)?.pc.close();
      const pc = new RTCPC({ iceServers: [{ urls: 'stun:stun.l.google.com:19302' }] }), ch = pc.createDataChannel('live', { ordered: true });
      const kid = { pc, ch, waiting: [] }; kids.set(peer, kid);
      ch.onopen = () => { if (seg0) core.dcSend(ch, seg0); for (const g of recent) if (g.n !== 0) core.dcSend(ch, g); };
      pc.onicecandidate = (e) => { if (e.candidate) sendWs({ t: 'signal', stream, to: peer, data: { c: cand(e.candidate) } }); };
      pc.onconnectionstatechange = () => { if (['failed', 'closed'].includes(pc.connectionState) && kids.get(peer) === kid) kids.delete(peer); };
      (async () => { await pc.setLocalDescription(await pc.createOffer()); sendWs({ t: 'signal', stream, to: peer, data: { sdp: { type: pc.localDescription.type, sdp: pc.localDescription.sdp } } }); })().catch(e => log('[live] offer failed:', e.message));
    },
    unchild(peer) { kids.get(peer)?.pc.close(); kids.delete(peer); },
    async signal(peer, data) {
      const k = kids.get(peer); if (!k || !data) return;
      try {
        if (data.sdp) { await k.pc.setRemoteDescription(data.sdp); for (const c of k.waiting.splice(0)) await k.pc.addIceCandidate(c).catch(() => {}); }
        else if (data.c) { if (k.pc.remoteDescription) await k.pc.addIceCandidate(data.c).catch(() => {}); else k.waiting.push(data.c); }
      } catch (e) { log('[live] signal:', e.message); }
    },
    seg(g) { if (g.n === 0) seg0 = g; recent.push(g); if (recent.length > 8) recent.shift(); for (const { ch } of kids.values()) core.dcSend(ch, g); },
    close() { for (const k of kids.values()) try { k.pc.close(); } catch {} kids.clear(); },
    get size() { return kids.size; }
  };
}
// Live chunks: every ~10 consecutive segments become one small torrent this node seeds (with hosting on), announced to
// the hub with the list of segments inside. Viewers who missed segments (members saving the stream) fetch them over
// BitTorrent from everyone who has the chunk, and check each segment's signature as usual.
function chunker(stream, announce) {
  let cur = [], busy = Promise.resolve(), announced = 0;
  const flush = () => {
    const segs = cur; cur = [];
    if (!segs.length || !globalThis.openTubeHost?.seedChunk) return busy;
    return (busy = busy.then(async () => {
      const buf = Buffer.concat(segs.map(g => Buffer.from(g.d, 'base64'))), from = segs[0].n, to = segs.at(-1).n;
      const ih = await globalThis.openTubeHost.seedChunk(buf, `live-${stream.slice(0, 8)}-${from}-${to}.bin`);
      const c = { stream, from, to, o: segs[0].o, len: buf.length, ih, by: signer.pub, segs: segs.map(g => ({ n: g.n, o: g.o, h: g.h, sig: g.sig, ...(g.k ? { k: 1 } : {}) })) };
      c.sig = await signer.sign(core.liveChunkPreimage(c));
      announce(c);
      if (!announced++) log(`[live] sharing the stream over BitTorrent too, in chunks of ~10 segments (first: ${ih})`);
    }).catch(e => log('[live] chunk:', e.message)));
  };
  return {
    seg(g) { if (!g?.d || cur.length && g.n !== cur.at(-1).n + 1) flush(); if (g?.d) cur.push(g); if (cur.length >= 10) flush(); },
    end: () => flush()
  };
}
// The site's live hub over a WebSocket, as this node's key: for broadcasting (OBS) or relaying (watch).
// An entry node (reachable from the internet: PUBLIC_URL, or ENTRY=1) is a tracker, a live hub and a way into the
// network for browsers, so the site itself only needs to serve the page. Its own streams use its own hub.
const isEntry = () => !!process.env.PUBLIC_URL || process.env.ENTRY === '1';   // --public sets PUBLIC_URL once HTTPS is up
const ownHubUrl = () => `ws://127.0.0.1:${PORT}/live`;
// Meet places: where browsers and nodes find each other for video (BitTorrent trackers). Every node's own tracker, and
// every entry node's (from signed node statuses and seeds.json), so files never depend on the site being up.
function meetPlaces() {
  const out = [`ws://127.0.0.1:${PORT}/tracker`], own = (process.env.PUBLIC_URL || '').replace(/\/+$/, '');
  const add = (u) => { u = String(u || '').replace(/\/+$/, ''); if (/^https:\/\//.test(u) && u !== own) out.push(u.replace(/^http/, 'ws') + '/tracker'); };
  for (const n of engine.nodes.values()) if (n.node !== signer.pub && n.roles?.includes('entry') && Date.now() - n.at < 30 * 60000) add((n.addrs || []).find(a => /^https:/.test(a)));
  try { for (const e of JSON.parse(fs.readFileSync(path.join(DIR, 'seeds.json'), 'utf8')).entry || []) add(typeof e === 'string' ? e : e?.url); } catch {}
  return [...new Set(out)].slice(0, 16);
}
const siteHubUrl = () => process.env.NODE_SITE ? process.env.NODE_SITE.replace(/^http/, 'ws') + '/live' : null;   // the site's: fallback only
// public entry nodes this computer knows (signed statuses, newest first): { node, url (its /live) }
function entryHubs() {
  return [...engine.nodes.values()].filter(n => n.node !== signer.pub && n.roles?.includes('entry') && Date.now() - n.at < 10 * 60000)
    .sort((a, b) => b.at - a.at).map(n => { const a = (n.addrs || []).find(x => /^https:\/\//.test(x)); return a ? { node: n.node, url: a.replace(/^http/, 'ws').replace(/\/+$/, '') + '/live' } : null; }).filter(Boolean);
}
// the hub this computer's own streams go on: its own (it's an entry node); else a public entry node; the site's last
function pickNodeHub() {
  if (isEntry()) return { node: signer.pub, url: ownHubUrl() };
  const e = entryHubs()[0]; if (e) return e;
  return siteHubUrl() ? { node: null, url: siteHubUrl() } : { node: signer.pub, url: ownHubUrl() };
}
const defaultHubUrl = () => pickNodeHub().url;
function hubUrlOfNode(hub) {   // a node's live hub: ours, or its public (else any) address
  if (hub === signer.pub) return ownHubUrl();
  const n = engine.nodes.get(hub), a = (n?.addrs || []).find(x => /^https:/.test(x)) || (n?.addrs || []).find(x => !/localhost|127\./.test(x)) || n?.addrs?.[0];
  return a ? a.replace(/^http/, 'ws').replace(/\/+$/, '') + '/live' : defaultHubUrl();
}
function hubUrlFor(chain, stream) {   // the hub a stream lives on: named in its live block (its `hub` node), else the site's
  const lb = engine.chains.has(chain) && engine.derive(chain)?.streams?.find(b => b.payload.stream === stream), hub = lb?.payload?.hub;
  if (!hub) return siteHubUrl() || entryHubs()[0]?.url || ownHubUrl();   // older streams name no hub: they're on the site's
  if (hub === signer.pub) return ownHubUrl();
  const n = engine.nodes.get(hub), a = (n?.addrs || []).find(x => /^https:/.test(x)) || (n?.addrs || []).find(x => !/localhost|127\./.test(x)) || n?.addrs?.[0];
  return a ? a.replace(/^http/, 'ws').replace(/\/+$/, '') + '/live' : defaultHubUrl();
}
function hubSocket(onMsg, onOpen, url = defaultHubUrl()) {
  let ws = null, ready = false, closed = false;
  const connect = () => {
    ready = false; ws = new WebSocket(url);
    ws.onmessage = async (e) => {
      let m; try { m = JSON.parse(e.data); } catch { return; }
      if (m.t === 'hi') { await rtcReady; ws.send(JSON.stringify({ t: 'auth', key: signer.pub, sig: await signer.sign(core.liveAuthPreimage(m.nonce)) })); ready = true; onOpen(); }
      else onMsg(m);
    };
    ws.onclose = () => { ready = false; if (!closed) setTimeout(connect, 2000); };
    ws.onerror = () => {};
  };
  connect();
  return { send: (m) => { if (ready) try { ws.send(JSON.stringify(m)); } catch {} }, get ready() { return ready; }, close: () => { closed = true; try { ws.close(); } catch {} } };
}
// Relaying: when this node's owner (or someone sharing it) streams from a browser, or a channel they joined is live,
// this node watches the stream as a member (it gets every segment from the hub) and serves many viewers over WebRTC
// data channels, so neither the streamer's browser nor the hub has to.
const relaying = new Map();   // stream -> { hub, kids }
const relayAsked = new Map();  // stream -> { chain, at }: the owner's browser asked us to relay it (they went live)
function liveRelays() {
  if (!GRANT || process.env.LIVE_RELAY === '0') return;
  const people = [GRANT.user, ...Object.keys(obsStore.users)];
  const chans = new Set(people.flatMap(u => { const m = engine.memberships.get(u); return [...(m?.own || []), ...(m?.joined || [])]; }));
  const want = new Map();
  for (const c of chans) { const d = engine.chains.has(c) && engine.derive(c); if (!d?.liveNow) continue; for (const lb of d.liveNow) if (lb.author !== signer.pub && lb.payload.relay !== signer.pub) want.set(lb.payload.stream, c); }
  const fromBrowser = (stream, chain) => { const d = engine.chains.has(chain) && engine.derive(chain), lb = d?.streams?.find(b => b.payload.stream === stream); return !lb || lb.author === d.owner && !lb.payload.relay; };   // a computer streaming makes its own chunks
  for (const [stream, x] of relayAsked) { if (Date.now() - x.at > 24 * 3600000 || engine.chains.has(x.chain) && engine.derive(x.chain)?.liveEnds?.has(stream)) relayAsked.delete(stream); else want.set(stream, x.chain); }
  for (const [stream, r] of relaying) if (!want.has(stream)) { r.close(); relaying.delete(stream); }
  for (const [stream, chain] of want) {
    if (relaying.has(stream) || relaying.size >= 6) continue;
    const kids = liveKids(stream, (m) => hub.send(m));
    const chunks = fromBrowser(stream, chain) ? chunker(stream, (c) => hub.send({ t: 'lchunk', stream, chunk: c })) : null;
    const ask = relayAsked.get(stream), rec = ask?.record ? relayRecorder(chain, stream, ask.title) : null;   // their browser has no folder: we keep the recording
    let got = 0;
    const hub = hubSocket((m) => {
      if (m.t === 'seg' && m.seg?.stream === stream) { kids.seg(m.seg); chunks?.seg(m.seg); rec?.seg(m.seg); if (!got++) log(`[live] relaying “${engine.derive(chain)?.streams.find(b => b.payload.stream === stream)?.payload.title || 'a stream'}” on ${engine.derive(chain)?.profile.name || chain.slice(0, 8)} to viewers over WebRTC`); }
      else if (m.t === 'child' && m.dc) kids.child(m.peer);
      else if (m.t === 'unchild') kids.unchild(m.peer);
      else if (m.t === 'signal') kids.signal(m.from, m.data);
      else if (m.t === 'ended' || m.t === 'nostream') { r.close(); relaying.delete(stream); }
      else if (m.t === 'viewers') liveStats.set(stream, m);
    }, () => { kids.close(); if (RTCPC) hub.send({ t: 'watch', stream, segOnly: true, media: false, dcSlots: 16, slots: 0, relay: true }); }, hubUrlFor(chain, stream));
    const alive = setInterval(() => hub.send({ t: 'alive', stream }), 15000);
    const r = { close: () => { clearInterval(alive); rec?.finish(); (chunks?.end() || Promise.resolve()).then(() => { hub.send({ t: 'leave', stream }); kids.close(); hub.close(); }); } };
    relaying.set(stream, r);
  }
}
setInterval(liveRelays, 10000);
// ---- updating itself: every 10 minutes, compare our program files with the site's. When the site has a new version and
// this computer isn't busy (receiving OBS, a group stream, relaying or recording someone's stream), restart with it: the
// one-command launcher downloads the site's current code on restart (exit code 75). Busy: try again later.
let updateSeen = null;
const busyWith = () => {
  if (globalThis.openTubeRtmp?.status?.().live) return 'receiving OBS';
  if (Object.values(obsStore.profiles).some(p => p._group?.enc || [...(p._group?.inputs?.values() || [])].some(x => !x.ended))) return 'a group stream';
  if (relaying.size) return 'relaying a live stream';
  return null;
};
async function checkForUpdate() {
  if (!process.env.OPENTUBE_LAUNCHER || !process.env.NODE_SITE || process.env.AUTO_UPDATE === '0') return;
  try {
    const differ = [];
    for (const f of [...Object.keys(FILES), 'index.html']) {
      const r = await fetch(new URL(f, process.env.NODE_SITE + '/') + '?v=' + Date.now(), { signal: AbortSignal.timeout(15000) }); if (!r.ok) continue;
      const h = crypto.createHash('sha256').update(Buffer.from(await r.arrayBuffer())).digest('hex').slice(0, 12);
      if (h !== (f === 'index.html' ? BUILD : FILES[f])) differ.push(f);
    }
    if (!differ.length) return;
    const busy = busyWith();
    if (busy) { if (updateSeen !== differ.join()) log(`[update] a new OpenTube is on the site (${differ.join(', ')}); updating once this computer is idle (now: ${busy})`); updateSeen = differ.join(); return; }
    log(`[update] a new OpenTube is on the site (${differ.join(', ')}): restarting to update`);
    setTimeout(() => process.exit(75), 1000);
  } catch (e) { log('[update] check failed:', e.message); }
}
setTimeout(checkForUpdate, 2 * 60000); setInterval(checkForUpdate, 10 * 60000);
// ---- flash computers: this computer starts a cloud server for a stream (it does the heavy work: combining cameras,
// serving viewers), keeps a copy of everything it records, and deletes it once it's idle and every recording is copied
// here (or at the time limit). The provider's API key stays on this computer: it's set with the launcher
// (--flash-provider=… --flash-token=…) and never sent over the network (control messages are signed, not encrypted).
const flashFile = path.join(DATA, 'flash.json');
const flash = Object.assign({ provider: null, token: null, region: null, plan: null, image: null, maxHours: 6, idleMin: 15, createCmd: null, deleteCmd: null, children: [] },
  (() => { try { return JSON.parse(fs.readFileSync(flashFile, 'utf8')); } catch { return {}; } })());
for (const [k, e] of [['provider', 'FLASH_PROVIDER'], ['token', 'FLASH_TOKEN'], ['region', 'FLASH_REGION'], ['plan', 'FLASH_PLAN'], ['image', 'FLASH_IMAGE'], ['createCmd', 'FLASH_CREATE_CMD'], ['deleteCmd', 'FLASH_DELETE_CMD']]) if (process.env[e]) flash[k] = process.env[e];
if (process.env.FLASH_MAX_HOURS) flash.maxHours = Number(process.env.FLASH_MAX_HOURS) || 6;
if (process.env.FLASH_IDLE_MIN) flash.idleMin = Number(process.env.FLASH_IDLE_MIN) || 15;   // (tests use a short one)
const saveFlash = () => { try { fs.writeFileSync(flashFile, JSON.stringify(flash, null, 2), { mode: 0o600 }); } catch {} };
if (flash.provider) saveFlash();
const FLASH_PROVIDERS = {
  // each: defaults (region, plan, image), create(name, userData) -> { id, ip }, remove(id)
  hetzner: { defaults: { region: 'ash', plan: 'cpx21', image: 'ubuntu-24.04' }, api: 'https://api.hetzner.cloud/v1',
    async create(name, ud) { const r = await flashApi('POST', '/servers', { name, server_type: flashOpt('plan'), image: flashOpt('image'), location: flashOpt('region'), user_data: ud }); return { id: String(r.server.id), ip: r.server.public_net?.ipv4?.ip || null }; },
    async remove(id) { await flashApi('DELETE', `/servers/${id}`); } },
  digitalocean: { defaults: { region: 'nyc3', plan: 's-2vcpu-4gb', image: 'ubuntu-24-04-x64' }, api: 'https://api.digitalocean.com/v2',
    async create(name, ud) { const r = await flashApi('POST', '/droplets', { name, region: flashOpt('region'), size: flashOpt('plan'), image: flashOpt('image'), user_data: ud }); return { id: String(r.droplet.id), ip: null }; },
    async remove(id) { await flashApi('DELETE', `/droplets/${id}`); } },
  vultr: { defaults: { region: 'ewr', plan: 'vhf-2c-4gb', image: '2284' }, api: 'https://api.vultr.com/v2',   // image: Vultr's os_id (2284 = Ubuntu 24.04)
    async create(name, ud) { const r = await flashApi('POST', '/instances', { region: flashOpt('region'), plan: flashOpt('plan'), os_id: Number(flashOpt('image')), label: name, hostname: name, user_data: Buffer.from(ud).toString('base64') }); return { id: String(r.instance.id), ip: r.instance.main_ip || null }; },
    async remove(id) { await flashApi('DELETE', `/instances/${id}`); } },
  // Amazon EC2 (token: ACCESS_KEY_ID:SECRET_ACCESS_KEY; region e.g. us-east-1; plan an instance type). The image is
  // Canonical's current Ubuntu 24.04, looked up in the region (SSM public parameter) unless --flash-image names an AMI.
  aws: { defaults: { region: 'us-east-1', plan: 'c7a.large', image: '' },
    async create(name, ud) {
      const region = flashOpt('region');
      let ami = flashOpt('image');
      if (!ami) { const r = await awsCall('ssm', region, 'POST', '/', JSON.stringify({ Name: '/aws/service/canonical/ubuntu/server/24.04/stable/current/amd64/hvm/ebs-gp3/ami-id' }), { 'content-type': 'application/x-amz-json-1.1', 'x-amz-target': 'AmazonSSM.GetParameter' }); ami = JSON.parse(r).Parameter.Value; }
      const q = new URLSearchParams({ Action: 'RunInstances', Version: '2016-11-15', ImageId: ami, InstanceType: flashOpt('plan'), MinCount: '1', MaxCount: '1', UserData: Buffer.from(ud).toString('base64'),
        'InstanceInitiatedShutdownBehavior': 'terminate', 'TagSpecification.1.ResourceType': 'instance', 'TagSpecification.1.Tag.1.Key': 'Name', 'TagSpecification.1.Tag.1.Value': name });
      const xml = await awsCall('ec2', region, 'POST', '/', q.toString(), { 'content-type': 'application/x-www-form-urlencoded; charset=utf-8' });
      const id = /<instanceId>([^<]+)<\/instanceId>/.exec(xml)?.[1]; if (!id) throw new Error('EC2 didn’t return an instance: ' + xml.slice(0, 200));
      return { id, ip: null };
    },
    async remove(id) { await awsCall('ec2', flashOpt('region'), 'POST', '/', new URLSearchParams({ Action: 'TerminateInstances', Version: '2016-11-15', 'InstanceId.1': id }).toString(), { 'content-type': 'application/x-www-form-urlencoded; charset=utf-8' }); } },
  // Google Compute Engine (token: a service account's JSON key, or a path to it; region a zone, e.g. us-central1-a)
  gcp: { defaults: { region: 'us-central1-a', plan: 'e2-standard-2', image: 'projects/ubuntu-os-cloud/global/images/family/ubuntu-2404-lts-amd64' },
    async create(name, ud) {
      const { project, bearer } = await gcpAuth(), zone = flashOpt('region');
      const body = { name, machineType: `zones/${zone}/machineTypes/${flashOpt('plan')}`,
        disks: [{ boot: true, autoDelete: true, initializeParams: { sourceImage: flashOpt('image'), diskSizeGb: '30' } }],
        networkInterfaces: [{ network: 'global/networks/default', accessConfigs: [{ type: 'ONE_TO_ONE_NAT', name: 'External NAT' }] }],
        metadata: { items: [{ key: 'startup-script', value: ud }] }, labels: { opentube: 'flash' } };
      await flashFetch('POST', `https://compute.googleapis.com/compute/v1/projects/${project}/zones/${zone}/instances`, body, { Authorization: bearer });
      return { id: `${zone}/${name}`, ip: null };
    },
    async remove(id) { const { project, bearer } = await gcpAuth(), [zone, name] = id.split('/'); await flashFetch('DELETE', `https://compute.googleapis.com/compute/v1/projects/${project}/zones/${zone}/instances/${name}`, null, { Authorization: bearer }); } },
  // Akamai / Linode (token: a personal access token; metadata user data needs a region with Metadata support)
  linode: { defaults: { region: 'us-ord', plan: 'g6-standard-2', image: 'linode/ubuntu24.04' },
    async create(name, ud) {
      const r = await flashFetch('POST', 'https://api.linode.com/v4/linode/instances', { label: name, region: flashOpt('region'), type: flashOpt('plan'), image: flashOpt('image'),
        root_pass: crypto.randomBytes(24).toString('base64url') + 'A1!', metadata: { user_data: Buffer.from(ud).toString('base64') }, tags: ['opentube-flash'] }, { Authorization: 'Bearer ' + flash.token });
      return { id: String(r.id), ip: r.ipv4?.[0] || null };
    },
    async remove(id) { await flashFetch('DELETE', `https://api.linode.com/v4/linode/instances/${id}`, null, { Authorization: 'Bearer ' + flash.token }); } },
  // Scaleway (token: PROJECT_ID:SECRET_KEY; region a zone, e.g. fr-par-1)
  scaleway: { defaults: { region: 'fr-par-1', plan: 'PLAY2-MICRO', image: 'ubuntu_noble' },
    async create(name, ud) {
      const [project, key] = String(flash.token).split(':'), zone = flashOpt('region'), base = `https://api.scaleway.com/instance/v1/zones/${zone}`, hd = { 'X-Auth-Token': key };
      const r = await flashFetch('POST', `${base}/servers`, { name, project, commercial_type: flashOpt('plan'), image: flashOpt('image'), dynamic_ip_required: true, tags: ['opentube-flash'] }, hd);
      const id = r.server.id;
      await flashFetch('PATCH', `${base}/servers/${id}/user_data/cloud-init`, ud, { ...hd, 'content-type': 'text/plain' });
      await flashFetch('POST', `${base}/servers/${id}/action`, { action: 'poweron' }, hd);
      return { id, ip: null };
    },
    async remove(id) { const [, key] = String(flash.token).split(':'); await flashFetch('POST', `https://api.scaleway.com/instance/v1/zones/${flashOpt('region')}/servers/${id}/action`, { action: 'terminate' }, { 'X-Auth-Token': key }); } },
  // any other provider (Azure, Oracle, OVH, Cloudzy, …): your own commands, e.g. its CLI. create gets OT_NAME and
  // OT_USERDATA_FILE (the setup script) and prints
  // {"id": "...", "ip": "..."}; delete gets OT_ID.
  custom: { defaults: {},
    async create(name, ud) { const f = path.join(DATA, `flash-${name}.sh`); fs.writeFileSync(f, ud, { mode: 0o600 }); const out = await flashRun(flash.createCmd, { OT_NAME: name, OT_USERDATA_FILE: f }); const j = JSON.parse(out.trim().split('\n').pop()); return { id: String(j.id), ip: j.ip || null }; },
    async remove(id) { await flashRun(flash.deleteCmd, { OT_ID: String(id) }); } },
  // testing on one machine: the flash computer is another OpenTube process here
  local: { defaults: {},
    async create(name, ud, tag) {
      const port = 20000 + Math.floor(Math.random() * 20000), dir = path.join(DATA, 'flash-local', tag);
      const p = spawn(process.execPath, [path.join(DIR, 'server.mjs')], { stdio: ['ignore', 'pipe', 'pipe'], env: { ...process.env, PORT: String(port), DATA_DIR: path.join(dir, 'data'), VIDEO_DIR: path.join(dir, 'videos'),
        PEERS: `ws://127.0.0.1:${PORT}/ot`, MINE: 'own', RANKER: '0', MESH: '0', NODE_GRANT: process.env.NODE_GRANT, NODE_SITE: process.env.NODE_SITE || '', NODE_ROLES: 'node,obs,host', NODE_LABEL: name,
        RTMP_PORT: String(port + 1), HOST_USER: GRANT.user, ENTRY: '1', FLASH_PARENT: signer.pub, FLASH_TAG: tag, FLASH_PROVIDER: '', FLASH_TOKEN: '', LIVE_DEBUG: process.env.LIVE_DEBUG || '' } });
      p.stdout.on('data', d => { for (const l of String(d).split('\n')) if (l.trim() && process.env.FLASH_DEBUG) log(`[flash ${tag}]`, l.trim().slice(0, 200)); });
      p.stderr.on('data', () => {});
      (flashLocal ||= new Map()).set(tag, p); return { id: tag, ip: '127.0.0.1' };
    },
    async remove(id) { flashLocal?.get(id)?.kill('SIGTERM'); flashLocal?.delete(id); } }
};
var flashLocal = null;
// any provider's JSON (or text) API
async function flashFetch(method, url, body, headers = {}) {
  const isText = typeof body === 'string';
  const r = await fetch(url, { method, headers: { ...(body && !isText ? { 'content-type': 'application/json' } : {}), ...headers }, body: body == null ? undefined : isText ? body : JSON.stringify(body), signal: AbortSignal.timeout(30000) });
  const text = await r.text();
  if (!r.ok) throw new Error(`${new URL(url).host} answered ${r.status}: ${text.slice(0, 200)}`);
  try { return text ? JSON.parse(text) : {}; } catch { return text; }
}
// AWS Signature Version 4 (token: ACCESS_KEY_ID:SECRET_ACCESS_KEY)
async function awsCall(service, region, method, p, body, headers) {
  const [ak, sk] = String(flash.token || '').split(':'); if (!ak || !sk) throw new Error('the AWS token is ACCESS_KEY_ID:SECRET_ACCESS_KEY');
  const host = `${service}.${region}.amazonaws.com`, now = new Date(), amzDate = now.toISOString().replace(/[-:]/g, '').replace(/\.\d{3}/, ''), day = amzDate.slice(0, 8);
  const sha = (s) => crypto.createHash('sha256').update(s).digest('hex'), hmac = (k, s) => crypto.createHmac('sha256', k).update(s).digest();
  const hd = { host, 'x-amz-date': amzDate, ...Object.fromEntries(Object.entries(headers).map(([k, v]) => [k.toLowerCase(), v])) };
  const names = Object.keys(hd).sort(), signed = names.join(';');
  const canonical = [method, p, '', names.map(k => `${k}:${String(hd[k]).trim()}\n`).join(''), signed, sha(body)].join('\n');
  const scope = `${day}/${region}/${service}/aws4_request`;
  const toSign = ['AWS4-HMAC-SHA256', amzDate, scope, sha(canonical)].join('\n');
  const key = hmac(hmac(hmac(hmac('AWS4' + sk, day), region), service), 'aws4_request');
  const sig = crypto.createHmac('sha256', key).update(toSign).digest('hex');
  const r = await fetch(`https://${host}${p}`, { method, headers: { ...hd, Authorization: `AWS4-HMAC-SHA256 Credential=${ak}/${scope}, SignedHeaders=${signed}, Signature=${sig}` }, body, signal: AbortSignal.timeout(30000) });
  const text = await r.text(); if (!r.ok) throw new Error(`AWS ${service} answered ${r.status}: ${text.slice(0, 300)}`);
  return text;
}
// Google: a service account's JSON key (the token itself, or a path to the file) -> an OAuth access token (1 h)
let gcpTok = null;
async function gcpAuth() {
  if (gcpTok && gcpTok.exp > Date.now() + 60000) return gcpTok;
  let raw = String(flash.token || ''); if (!raw.trim().startsWith('{')) raw = fs.readFileSync(raw, 'utf8');
  const sa = JSON.parse(raw), now = Math.floor(Date.now() / 1000);
  const b64 = (o) => Buffer.from(JSON.stringify(o)).toString('base64url');
  const unsigned = b64({ alg: 'RS256', typ: 'JWT' }) + '.' + b64({ iss: sa.client_email, scope: 'https://www.googleapis.com/auth/compute', aud: 'https://oauth2.googleapis.com/token', iat: now, exp: now + 3600 });
  const jwt = unsigned + '.' + crypto.sign('RSA-SHA256', Buffer.from(unsigned), sa.private_key).toString('base64url');
  const r = await flashFetch('POST', 'https://oauth2.googleapis.com/token', `grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer&assertion=${jwt}`, { 'content-type': 'application/x-www-form-urlencoded' });
  gcpTok = { project: sa.project_id, bearer: 'Bearer ' + r.access_token, exp: Date.now() + (r.expires_in || 3600) * 1000 };
  return gcpTok;
}

const flashOpt = (k) => flash[k] || FLASH_PROVIDERS[flash.provider]?.defaults?.[k];
async function flashApi(method, p, body) {
  const pr = FLASH_PROVIDERS[flash.provider];
  const r = await fetch(pr.api + p, { method, headers: { Authorization: 'Bearer ' + flash.token, 'content-type': 'application/json' }, body: body ? JSON.stringify(body) : undefined, signal: AbortSignal.timeout(30000) });
  const text = await r.text();
  if (!r.ok) throw new Error(`${flash.provider} answered ${r.status}: ${text.slice(0, 200)}`);
  return text ? JSON.parse(text) : {};
}
function flashRun(cmd, env) {
  return new Promise((res, rej) => { if (!cmd) return rej(new Error('no command set (FLASH_CREATE_CMD / FLASH_DELETE_CMD)'));
    const p = spawn('sh', ['-c', cmd], { env: { ...process.env, ...env } }); let out = '', err = '';
    p.stdout.on('data', d => out += d); p.stderr.on('data', d => err += d);
    p.on('exit', c => c === 0 ? res(out) : rej(new Error(`command failed (${c}): ${err.slice(0, 200)}`))); });
}
// the setup script the new server runs once: Node.js and ffmpeg, then OpenTube as a service, as a flash computer of this one
function flashUserData(tag) {
  const site = process.env.NODE_SITE, grant = process.env.NODE_GRANT;
  const args = [`--site=${site}`, `--grant=${grant}`, '--tunnel', `--name=${tag}`, `--flash-parent=${signer.pub}`, `--flash-tag=${tag}`].join(' ');
  return `#!/bin/bash
export DEBIAN_FRONTEND=noninteractive HOME=/root
apt-get update -y && apt-get install -y curl ca-certificates ffmpeg
curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && apt-get install -y nodejs
mkdir -p /root/.opentube && curl -fsSL ${site}/opentube.mjs -o /root/.opentube/opentube.mjs
cat > /etc/systemd/system/opentube.service <<'UNIT'
[Unit]
Description=OpenTube (flash computer)
After=network-online.target
[Service]
Environment=HOME=/root
ExecStart=/usr/bin/node /root/.opentube/opentube.mjs ${args}
Restart=always
RestartSec=5
[Install]
WantedBy=multi-user.target
UNIT
systemctl daemon-reload && systemctl enable --now opentube
`;
}
async function flashStart() {
  const pr = FLASH_PROVIDERS[flash.provider]; if (!pr) throw new Error(`no provider set: start this computer with --flash-provider=${Object.keys(FLASH_PROVIDERS).filter(k => k !== 'local').join('|')} and --flash-token=…`);
  if (!GRANT) throw new Error('this computer has no grant');
  if (flash.children.filter(c => c.state !== 'deleted').length >= 3) throw new Error('3 flash computers are running already');
  const tag = 'flash-' + crypto.randomBytes(3).toString('hex');
  const r = await pr.create(tag, flashUserData(tag), tag);
  const ch = { tag, id: r.id, ip: r.ip, provider: flash.provider, plan: flashOpt('plan') || null, region: flashOpt('region') || null, createdAt: Date.now(), state: 'starting', node: null, idleSince: 0 };
  flash.children.push(ch); saveFlash(); log(`[flash] started ${tag} on ${flash.provider} (${ch.plan || 'default plan'})`);
  return ch;
}
async function flashDelete(ch, why) {
  if (ch.state === 'deleted' || ch.deleting) return; ch.deleting = true;
  try { await FLASH_PROVIDERS[ch.provider].remove(ch.id); ch.state = 'deleted'; ch.deletedAt = Date.now(); ch.why = why; log(`[flash] deleted ${ch.tag}: ${why}`); }
  catch (e) { ch.error = e.message; log(`[flash] couldn’t delete ${ch.tag} (${e.message}); trying again in a minute`); }
  finally { ch.deleting = false; saveFlash(); }
}
// what a flash computer recorded (recordings its liveend blocks offer), and whether this computer has each one complete
function flashRecordings(nodeKey) {
  const out = [];
  for (const id of engine.chains.keys()) { let d; try { d = engine.derive(id); } catch { continue; } for (const [, r] of d?.nodeRecs || []) if (r.by === nodeKey) out.push([id, r]); }
  return out;
}
function flashLoop() {
  if (!flash.children.length) return;
  const host = globalThis.openTubeHost;
  for (const ch of flash.children) {
    if (ch.state === 'deleted') continue;
    const n = [...engine.nodes.values()].find(x => x.info?.flash?.tag === ch.tag && x.info?.flash?.parent === signer.pub && x.grant?.user === GRANT?.user);
    if (n) { ch.node = n.node; ch.seenAt = n.at; if (ch.state === 'starting') { ch.state = 'running'; log(`[flash] ${ch.tag} is online`); } }
    const recs = ch.node ? flashRecordings(ch.node) : [];
    for (const [chain, r] of recs) host?.keepRecording?.(chain, r)?.catch?.(e => log('[flash] keeping a recording:', e.message));
    ch.recordings = recs.length; ch.copied = recs.filter(([, r]) => host?.hasComplete?.(r.infohash)).length;
    const liveOnIt = ch.node && [...engine.chains.keys()].some(id => { try { return engine.derive(id)?.liveNow?.some(b => b.author === ch.node); } catch { return false; } });
    const busy = !!(n?.info?.busy || liveOnIt), now = Date.now(), age = now - ch.createdAt;
    ch.busy = busy ? (n?.info?.busy || 'live') : null;
    if (busy || !n) ch.idleSince = 0; else ch.idleSince ||= now;
    let why = null;
    if (age > (flash.maxHours || 6) * 3600000) why = `its time limit (${flash.maxHours || 6} h)` + (ch.copied < ch.recordings ? ` — ${ch.recordings - ch.copied} recording(s) not copied yet!` : '');
    else if (!ch.node && age > 20 * 60000) why = 'it never came online';
    else if (ch.node && n && now - n.at > 10 * 60000 && age > 30 * 60000) why = 'it stopped answering';
    else if (ch.idleSince && now - ch.idleSince > (flash.idleMin || 15) * 60000 && ch.copied === ch.recordings && !ch.keep) why = `idle ${flash.idleMin || 15} min, every recording copied here`;
    if (why) flashDelete(ch, why);
  }
  saveFlash();
}
setInterval(flashLoop, 30000);
// Recording a stream we relay (someone without a video folder went live through this computer): every segment is
// written at its offset, so the file is the streamer's recording byte for byte. When it ends: shared over BitTorrent and
// offered on the channel (liveend with the recording), like an OBS recording.
function relayRecorder(chain, stream, title) {
  let fd = null, file = null, size = 0, done = false;
  const ready = (async () => { const p = await globalThis.openTubeHost.recordingPath(chain, title, '.webm'); file = p; fd = fs.openSync(p.full, 'w'); log(`[live] recording “${title}” to ${p.full}`); })().catch(e => log('[live] can’t record:', e.message));
  return {
    seg(g) { ready.then(() => { if (!fd || done || !g?.d) return; const b = Buffer.from(g.d, 'base64'); try { fs.writeSync(fd, b, 0, b.length, g.o); size = Math.max(size, g.o + b.length); } catch (e) { log('[live] recording:', e.message); } }); },
    finish() {
      if (done) return; done = true;
      ready.then(async () => {
        if (!fd) return; fs.closeSync(fd);
        if (size < 1000) return fs.rmSync(file.full, { force: true });
        const r = await globalThis.openTubeHost.seedRecording(chain, file.file, title, stream);
        await engine.append(chain, { type: 'liveend', stream, outcome: 'ended', rec: { infohash: r.infohash, file: r.file.slice(0, 200), size: r.size, mime: 'video/webm', thumb: await thumbOf(file.full) } });
        log(`[live] recording ready: the channel’s owner can publish it from the site`);
      }).catch(e => log('[live] recording failed:', e.message));
    }
  };
}
function liveSink(chain, stream, mime, hubUrl = defaultHubUrl()) {
  let n = 0, o = 0, queue = Promise.resolve(), ended = false;
  const seg = (bytes, k) => (queue = queue.then(async () => {
    const g = { stream, n: n++, o, h: crypto.createHash('sha256').update(bytes).digest('hex') };
    o += bytes.length;
    g.sig = await signer.sign(core.liveSegPreimage(g)); g.d = bytes.toString('base64'); if (k) g.k = 1;
    send(g); engine.liveMesh.publish(g);   // and over the mesh: viewers who want it get it without any hub
    if (process.env.LIVE_DEBUG && g.n % 5 === 0) log('[live] segment', g.n, bytes.length, 'bytes');
  }).catch(e => log('[obs] segment failed:', e.message)));
  let send, finish;
  if (hubUrl === ownHubUrl() && !isEntry() && !process.env.NODE_SITE) {   // this node is the site (self-hosted): feed its own hub
    const src = liveHub.local(stream, chain, mime), chunks = chunker(stream, (c) => src.chunk(c));
    send = (g) => { src.seg(g); chunks.seg(g); }; finish = () => chunks.end().then(() => src.end());
  } else {       // relay to the site's hub as the broadcaster, and feed the segment tree ourselves
    const pending = []; let on = false;
    const kids = liveKids(stream, (m) => hub.send(m));
    const chunks = chunker(stream, (c) => hub.send({ t: 'lchunk', stream, chunk: c }));
    const flush = () => { while (on && hub.ready && pending.length) hub.send({ t: 'seg', seg: pending.shift() }); };
    const hub = hubSocket((m) => {
      if (m.t === 'broadcasting') { on = true; refused = 0; flush(); log('[obs] streaming through ' + hubUrl.replace(/^wss?:\/\//, '').replace(/\/live$/, '') + (RTCPC ? ' (and to viewers directly, over WebRTC)' : '')); }
      else if (m.t === 'child' && m.dc) kids.child(m.peer);
      else if (m.t === 'unchild') kids.unchild(m.peer);
      else if (m.t === 'signal') kids.signal(m.from, m.data);
      else if (m.t === 'viewers') { liveStats.set(stream, m); liveStats.current = m; }
      else if (m.t === 'error') {   // usually: the site hasn't received our live block yet, so it doesn't know we may broadcast. Ask again.
        if (++refused === 1 || refused % 15 === 0) log('[obs] the hub refused the stream:', m.error, refused < 60 ? '(asking again)' : '');
        if (refused < 60 && !ended) setTimeout(() => hub.send(hello()), 2000);
      }
    }, () => { on = false; kids.close(); hub.send(hello()); }, hubUrl);
    let refused = 0;
    const hello = () => ({ t: 'broadcast', stream, chain, mime, rtc: false, dcSlots: RTCPC ? Number(process.env.LIVE_DC_SLOTS) || 8 : 0 });
    send = (g) => { pending.push(g); flush(); kids.seg(g); chunks.seg(g); };
    finish = () => { const done = () => { chunks.end().then(() => setTimeout(() => { hub.send({ t: 'leave', stream }); ended = true; kids.close(); setTimeout(() => hub.close(), 500); }, 500)); };
      const wait = () => (pending.length && Date.now() - t0 < 15000 ? setTimeout(wait, 200) : done()); const t0 = Date.now(); wait(); };
  }
  return { init: (bytes) => seg(bytes, false), fragment: (bytes, k) => seg(bytes, k), end: () => { queue.then(() => finish()); } };
}
// OBS settings for this computer (set from the owner's console): the stream key OBS must use, the channel it goes live
// on (the owner links this computer to the channel with a `relay` block), the title, YouTube forwarding, and tiles.
const obsFile = path.join(DATA, 'obs.json');
// One OBS profile per person who uses this computer (the owner, and people the owner shared it with): their own stream
// key, so the computer knows who is streaming and goes live on that person's channel.
const newStreamKey = () => 'ot-' + crypto.randomBytes(12).toString('base64url');
const obsStore = (() => { try { return JSON.parse(fs.readFileSync(obsFile, 'utf8')); } catch { return {}; } })();
if (!obsStore.profiles) { const { profiles, users, ...legacy } = obsStore; obsStore.profiles = legacy.key ? { [GRANT?.user || 'owner']: legacy } : {}; for (const k of Object.keys(legacy)) delete obsStore[k]; }
obsStore.users ||= {};   // user key -> { can: ['obs', 'host', 'manage'], name }
const saveObs = () => fs.writeFileSync(obsFile, JSON.stringify(obsStore, (k, v) => k.startsWith('_') || k === 'owner' ? undefined : v, 2), { mode: 0o600 });   // _tiles etc. are runtime only
function profileOf(user) {
  const p = obsStore.profiles[user] ||= {};
  Object.assign(p, { key: p.key || newStreamKey(), channel: p.channel ?? null, title: p.title || 'Live', youtube: p.youtube || { key: '', on: false }, tile: p.tile || { on: false, n: 4 } });
  return p;
}
const nodeUsers = () => obsStore.users;
// a channel someone may go live on from here: theirs (its key is theirs, or bound to their identity)
const ownsChannel = (user, ch) => { const d = ch && engine.derive(ch); return !!d && (d.owner === user || engine.ledger.resolve(d.owner) === engine.ledger.resolve(user)); };
const may = (user, what) => user === GRANT?.user || (!!obsStore.users[user]?.can?.includes(what) && !(obsStore.users[user].until < Date.now()));   // guests' access ends at `until`
setInterval(() => { let gone = false; for (const [k, u] of Object.entries(obsStore.users)) if (u.until && u.until < Date.now()) { delete obsStore.users[k]; delete obsStore.profiles[k]; gone = true; } if (gone) { saveObs(); announceSoon?.(); } }, 60000);
if (GRANT) profileOf(GRANT.user);
saveObs();
setTimeout(() => { for (const p of Object.values(obsStore.profiles)) if (p.channel) { const ch = engine.openChain(p.channel); if (ch) engine.requestChain(ch); } }, 3000);   // keep the linked channels loaded
// fMP4 splitting (ffmpeg output): ftyp+moov is the header, then moof+mdat pairs
function mp4Boxes(onInit, onFrag) {
  let buf = Buffer.alloc(0), init = null, moof = null;
  return (chunk) => {
    buf = Buffer.concat([buf, chunk]);
    for (;;) {
      if (buf.length < 8) return; const size = buf.readUInt32BE(0), type = buf.toString('latin1', 4, 8);
      if (size < 8 || buf.length < size) return;
      const box = buf.subarray(0, size); buf = buf.subarray(size);
      if (type === 'ftyp') init = Buffer.from(box); else if (type === 'moov') { onInit(Buffer.concat([init || Buffer.alloc(0), box])); init = null; }
      else if (type === 'moof') moof = Buffer.from(box); else if (type === 'mdat' && moof) { onFrag(Buffer.concat([moof, box])); moof = null; }
    }
  };
}
let pubIp = null;
async function publicIp() {
  if (pubIp !== null) return pubIp || null;
  if (/^\d{1,3}(\.\d{1,3}){3}$/.test(process.env.PUBLIC_IP || '')) return (pubIp = process.env.PUBLIC_IP);
  for (const u of ['https://api.ipify.org', 'https://ifconfig.me/ip', 'https://icanhazip.com']) { try { const t = (await (await fetch(u, { signal: AbortSignal.timeout(4000) })).text()).trim(); if (/^\d{1,3}(\.\d{1,3}){3}$/.test(t)) return (pubIp = t); } catch {} }
  pubIp = ''; return null;
}
function rtmpProbe(host, port) {   // TCP connect + RTMP C0/C1; an RTMP server answers with S0
  return new Promise(async (res) => {
    const net = await import('node:net'), t0 = Date.now();
    const sock = net.connect({ host, port, timeout: 6000 });
    const done = (r) => { try { sock.destroy(); } catch {} res({ ...r, ms: Date.now() - t0, host, port }); };
    sock.on('connect', () => { const c1 = Buffer.alloc(1537); c1[0] = 3; sock.write(c1); });
    sock.on('data', (d) => done(d[0] === 3 ? { ok: true } : { ok: false, error: 'something else answers on that port' }));
    sock.on('timeout', () => done({ ok: false, error: 'no answer: the port is blocked by a firewall, or the address isn’t this computer' }));
    sock.on('error', (e) => done({ ok: false, error: e.code === 'ECONNREFUSED' ? 'refused: nothing listens there (is OpenTube running with OBS on?)' : e.message }));
  });
}
const ffmpegOk = (() => { try { return !spawnSync('ffmpeg', ['-version']).error; } catch { return false; } })();
if (!ffmpegOk && process.env.RTMP_PORT) log('[obs] ffmpeg isn’t installed here, so YouTube forwarding and group streams (tiles) are off. Install it (Linux: sudo apt install ffmpeg) and restart OpenTube.');
function youtubeOut(log, obsCfg) {   // forward the stream (fMP4, H.264 + AAC) to YouTube, unchanged
  if (!obsCfg.youtube.on || !obsCfg.youtube.key || !ffmpegOk) return null;
  const ff = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-f', 'mp4', '-i', 'pipe:0', '-c', 'copy', '-f', 'flv', 'rtmp://a.rtmp.youtube.com/live2/' + obsCfg.youtube.key], { stdio: ['pipe', 'ignore', 'pipe'] });
  ff.stderr.on('data', d => log('[youtube]', String(d).trim().slice(0, 200))); ff.stdin.on('error', () => {});
  log('[youtube] forwarding the stream to YouTube');
  return { write: (b) => { try { ff.stdin.write(b); } catch {} }, end: () => { try { ff.stdin.end(); } catch {} } };
}
// Go live on the linked channel: writes the live block (as the channel's relay), then feeds the mesh/hub and YouTube.
// The recording: OBS's stream (fragmented MP4) written as it arrives into the channel's folder in the video folder,
// shared over BitTorrent when it ends and announced in the liveend block, so the owner can publish it as a video.
async function thumbOf(file) {   // a small JPEG from 2 s in (needs ffmpeg), as a data URL; '' without
  if (!ffmpegOk) return '';
  return new Promise((res) => {
    const ff = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-ss', '2', '-i', file, '-frames:v', '1', '-vf', 'scale=320:-2', '-q:v', '6', '-f', 'image2', '-c:v', 'mjpeg', 'pipe:1'], { stdio: ['ignore', 'pipe', 'ignore'] });
    const parts = []; ff.stdout.on('data', d => parts.push(d));
    ff.on('exit', () => { const b = Buffer.concat(parts); res(b.length && b.length < 44000 ? 'data:image/jpeg;base64,' + b.toString('base64') : ''); });
    ff.on('error', () => res(''));
  });
}
function recorder(chain, title, log) {
  // parts: a group stream restarts ffmpeg when people join or leave; each run is one part, joined into one file at the end
  let out = null, file = null, bytes = 0, open, parts = [];
  const partPath = (i) => path.join(path.dirname(file.full), `.${path.basename(file.full)}.part${i}.mp4`);
  open = (async () => {
    const host = globalThis.openTubeHost;
    const p = host?.recordingPath ? await host.recordingPath(chain, title) : (() => { const dir = path.join(DATA, 'recordings'); fs.mkdirSync(dir, { recursive: true }); const name = `${title.replace(/[^\w .()-]/g, ' ').trim() || 'Live'} ${new Date().toISOString().slice(0, 16).replace(/[:T]/g, '-')}.mp4`; return { full: path.join(dir, name), file: name }; })();
    file = p; parts.push(p.full); out = fs.createWriteStream(p.full); out.on('error', e => log('[obs] recording:', e.message));
    log(`[obs] recording to ${p.full}`);
  })().catch(e => log('[obs] can’t record:', e.message));
  return {
    write: (b) => { open.then(() => { if (out) { out.write(b); bytes += b.length; } }); },
    nextPart() { open = open.then(() => new Promise(r => { if (!out) return r(); out.end(() => { const p = partPath(parts.length); parts.push(p); out = fs.createWriteStream(p); r(); }); })); },
    async finish(stream) {
      await open; if (!out) return null;
      await new Promise(r => out.end(r));
      if (bytes < 1000) { for (const p of parts) fs.rmSync(p, { force: true }); return null; }
      if (parts.length > 1 && ffmpegOk) {   // join the parts (same size and codecs: no re-encoding)
        const first = partPath(0); fs.renameSync(parts[0], first); parts[0] = first;
        const list = file.full + '.parts.txt'; fs.writeFileSync(list, parts.map(p => `file '${p.replace(/'/g, "'\\''")}'`).join('\n'));
        await new Promise(r => { const ff = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-f', 'concat', '-safe', '0', '-i', list, '-c', 'copy', '-movflags', '+faststart', '-y', file.full]); ff.on('exit', r); ff.on('error', r); });
        for (const p of [...parts, list]) fs.rmSync(p, { force: true });
      }
      const host = globalThis.openTubeHost;
      if (!host?.seedRecording) { log(`[obs] recording saved to ${file.full} (video hosting is off on this computer, so it isn’t shared for publishing)`); return null; }
      const r = await host.seedRecording(chain, file.file, title, stream);
      return { ...r, mime: 'video/mp4', thumb: await thumbOf(file.full) };
    }
  };
}
function goLiveSink(log, obsCfg) {
  let real = null, yt = youtubeOut(log, obsCfg), queued = [], stream = null, chain = null, rec = null, hubPick = null, outBlock = null;
  return {
    get block() { return outBlock; },   // the live block's hash, once it's on the channel
    async init(bytes, mime) {
      const armedOld = obsCfg.owner ? armed?.() : null;   // the older "Go live → pick this computer" flow (owner only)
      if (armedOld) { chain = armedOld.chain; stream = armedOld.b.payload.stream; mime = armedOld.b.payload.mime; }
      else {
        chain = obsCfg.channel; if (!chain || !engine.chains.get(chain)) throw new Error('no channel linked to this computer');
        stream = crypto.randomBytes(32).toString('hex');
        hubPick = pickNodeHub();   // viewers find the stream on the hub its live block names
        const title = obsCfg._absorbedTitle || obsCfg.title || 'Live'; delete obsCfg._absorbedTitle;   // the host's ongoing stream joined: keep its title
        outBlock = (await engine.append(chain, { type: 'live', title: title.slice(0, 140), stream, mime, ...(hubPick.node ? { hub: hubPick.node } : {}) }))?.hash || null; myLive.add(stream);
        log(`[obs] live on ${engine.derive(chain)?.profile.name || chain.slice(0, 8)}: “${obsCfg.title}”`);
      }
      real = liveSink(chain, stream, mime, hubPick?.url || hubUrlFor(chain, stream)); real.init(bytes); yt?.write(bytes);
      if (!armedOld) { rec = recorder(chain, (obsCfg.title || 'Live').slice(0, 100), log); rec.write(bytes); }   // the older flow records in the owner's browser
      for (const [b, k] of queued) { real.fragment(b, k); yt?.write(b); rec?.write(b); } queued = [];
    },
    fragment(bytes, k) { if (real) { real.fragment(bytes, k); yt?.write(bytes); rec?.write(bytes); } else queued.push([bytes, k]); },
    reinit(bytes) {   // the group picture restarted (someone joined or left): same stream, new start
      if (!real) return queued.push([bytes, true]);
      real.fragment(bytes, true); rec?.nextPart(); rec?.write(bytes);
      if (yt) { yt.end(); yt = youtubeOut(log, obsCfg); yt?.write(bytes); }
    },
    end() {
      real?.end(); yt?.end(); log('[obs] stream ended'); setTimeout(() => myLive.delete(stream), 120000);
      if (!stream || armed?.()) return;
      (async () => {
        const r = await rec?.finish(stream).catch(e => { log('[obs] recording failed:', e.message); return null; });
        const p = { type: 'liveend', stream, outcome: 'ended' };
        const st = core.liveStatsOf(liveStats.get(stream) || liveStats.current); if (st && core.liveStatsOk(st)) p.stats = st;   // public statistics
        if (r) p.rec = { infohash: r.infohash, file: r.file.slice(0, 200), size: r.size, mime: r.mime, thumb: r.thumb };
        await engine.append(chain, p);
        if (r) log(`[obs] recording ready: the channel’s owner can publish it from the site (Go live, or the channel’s Videos)`);
      })().catch(e => log('[obs] liveend:', e.message));
    }
  };
}
// The group stream: everyone streaming into one person's group (their own OBS keys, guests they invited, and browser
// streams sent into it) combined into one 1280×720 picture, laid out by how many are connected (1 full, 2 side by
// side, 3–4 in 2×2, 5–9 in 3×3), audio mixed.
// Nothing restarts when people join or leave: each input has its own decoder (ffmpeg → raw frames at its tile size,
// and raw audio), this computer lays the picture out 30 times a second from each input's latest frame (a late input
// just shows its last frame), and one encoder runs for the whole stream, fed at a steady pace. A join only changes
// the layout; viewers never see the stream stall for it.
const GROUP_MAX = 9, GW = 1280, GH = 720, GFPS = 30, AFRAME = 48000 / GFPS * 4;   // audio bytes per video frame (s16le stereo)
function groupOf(hostCfg) { return hostCfg._group ||= { inputs: new Map(), enc: null, out: null, clock: null, timer: null, t0: 0, sent: 0, log: null, layoutKey: '' }; }
function groupLayout(k) {
  const cols = k <= 1 ? 1 : k <= 4 ? 2 : 3, rows = Math.ceil(k / cols), cw = Math.floor(GW / cols / 2) * 2, ch = Math.floor(GH / rows / 2) * 2;
  return { cols, rows, cw, ch, xy: [...Array(k)].map((_, i) => [(i % cols) * cw, Math.floor(i / cols) * ch]) };
}
// The host was already live on their channel from a browser (not into the group) when someone joined: their stream
// joins the picture too, and once the group stream is on the channel, that stream points its viewers to it (a liveend
// with `next`): one stream to watch, everyone side by side.
function absorbHostLive(hostCfg) {
  const chain = hostCfg.channel, g = groupOf(hostCfg); if (!chain || !engine.chains.has(chain)) return;
  const hostUser = Object.entries(obsStore.profiles).find(([, p]) => p === hostCfg)?.[0]; if (!hostUser) return;
  const d = engine.derive(chain);
  for (const lb of d?.liveNow || []) {
    const s = lb.payload.stream;
    if (lb.payload.group || lb.payload.relay || lb.author === signer.pub || myLive.has(s) || g.absorbed?.has(s)) continue;   // only the owner's own browser streams
    if (Date.now() - lb.ts > 12 * 3600000) continue;
    const r = pullIntoGroup(hostCfg, s, hostUser, 'host (browser)', chain, lb.payload.hub || null);
    if (typeof r === 'string') continue;
    (g.absorbed ||= new Set()).add(s); log(`[group] the host was already live: “${lb.payload.title}” joins the picture`);
    if (!g.out) hostCfg._absorbedTitle = lb.payload.title;
  }
  if (!g.absorbed?.size || g.pointing) return;
  g.pointing = true; let tries = 0;
  const point = async () => {
    const blk = g.out?.block;
    if (!blk) { if (++tries < 60) return setTimeout(point, 2000); g.pointing = false; return; }
    for (const s of g.absorbed) if (!engine.derive(chain)?.liveEnds.has(s)) await engine.append(chain, { type: 'liveend', stream: s, outcome: 'ended', next: blk }).catch(e => log('[group] pointing viewers:', e.message));
    g.pointing = false;
  };
  point();
}
// a tile's frame into the picture (YUV 4:2:0 planes); resized nearest-neighbour when it isn't the tile's size yet
const BLACK = (() => { const f = Buffer.alloc(GW * GH * 3 / 2, 128); f.fill(16, 0, GW * GH); return f; })();
function drawTile(frame, img, iw, ih, x0, y0, cw, ch) {
  const planes = [[0, GW, 0, iw, 1], [GW * GH, GW / 2, iw * ih, iw / 2, 2], [GW * GH * 5 / 4, GW / 2, iw * ih * 5 / 4, iw / 2, 2]];
  for (const [fo, fs, io, is, sub] of planes) {
    const tw = cw / sub, th = ch / sub, sw = iw / sub, sh = ih / sub, ox = x0 / sub, oy = y0 / sub;
    if (sw === tw && sh === th) { for (let y = 0; y < th; y++) img.copy(frame, fo + (oy + y) * fs + ox, io + y * is, io + y * is + tw); continue; }
    for (let y = 0; y < th; y++) { const sy = Math.min(sh - 1, Math.floor(y * sh / th)), row = fo + (oy + y) * fs + ox, srow = io + sy * is;
      for (let x = 0; x < tw; x++) frame[row + x] = img[srow + Math.min(sw - 1, Math.floor(x * sw / tw))]; }
  }
}
// one input's decoder, at its tile size: raw frames out (we keep the latest), raw audio out (we keep at most ½ s)
function decStart(g, x, cw, ch) {
  try { x.dec?.kill('SIGKILL'); } catch {}
  const size = cw * ch * 3 / 2, log = g.log || console.log;
  const dec = x.dec = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-probesize', '256000', '-analyzeduration', '500000', '-fflags', '+nobuffer+genpts+discardcorrupt',
    '-f', x.fmt === 'webm' ? 'matroska' : 'mp4', '-i', 'pipe:0',
    '-map', '0:v:0', '-vf', `scale=${cw}:${ch}:force_original_aspect_ratio=decrease,pad=${cw}:${ch}:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p`, '-f', 'rawvideo', 'pipe:1',
    '-map', '0:a:0?', '-f', 's16le', '-ar', '48000', '-ac', '2', 'pipe:3'], { stdio: ['pipe', 'pipe', 'pipe', 'pipe'] });
  x.decW = cw; x.decH = ch;
  let acc = []; let have = 0;
  dec.stdout.on('data', (d) => { acc.push(d); have += d.length; if (have < size) return; let all = Buffer.concat(acc); const n = Math.floor(all.length / size);
    x.last = Buffer.from(all.subarray((n - 1) * size, n * size)); x.lastW = cw; x.lastH = ch; all = all.subarray(n * size); acc = all.length ? [all] : []; have = all.length; });
  dec.stdio[3].on('data', (d) => { x.aq = x.aq.length ? Buffer.concat([x.aq, d]) : d; if (x.aq.length > AFRAME * GFPS / 2) x.aq = x.aq.subarray(x.aq.length - AFRAME * GFPS / 4 & ~3); });   // keep it short: ¼ s when it piles up
  dec.stderr.on('data', () => {}); dec.stdin.on('error', () => {}); dec.stdio[3].on('error', () => {});
  dec.on('exit', () => { if (x.dec === dec) x.dec = null; });
  dec.stdin.write(x.init); for (const c of x.recent) dec.stdin.write(c);   // from its latest video: decodes from the next keyframe
}
// The host's studio: who's on air (a waiting room for the rest, with previews), the layout, a ticker, a logo. Kept with
// the host's OBS profile, so it stays for their next stream.
const THUMB_W = 320, THUMB_H = 180, TICKER_H = 48;
function studioOf(hostCfg) {
  const s = hostCfg.studio ||= {};
  s.layout = ['grid', 'spotlight', 'solo'].includes(s.layout) ? s.layout : 'grid'; s.spot ??= null; s.waiting ??= true;
  s.ticker = Object.assign({ on: false, text: '', speed: 4 }, s.ticker); s.logo = Object.assign({ on: true, pos: 'tr', file: null }, s.logo);   // shown by default: the channel's avatar unless another image is set
  return s;
}
const hostUserOf = (hostCfg) => Object.entries(obsStore.profiles).find(([, p]) => p === hostCfg)?.[0] || null;
function studioTiles(mode, k) {   // where each on-air input goes: [{ x, y, w, h }]
  if (!k) return [];
  if (mode === 'solo' || k === 1) return [{ x: 0, y: 0, w: GW, h: GH }];
  if (mode === 'spotlight') { const n = Math.min(k - 1, 4), y0 = Math.floor((GH - n * THUMB_H) / 4) * 2;
    return [{ x: 0, y: 90, w: 960, h: 540 }, ...[...Array(n)].map((_, i) => ({ x: 960, y: y0 + i * THUMB_H, w: THUMB_W, h: THUMB_H }))]; }
  const L = groupLayout(k); return L.xy.map(([x, y]) => ({ x, y, w: L.cw, h: L.ch }));
}
function relayout(hostCfg, why) {
  const g = groupOf(hostCfg), st = studioOf(hostCfg);
  const all = [...g.inputs.values()].filter(x => !x.ended && x.init).sort((a, b) => a.at - b.at);
  let air = all.filter(x => x.onAir);
  const spot = air.find(x => x.id === st.spot) || air[0];
  if (st.layout === 'solo') air = spot ? [spot] : [];
  else if (st.layout === 'spotlight') air = spot ? [spot, ...air.filter(x => x !== spot)].slice(0, 5) : [];
  else air = air.slice(0, GROUP_MAX);
  const tiles = studioTiles(st.layout, air.length);
  const key = air.map((x, i) => `${x.id}@${tiles[i].x},${tiles[i].y},${tiles[i].w}x${tiles[i].h}`).join() + '|' + all.map(x => x.id).join();
  if (key === g.layoutKey) return; g.layoutKey = key; g.list = air; g.tiles = tiles; g.all = all;
  air.forEach((x, i) => { if (x.decW !== tiles[i].w || x.decH !== tiles[i].h || !x.dec) decStart(g, x, tiles[i].w, tiles[i].h); });
  for (const x of all) if (!air.includes(x) && (x.decW !== THUMB_W || x.decH !== THUMB_H || !x.dec)) decStart(g, x, THUMB_W, THUMB_H);   // off air: a preview for the host
  const waiting = all.filter(x => !x.onAir);
  (g.log || console.log)(`[group] on air (${st.layout}): ${air.map(x => x.label).join(', ') || 'nobody'}${waiting.length ? ` · waiting: ${waiting.map(x => x.label).join(', ')}` : ''}${why ? ` (${why})` : ''}`);
}
// a ticker: ffmpeg renders the text once (any font, any language), the compositor scrolls it along the bottom
function buildTicker(g, text) {
  if (!text) { g.ticker = null; return Promise.resolve(); }
  const est = Math.min(8000, Math.ceil((text.length * 22 + 48) / 2) * 2), f = path.join(DATA, `ticker-${process.pid}.txt`);
  fs.writeFileSync(f, text);
  return new Promise((res) => {
    const p = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i', `color=c=0x1a1a1a:s=${est}x${TICKER_H}:d=1`, '-vf', `drawtext=textfile=${f}:fontcolor=white:fontsize=28:x=16:y=(h-text_h)/2`, '-frames:v', '1', '-f', 'rawvideo', '-pix_fmt', 'yuv420p', 'pipe:1']);
    const parts = []; p.stdout.on('data', d => parts.push(d)); p.stderr.on('data', () => {});
    p.on('exit', () => {
      const buf = Buffer.concat(parts); if (buf.length < est * TICKER_H * 1.5) { g.ticker = null; return res(); }
      let right = 0; for (let y = 0; y < TICKER_H; y++) for (let x = est - 1; x > right; x--) if (buf[y * est + x] > 60) { right = x; break; }
      const w = Math.min(est, Math.ceil((right + 40) / 2) * 2), out = Buffer.alloc(w * TICKER_H * 3 / 2);
      for (const [so, ss, dO, ds, rows] of [[0, est, 0, w, TICKER_H], [est * TICKER_H, est / 2, w * TICKER_H, w / 2, TICKER_H / 2], [est * TICKER_H * 5 / 4, est / 2, w * TICKER_H * 5 / 4, w / 2, TICKER_H / 2]])
        for (let y = 0; y < rows; y++) buf.copy(out, dO + y * ds, so + y * ss, so + y * ss + ds);
      g.ticker = { buf: out, w }; res();
    });
  });
}
function drawTicker(frame, T, n, speed) {
  const y0 = GH - TICKER_H;
  frame.fill(26, y0 * GW, GH * GW); frame.fill(128, GW * GH + y0 / 2 * GW / 2, GW * GH + GH / 2 * GW / 2); frame.fill(128, GW * GH * 5 / 4 + y0 / 2 * GW / 2, GW * GH * 3 / 2);
  const period = T.w + GW, off = ((n * speed) % period) & ~1, sx = GW - off, x0 = Math.max(0, sx), x1 = Math.min(GW, sx + T.w);
  if (x1 <= x0) return;
  for (const [fo, fs, to, ts, rows, sub] of [[0, GW, 0, T.w, TICKER_H, 1], [GW * GH, GW / 2, T.w * TICKER_H, T.w / 2, TICKER_H / 2, 2], [GW * GH * 5 / 4, GW / 2, T.w * TICKER_H * 5 / 4, T.w / 2, TICKER_H / 2, 2]])
    for (let y = 0; y < rows; y++) T.buf.copy(frame, fo + (y0 / sub + y) * fs + x0 / sub, to + y * ts + (x0 - sx) / sub, to + y * ts + (x1 - sx) / sub);
}
// a logo: ffmpeg turns the image into a 160×90 overlay with transparency once; the compositor blends it into each frame
const LOGO_W = 160, LOGO_H = 90;
function buildLogo(g, file) {
  if (!file || !fs.existsSync(file)) { g.logo = null; return Promise.resolve(); }
  return new Promise((res) => {
    const p = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-i', file, '-vf', `scale=${LOGO_W}:${LOGO_H}:force_original_aspect_ratio=decrease,pad=${LOGO_W}:${LOGO_H}:(ow-iw)/2:(oh-ih)/2:color=black@0,format=yuva420p`, '-frames:v', '1', '-f', 'rawvideo', 'pipe:1']);
    const parts = []; p.stdout.on('data', d => parts.push(d)); p.stderr.on('data', () => {});
    p.on('exit', () => { const b = Buffer.concat(parts); g.logo = b.length >= LOGO_W * LOGO_H * 2.5 ? b : null; res(); });
  });
}
// the logo image: the one the host set, else the channel's avatar
function logoFileOf(hostCfg) {
  const st = studioOf(hostCfg); if (st.logo.file && fs.existsSync(st.logo.file)) return st.logo.file;
  let av = null; try { av = hostCfg.channel && engine.chains.has(hostCfg.channel) && engine.derive(hostCfg.channel)?.profile?.avatar; } catch {}
  const m = /^data:image\/(png|jpeg|webp|gif);base64,(.+)$/.exec(av || ''); if (!m) return null;
  const f = path.join(DATA, `avatar-${hostCfg.channel.slice(0, 16)}.${m[1]}`); try { fs.writeFileSync(f, Buffer.from(m[2], 'base64')); } catch { return null; }
  return f;
}
function drawLogo(frame, L, pos, tickerOn) {
  const A = LOGO_W * LOGO_H * 3 / 2, m = 16, lx = pos.includes('r') ? GW - LOGO_W - m : m, ly = pos.includes('b') ? GH - LOGO_H - m - (tickerOn ? TICKER_H : 0) : m;
  for (let y = 0; y < LOGO_H; y++) for (let x = 0; x < LOGO_W; x++) { const a = L[A + y * LOGO_W + x]; if (!a) continue; const d = (ly + y) * GW + lx + x; frame[d] = (L[y * LOGO_W + x] * a + frame[d] * (255 - a)) / 255 | 0; }
  for (let y = 0; y < LOGO_H / 2; y++) for (let x = 0; x < LOGO_W / 2; x++) { const a = L[A + 2 * y * LOGO_W + 2 * x]; if (!a) continue;
    for (const [fo, lo] of [[GW * GH, LOGO_W * LOGO_H], [GW * GH * 5 / 4, LOGO_W * LOGO_H * 5 / 4]]) { const d = fo + (ly / 2 + y) * (GW / 2) + lx / 2 + x; frame[d] = (L[lo + y * (LOGO_W / 2) + x] * a + frame[d] * (255 - a)) / 255 | 0; } }
}
// a small JPEG of an input's latest frame (the host's preview)
function inputThumb(x) {
  if (!x.last) return Promise.resolve(null);
  return new Promise((res) => {
    const p = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-f', 'rawvideo', '-pix_fmt', 'yuv420p', '-s', `${x.lastW}x${x.lastH}`, '-i', 'pipe:0', '-vf', 'scale=192:-2', '-frames:v', '1', '-q:v', '6', '-f', 'mjpeg', 'pipe:1']);
    const parts = []; p.stdout.on('data', d => parts.push(d)); p.stderr.on('data', () => {}); p.stdin.on('error', () => {});
    p.on('exit', () => res(parts.length ? 'data:image/jpeg;base64,' + Buffer.concat(parts).toString('base64') : null));
    p.stdin.end(x.last);
  });
}
function groupStart(hostCfg) {
  const g = groupOf(hostCfg), log = g.log || console.log;
  clearTimeout(g.timer); g.timer = null;
  if (g.enc) return relayout(hostCfg);
  absorbHostLive(hostCfg);   // bring in the host's ongoing stream
  relayout(hostCfg); if (!g.all?.length) return;
  { const st = studioOf(hostCfg); buildTicker(g, st.ticker.text); buildLogo(g, logoFileOf(hostCfg)); }
  // the stream: one encoder for the whole session, raw frames in at a steady 30 per second, raw mixed audio beside them
  g.out = goLiveSink(log, hostCfg); g.t0 = Date.now(); g.sent = 0;
  const enc = g.enc = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error',
    // big input queues: ffmpeg reads video and audio in step, and a small queue on one deadlocks the other at startup
    '-thread_queue_size', '512', '-f', 'rawvideo', '-pix_fmt', 'yuv420p', '-s', `${GW}x${GH}`, '-r', String(GFPS), '-i', 'pipe:0',
    '-thread_queue_size', '512', '-f', 's16le', '-ar', '48000', '-ac', '2', '-i', 'pipe:3',
    '-map', '0:v', '-map', '1:a', '-c:v', 'libx264', '-preset', 'superfast', '-tune', 'zerolatency', '-profile:v', 'high', '-level', '4.0',
    '-g', String(GFPS), '-keyint_min', String(GFPS), '-sc_threshold', '0', '-pix_fmt', 'yuv420p',   // a keyframe (a fragment) every second
    '-c:a', 'aac', '-b:a', '128k', '-ar', '48000', '-max_interleave_delta', '0', '-flush_packets', '1',
    '-f', 'mp4', '-movflags', 'frag_keyframe+empty_moov+default_base_moof', 'pipe:1'], { stdio: ['pipe', 'pipe', 'pipe', 'pipe'] });
  enc.stdin.on('error', () => {}); enc.stdio[3].on('error', () => {}); enc.stderr.on('data', d => log('[group]', String(d).trim().slice(0, 200)));
  const out = g.out;
  enc.stdout.on('data', (d) => { g.s2 = (g.s2 || 0) + d.length; });
  enc.stdout.on('data', mp4Boxes((init) => out.init(init, 'video/mp4; codecs="avc1.640028,mp4a.40.2"').catch(e => log('[group]', e.message)), (frag) => out.fragment(frag, true)));   // every fragment starts with a keyframe
  enc.on('exit', () => { out.end(); clearInterval(g.clock); if (g.enc === enc) { g.enc = null; g.out = null; g.t0 = 0; g.layoutKey = ''; } log('[group] ended'); });
  const frame = Buffer.alloc(GW * GH * 3 / 2), mix = new Int32Array(AFRAME / 2), abytes = Buffer.alloc(AFRAME);
  let late = 0;
  const tick = () => { try { tickInner(); } catch (e) { log('[group] frame:', e.message); } };
  const tickInner = () => {
    const due = Math.floor((Date.now() - g.t0) * GFPS / 1000) + 1;
    if (due - g.sent > GFPS * 2) { late++; g.sent = due - 1; }   // over 2 s behind (an overloaded computer): skip ahead instead of piling up
    while (g.sent < due) {
      if (enc.stdin.writableLength > frame.length * GFPS * 2) { g.sent = due; late++; break; }   // the encoder is 2 s behind (overloaded): skip, don't pile up
      BLACK.copy(frame); mix.fill(0);
      const tiles = g.tiles || [], list = g.list || [], st = studioOf(hostCfg);
      list.forEach((x, i) => {
        const T = tiles[i]; if (x.last && T) drawTile(frame, x.last, x.lastW, x.lastH, T.x, T.y, T.w, T.h);
        const take = Math.min(AFRAME, x.aq.length) & ~3;   // this frame's share of its audio (whole stereo samples)
        if (!x.muted) for (let k = 0; k < take; k += 2) mix[k >> 1] += x.aq.readInt16LE(k);
        x.aq = x.aq.subarray(take);
      });
      for (const x of g.all || []) if (!list.includes(x)) x.aq = x.aq.subarray(Math.min(AFRAME, x.aq.length) & ~3);   // off air: not heard
      if (st.ticker.on && g.ticker) drawTicker(frame, g.ticker, g.sent, st.ticker.speed);
      if (st.logo.on && g.logo) drawLogo(frame, g.logo, st.logo.pos, st.ticker.on && !!g.ticker);
      for (let k = 0; k < mix.length; k++) abytes.writeInt16LE(Math.max(-32768, Math.min(32767, mix[k])), k * 2);
      enc.stdin.write(Buffer.from(frame)); enc.stdio[3].write(Buffer.from(abytes)); g.sent++; g.s1 = (g.s1 || 0) + 1;
    }
  };
  g.clock = setInterval(tick, 1000 / GFPS / 2);
  if (process.env.LIVE_DEBUG && !g.dbg) g.dbg = setInterval(() => { log(`[group] per second: frames ${g.s1 || 0}, encoder ${g.s2 || 0} B, queued video ${g.enc?.stdin.writableLength ?? '-'} audio ${g.enc?.stdio[3].writableLength ?? '-'}, inputs ${[...g.inputs.values()].filter(x => !x.ended).map(x => x.label + ' ' + (x.inB || 0)).join(', ')}`); g.s1 = 0; g.s2 = 0; for (const x of g.inputs.values()) x.inB = 0; }, 1000);
}
function groupStop(hostCfg) { const g = groupOf(hostCfg); clearInterval(g.clock); g.clock = null; try { g.enc?.stdin.end(); g.enc?.stdio[3].end(); } catch {} for (const x of g.inputs.values()) try { x.dec?.kill('SIGKILL'); } catch {} }
// One input (an OBS connection, or a browser stream this node pulls): returns the sink its data goes to.
function groupInput(hostCfg, { id, label, user, kind, fmt = 'mp4', close = null }, log) {
  const g = groupOf(hostCfg); g.log = log;
  const old = g.inputs.get(id); if (old && !old.ended) { old.ended = true; try { old.dec?.kill('SIGKILL'); } catch {} try { old.close?.(); } catch {} }   // reconnecting replaces itself
  if (!old && [...g.inputs.values()].filter(x => !x.ended).length >= GROUP_MAX) return `The group picture is full (${GROUP_MAX}).`;
  const x = { id, label, user, kind, fmt, at: Date.now(), init: null, recent: [], dec: null, last: null, aq: Buffer.alloc(0), ended: false, close,
    onAir: user === hostUserOf(hostCfg) || !studioOf(hostCfg).waiting, muted: false };   // the host is on air; others wait to be put on air (unless the waiting room is off)
  g.inputs.set(id, x);
  const write = (b) => { x.inB = (x.inB || 0) + b.length; x.recent.push(b); if (x.recent.length > 4) x.recent.shift(); if (x.dec) { try { x.dec.stdin.write(b); } catch {} } };
  return x.sink = {
    input: x,
    init(bytes) {
      x.init = bytes; x.recent = [];
      if (g.enc) return relayout(hostCfg, `${label} joined`);
      if (!g.timer) g.timer = setTimeout(() => groupStart(hostCfg), 1500);   // a moment for others connecting at the same time
    },
    fragment(bytes) { write(bytes); },
    end() {
      if (x.ended) return; x.ended = true; try { x.dec?.kill('SIGKILL'); } catch {}
      g.inputs.delete(id);
      if (![...g.inputs.values()].some(y => !y.ended)) { clearTimeout(g.timer); g.timer = null; groupStop(hostCfg); return; }   // the encoder finishes the stream
      relayout(hostCfg, `${label} left`);
    }
  };
}
// A browser stream sent into the group: watched from the hub like a relay, its segments (WebM) fed to ffmpeg in order.
function pullIntoGroup(hostCfg, stream, user, label, chainOf = null, hubNode = null) {
  let sink = null, last = -1, hub = null, missing = 0;
  // one input per browser stream (the same person can send two webcams, from two browsers)
  sink = groupInput(hostCfg, { id: 'web:' + user + ':' + stream.slice(0, 8), label, user, kind: 'web', fmt: 'webm', close: () => { hub?.send({ t: 'leave', stream }); hub?.close(); } }, log);
  if (typeof sink === 'string') return sink;
  const watch = () => hub.send({ t: 'watch', stream, segOnly: true, media: false, dcSlots: 0, slots: 0, relay: true });
  const url = hubNode ? hubUrlOfNode(hubNode) : hubUrlFor(chainOf, stream);
  hub = hubSocket((m) => {
    if (m.t === 'seg' && m.seg?.stream === stream && m.seg.n > last) {
      missing = 0; const b = Buffer.from(m.seg.d, 'base64');
      if (m.seg.n === 0) { last = 0; sink.init(b); } else if (last >= 0) { last = m.seg.n; sink.fragment(b); }
    } else if (m.t === 'ended') { log(`[group] ${label} stopped`); sink.end(); hub.close(); }
    else if (m.t === 'nostream') {   // the browser has only just started: ask again for a minute
      if (++missing > 20) { log(`[group] ${label}: the hub (${url}) doesn’t have their stream; giving up`); sink.end(); hub.close(); } else setTimeout(() => !sink.input.ended && watch(), 3000);
    }
  }, watch, url);
  const alive = setInterval(() => { if (sink.input.ended) { clearInterval(alive); hub.close(); } else hub.send({ t: 'alive', stream }); }, 15000);
  log(`[group] pulling ${label}’s browser stream into the picture`);
  return sink;
}
function groupKick(hostCfg, pred) {   // disconnect inputs (OBS connection closed, browser stream no longer pulled)
  const g = groupOf(hostCfg); let n = 0;
  for (const x of [...g.inputs.values()]) if (!x.ended && pred(x)) { n++; try { x.close?.(); x.sink?.kick?.(); } catch {} x.sink?.end(); }
  return n;
}
var armed = null, announceSoon = null;
// Streams this computer started and is still sending. Any other open stream of its own (it crashed, or was stopped
// mid-stream) is closed on its channel, so it doesn't stay "live" for everyone.
var myLive = new Set();
setInterval(() => {
  for (const id of engine.chains.keys()) {
    let d; try { d = engine.derive(id); } catch { continue; }
    for (const lb of d?.liveNow || []) if (lb.author === signer.pub && !myLive.has(lb.payload.stream) && Date.now() - lb.ts > 3 * 60000) {
      myLive.add(lb.payload.stream);   // once
      engine.append(id, { type: 'liveend', stream: lb.payload.stream, outcome: 'ended' }).then(() => log(`[obs] closed a stream left open: ${lb.payload.title || lb.payload.stream.slice(0, 8)}`)).catch(e => log('[obs] liveend:', e.message));
    }
  }
}, 2 * 60000);
if (process.env.RTMP_PORT) {
  try {
    const { startRtmp } = await import('./rtmp.mjs');
    armed = () => {   // the newest live stream, not ended, whose owner named this node as its relay (Go live → pick this computer)
      let best = null;
      for (const c of engine.chains.values()) {
        if (c === engine.main) continue;
        const d = engine.derive(c.id); if (!d || d.kind !== 'channel') continue;
        for (const b of d.liveNow) if (b.payload.relay === signer.pub && (!best || b.ts > best.b.ts)) best = { chain: c.id, b };
      }
      return best;
    };
    // OBS needs this computer's stream key; with one, any address may connect (OBS on another computer, or a VPS)
    const rtmpHost = process.env.RTMP_HOST || '::';
    if (/^(127\.|::1$|localhost$)/.test(rtmpHost)) log(`[obs] ⚠ RTMP_HOST=${rtmpHost}: OBS on other computers can’t reach this one. Unset RTMP_HOST to listen on all addresses.`);
    const rtmp = startRtmp({ port: Number(process.env.RTMP_PORT), host: rtmpHost, log: (...a) => log('[obs]', ...a),
      onPublish: (key) => {
        key = String(key || '').trim();
        let user = null, slot = 0;   // whose key is it? the key itself, or key-2…key-9 (more cameras into your group stream)
        for (const [u, pr] of Object.entries(obsStore.profiles)) { if (!may(u, 'obs')) continue; const s_ = key === pr.key ? 1 : [2, 3, 4, 5, 6, 7, 8, 9].find(i => key === pr.key + '-' + i) || 0; if (s_) { user = u; slot = s_; break; } }
        if (!slot) return 'Wrong stream key. Copy it from Go live (or Devices) on the site.';
        const who = obsStore.users[user]?.name || (user === GRANT?.user ? 'the owner' : user.slice(0, 8));
        log(`[obs] stream from ${who}`);
        const guestOf = obsStore.users[user]?.guestOf;
        if (guestOf) {   // an invited guest: into the host's group stream
          const host = Object.assign(profileOf(guestOf), { owner: guestOf === GRANT?.user });
          if (!host.tile.on) return 'The group stream you were invited to isn’t on right now.';
          if (!ffmpegOk) return 'Group streams need ffmpeg on this computer.';
          return groupInput(host, { id: 'obs:' + user + (slot > 1 ? '-' + slot : ''), label: who + (slot > 1 ? ' ' + slot : ''), user, kind: 'obs' }, log);
        }
        const obsCfg = Object.assign(profileOf(user), { owner: user === GRANT?.user });
        if (obsCfg.tile.on) { if (!ffmpegOk) return 'Group streams need ffmpeg on this computer.'; return groupInput(obsCfg, { id: 'obs:' + user + (slot > 1 ? '-' + slot : ''), label: 'host' + (slot > 1 ? ' (camera ' + slot + ')' : ''), user, kind: 'obs' }, log); }
        if (slot > 1) return 'The group stream is off on this computer: use the main stream key.';
        if (obsCfg.channel && !obsCfg.owner && obsCfg.channelBy !== user && !ownsChannel(user, obsCfg.channel)) return 'That channel isn’t yours.';
        if (!(obsCfg.owner && armed()) && !(obsCfg.channel && engine.derive(obsCfg.channel)?.relays.has(signer.pub))) return 'Not linked to a channel yet. On the site: Go live → OBS → pick this computer and link it to your channel.';
        return goLiveSink(log, obsCfg);
      } });
    globalThis.openTubeRtmp = { lastConn: () => rtmp.status().lastConn || null, status: () => ({ ...rtmp.status(), armed: !!armed(), port: Number(process.env.RTMP_PORT) }) };
  } catch (e) { log('OBS receiver unavailable:', e.message); }
}

// ---- video hosting without a browser (VIDEO_DIR + HOST_USER, set by opentube.mjs): keep and share the videos of
// HOST_USER's joined and own channels (host.mjs, needs the webtorrent package)
if (process.env.VIDEO_DIR && process.env.HOST_USER) {
  try {
    const { startHost } = await import('./host.mjs');
    globalThis.openTubeHost = await startHost({ engine, nodeKey: signer.pub, tabs: pieceTabs(), users: () => Object.entries(obsStore.users).filter(([, u]) => u.can.includes('host')).map(([k]) => k), meetPlaces, user: process.env.HOST_USER, videoDir: path.resolve(process.env.VIDEO_DIR), site: process.env.NODE_SITE || `http://localhost:${PORT}`, capGB: Number(process.env.CAP_GB) || 5, log });
  } catch (e) { log('video hosting unavailable:', e.message, '(npm install webtorrent next to server.mjs)'); }
}

// ---- getting paid for resources: browsers send signed receipts (bytes received from this node, time connected, tracker
// service); after each 10-minute period this node submits them in a `serve` transaction (core: SERVE), paid to its
// owner when it has a grant. Receipts for other nodes are passed on once.
var receipts = (() => {
  const byEpoch = new Map(), seen = new Set(), submitted = new Set();
  const take = async (r, from) => {
    if (!r || typeof r !== 'object' || typeof r.sig !== 'string' || seen.has(r.sig)) return;
    seen.add(r.sig); if (seen.size > 50000) seen.clear();
    if (r.provider !== signer.pub) { for (const [id, p] of peers) if (id !== from) p.send(JSON.stringify({ t: 'rcpt', r })); return; }
    if (r.v !== 1 || r.kind !== 'receipt' || !core.SERVE.res[r.res] || !(r.amt > 0) || r.epoch >= core.epochOf(Date.now()) || submitted.has(r.epoch)) return;
    if (!(await core.verify(r.by, core.receiptPreimage(r), r.sig))) return;
    const m = byEpoch.get(r.epoch) || byEpoch.set(r.epoch, new Map()).get(r.epoch);
    const k = r.res + '|' + r.by, cur = m.get(k); if (!cur || r.amt > cur.amt) m.set(k, r);
  };
  setInterval(async () => {
    const now = core.epochOf(Date.now());
    for (const [e, m] of byEpoch) {
      if (e >= now) continue;
      byEpoch.delete(e); submitted.add(e);
      if (e < now - core.SERVE.grace) continue;
      const list = [...m.values()];
      for (let i = 0; i < list.length; i += core.SERVE.maxReceipts) {
        try { await engine.submitTx({ serve: { epoch: e, receipts: list.slice(i, i + core.SERVE.maxReceipts), ...(GRANT ? { grant: GRANT } : {}) } }); log(`[earn] submitted ${list.length} receipt(s) for period ${e}`); }
        catch (err) { log('[earn] couldn’t submit receipts:', err.message); }
      }
    }
  }, 60000);
  return { take };
})();

// ---- pay per piece (core: PIECE). Viewers' tabs for the pieces this node sends are cashed in once a minute; the node
// pays (from its own key) for pieces it downloads, except for channels its owner hosts. What it earns beyond a small
// reserve goes to its owner every 10 minutes.
function pieceTabs() {
  const index = { at: 0, map: new Map() };
  const find = (ih) => {
    if (Date.now() - index.at > 10000 || !index.map.has(ih)) {
      index.map.clear(); index.at = Date.now();
      for (const c of engine.chains.values()) if (c !== engine.main && c.genesis.payload.kind === 'channel') for (const b of engine.derive(c.id)?.videos || []) index.map.set(b.payload.infohash, { chain: c.id, block: b });
    }
    return index.map.get(ih) || null;
  };
  const hosts = (k, chain) => {   // a registered host, or the channel's owner (by their published list), or a node of either
    const users = [k, engine.nodes.get(k)?.grant.user].filter(Boolean), hs = engine.ledger.hosts.get(chain);
    return users.some(u => hs?.has(u) || engine.memberships.get(u)?.own?.includes(chain));
  };
  const user = process.env.HOST_USER;
  const tabs = core.createPieceTabs({
    signer, price: () => engine.ledger.prices.piece, balanceOf: (k) => engine.ledger.balance(k), log: (m) => log('[pay]', m),
    infoFor: (ih) => {
      const x = find(ih); if (!x) return null;
      const owner = engine.derive(x.chain).owner, b = x.block;
      return { chain: x.chain, video: b.hash, creator: owner,
        free: (k) => b.ts < core.PIECE.start || k === owner || hosts(k, x.chain) || (k === signer.pub && (user === owner || hosts(user, x.chain))) };
    }
  });
  const done = new Map();
  setInterval(async () => {
    const due = tabs.pending().filter(x => x.amt > (engine.ledger.tabsPaid.get(`${x.payer}|${x.payee}|${x.video}`) || 0) && x.amt > (done.get(x.payer + '|' + x.video) || 0));
    for (let i = 0; i < due.length; i += core.PIECE.maxTabs) {
      const part = due.slice(i, i + core.PIECE.maxTabs);
      try { await engine.submitTx({ tabs: part }); for (const x of part) done.set(x.payer + '|' + x.video, x.amt); log(`[pay] cashing in ${part.length} viewer tab(s)`); }
      catch (e) { log('[pay] couldn’t cash in tabs:', e.message); }
    }
  }, 60000);
  if (GRANT) setInterval(async () => {   // pass earnings to the owner, keeping 20 coins for this node's own downloads
    const extra = engine.ledger.balance(signer.pub) - 2000;
    if (extra >= 500 && ![...engine.txpool.values()].some(t => t.from === signer.pub && t.pay)) {
      try { await engine.submitTx({ pay: [{ to: GRANT.user, amt: extra }], memo: 'node earnings' }); log(`[pay] sent ${extra / 100} coins of earnings to the owner`); } catch (e) { log('[pay] couldn’t send earnings:', e.message); }
    }
  }, 10 * 60000);
  globalThis.openTubeTabs = tabs;
  return tabs;
}

// ---- control from the owner's console, and storage proofs. Both arrive as routed messages (engine.route): over a
// direct WebSocket when the console can reach this node's address, otherwise hop by hop over the mesh.
const ctlSeen = new Map();   // nonce -> time (replay protection)
// ---- the scratch pad: short-lived coordination shared between entry nodes, in memory only. Each entry node sends the
// others what's live on its hub (viewer counts, who watches) every 15 s, signed; entries expire after a minute. So any
// entry node's /status shows the network's live streams, and a browser needs to ask only one.
const pad = new Map();   // entry node key -> { at, items }
const padPreimage = (b) => 'opentube-pad:' + core.canon({ from: b.from, at: b.at, items: b.items });
async function padTake(b) {
  if (!b || typeof b.from !== 'string' || !Array.isArray(b.items) || b.items.length > 200 || Math.abs(Date.now() - b.at) > 60000) return { ok: false };
  if (!engine.nodes.get(b.from)?.roles?.includes('entry') || !(await core.verify(b.from, padPreimage(b), b.sig).catch(() => false))) return { ok: false };
  pad.set(b.from, { at: b.at, items: b.items.filter(x => x && /^[0-9a-f]{64}$/.test(x.stream)).slice(0, 200) });
  return { ok: true };
}
const padStreams = () => { const now = Date.now(), out = []; for (const [k, x] of pad) { if (now - x.at > 60000) { pad.delete(k); continue; } for (const it of x.items) out.push({ ...it, hub: k }); } return out; };
setInterval(async () => {
  if (!isEntry()) return;
  const b = { kind: 'pad', from: signer.pub, at: Date.now(), items: liveHub.live().slice(0, 100) };
  b.sig = await signer.sign(padPreimage(b));
  for (const n of engine.nodes.values()) if (n.node !== signer.pub && n.roles?.includes('entry') && Date.now() - n.at < 120000) engine.route(n.node, b, 10000).catch(() => {});
}, 15000);
async function onRoute(body, reply) {
  if (!body || typeof body !== 'object') return;
  if (body.kind === 'nodectl') return reply(await control(body));
  if (body.kind === 'pchal') return reply(await answerChallenge(body));
  if (body.kind === 'reach') return reply(await reachProbe(body));
  if (body.kind === 'pad') return reply(await padTake(body));
}
// ---- "can the internet reach that computer?": another node asks us to connect to its OBS and web ports from here.
// A computer can't test this itself: its public address is on its own network card, so the connection never leaves
// it and skips the firewalls in between. Only addresses and ports a node announced for itself, and a few per minute.
const reachTimes = [];
async function reachProbe(b) {
  const now = Date.now(); while (reachTimes.length && now - reachTimes[0] > 60000) reachTimes.shift();
  if (reachTimes.length >= 10) return { ok: false, error: 'busy' }; reachTimes.push(now);
  const host = String(b.host || ''), rtmp = Number(b.rtmp) || 0, http = Number(b.http) || 0;
  if (!/^\d{1,3}(\.\d{1,3}){3}$/.test(host)) return { ok: false, error: 'bad address' };
  if (host === await publicIp()) return { ok: false, error: 'same network', same: true };
  const announced = [...engine.nodes.values()].filter(n => n.info?.publicIp === host || (n.addrs || []).some(a => { try { return new URL(a).hostname === host; } catch { return false; } }));
  if (!announced.length) return { ok: false, error: 'no node announced that address' };
  const out = { by: signer.pub, name: (process.env.NODE_LABEL || os.hostname()).slice(0, 60), from: await publicIp() };
  if (rtmp && announced.some(n => n.info?.obs?.port === rtmp)) out.rtmp = await rtmpProbe(host, rtmp);
  if (http && announced.some(n => n.addrs.some(a => { try { const u = new URL(a); return (u.hostname === host || n.info?.publicIp === host) && Number(u.port || 80) === http; } catch { return false; } }))) {
    const t0 = Date.now();
    try { const r = await fetch(`http://${host}:${http}/status`, { signal: AbortSignal.timeout(6000) }); out.http = { ok: r.ok, ms: Date.now() - t0, port: http }; }
    catch (e) { out.http = { ok: false, port: http, ms: Date.now() - t0, error: /abort|timeout/i.test(e.name + e.message) ? 'no answer (blocked by a firewall?)' : /ECONNREFUSED/.test(String(e.cause?.code || e.message)) ? 'refused' : (e.cause?.code || e.message) }; }
  }
  return out;
}
async function signed(x) { x.sig = await signer.sign(core.ctlPreimage(x)); return x; }
async function control(c) {
  const fail = (error) => signed({ v: 1, kind: 'nodectlr', node: signer.pub, re: c.nonce, at: Date.now(), ok: false, error });
  if (c.v !== 1 || c.node !== signer.pub || typeof c.nonce !== 'string' || c.nonce.length > 40 || typeof c.cmd !== 'string') return fail('bad command');
  if (!GRANT || (c.user !== GRANT.user && (!obsStore.users[c.user] || obsStore.users[c.user].until < Date.now()))) return fail('this computer isn’t yours, and it isn’t shared with you (or your invitation ended)');
  if (Math.abs(Date.now() - c.at) > 2 * 60000 || ctlSeen.has(c.nonce)) return fail('stale or repeated command');
  if (!(await core.verify(c.user, core.ctlPreimage(c), c.sig))) return fail('bad signature');
  ctlSeen.set(c.nonce, Date.now()); for (const [k, at] of ctlSeen) if (Date.now() - at > 5 * 60000) ctlSeen.delete(k);
  try {
    let data;
    const owner = c.user === GRANT.user;
    if (c.cmd.startsWith('group')) {   // the group stream: yours (or, for a guest, the one you were invited to)
      const a = c.args || {}, me_ = obsStore.users[c.user], guestOf = me_?.guestOf;
      // someone this computer is shared with may also join another user's group here (the owner's, usually): `host`
      const joinHost = !guestOf && typeof a.host === 'string' && a.host !== c.user && (a.host === GRANT.user || (obsStore.users[a.host] && !obsStore.users[a.host].guestOf)) ? a.host : null;
      const hostUser = guestOf || joinHost || c.user;
      if (!may(c.user, 'obs')) return fail('you may not stream through this computer');
      if (joinHost && !['group', 'group.join', 'group.kick'].includes(c.cmd)) return fail('only the host can change their group stream');
      const hostCfg = Object.assign(profileOf(hostUser), { owner: hostUser === GRANT.user }), g = groupOf(hostCfg);
      const isHost = !guestOf && !joinHost, label = (u) => u === hostUser ? 'host' : obsStore.users[u]?.name || u.slice(0, 8);
      if (c.cmd === 'group.set') { if (!isHost) return fail('only the host can switch the group stream'); if (typeof a.on === 'boolean') { hostCfg.tile.on = a.on; saveObs(); } }
      else if (c.cmd === 'group.invite') {
        if (!isHost) return fail('only the host can invite');
        if (!/^[A-Za-z0-9_-]{43}$/.test(a.key || '') || a.key === GRANT.user || a.key === c.user) return fail('not a user ID');
        if (!hostCfg.tile.on && ffmpegOk) { hostCfg.tile.on = true; saveObs(); }   // inviting someone on camera switches the group stream on
        if (obsStore.users[a.key] && !obsStore.users[a.key].guestOf) { if (!obsStore.users[a.key].can.includes('obs')) { obsStore.users[a.key].can.push('obs'); saveObs(); announceSoon?.(); } log(`[group] ${a.name || a.key.slice(0, 8)} shares this computer: they can join the group from the live page`); }
        else { const hours = Math.min(Math.max(Number(a.hours) || 6, 0.25), 24 * 14);
        obsStore.users[a.key] = { can: ['obs'], name: String(a.name || '').slice(0, 60), guestOf: c.user, until: Date.now() + hours * 3600000 };
        profileOf(a.key); saveObs(); announceSoon?.();
        log(`[group] the host invited ${a.name || a.key.slice(0, 8)} for ${hours} h`); }
      }
      else if (c.cmd === 'group.uninvite') {   // hang up: disconnect and end their access
        const u = obsStore.users[a.key]; if (!u?.guestOf || (u.guestOf !== c.user && !owner)) return fail('not your guest');
        groupKick(hostCfg, x => x.user === a.key); delete obsStore.users[a.key]; delete obsStore.profiles[a.key]; saveObs(); announceSoon?.();
        log(`[group] invitation of ${u.name || a.key.slice(0, 8)} cancelled`);
      }
      else if (c.cmd === 'group.studio' || c.cmd === 'group.air' || c.cmd === 'group.mute' || c.cmd === 'group.logo') {   // the host's studio controls
        if (!isHost) return fail('only the host controls the picture');
        const st = studioOf(hostCfg), x = a.id && g.inputs.get(a.id);
        if (c.cmd === 'group.air') { if (!x) return fail('not connected'); x.onAir = !!a.on; if (!a.on && st.spot === x.id) st.spot = null; log(`[group] ${x.label} ${a.on ? 'on air' : 'off air (waiting)'}`); }
        else if (c.cmd === 'group.mute') { if (!x) return fail('not connected'); x.muted = !!a.mute; }
        else if (c.cmd === 'group.logo') {   // an image from the host's browser (a data URL, kept on this computer)
          const m = /^data:image\/(png|jpeg|webp|gif);base64,([A-Za-z0-9+/=]+)$/.exec(String(a.data || ''));
          if (!m) st.logo.file = null;   // removed: back to the channel's avatar
          else {
            const buf = Buffer.from(m[2], 'base64'); if (buf.length > 400000) return fail('the logo is too big (400 KB at most)');
            const f = path.join(DATA, `logo-${hostUser.slice(0, 16)}.${m[1]}`); fs.writeFileSync(f, buf); st.logo.file = f; st.logo.on = true;
          }
          await buildLogo(g, logoFileOf(hostCfg));
        } else {
          if (['grid', 'spotlight', 'solo'].includes(a.layout)) st.layout = a.layout;
          if (a.spot !== undefined) { st.spot = a.spot || null; const sx = st.spot && g.inputs.get(st.spot); if (sx) sx.onAir = true; }
          if (typeof a.waiting === 'boolean') st.waiting = a.waiting;
          if (a.ticker && typeof a.ticker === 'object') {
            const t = a.ticker, newText = typeof t.text === 'string' ? t.text.replace(/[\r\n]+/g, '   ').slice(0, 500) : st.ticker.text;
            if (typeof t.on === 'boolean') st.ticker.on = t.on; if (Number.isFinite(t.speed)) st.ticker.speed = Math.min(12, Math.max(1, Math.round(t.speed)));
            if (newText !== st.ticker.text || (st.ticker.on && !g.ticker)) { st.ticker.text = newText; await buildTicker(g, newText); }
          }
          if (a.logo && typeof a.logo === 'object') { if (typeof a.logo.on === 'boolean') st.logo.on = a.logo.on; if (['tl', 'tr', 'bl', 'br'].includes(a.logo.pos)) st.logo.pos = a.logo.pos; if (st.logo.on && !g.logo) await buildLogo(g, logoFileOf(hostCfg)); }
        }
        saveObs(); if (g.enc) relayout(hostCfg, 'the host changed the picture');
      }
      else if (c.cmd === 'group.kick.all') {   // the host ended the stream from the site: everyone out
        if (!isHost) return fail('only the host can end the group stream');
        groupKick(hostCfg, () => true);
      }
      else if (c.cmd === 'group.kick') {   // disconnect one input (they can come back while invited)
        if (!isHost && a.id && !String(a.id).includes(c.user)) return fail('only the host can disconnect others');
        if (!groupKick(hostCfg, x => x.id === a.id)) return fail('not connected');
      }
      else if (c.cmd === 'group.join') {   // a browser stream (host's or a guest's) into the picture: we pull its segments
        if (!ffmpegOk) return fail('group streams need ffmpeg on this computer');
        if (!hostCfg.tile.on) { hostCfg.tile.on = true; saveObs(); log('[group] switched on: someone joined on camera'); }   // joining is what a group stream is for
        if (!/^[0-9a-f]{64}$/.test(a.stream || '') || !/^[0-9a-f]{64}$/.test(a.chain || '')) return fail('bad stream');
        let ok = ownsChannel(c.user, a.chain);
        if (!ok && typeof a.channelSig === 'string') { const key = engine.derive(a.chain)?.owner || engine.directory.get(a.chain)?.genesis.author; ok = !!key && await core.verify(key, core.obsChannelPreimage(signer.pub, c.user, a.chain), a.channelSig).catch(() => false); }
        if (!ok) return fail('that stream’s channel isn’t yours');
        const hubNode = /^[A-Za-z0-9_-]{43}$/.test(a.hub || '') ? a.hub : null;   // the hub the browser streams on (its live block may not have reached us yet)
        const r = pullIntoGroup(hostCfg, a.stream, c.user, label(c.user) + ' (browser)', a.chain, hubNode); if (typeof r === 'string') return fail(r);
      }
      const now = Date.now();
      data = { on: !!hostCfg.tile.on, host: isHost, live: !!g.out, ffmpeg: ffmpegOk,
        inputs: await Promise.all([...g.inputs.values()].filter(x => !x.ended).map(async x => ({ id: x.id, label: x.label, user: x.user, kind: x.kind, since: x.at, guest: !!obsStore.users[x.user]?.guestOf,
          onAir: !!x.onAir, muted: !!x.muted, shown: !!g.list?.includes(x), ...(isHost && a.thumbs ? { thumb: await inputThumb(x) } : {}) }))),
        ...(isHost ? { studio: (({ layout, spot, waiting, ticker, logo }) => ({ layout, spot, waiting, ticker, logo: { on: logo.on, pos: logo.pos, has: !!logoFileOf(hostCfg), own: !!logo.file } }))(studioOf(hostCfg)) } : {}),
        me: [...g.inputs.values()].filter(x => !x.ended && x.user === c.user).map(x => ({ id: x.id, onAir: !!x.onAir, shown: !!g.list?.includes(x) })),
        guests: Object.entries(obsStore.users).filter(([, u]) => u.guestOf === hostUser && !(u.until < now)).map(([k, u]) => ({ key: k, name: u.name, until: u.until })),
        ...(isHost ? {} : { invitedBy: label(hostUser), until: guestOf ? me_.until : null, joined: !!joinHost, key: profileOf(c.user).key, port: Number(process.env.RTMP_PORT) || null }) };
      return signed({ v: 1, kind: 'nodectlr', node: signer.pub, re: c.nonce, at: Date.now(), ok: true, data });
    }
    if (c.cmd === 'store' || c.cmd === 'store.status') {   // keep a video someone uploaded from a browser (their storage is this computer)
      const a = c.args || {};
      if (!owner && !may(c.user, 'host')) return fail('you may not store videos on this computer');
      if (!globalThis.openTubeHost?.storeVideo) return fail('video hosting is off on this computer');
      if (!/^[0-9a-f]{40}$/.test(a.ih || '')) return fail('bad video');
      if (c.cmd === 'store') {
        if (!/^[0-9a-f]{64}$/.test(a.chain || '')) return fail('bad channel');
        let ok = owner || ownsChannel(c.user, a.chain);
        if (!ok && typeof a.channelSig === 'string') { const key = engine.derive(a.chain)?.owner || engine.directory.get(a.chain)?.genesis.author; ok = !!key && await core.verify(key, core.obsChannelPreimage(signer.pub, c.user, a.chain), a.channelSig).catch(() => false); }
        if (!ok) return fail('that channel isn’t yours');
        try { await globalThis.openTubeHost.storeVideo(a.chain, a.ih); } catch (e) { return fail(e.message); }
        log(`[host] storing a video for ${owner ? 'the owner' : obsStore.users[c.user]?.name || c.user.slice(0, 8)}`);
      }
      return signed({ v: 1, kind: 'nodectlr', node: signer.pub, re: c.nonce, at: Date.now(), ok: true, data: globalThis.openTubeHost.progressOf(a.ih) });
    }
    if (c.cmd === 'relay') {   // "I just went live: help serve it" (from the owner's or a sharing person's browser)
      const a = c.args || {};
      if (!owner && !may(c.user, 'host')) return fail('you may not use this computer for relaying');
      if (!/^[0-9a-f]{64}$/.test(a.stream || '') || !/^[0-9a-f]{64}$/.test(a.chain || '')) return fail('bad stream');
      if (a.record === true && relaying.has(a.stream)) { relaying.get(a.stream).close(); relaying.delete(a.stream); }   // already relaying it: restart, recording this time
      relayAsked.set(a.stream, { chain: a.chain, at: Date.now(), record: a.record === true && !!globalThis.openTubeHost?.seedRecording, title: String(a.title || 'Live').slice(0, 100) }); setTimeout(liveRelays, 500);
      return signed({ v: 1, kind: 'nodectlr', node: signer.pub, re: c.nonce, at: Date.now(), ok: true, data: { relaying: !!RTCPC } });
    }
    if (c.cmd.startsWith('flash')) {   // flash computers (owner only): status, start one, delete one, keep one
      if (!owner) return fail('only the owner can start or stop flash computers');
      const a = c.args || {}, ch = a.tag && flash.children.find(x => x.tag === a.tag);
      if (c.cmd === 'flash.start') { try { await flashStart(); } catch (e) { return fail(e.message); } }
      else if (c.cmd === 'flash.stop') { if (!ch) return fail('no such flash computer'); await flashDelete(ch, 'deleted by the owner'); if (ch.error && ch.state !== 'deleted') return fail(ch.error); }
      else if (c.cmd === 'flash.keep') { if (!ch) return fail('no such flash computer'); ch.keep = !!a.keep; saveFlash(); }
      else if (c.cmd === 'flash.set') { for (const k of ['region', 'plan', 'image']) if (typeof a[k] === 'string') flash[k] = a[k].trim().slice(0, 60) || null; if (a.maxHours) flash.maxHours = Math.min(48, Math.max(1, Number(a.maxHours) || 6)); if (a.idleMin) flash.idleMin = Math.min(240, Math.max(5, Number(a.idleMin) || 15)); saveFlash(); }
      const children = flash.children.filter(x => x.state !== 'deleted' || Date.now() - (x.deletedAt || 0) < 24 * 3600000).slice(-10)
        .map(({ tag, provider, plan, region, createdAt, state, node, busy, recordings, copied, idleSince, keep, why, error, deletedAt }) => ({ tag, provider, plan, region, createdAt, state, node, busy, recordings, copied, idleSince, keep, why, error, deletedAt }));
      data = { provider: flash.provider || null, ready: !!(flash.provider && (flash.provider === 'local' || flash.provider === 'custom' ? true : flash.token)), region: flashOpt('region') || null, plan: flashOpt('plan') || null, maxHours: flash.maxHours, idleMin: flash.idleMin, children };
      log(`[control] ${c.cmd}`);
      return signed({ v: 1, kind: 'nodectlr', node: signer.pub, re: c.nonce, at: Date.now(), ok: true, data });
    }
    if (c.cmd === 'public.set') {   // make this computer public through Cloudflare Tunnel (or stop): saved for every start, then restart
      if (!owner) return fail('only the owner can change this');
      if (!process.env.OPENTUBE_LAUNCHER || !process.env.LAUNCH_FILE) return fail('this node wasn’t started with the OpenTube command, so it can’t change how it starts');
      const a = c.args || {}, mode = ['tunnel', 'off'].includes(a.mode) ? a.mode : null; if (!mode) return fail('bad mode');
      let saved = {}; try { saved = JSON.parse(fs.readFileSync(process.env.LAUNCH_FILE, 'utf8')); } catch {}
      saved.tunnel = mode === 'tunnel'; fs.writeFileSync(process.env.LAUNCH_FILE, JSON.stringify(saved, null, 2));
      log(`[control] public ${mode === 'tunnel' ? 'on (Cloudflare Tunnel)' : 'off'}: restarting`);
      setTimeout(() => process.exit(75), 800);
      return signed({ v: 1, kind: 'nodectlr', node: signer.pub, re: c.nonce, at: Date.now(), ok: true, data: { restarting: true } });
    }
    if (c.cmd === 'update') {   // download the site's current code and restart (needs the one-command launcher, which restarts us)
      if (!owner && !may(c.user, 'manage')) return fail('only the owner can update this computer');
      if (!process.env.OPENTUBE_LAUNCHER) return fail('this node wasn’t started with the OpenTube command, so it can’t update itself: stop it and start it again');
      log(`[control] update from ${owner ? 'the owner' : obsStore.users[c.user]?.name || c.user.slice(0, 8)}'s console: restarting with the site’s current code`);
      setTimeout(() => process.exit(75), 800);
      return signed({ v: 1, kind: 'nodectlr', node: signer.pub, re: c.nonce, at: Date.now(), ok: true, data: { restarting: true } });
    }
    if (c.cmd === 'users' || c.cmd === 'users.set') {   // the owner shares this computer: who may use OBS, hosting, management
      if (!owner) return fail('only the owner can share this computer');
      const a = c.args || {};
      if (c.cmd === 'users.set') {
        if (!/^[A-Za-z0-9_-]{43}$/.test(a.key || '') || a.key === GRANT.user) return fail('not a user ID');
        const can = (Array.isArray(a.can) ? a.can : []).filter(x => ['obs', 'host', 'manage'].includes(x));
        if (!can.length) { delete obsStore.users[a.key]; delete obsStore.profiles[a.key]; } else obsStore.users[a.key] = { can, name: String(a.name || '').slice(0, 60) };
        saveObs(); globalThis.openTubeHost?.follow?.(); announceSoon?.();
      }
      data = { users: obsStore.users };
      log(`[control] ${c.cmd}: shared with ${Object.keys(obsStore.users).length} people`);
      return signed({ v: 1, kind: 'nodectlr', node: signer.pub, re: c.nonce, at: Date.now(), ok: true, data });
    }
    if (c.cmd === 'obs' || c.cmd === 'obs.set' || c.cmd === 'obs.rotate') {   // OBS settings: each person's own (the key is sent only to them)
      if (!may(c.user, 'obs')) return fail('you may not stream through this computer');
      const a = c.args || {}, obsCfg = profileOf(c.user);
      if (c.cmd === 'obs.rotate') obsCfg.key = newStreamKey();
      if (c.cmd === 'obs.set') {
        if (a.channel && /^[0-9a-f]{64}$/.test(a.channel) && !engine.chains.has(a.channel)) {   // not loaded here yet: fetch it first
          const ch = engine.openChain(a.channel); if (ch) engine.requestChain(ch);
          for (let i = 0; i < 40 && !(engine.derive(a.channel)?.owner); i++) await new Promise(r => setTimeout(r, 250));
        }
        let proved = false;
        if (a.channel && typeof a.channelSig === 'string') {   // signed by the channel's key itself
          const key = engine.derive(a.channel)?.owner || engine.directory.get(a.channel)?.genesis.author;
          proved = !!key && await core.verify(key, core.obsChannelPreimage(signer.pub, c.user, a.channel), a.channelSig).catch(() => false);
        }
        if (a.channel !== undefined) { if (a.channel && !proved && !ownsChannel(c.user, a.channel)) return fail('that channel isn’t yours (or this computer couldn’t load it yet: try again)'); obsCfg.channelBy = a.channel && (proved || ownsChannel(c.user, a.channel)) ? c.user : null; obsCfg.channel = /^[0-9a-f]{64}$/.test(a.channel) ? a.channel : null; }
        if (typeof a.title === 'string') obsCfg.title = a.title.slice(0, 140) || 'Live';
        if (a.youtube) { if (typeof a.youtube.key === 'string') obsCfg.youtube.key = a.youtube.key.trim().slice(0, 200); if (typeof a.youtube.on === 'boolean') obsCfg.youtube.on = a.youtube.on; }
        if (a.tile) { if (typeof a.tile.on === 'boolean') obsCfg.tile.on = a.tile.on; if ([2, 3, 4].includes(a.tile.n)) obsCfg.tile.n = a.tile.n; }
        if (obsCfg.channel && !engine.chains.has(obsCfg.channel)) engine.requestChain(engine.openChain(obsCfg.channel));
      }
      saveObs();
      const os = await import('node:os');
      const ips = Object.values(os.networkInterfaces()).flat().filter(i => i && i.family === 'IPv4' && !i.internal).map(i => i.address);
      if (c.cmd === 'obs' && a.test) {   // can OBS reach us from the internet? connect to our own public address and do the RTMP greeting
        const pub = await publicIp(), port = Number(process.env.RTMP_PORT), httpPort = Number(PORT);
        // ask up to 3 other online nodes, elsewhere on the internet, to connect to us (the honest test)
        const others = pub ? [...engine.nodes.values()].filter(n => n.node !== signer.pub && Date.now() - n.at < 120000 && !(n.addrs || []).some(x => x.includes('//' + pub + ':') || x.includes('//' + pub + '/')))
          .sort(() => Math.random() - 0.5).slice(0, 4) : [];
        const outside = (await Promise.all(others.map(n => engine.route(n.node, { kind: 'reach', host: pub, rtmp: port, http: httpPort, nonce: crypto.randomBytes(8).toString('hex') }, 20000).catch(() => null))))
          .filter(r => r && !r.same && (r.rtmp || r.http)).slice(0, 3);
        data = { test: pub ? await rtmpProbe(pub, port) : { ok: false, error: 'couldn’t find this computer’s public address' }, outside, asked: others.length, publicIp: pub, httpPort };
        return signed({ v: 1, kind: 'nodectlr', node: signer.pub, re: c.nonce, at: Date.now(), ok: true, data });
      }
      const { portHolders } = await import('./rtmp.mjs');
      const bound = portHolders(Number(process.env.RTMP_PORT) || 0).map(x => ({ addr: x.addr, pid: x.pid, self: x.pid === process.pid, cmd: x.pid === process.pid ? '' : x.cmd.slice(0, 120) }));
      data = { watching: globalThis.openTubeRtmp?.status?.().live && liveStats.current ? liveStats.current.viewers : null, bound, version: { name: core.VERSION, build: BUILD, files: FILES }, key: obsCfg.key, port: Number(process.env.RTMP_PORT) || null, ips, publicIp: await publicIp(), listen: globalThis.openTubeRtmp?.status?.().host || process.env.RTMP_HOST || '::', lastConn: globalThis.openTubeRtmp?.lastConn?.() || null, publicHost: process.env.PUBLIC_URL && !process.env.TUNNEL_STATE ? new URL(process.env.PUBLIC_URL).hostname : null,   // a Cloudflare Tunnel carries only web traffic, not RTMP
      channel: obsCfg.channel, title: obsCfg.title,
        linked: !!(obsCfg.channel && engine.derive(obsCfg.channel)?.relays.has(signer.pub)), youtube: { on: obsCfg.youtube.on, set: !!obsCfg.youtube.key }, tile: obsCfg.tile, ffmpeg: ffmpegOk, node: signer.pub, live: globalThis.openTubeRtmp?.status?.() || null };
    }
    else if (c.cmd === 'status') data = { height: engine.main.best.height, peers: peers.size, chains: engine.chains.size, host: globalThis.openTubeHost?.status() || null, obs: globalThis.openTubeRtmp?.status?.() || null };
    else if (!owner && !may(c.user, 'manage')) return fail('you may not manage this computer’s files');
    else if (!globalThis.openTubeHost) return fail('video hosting is off on this node (started with --no-host?)');
    else data = globalThis.openTubeHost.command(c.cmd, c.args || {});
    log(`[control] ${c.cmd} from ${owner ? 'the owner' : obsStore.users[c.user]?.name || c.user.slice(0, 8)}'s console`);
    return signed({ v: 1, kind: 'nodectlr', node: signer.pub, re: c.nonce, at: Date.now(), ok: true, data });
  } catch (e) { return fail(e.message); }
}
async function answerChallenge(q) {
  const host = globalThis.openTubeHost;
  if (!host || !/^[0-9a-f]{40}$/.test(q.ih) || typeof q.nonce !== 'string' || q.nonce.length !== 32) return { kind: 'pproof', has: false };
  const bytes = await host.piece(q.ih, q.piece);
  dbg('challenged on', q.ih.slice(0, 8), 'piece', q.piece, bytes ? bytes.length + ' bytes' : 'no piece');
  if (!bytes) return { kind: 'pproof', has: false };
  const a = { v: 1, kind: 'pproof', has: true, ih: q.ih, piece: q.piece, nonce: q.nonce, node: signer.pub, at: Date.now(), h: await core.pieceProof(q.nonce, signer.pub, bytes) };
  a.sig = await signer.sign(core.proofPreimage(a));
  return a;
}
// Challenge other hosts of the videos this node holds: a random piece, a fresh nonce, an answer within PROOF.answerMs
// that must match what this node computes from its own copy. Passed proofs count as copies (trimming keeps rare videos)
// and earn the prover storage receipts (MB proven, once per video per period), signed by this node at period end.
const provedOnce = new Set();
const dbg = process.env.PROOF_DEBUG ? (...a) => log('[proof-debug]', ...a) : () => {};
const storage = new Map();   // epoch -> Map(provider -> Map(ih -> MB))
async function challengeRound() {
  const host = globalThis.openTubeHost; if (!host) return;
  const mine = host.complete(); if (!mine.length) return;
  const { ih, t } = mine[Math.floor(Math.random() * mine.length)];
  // candidates: nodes seen in this video's swarm (peer IDs come from node keys) and other hosting nodes
  const byPeer = new Map([...engine.nodes.keys()].map(k => [nodePeerId(k), k]));
  const inSwarm = (t.wires || []).map(w => byPeer.get(w.peerId)).filter(Boolean);
  const others = [...engine.nodes.values()].filter(n => n.roles.includes('host') && Date.now() - n.at < 120000).map(n => n.node);
  const targets = [...new Set([...inSwarm, ...others.sort(() => Math.random() - 0.5)])].filter(k => k !== signer.pub).slice(0, 4);
  dbg('round', ih.slice(0, 8), 'nodes', engine.nodes.size, 'swarm', inSwarm.length, 'targets', targets.length);
  for (const key of targets) {
    const index = Math.floor(Math.random() * t.pieces.length), nonce = crypto.randomBytes(16).toString('hex');
    const mineBytes = await host.piece(ih, index); if (!mineBytes) return;
    const expect = await core.pieceProof(nonce, key, mineBytes);
    engine.route(key, { kind: 'pchal', ih, piece: index, nonce }, core.PROOF.answerMs).then(async (a) => {
      dbg('answer from', key.slice(0, 8), a ? JSON.stringify(a).slice(0, 120) : 'none');
      if (!a || a.has === false) return;   // doesn't have it (or didn't answer in time)
      const ok = a.node === key && a.nonce === nonce && a.piece === index && a.h === expect && await core.verify(key, core.proofPreimage(a), a.sig);
      host.noteProof(ih, key, ok);
      if (!ok) { log(`[proof] ${key.slice(0, 8)} failed a storage proof for ${ih.slice(0, 8)}`); return; }
      if (!provedOnce.has(key + ih)) { provedOnce.add(key + ih); log(`[proof] ${key.slice(0, 8)} proved it holds ${ih.slice(0, 8)} (piece ${index})`); }
      const e = core.epochOf(Date.now()), m = storage.get(e) || storage.set(e, new Map()).get(e), v = m.get(key) || m.set(key, new Map()).get(key);
      v.set(ih, Math.max(1, Math.round(t.length / 1e6)));
    });
  }
}
setInterval(() => challengeRound().catch(e => log('[proof]', e.message)), Number(process.env.PROOF_EVERY_MS) || core.PROOF.everyMs);
setInterval(async () => {   // sign storage receipts for finished periods and send them toward the provers
  const now = core.epochOf(Date.now());
  for (const [e, m] of storage) {
    if (e >= now) continue; storage.delete(e);
    for (const [provider, vids] of m) {
      const r = { v: 1, kind: 'receipt', res: 'storage', provider, by: signer.pub, epoch: e, amt: [...vids.values()].reduce((a, b) => a + b, 0) };
      r.sig = await signer.sign(core.receiptPreimage(r));
      for (const p of peers.values()) p.send(JSON.stringify({ t: 'rcpt', r }));
    }
  }
}, 60000);
// Receipts only count from keys that did the starter proof of work, so a node that signs receipts claims its starter once.
setTimeout(async () => {
  if (!globalThis.openTubeHost || engine.ledger.starters.has(signer.pub) || !(MINE || MINE_OWN)) return;
  try { await engine.submitTx({ starter: true }); log('[earn] claimed starter coins for this node’s key (so its receipts count)'); } catch (e) { log('[earn] starter:', e.message); }
}, 20000);

// ---- "your node": started from someone's My node command, it announces itself to their My node page
if (process.env.NODE_GRANT && !(GRANT && core.validNodeGrant(GRANT))) log('NODE_GRANT is not a valid grant; not announcing this node');
else if (GRANT) {
  const os = await import('node:os');
  const baseRoles = (process.env.NODE_ROLES || 'node').split(',').filter(Boolean);
  const rolesNow = () => [...new Set([...baseRoles, ...(isEntry() ? ['entry'] : [])])].slice(0, 8);   // entry once --public's HTTPS is up
  const addrs = () => [...(process.env.PUBLIC_URL ? [process.env.PUBLIC_URL] : []), ...Object.values(os.networkInterfaces()).flat()
    .filter(i => i && i.family === 'IPv4' && !i.internal).map(i => `http://${i.address}:${PORT}`), `http://localhost:${PORT}`].slice(0, 8);
  const announce = async () => {
    let host = globalThis.openTubeHost?.status() || null;
    if (!host) try { host = JSON.parse(fs.readFileSync(process.env.HOST_STATUS_FILE, 'utf8')); if (Date.now() - host.at > 120000) host = { ...host, stale: true }; } catch {}
    const held = globalThis.openTubeHost?.status?.().held || [];
    const info = { ...(held.length ? { has: core.holdBloom(held) } : {}),
      ...(process.env.FLASH_PARENT ? { flash: { parent: process.env.FLASH_PARENT, tag: process.env.FLASH_TAG || null } } : {}), busy: busyWith() || null,
      ...(flash.provider ? { flashes: flash.provider } : {}), public: process.env.PUBLIC_URL || null, tunnel: process.env.TUNNEL_STATE || null, publicIp: (await publicIp()) || null, users: Object.fromEntries(Object.entries(obsStore.users).filter(([, u]) => !(u.until < Date.now())).slice(0, 20).map(([k, u]) => [k, u.can.map(x => x[0]).join('') + (u.guestOf ? 'g' : '')])),   // who may use it: o(bs) h(ost) m(anage)
      version: { name: core.VERSION, build: BUILD, files: FILES }, peerId: nodePeerId(signer.pub), name: (process.env.NODE_LABEL || os.hostname()).slice(0, 60), os: `${os.platform()} ${os.arch()}`, node: process.version, height: engine.main.best.height,
      peers: peers.size, chains: engine.chains.size, site: process.env.NODE_SITE || null, host, obs: globalThis.openTubeRtmp?.status?.() || null };
    // a status must stay under 4000 bytes or no one accepts it: drop the least important parts first
    for (const k of ['has', 'obs', 'host']) if (JSON.stringify(info).length > 3800) delete info[k];
    if (JSON.stringify(info).length > 3800) info.users = Object.fromEntries(Object.entries(info.users).slice(0, 5));
    try { await engine.publishNodeInfo({ grant: GRANT, roles: rolesNow(), addrs: addrs(), info }); } catch (e) { log('announce failed:', e.message); }
  };
  log(`this node belongs to user ${GRANT.user.slice(0, 8)}… (${rolesNow().join(', ')}); it will show up on their My node page`);
  setTimeout(announce, 4000); setInterval(announce, 30000); announceSoon = () => setTimeout(announce, 500);
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
  signerOf: (c, s) => { if (!engine.chains.has(c)) return null; const b = engine.derive(c)?.streams.find(x => x.payload.stream === s); return b ? (b.payload.relay || (b.author !== engine.derive(c).owner ? b.author : null)) : null; },   // the relay named in the live block, or the linked computer that wrote it
  isJoined: (k, c) => !!engine.memberships.get(k)?.joined?.includes(c),
  userOf: (k) => engine.nodes.get(k)?.grant?.user || null,   // a computer counts as its owner (it relays their channels)
  debug: process.env.LIVE_DEBUG ? (...a) => log(...a) : null,
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
  send({ t: 'nodehi', node: signer.pub }, id);
}

// ---- outgoing links to other nodes: entry nodes (public OpenTube computers) first; PEERS (the launcher passes the
// site's own node) is kept only while no entry node answers, so the site isn't needed.
const dialed = new Map();   // url -> { ws, closed }
function dial(url) {
  if (typeof WebSocket !== 'function') return log('PEERS needs Node 22+ (global WebSocket)');
  if (dialed.has(url)) return;
  const id = 'node:' + url, d = { ws: null, closed: false, tries: 0 }; dialed.set(url, d);
  const open = () => {
    let ws; try { ws = d.ws = new WebSocket(url); } catch { return; }
    ws.onopen = () => { d.tries = 0; peers.set(id, { send: (s) => ws.readyState === 1 && ws.send(s), label: url }); log(`linked to ${url}`); engine.peerJoined(id); };
    ws.onmessage = (e) => { try { engine.handle(JSON.parse(e.data), id); } catch {} };
    ws.onclose = () => { peers.delete(id); if (!d.closed) setTimeout(open, Math.min(10 * 60000, 10000 * 2 ** d.tries++)); };
    ws.onerror = () => {};
  };
  open();
}
const undial = (url) => { const d = dialed.get(url); if (!d) return; d.closed = true; try { d.ws?.close(); } catch {} dialed.delete(url); log(`let go of ${url} (an entry node answers)`); };
const ownPublic = () => (process.env.PUBLIC_URL || '').replace(/\/+$/, '');
function entryPeerUrls() {
  const out = [];
  const add = (u) => { u = String(u || '').replace(/\/+$/, ''); if (/^https:\/\//.test(u) && u !== ownPublic()) out.push(u.replace(/^http/, 'ws') + '/ot'); };
  for (const h of entryHubs()) add(h.url.replace(/^ws/, 'http').replace(/\/live$/, ''));
  try { for (const e of JSON.parse(fs.readFileSync(path.join(DIR, 'seeds.json'), 'utf8')).entry || []) add(typeof e === 'string' ? e : e?.url); } catch {}
  return [...new Set(out)];
}
function maintainPeers() {
  const entries = entryPeerUrls();
  for (const u of entries.slice(0, 3)) dial(u);
  const entryUp = entries.some(u => peers.has('node:' + u));
  for (const u of PEERS) { if (entryUp && u === siteHubUrl()?.replace(/\/live$/, '/ot')) undial(u); else if (!entryUp) dial(u); }
}
maintainPeers(); setInterval(maintainPeers, 60000);

// ---- the peer mesh: join the network's WebRTC room like every browser does, so browsers and other nodes connect to
// this node directly (no server in between), introduced by any tracker: the site's, this node's own, public ones.
// Needs the @trystero-p2p/torrent and webtorrent packages (opentube.mjs installs both). MESH=0 turns it off.
async function joinMesh() {   // called once the HTTP server listens (its own tracker must be up)
  try {
    const [{ joinRoom }, { RTCPeerConnection }] = await Promise.all([import('@trystero-p2p/torrent'), import('webrtc-polyfill')]);
    const site = process.env.NODE_SITE;
    let listed = []; try { listed = (await (await fetch('https://raw.githubusercontent.com/ngosang/trackerslist/master/trackers_all_ws.txt', { signal: AbortSignal.timeout(5000) })).text()).split('\n').map(x => x.trim()).filter(x => x.startsWith('wss://')).slice(0, 20); } catch {}
    const urls = [...new Set([...meetPlaces(), ...(engine.CFG.trackers || []), ...listed, site && site.replace(/^http/, 'ws') + '/tracker'].filter(Boolean))];   // the site's last
    const room = joinRoom({ appId: 'opentube-' + engine.MAIN_ID.slice(0, 16), relayConfig: { urls }, rtcPolyfill: RTCPeerConnection, maxReceiveBytes: 16 * 1024 ** 2 }, 'opentube/' + engine.MAIN_ID.slice(0, 32));
    const action = room.makeAction('ot', { onMessage: (data, ctx) => engine.handle(data, 'rtc:' + ctx.peerId).catch(() => {}) });
    room.onPeerJoin = (pid) => {
      const id = 'rtc:' + pid;
      peers.set(id, { send: (s) => action.send(JSON.parse(s), { target: pid }).catch(() => {}), label: 'webrtc ' + pid.slice(0, 6) });
      log(`+ peer ${pid.slice(0, 8)} over WebRTC (${peers.size} connected)`); engine.peerJoined(id);
      send({ t: 'nodehi', node: signer.pub }, id);
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
  if (url.pathname === '/entries.json') {   // the entry nodes this node knows right now (from signed node statuses): how browsers and the seed list stay current
    const fresh = [...engine.nodes.values()].filter(n => n.roles?.includes('entry') && Date.now() - n.at < 15 * 60000)
      .map(n => ({ node: n.node, url: (n.addrs || []).find(a => /^https:\/\//.test(a)) || null, at: n.at })).filter(x => x.url);
    if (isEntry() && /^https:/.test(process.env.PUBLIC_URL || '') && !fresh.some(x => x.node === signer.pub)) fresh.unshift({ node: signer.pub, url: process.env.PUBLIC_URL.replace(/\/+$/, ''), at: Date.now() });
    res.writeHead(200, { 'content-type': 'application/json', 'access-control-allow-origin': '*', 'cache-control': 'max-age=60' });
    return res.end(JSON.stringify({ network: engine.MAIN_ID, at: Date.now(), entry: fresh.slice(0, 50) }));
  }
  if (url.pathname === '/seeds.json') {   // the entry nodes browsers try first (served by this node too, for self-hosted sites)
    res.writeHead(200, { 'content-type': 'application/json', 'access-control-allow-origin': '*' });
    try { return res.end(fs.readFileSync(path.join(DIR, 'seeds.json'))); } catch { return res.end('{"entry":[]}'); }
  }
  if (url.pathname === '/sw.js') {  // WebTorrent's streaming service worker: lets videos play while downloading
    res.writeHead(200, { 'content-type': 'text/javascript; charset=utf-8', 'cache-control': 'no-cache' });
    return res.end(fs.readFileSync(path.join(DIR, 'sw.js')));
  }
  if (url.pathname.startsWith('/ingest/') && (req.method === 'POST' || req.method === 'PUT')) return ingest(req, res, url.pathname.slice(8));
  if (url.pathname === '/status') {
    res.writeHead(200, { 'content-type': 'application/json', 'access-control-allow-origin': '*' });
    return res.end(JSON.stringify({ version: core.VERSION, build: BUILD, network: engine.MAIN_ID, height: engine.main.best.height, tip: engine.main.best.hash, registered: engine.directory.size, pending: engine.mempool.size, chains: engine.chains.size, peers: peers.size, mining: !!engine.miner, node: signer.pub, now: Date.now(), ranker: RANKER_ID, tracker: tracker.stats(), live: liveHub.stats(), liveStreams: [...liveHub.live().map(x => ({ ...x, hub: isEntry() ? signer.pub : null })), ...padStreams().filter(x => !liveHub.live().some(y => y.stream === x.stream))].slice(0, 100), ingest: true, memberships: engine.memberships.size }));
  }
  res.writeHead(404); res.end('not found');
}
function upgrade(req, socket) {
  const p = new URL(req.url, 'http://x').pathname;
  if (p === '/ot') acceptSocket(req, socket, 'node');
  else if (p === '/tracker') acceptSocket(req, socket, 'tracker');
  else if (p === '/live') acceptSocket(req, socket, 'live');
  else if (p === '/rtmp' && process.env.RTMP_PORT) rtmpOverWs(req, socket);
  else socket.destroy();
}
// OBS through the public address: RTMP carried inside a WebSocket (binary frames both ways), handed to this computer's
// own OBS receiver, so OBS works through a Cloudflare Tunnel (web traffic only) too. The stream key is checked as usual.
// On the OBS side: node opentube.mjs --obs-via=<this computer's https address>, then OBS → rtmp://127.0.0.1/live.
function rtmpOverWs(req, socket) {
  const key = req.headers['sec-websocket-key']; if (!key) return socket.destroy();
  socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${crypto.createHash('sha1').update(key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64')}\r\n\r\n`);
  socket.setNoDelay(true);
  const tcp = net.connect(Number(process.env.RTMP_PORT), '127.0.0.1'); tcp.setNoDelay(true);
  const frame = (op, payload) => { const len = payload.length;
    const head = len < 126 ? Buffer.from([0x80 | op, len]) : len < 65536 ? Buffer.from([0x80 | op, 126, len >> 8, len & 255]) : (() => { const b = Buffer.alloc(10); b[0] = 0x80 | op; b[1] = 127; b.writeBigUInt64BE(BigInt(len), 2); return b; })();
    if (!socket.destroyed) socket.write(Buffer.concat([head, payload])); };
  tcp.on('data', (d) => frame(2, d));
  const end = () => { tcp.destroy(); socket.destroy(); };
  tcp.on('close', end); tcp.on('error', end); socket.on('close', end); socket.on('error', end);
  let buf = Buffer.alloc(0);
  socket.on('data', (chunk) => {
    buf = Buffer.concat([buf, chunk]);
    for (;;) {
      if (buf.length < 2) return;
      const op = buf[0] & 0x0f, masked = buf[1] & 0x80; let len = buf[1] & 0x7f, off = 2;
      if (len === 126) { if (buf.length < 4) return; len = buf.readUInt16BE(2); off = 4; }
      else if (len === 127) { if (buf.length < 10) return; len = Number(buf.readBigUInt64BE(2)); off = 10; }
      if (len > 16 * 1024 * 1024) return end();
      const need = off + (masked ? 4 : 0) + len; if (buf.length < need) return;
      let payload = buf.subarray(off + (masked ? 4 : 0), need);
      if (masked) { const m = buf.subarray(off, off + 4); payload = Buffer.from(payload); for (let i = 0; i < payload.length; i++) payload[i] ^= m[i & 3]; }
      buf = buf.subarray(need);
      if (op === 8) return end();
      if (op === 9) { frame(10, payload); continue; }
      if (op === 2 || op === 0) tcp.write(payload);
    }
  });
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
