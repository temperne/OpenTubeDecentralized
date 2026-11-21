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
log(`OpenTube ${core.VERSION} (build ${BUILD}) · network ${engine.MAIN_ID.slice(0, 16)}… · main height ${engine.main.best.height} · ${engine.directory.size} registered · ${engine.chains.size} chains loaded`);
setInterval(() => engine.gossip(), 60000);   // repair pass; new blocks are pushed right away
// The public feed (core: SQUARE): keep this month's chain from the start (older months open as peers mention them).
const openSquare = () => { for (const kind of ['square', 'mail']) { const id = engine.squareId(core.monthKey(Date.now()), kind); if (id && !engine.chains.has(id)) engine.requestChain(engine.openChain(id)); } };   // and this month's private messages
if (!MAIN_ONLY) { openSquare(); setInterval(openSquare, 3600000); }

// ---- OBS "Start Streaming" (RTMP_PORT): OBS → rtmp://localhost/live → this node → the live hub. The owner first clicks
// Go live → Stream from OBS on the site and picks this computer; that live block names this node's key as `relay`.
function liveSink(chain, stream, mime) {
  let n = 0, o = 0, queue = Promise.resolve(), ended = false;
  const seg = (bytes, k) => (queue = queue.then(async () => {
    const g = { stream, n: n++, o, h: crypto.createHash('sha256').update(bytes).digest('hex') };
    o += bytes.length;
    g.sig = await signer.sign(core.liveSegPreimage(g)); g.d = bytes.toString('base64'); if (k) g.k = 1;
    send(g); engine.liveMesh.publish(g);   // and over the mesh: viewers who want it get it without any hub
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
// OBS settings for this computer (set from the owner's console): the stream key OBS must use, the channel it goes live
// on (the owner links this computer to the channel with a `relay` block), the title, YouTube forwarding, and tiles.
const obsFile = path.join(DATA, 'obs.json');
// One OBS profile per person who uses this computer (the owner, and people the owner shared it with): their own stream
// key, so the computer knows who is streaming and goes live on that person's channel.
const newStreamKey = () => 'ot-' + crypto.randomBytes(12).toString('base64url');
const obsStore = (() => { try { return JSON.parse(fs.readFileSync(obsFile, 'utf8')); } catch { return {}; } })();
if (!obsStore.profiles) { const { profiles, users, ...legacy } = obsStore; obsStore.profiles = legacy.key ? { [GRANT?.user || 'owner']: legacy } : {}; for (const k of Object.keys(legacy)) delete obsStore[k]; }
obsStore.users ||= {};   // user key -> { can: ['obs', 'host', 'manage'], name }
const saveObs = () => fs.writeFileSync(obsFile, JSON.stringify(obsStore, null, 2), { mode: 0o600 });
function profileOf(user) {
  const p = obsStore.profiles[user] ||= {};
  Object.assign(p, { key: p.key || newStreamKey(), channel: p.channel ?? null, title: p.title || 'Live', youtube: p.youtube || { key: '', on: false }, tile: p.tile || { on: false, n: 4 } });
  return p;
}
const nodeUsers = () => obsStore.users;
// a channel someone may go live on from here: theirs (its key is theirs, or bound to their identity)
const ownsChannel = (user, ch) => { const d = ch && engine.derive(ch); return !!d && (d.owner === user || engine.ledger.resolve(d.owner) === engine.ledger.resolve(user)); };
const may = (user, what) => user === GRANT?.user || !!obsStore.users[user]?.can?.includes(what);
if (GRANT) profileOf(GRANT.user);
saveObs();
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
function youtubeOut(log, obsCfg) {   // forward the stream (fMP4, H.264 + AAC) to YouTube, unchanged
  if (!obsCfg.youtube.on || !obsCfg.youtube.key || !ffmpegOk) return null;
  const ff = spawn('ffmpeg', ['-hide_banner', '-loglevel', 'error', '-f', 'mp4', '-i', 'pipe:0', '-c', 'copy', '-f', 'flv', 'rtmp://a.rtmp.youtube.com/live2/' + obsCfg.youtube.key], { stdio: ['pipe', 'ignore', 'pipe'] });
  ff.stderr.on('data', d => log('[youtube]', String(d).trim().slice(0, 200))); ff.stdin.on('error', () => {});
  log('[youtube] forwarding the stream to YouTube');
  return { write: (b) => { try { ff.stdin.write(b); } catch {} }, end: () => { try { ff.stdin.end(); } catch {} } };
}
// Go live on the linked channel: writes the live block (as the channel's relay), then feeds the mesh/hub and YouTube.
function goLiveSink(log, obsCfg) {
  let real = null, yt = youtubeOut(log, obsCfg), queued = [], stream = null, chain = null;
  return {
    async init(bytes, mime) {
      const armedOld = obsCfg.owner ? armed?.() : null;   // the older "Go live → pick this computer" flow (owner only)
      if (armedOld) { chain = armedOld.chain; stream = armedOld.b.payload.stream; mime = armedOld.b.payload.mime; }
      else {
        chain = obsCfg.channel; if (!chain || !engine.chains.get(chain)) throw new Error('no channel linked to this computer');
        stream = crypto.randomBytes(32).toString('hex');
        await engine.append(chain, { type: 'live', title: (obsCfg.title || 'Live').slice(0, 140), stream, mime });
        log(`[obs] live on ${engine.derive(chain)?.profile.name || chain.slice(0, 8)}: “${obsCfg.title}”`);
      }
      real = liveSink(chain, stream, mime); real.init(bytes); yt?.write(bytes);
      for (const [b, k] of queued) { real.fragment(b, k); yt?.write(b); } queued = [];
    },
    fragment(bytes, k) { if (real) { real.fragment(bytes, k); yt?.write(bytes); } else queued.push([bytes, k]); },
    end() { real?.end(); yt?.end(); if (stream && !armed?.()) engine.append(chain, { type: 'liveend', stream, outcome: 'ended' }).catch(() => {}); log('[obs] stream ended'); }
  };
}
// Tiles: several OBS streams (keys <key>, <key>-2, <key>-3, <key>-4) combined by ffmpeg into one picture (2 → side by
// side, 3-4 → 2×2), audio mixed. It starts when every slot has connected, or 20 s after the first with those present.
function tileSlot(n, log, obsCfg) {
  const tiles = obsCfg._tiles ||= { slots: new Map(), ff: null, timer: null };   // each person's group stream is their own
  const slot = { n, chunks: [], pipe: null, ended: false };
  tiles.slots.set(n, slot);
  const startTiles = () => {
    if (tiles.ff) return; clearTimeout(tiles.timer);
    const list = [...tiles.slots.values()].filter(s => !s.ended).sort((a, b) => a.n - b.n); if (!list.length) return;
    const k = list.length, lay = { 1: '0_0', 2: '0_0|w0_0', 3: '0_0|w0_0|0_h0', 4: '0_0|w0_0|0_h0|w0_h0' }[k];
    const vf = list.map((s, i) => `[${i}:v]scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2,setsar=1[v${i}]`).join(';')
      + ';' + list.map((s, i) => `[v${i}]`).join('') + (k > 1 ? `xstack=inputs=${k}:layout=${lay}[v]` : 'copy[v]')
      + ';' + list.map((s, i) => `[${i}:a]`).join('') + (k > 1 ? `amix=inputs=${k}[a]` : 'anull[a]');
    const args = ['-hide_banner', '-loglevel', 'error', ...list.flatMap((s, i) => ['-f', 'mp4', '-i', 'pipe:' + (3 + i)]), '-filter_complex', vf, '-map', '[v]', '-map', '[a]',
      '-c:v', 'libx264', '-preset', 'veryfast', '-tune', 'zerolatency', '-profile:v', 'high', '-level', '4.0', '-g', '60', '-c:a', 'aac', '-b:a', '128k',
      '-f', 'mp4', '-movflags', 'frag_keyframe+empty_moov+default_base_moof', 'pipe:1'];
    const ff = tiles.ff = spawn('ffmpeg', args, { stdio: ['ignore', 'pipe', 'pipe', ...list.map(() => 'pipe')] });
    ff.stderr.on('data', d => log('[tiles]', String(d).trim().slice(0, 200)));
    const out = goLiveSink(log, obsCfg); let first = true;
    ff.stdout.on('data', mp4Boxes((init) => out.init(init, 'video/mp4; codecs="avc1.640028,mp4a.40.2"').catch(e => log('[tiles]', e.message)), (frag) => out.fragment(frag, first ? (first = false, true) : false)));
    ff.on('exit', () => { out.end(); tiles.ff = null; tiles.slots.clear(); log('[tiles] ended'); });
    list.forEach((s, i) => { s.pipe = ff.stdio[3 + i]; s.pipe.on('error', () => {}); for (const c of s.chunks) s.pipe.write(c); s.chunks = []; });
    log(`[tiles] combining ${k} stream(s)`);
  };
  if (!tiles.timer && !tiles.ff) tiles.timer = setTimeout(startTiles, 20000);
  const write = (b) => { if (slot.pipe) { try { slot.pipe.write(b); } catch {} } else slot.chunks.push(b); };
  return {
    init(bytes) { write(bytes); if (tiles.slots.size >= obsCfg.tile.n) startTiles(); },
    fragment(bytes) { write(bytes); },
    end() { slot.ended = true; try { slot.pipe?.end(); } catch {} if ([...tiles.slots.values()].every(s => s.ended)) { tiles.ff?.kill('SIGTERM'); tiles.slots.clear(); clearTimeout(tiles.timer); tiles.timer = null; } }
  };
}
var armed = null, announceSoon = null;
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
    const rtmp = startRtmp({ port: Number(process.env.RTMP_PORT), host: process.env.RTMP_HOST || '::', log: (...a) => log('[obs]', ...a),
      onPublish: (key) => {
        key = String(key || '').trim();
        let user = null, slot = 0;   // whose key is it? the key itself, or key-2…key-4 for tiles
        for (const [u, pr] of Object.entries(obsStore.profiles)) { if (!may(u, 'obs')) continue; const s_ = key === pr.key ? 1 : [2, 3, 4].find(i => key === pr.key + '-' + i) || 0; if (s_) { user = u; slot = s_; break; } }
        if (!slot) return 'Wrong stream key. Copy it from Go live (or Devices) on the site.';
        const obsCfg = Object.assign(profileOf(user), { owner: user === GRANT?.user });
        log(`[obs] stream from ${obsStore.users[user]?.name || (obsCfg.owner ? 'the owner' : user.slice(0, 8))}`);
        if (obsCfg.tile.on) { if (!ffmpegOk) return 'Tiles need ffmpeg on this computer.'; if (slot > obsCfg.tile.n) return `Only ${obsCfg.tile.n} tiles are set up.`; return tileSlot(slot, log, obsCfg); }
        if (slot > 1) return 'Tiles are off on this computer: use the main stream key.';
        if (obsCfg.channel && !obsCfg.owner && !ownsChannel(user, obsCfg.channel)) return 'That channel isn’t yours.';
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
    globalThis.openTubeHost = await startHost({ engine, nodeKey: signer.pub, tabs: pieceTabs(), users: () => Object.entries(obsStore.users).filter(([, u]) => u.can.includes('host')).map(([k]) => k), user: process.env.HOST_USER, videoDir: path.resolve(process.env.VIDEO_DIR), site: process.env.NODE_SITE || `http://localhost:${PORT}`, capGB: Number(process.env.CAP_GB) || 5, log });
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
async function onRoute(body, reply) {
  if (!body || typeof body !== 'object') return;
  if (body.kind === 'nodectl') return reply(await control(body));
  if (body.kind === 'pchal') return reply(await answerChallenge(body));
}
async function signed(x) { x.sig = await signer.sign(core.ctlPreimage(x)); return x; }
async function control(c) {
  const fail = (error) => signed({ v: 1, kind: 'nodectlr', node: signer.pub, re: c.nonce, at: Date.now(), ok: false, error });
  if (c.v !== 1 || c.node !== signer.pub || typeof c.nonce !== 'string' || c.nonce.length > 40 || typeof c.cmd !== 'string') return fail('bad command');
  if (!GRANT || (c.user !== GRANT.user && !obsStore.users[c.user])) return fail('this computer isn’t yours, and it isn’t shared with you');
  if (Math.abs(Date.now() - c.at) > 2 * 60000 || ctlSeen.has(c.nonce)) return fail('stale or repeated command');
  if (!(await core.verify(c.user, core.ctlPreimage(c), c.sig))) return fail('bad signature');
  ctlSeen.set(c.nonce, Date.now()); for (const [k, at] of ctlSeen) if (Date.now() - at > 5 * 60000) ctlSeen.delete(k);
  try {
    let data;
    const owner = c.user === GRANT.user;
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
        if (a.channel !== undefined) { if (a.channel && !ownsChannel(c.user, a.channel)) return fail('that channel isn’t yours'); obsCfg.channel = /^[0-9a-f]{64}$/.test(a.channel) ? a.channel : null; }
        if (typeof a.title === 'string') obsCfg.title = a.title.slice(0, 140) || 'Live';
        if (a.youtube) { if (typeof a.youtube.key === 'string') obsCfg.youtube.key = a.youtube.key.trim().slice(0, 200); if (typeof a.youtube.on === 'boolean') obsCfg.youtube.on = a.youtube.on; }
        if (a.tile) { if (typeof a.tile.on === 'boolean') obsCfg.tile.on = a.tile.on; if ([2, 3, 4].includes(a.tile.n)) obsCfg.tile.n = a.tile.n; }
        if (obsCfg.channel && !engine.chains.has(obsCfg.channel)) engine.requestChain(engine.openChain(obsCfg.channel));
      }
      saveObs();
      const os = await import('node:os');
      const ips = Object.values(os.networkInterfaces()).flat().filter(i => i && i.family === 'IPv4' && !i.internal).map(i => i.address);
      if (c.cmd === 'obs' && a.test) {   // can OBS reach us from the internet? connect to our own public address and do the RTMP greeting
        const pub = await publicIp(), port = Number(process.env.RTMP_PORT);
        data = { test: pub ? await rtmpProbe(pub, port) : { ok: false, error: 'couldn’t find this computer’s public address' }, publicIp: pub };
        return signed({ v: 1, kind: 'nodectlr', node: signer.pub, re: c.nonce, at: Date.now(), ok: true, data });
      }
      data = { version: { name: core.VERSION, build: BUILD }, key: obsCfg.key, port: Number(process.env.RTMP_PORT) || null, ips, publicIp: await publicIp(), listen: globalThis.openTubeRtmp?.status?.().host || process.env.RTMP_HOST || '::', lastConn: globalThis.openTubeRtmp?.lastConn?.() || null, publicHost: process.env.PUBLIC_URL ? new URL(process.env.PUBLIC_URL).hostname : null, channel: obsCfg.channel, title: obsCfg.title,
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
  const roles = (process.env.NODE_ROLES || 'node').split(',').filter(Boolean).slice(0, 8);
  const addrs = () => [...(process.env.PUBLIC_URL ? [process.env.PUBLIC_URL] : []), ...Object.values(os.networkInterfaces()).flat()
    .filter(i => i && i.family === 'IPv4' && !i.internal).map(i => `http://${i.address}:${PORT}`), `http://localhost:${PORT}`].slice(0, 8);
  const announce = async () => {
    let host = globalThis.openTubeHost?.status() || null;
    if (!host) try { host = JSON.parse(fs.readFileSync(process.env.HOST_STATUS_FILE, 'utf8')); if (Date.now() - host.at > 120000) host = { ...host, stale: true }; } catch {}
    const info = { users: Object.fromEntries(Object.entries(obsStore.users).slice(0, 20).map(([k, u]) => [k, u.can.map(x => x[0]).join('')])),   // who may use it: o(bs) h(ost) m(anage)
      version: { name: core.VERSION, build: BUILD }, peerId: nodePeerId(signer.pub), name: (process.env.NODE_LABEL || os.hostname()).slice(0, 60), os: `${os.platform()} ${os.arch()}`, node: process.version, height: engine.main.best.height,
      peers: peers.size, chains: engine.chains.size, site: process.env.NODE_SITE || null, host, obs: globalThis.openTubeRtmp?.status?.() || null };
    try { await engine.publishNodeInfo({ grant: GRANT, roles, addrs: addrs(), info }); } catch (e) { log('announce failed:', e.message); }
  };
  log(`this node belongs to user ${GRANT.user.slice(0, 8)}… (${roles.join(', ')}); it will show up on their My node page`);
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
  if (url.pathname === '/sw.js') {  // WebTorrent's streaming service worker: lets videos play while downloading
    res.writeHead(200, { 'content-type': 'text/javascript; charset=utf-8', 'cache-control': 'no-cache' });
    return res.end(fs.readFileSync(path.join(DIR, 'sw.js')));
  }
  if (url.pathname.startsWith('/ingest/') && (req.method === 'POST' || req.method === 'PUT')) return ingest(req, res, url.pathname.slice(8));
  if (url.pathname === '/status') {
    res.writeHead(200, { 'content-type': 'application/json', 'access-control-allow-origin': '*' });
    return res.end(JSON.stringify({ version: core.VERSION, build: BUILD, network: engine.MAIN_ID, height: engine.main.best.height, tip: engine.main.best.hash, registered: engine.directory.size, pending: engine.mempool.size, chains: engine.chains.size, peers: peers.size, mining: !!engine.miner, node: signer.pub, now: Date.now(), ranker: RANKER_ID, tracker: tracker.stats(), live: liveHub.stats(), liveStreams: liveHub.live().slice(0, 50), ingest: true, memberships: engine.memberships.size }));
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
