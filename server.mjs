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
// streams sent into it) combined by ffmpeg into one 1280×720 picture, laid out by how many are connected (1 full,
// 2 side by side, 3–4 in 2×2, 5–9 in 3×3), audio mixed. When someone joins or leaves the picture is rebuilt, but it
// stays the same live stream (a new start segment, timestamps continuing) and the recording is joined into one file.
const GROUP_MAX = 9;
function groupOf(hostCfg) { return hostCfg._group ||= { inputs: new Map(), ff: null, timer: null, out: null, t0: 0, restart: false, log: null }; }
function groupLayout(k) {
  const cols = k <= 1 ? 1 : k <= 4 ? 2 : 3, rows = Math.ceil(k / cols), W = 1280, H = 720, cw = Math.floor(W / cols / 2) * 2, ch = Math.floor(H / rows / 2) * 2;
  return { cols, rows, cw, ch, pos: [...Array(k)].map((_, i) => `${(i % cols) * cw}_${Math.floor(i / cols) * ch}`) };
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
function groupStart(hostCfg) {
  const g = groupOf(hostCfg), log = g.log || console.log;
  if (g.ff) return; clearTimeout(g.timer); g.timer = null;
  if (!g.enc) absorbHostLive(hostCfg);   // first start: bring in the host's ongoing stream
  const list = [...g.inputs.values()].filter(x => !x.ended && x.init).sort((a, b) => a.at - b.at).slice(0, GROUP_MAX); if (!list.length) return;
  const k = list.length, L = groupLayout(k);
  const vf = list.map((x, i) => `[${i}:v]setpts=PTS-STARTPTS,scale=${L.cw}:${L.ch}:force_original_aspect_ratio=decrease,pad=${L.cw}:${L.ch}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30[v${i}]`).join(';')
    + ';' + (k > 1 ? list.map((x, i) => `[v${i}]`).join('') + `xstack=inputs=${k}:layout=${L.pos.join('|')}:fill=black,pad=1280:720:0:0:black[v]` : `[v0]pad=1280:720:(ow-iw)/2:(oh-ih)/2:black[v]`)
    + ';' + list.map((x, i) => `[${i}:a]asetpts=PTS-STARTPTS,aresample=48000[a${i}]`).join(';') + ';' + list.map((x, i) => `[a${i}]`).join('') + (k > 1 ? `amix=inputs=${k}:normalize=0[a]` : 'anull[a]');
  // stage 1, the picture: rebuilt whenever someone joins or leaves, output as MPEG-TS (which can be strung together)
  const args = ['-hide_banner', '-loglevel', 'error', ...list.flatMap((x, i) => ['-thread_queue_size', '1024', '-f', x.fmt === 'webm' ? 'matroska' : 'mp4', '-i', 'pipe:' + (3 + i)]), '-filter_complex', vf, '-map', '[v]', '-map', '[a]',
    // an intermediate only stage 2 reads: MPEG-2 at high quality costs far less CPU than a second H.264 encode
    '-c:v', 'mpeg2video', '-q:v', '3', '-g', '15', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '160k', '-ar', '48000', '-f', 'mpegts', 'pipe:1'];
  const ff = g.ff = spawn('ffmpeg', args, { stdio: ['ignore', 'pipe', 'pipe', ...list.map(() => 'pipe')] });
  ff.stderr.on('data', d => log('[group]', String(d).trim().slice(0, 200)));
  // stage 2, the stream: one encoder for the whole session, timestamps from the clock, so viewers, the recording and
  // YouTube see one continuous stream however often the picture is rebuilt
  if (!g.enc) {
    g.out = goLiveSink(log, hostCfg); g.t0 = Date.now();
    const enc = g.enc = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-use_wallclock_as_timestamps', '1', '-fflags', '+genpts+discardcorrupt', '-f', 'mpegts', '-i', 'pipe:0',
      '-vf', 'fps=30', '-af', 'aresample=async=1000', '-c:v', 'libx264', '-preset', 'superfast', '-tune', 'zerolatency', '-profile:v', 'high', '-level', '4.0', '-g', '30', '-keyint_min', '30', '-sc_threshold', '0',   // a keyframe (a fragment) every second: smoother delivery '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '128k', '-ar', '48000',
      '-f', 'mp4', '-movflags', 'frag_keyframe+empty_moov+default_base_moof', 'pipe:1'], { stdio: ['pipe', 'pipe', 'pipe'] });
    enc.stdin.on('error', () => {}); enc.stderr.on('data', d => log('[group]', String(d).trim().slice(0, 200)));
    const out = g.out; let first = true;
    enc.stdout.on('data', mp4Boxes((init) => out.init(init, 'video/mp4; codecs="avc1.640028,mp4a.40.2"').catch(e => log('[group]', e.message)), (frag) => out.fragment(frag, true)));   // frag_keyframe: every fragment starts with a keyframe, so a viewer can start at any of them
    enc.on('exit', () => { out.end(); if (g.enc === enc) { g.enc = null; g.out = null; g.t0 = 0; } log('[group] ended'); });
  }
  const enc = g.enc;
  ff.stdout.on('data', (d) => { try { enc.stdin.write(d); } catch {} });
  ff.on('exit', (code) => {
    g.ff = null; for (const x of g.inputs.values()) x.pipe = null;
    const left = [...g.inputs.values()].filter(x => !x.ended);
    if ((g.restart || code) && left.length) { g.restart = false; for (const x of left) x.chunks = []; setTimeout(() => groupStart(hostCfg), 500); }   // rebuilt picture, same stream
    else { g.restart = false; for (const [id, x] of g.inputs) if (x.ended) g.inputs.delete(id); if (!left.length) { try { enc.stdin.end(); } catch {} } }
  });
  list.forEach((x, i) => { x.pipe = ff.stdio[3 + i]; x.pipe.on('error', () => {}); x.pipe.write(x.init); for (const c of x.chunks) x.pipe.write(c); x.chunks = []; });
  log(`[group] ${k} in the picture: ${list.map(x => x.label).join(', ')}`);
}
function groupRebuild(hostCfg, why) { const g = groupOf(hostCfg); if (!g.ff) return groupStart(hostCfg); (g.log || console.log)(`[group] ${why}: rebuilding the picture`); g.restart = true; g.ff.kill('SIGTERM'); }
// One input (an OBS connection, or a browser stream this node pulls): returns the sink its data goes to.
function groupInput(hostCfg, { id, label, user, kind, fmt = 'mp4', close = null }, log) {
  const g = groupOf(hostCfg); g.log = log;
  const old = g.inputs.get(id); if (old && !old.ended) { old.ended = true; try { old.pipe?.end(); } catch {} try { old.close?.(); } catch {} }   // reconnecting replaces itself
  if (!old && [...g.inputs.values()].filter(x => !x.ended).length >= GROUP_MAX) return `The group picture is full (${GROUP_MAX}).`;
  const x = { id, label, user, kind, fmt, at: Date.now(), init: null, chunks: [], pipe: null, ended: false, close };
  g.inputs.set(id, x);
  const write = (b) => { if (x.pipe) { try { x.pipe.write(b); } catch {} } else { x.chunks.push(b); if (x.chunks.length > 600) x.chunks.splice(0, 300); } };
  return x.sink = {
    input: x,
    init(bytes) { x.init = bytes; if (g.ff) return groupRebuild(hostCfg, `${label} joined`); if (!g.timer) g.timer = setTimeout(() => groupStart(hostCfg), 2500); },   // a moment for others connecting at the same time
    fragment(bytes) { write(bytes); },
    end() {
      if (x.ended) return; x.ended = true; try { x.pipe?.end(); } catch {}
      const left = [...g.inputs.values()].filter(y => !y.ended);
      if (!left.length) { g.ff?.kill('SIGTERM'); clearTimeout(g.timer); g.timer = null; if (!g.ff) { try { g.enc?.stdin.end(); } catch {} g.inputs.clear(); } }   // the encoder finishes the stream
      else if (g.ff) groupRebuild(hostCfg, `${label} left`);
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
        inputs: [...g.inputs.values()].filter(x => !x.ended).map(x => ({ id: x.id, label: x.label, user: x.user, kind: x.kind, since: x.at, guest: !!obsStore.users[x.user]?.guestOf })),
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
    const info = { ...(held.length ? { has: core.holdBloom(held) } : {}), public: process.env.PUBLIC_URL || null, tunnel: process.env.TUNNEL_STATE || null, publicIp: (await publicIp()) || null, users: Object.fromEntries(Object.entries(obsStore.users).filter(([, u]) => !(u.until < Date.now())).slice(0, 20).map(([k, u]) => [k, u.can.map(x => x[0]).join('') + (u.guestOf ? 'g' : '')])),   // who may use it: o(bs) h(ost) m(anage)
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
