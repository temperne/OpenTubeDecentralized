// Video hosting for a Node home node, no browser needed: keeps the videos of the channels a user follows in a folder
// and shares them with viewers' browsers over WebRTC (WebTorrent), announcing on the site's tracker.
//
// Started by server.mjs when VIDEO_DIR and HOST_USER are set (opentube.mjs does that). It follows HOST_USER's signed
// list of joined channels (published from their browser; their own channels are included), downloads every paid video
// of those channels into <VIDEO_DIR>/<Channel name>/ (same layout as the browser's video folder, with channel.json), and
// keeps sharing them. Needs the `webtorrent` package (opentube.mjs installs it into ~/.opentube).
//
// The owner can change what it keeps from any console they're logged into (My node → Manage files), with signed
// commands (server.mjs checks them): pin a video (never trimmed), drop one (deleted, not downloaded again), keep extra
// channels or single videos, stop keeping a channel, and change the size limit. These overrides live in
// <VIDEO_DIR>/.opentube-node.json. Trimming prefers removing videos that other hosts have proven they hold, and never
// removes the last proven copy unless the owner drops it.
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import path from 'node:path';

const cleanName = (s, fallback = 'Untitled') => String(s || '').replace(/[\\/:*?"<>|\u0000-\u001f\u007f]/g, ' ').replace(/\s+/g, ' ').trim().replace(/^\.+/, '').slice(0, 100).trim() || fallback;

// Public trackers from github.com/ngosang/trackerslist: WebSocket ones (shared with browsers) and the best UDP/HTTP ones.
export async function publicTrackers() {
  const get = async (f) => { try { const r = await fetch('https://raw.githubusercontent.com/ngosang/trackerslist/master/' + f, { signal: AbortSignal.timeout(5000) }); return (await r.text()).split('\n').map(x => x.trim()).filter(Boolean); } catch { return []; } };
  const [ws, best] = await Promise.all([get('trackers_all_ws.txt'), get('trackers_best.txt')]);
  return [...ws.slice(0, 20), ...best.slice(0, 20)];
}
const MIN_COPIES = 2;   // other proven copies a video needs before trimming may remove ours

export async function startHost({ engine, nodeKey, user, users = () => [], videoDir, site, capGB = 5, tabs = null, log = console.log }) {
  const { default: WebTorrent } = await import('webtorrent');
  // DHT and TCP/UDP trackers too: ordinary BitTorrent apps can share these videos with this node, and it bridges them to browsers.
  const { createHash } = await import('node:crypto');
  const peerId = nodeKey ? createHash('sha1').update('opentube-node:' + nodeKey).digest('hex') : undefined;   // browsers recognize this node's bytes
  // WebRTC, so browsers can swarm with this node (they only speak BitTorrent over WebRTC). Without it the node only
  // reaches ordinary BitTorrent apps (TCP/UDP, DHT).
  const wrtc = await import('webrtc-polyfill').then(m => ({ RTCPeerConnection: m.RTCPeerConnection, RTCSessionDescription: m.RTCSessionDescription, RTCIceCandidate: m.RTCIceCandidate })).catch(() => null);
  // Node's WebRTC never reports the end of its candidates, so simple-peer waits its full 5 s before sending each offer or
  // answer; by then the browser has paired with us the other way round, and each side drops one of the two
  // connections as a duplicate (opposite ones), so none survives. Our candidates are gathered within milliseconds:
  // stop waiting after 300 ms. (Patched in the copy WebTorrent itself loads.)
  if (wrtc) try {
    const { createRequire } = await import('node:module'), { pathToFileURL } = await import('node:url');
    let dir = path.dirname(fs.realpathSync(createRequire(import.meta.url).resolve('webtorrent')));
    while (path.basename(dir) !== 'webtorrent' && dir !== path.dirname(dir)) dir = path.dirname(dir);   // the webtorrent package folder
    const peerPath = [path.join(dir, 'node_modules/@thaunknown/simple-peer/lite.js'), path.join(path.dirname(dir), '@thaunknown/simple-peer/lite.js')].find(p => fs.existsSync(p));
    if (!peerPath) throw new Error('simple-peer not found next to webtorrent');
    const { default: Peer } = await import(pathToFileURL(peerPath).href);
    const start = Peer.prototype._startIceCompleteTimeout;
    if (start && !Peer.prototype._otQuick) { Peer.prototype._otQuick = true; Peer.prototype._startIceCompleteTimeout = function () { this.iceCompleteTimeout = Math.min(this.iceCompleteTimeout || 5000, 300); return start.call(this); }; }
  } catch (e) { log('[host] couldn’t shorten the WebRTC wait (browsers may connect slowly):', e.message); }
  if (!wrtc) log('[host] webrtc-polyfill missing: browsers can’t download from this node over BitTorrent (npm install webrtc-polyfill)');
  const client = new WebTorrent({ dht: true, lsd: false, natUpnp: false, natPmp: false, peerId, ...(wrtc ? { tracker: { wrtc } } : {}) });
  client.on('error', (e) => log('[host] webtorrent:', e.message));
  if (tabs) {   // pay per piece (core: createPieceTabs): viewers pay this node for the pieces it sends, and it pays for what it downloads
    client.on('add', (t) => tabs.attach(t));
    setInterval(() => tabs.tick(client.torrents).catch(e => log('[host] tabs:', e.message)), 4000);
  }
  const listed = await publicTrackers();
  const trackers = [...new Set([site ? site.replace(/^http/, 'ws') + '/tracker' : null, ...(engine.CFG.trackers || []), ...listed].filter(Boolean))];
  await fsp.mkdir(videoDir, { recursive: true });
  const folders = new Map();   // chain -> { dir, name, meta }
  const torrents = new Map();  // infohash -> { chain, t }

  // ---- the owner's overrides
  const ovFile = path.join(videoDir, '.opentube-node.json');
  const ov = Object.assign({ pinned: [], dropped: [], channels: [], unchannels: [], videos: [], capGB: null }, (() => { try { return JSON.parse(fs.readFileSync(ovFile, 'utf8')); } catch { return {}; } })());
  const saveOv = () => fsp.writeFile(ovFile, JSON.stringify(ov, null, 2)).catch(e => log('[host] overrides:', e.message));
  const cap = () => (ov.capGB ?? capGB) * 1e9;

  // ---- who else holds each video, from storage proofs (server.mjs challenges other hosts and reports here)
  const proven = new Map();   // infohash -> Map(key -> time of last passed proof)
  const copies = (ih) => {
    const m = proven.get(ih), fresh = m ? [...m.values()].filter(at => Date.now() - at < 2 * 3600000).length : 0;
    const seeds = torrents.get(ih)?.t.wires?.filter(w => w.isSeeder).length || 0;
    return { proven: fresh, seeds, others: Math.max(fresh, seeds) };
  };

  // ---- channel folders (same layout as the browser's video folder)
  for (const name of await fsp.readdir(videoDir).catch(() => [])) {
    try {
      const meta = JSON.parse(await fsp.readFile(path.join(videoDir, name, 'channel.json'), 'utf8'));
      if (meta?.app === 'OpenTube' && meta.network === engine.MAIN_ID && meta.chain) { meta.videos ||= {}; folders.set(meta.chain, { dir: path.join(videoDir, name), name, meta }); }
    } catch {}
  }
  const saveMeta = (f) => fsp.writeFile(path.join(f.dir, 'channel.json'), JSON.stringify(f.meta, null, 2)).catch(e => log('[host] channel.json:', e.message));
  const making = new Map();   // chain -> promise: videos starting together share one folder creation
  function folderFor(chain) {
    if (folders.has(chain)) return Promise.resolve(folders.get(chain));
    if (!making.has(chain)) making.set(chain, makeFolder(chain).finally(() => making.delete(chain)));
    return making.get(chain);
  }
  async function makeFolder(chain) {
    const d = engine.derive(chain), title = d?.profile?.name || 'Channel ' + chain.slice(0, 8);
    let name = cleanName(title, 'Channel');
    if (fs.existsSync(path.join(videoDir, name))) name = `${name} (${chain.slice(0, 8)})`;
    const f = { dir: path.join(videoDir, name), name, meta: { app: 'OpenTube', note: 'Videos of this OpenTube channel, kept and shared by your OpenTube node.', network: engine.MAIN_ID, chain, key: d?.owner || null, name: title, videos: {} } };
    await fsp.mkdir(path.join(f.dir, '.opentube'), { recursive: true });
    folders.set(chain, f); await saveMeta(f);
    return f;
  }

  // ---- one video: share it if the file is here, otherwise download it, then keep sharing
  async function ensure(chain, block) {
    const v = block.payload, ih = v.infohash;
    if (torrents.has(ih) || ov.dropped.includes(ih)) return;
    const f = await folderFor(chain);
    const entry = f.meta.videos[ih] || (f.meta.videos[ih] = { file: cleanName(v.file, 'video'), name: v.file, title: v.title, size: v.size, complete: false, ts: Date.now() });
    const torrentFile = path.join(f.dir, '.opentube', ih + '.torrent');
    const saved = fs.existsSync(torrentFile) ? await fsp.readFile(torrentFile) : null;
    const opts = { path: f.dir, announce: trackers, skipVerify: !!(saved && entry.complete && fs.existsSync(path.join(f.dir, entry.file))) };
    const t = client.add(saved || `magnet:?xt=urn:btih:${ih}&dn=${encodeURIComponent(v.file)}`, opts);
    torrents.set(ih, { chain, t });
    if (opts.skipVerify) log(`[host] sharing “${v.title}” from ${f.name}/${entry.file}`);
    else log(`[host] ${entry.complete ? 'checking' : 'downloading'} “${v.title}” into ${f.name}/`);
    t.on('error', (e) => { log(`[host] ${entry.file}: ${e.message}`); torrents.delete(ih); });
    t.once('metadata', async () => {
      // the file is written at <channel folder>/<name in the torrent>; refuse names that would leave the folder
      const target = path.resolve(f.dir, t.files[0]?.path || '');
      if (!target.startsWith(path.resolve(f.dir) + path.sep)) { log(`[host] refused ${ih}: bad file name in torrent`); t.destroy(); torrents.delete(ih); return; }
      entry.file = path.relative(f.dir, target);
      if (!saved) fsp.writeFile(torrentFile, t.torrentFile).catch(() => {});
      saveMeta(f);
    });
    t.once('done', () => {
      if (!entry.complete) log(`[host] saved “${v.title}” to ${f.name}/`);
      entry.complete = true; entry.size = t.length; entry.ts = entry.ts || Date.now(); saveMeta(f); trim(chain);
    });
  }
  function remove(ih, why) {   // stop sharing and delete the file
    const x = torrents.get(ih); if (x) { x.t.destroy(); torrents.delete(ih); }
    for (const f of folders.values()) {
      const e = f.meta.videos[ih]; if (!e) continue;
      fsp.rm(path.join(f.dir, e.file), { force: true }).catch(() => {}); fsp.rm(path.join(f.dir, '.opentube', ih + '.torrent'), { force: true }).catch(() => {});
      delete f.meta.videos[ih]; saveMeta(f); log(`[host] removed “${e.title || e.file}” (${why})`);
    }
  }
  // Keep each channel under the size limit. Never trimmed: your own channels, pinned videos, and videos without
  // MIN_COPIES other proven copies (the network would lose them). Among the rest, the most widely held go first.
  function trim(chain) {
    const f = folders.get(chain); if (!f) return;
    if (ownChannels().has(chain)) return;
    const vids = Object.entries(f.meta.videos);
    let total = vids.reduce((n, [, e]) => n + (e.size || 0), 0);
    if (total <= cap()) { f.overCap = false; return; }
    const removable = vids.filter(([ih]) => !ov.pinned.includes(ih) && copies(ih).others >= MIN_COPIES)
      .sort((a, b) => copies(b[0]).others - copies(a[0]).others || (a[1].ts || 0) - (b[1].ts || 0));
    for (const [ih, e] of removable) { if (total <= cap()) break; total -= e.size || 0; remove(ih, `over the ${cap() / 1e9} GB limit for ${f.name}; ${copies(ih).others} other copies exist`); }
    f.overCap = total > cap();   // what's left is pinned or rare: kept, and shown to the owner
  }
  // the owner's joined and own channels, plus those of the people the owner shared this computer with (for hosting)
  const membership = () => {
    const all = [user, ...users()].map(u => { const m = engine.memberships.get(u); if (!m) engine.requestMembership?.(u); return m; }).filter(Boolean);
    if (!all.length) return null;
    return { joined: [...new Set(all.flatMap(m => m.joined || []))], own: [...new Set(all.flatMap(m => m.own || []))], at: Math.max(...all.map(m => m.at || 0)) };
  };
  const ownChannels = () => new Set(membership()?.own || []);
  const wanted = () => new Set([...(membership()?.joined || []), ...ov.channels, ...ov.videos.map(v => v.chain)].filter(c => !ov.unchannels.includes(c)));
  function follow() {
    const m = membership();
    if (!m) engine.requestMembership?.(user);
    const want = wanted(), extraOnly = new Set(ov.videos.map(v => v.chain).filter(c => !(m?.joined || []).includes(c) && !ov.channels.includes(c)));
    for (const chain of want) {
      const c = engine.openChain(chain); if (!c) continue;
      if (!c.stats?.lastRequest || Date.now() - c.stats.lastRequest > 60000) engine.requestChain(c);
      const d = engine.derive(chain); if (!d || d.kind !== 'channel') continue;
      for (const b of d.videos) {
        if (!engine.postPaid(b)) continue;
        if (extraOnly.has(chain) && !ov.videos.some(v => v.ih === b.payload.infohash)) continue;   // only the single videos asked for
        ensure(chain, b).catch(e => log('[host]', e.message));
      }
    }
    for (const [ih, x] of torrents) if (!want.has(x.chain) && !ov.pinned.includes(ih)) { x.t.destroy(); torrents.delete(ih); }   // left: stop sharing (files stay)
  }
  setTimeout(follow, 3000); setInterval(follow, 30000);
  log(`[host] hosting for user ${user.slice(0, 8)}… in ${videoDir} (${trackers.length} trackers + DHT)`);

  // ---- storage proofs: read one piece of a video this node has completely
  function piece(ih, index) {
    const t = torrents.get(ih)?.t;
    if (!t?.done || !Number.isSafeInteger(index) || index < 0 || index >= t.pieces.length) return Promise.resolve(null);
    const length = index === t.pieces.length - 1 ? t.lastPieceLength : t.pieceLength;
    return new Promise((res) => t.store.get(index, { offset: 0, length }, (err, buf) => res(err ? null : new Uint8Array(buf))));
  }
  const complete = () => [...torrents.entries()].filter(([, x]) => x.t.done).map(([ih, x]) => ({ ih, chain: x.chain, t: x.t }));

  // ---- the owner's file manager (commands arrive signed and checked by server.mjs)
  function files() {
    const m = membership(), joined = new Set(m?.joined || []), own = ownChannels();
    const chans = [...new Set([...folders.keys(), ...wanted()])].map(chain => {
      const f = folders.get(chain), d = engine.derive(chain);
      const vids = Object.entries(f?.meta.videos || {}).map(([ih, e]) => {
        const t = torrents.get(ih)?.t, c = copies(ih);
        return { ih, title: e.title, file: e.file, size: e.size || t?.length || 0, complete: !!e.complete, progress: t ? Math.round(t.progress * 100) : e.complete ? 100 : 0,
          sharing: !!t, peers: t?.numPeers || 0, pinned: ov.pinned.includes(ih), proven: c.proven, seeds: c.seeds };
      });
      return { chain, name: f?.meta.name || d?.profile?.name || chain.slice(0, 8), folder: f?.name || null, own: own.has(chain), joined: joined.has(chain),
        extra: ov.channels.includes(chain), stopped: ov.unchannels.includes(chain), overCap: !!f?.overCap,
        used: vids.reduce((n, v) => n + (v.complete ? v.size : 0), 0), videos: vids };
    });
    return { videoDir, capGB: ov.capGB ?? capGB, minCopies: MIN_COPIES, dropped: ov.dropped, channels: chans };
  }
  const isHash = (x) => typeof x === 'string' && /^[0-9a-f]{40}$/.test(x);
  const isChain = (x) => typeof x === 'string' && /^[0-9a-f]{64}$/.test(x);
  const toggle = (list, x, on) => { const i = list.indexOf(x); if (on && i < 0) list.push(x); if (!on && i >= 0) list.splice(i, 1); };
  function command(cmd, a = {}) {
    switch (cmd) {
      case 'files': return files();
      case 'pin': if (!isHash(a.ih)) throw new Error('bad video'); toggle(ov.pinned, a.ih, a.on !== false); break;
      case 'drop':   // delete it here and don't download it again (undo with restore)
        if (!isHash(a.ih)) throw new Error('bad video'); toggle(ov.dropped, a.ih, true); toggle(ov.pinned, a.ih, false);
        ov.videos = ov.videos.filter(v => v.ih !== a.ih); remove(a.ih, 'dropped by the owner'); break;
      case 'restore': if (!isHash(a.ih)) throw new Error('bad video'); toggle(ov.dropped, a.ih, false); break;
      case 'keepChannel':   // host a channel on this node even if you haven't joined it (on: false undoes)
        if (!isChain(a.chain)) throw new Error('bad channel'); toggle(ov.channels, a.chain, a.on !== false); if (a.on !== false) toggle(ov.unchannels, a.chain, false); break;
      case 'stopChannel':   // stop hosting a channel on this node, though you're still a member (files stay until removed)
        if (!isChain(a.chain)) throw new Error('bad channel'); toggle(ov.unchannels, a.chain, a.on !== false); if (a.on !== false) toggle(ov.channels, a.chain, false); break;
      case 'keepVideo':
        if (!isChain(a.chain) || !isHash(a.ih)) throw new Error('bad video');
        if (a.on === false) ov.videos = ov.videos.filter(v => v.ih !== a.ih); else if (!ov.videos.some(v => v.ih === a.ih)) ov.videos.push({ chain: a.chain, ih: a.ih });
        toggle(ov.dropped, a.ih, false); break;
      case 'removeChannelFiles': {   // delete the files of a channel this node no longer hosts
        if (!isChain(a.chain)) throw new Error('bad channel');
        const f = folders.get(a.chain); if (!f) break;
        for (const ih of Object.keys(f.meta.videos)) if (!torrents.has(ih) || !wanted().has(a.chain)) remove(ih, 'removed by the owner');
        break;
      }
      case 'cap': { const gb = Number(a.gb); if (!(gb > 0 && gb <= 100000)) throw new Error('bad limit'); ov.capGB = gb; break; }
      default: throw new Error('unknown command ' + cmd);
    }
    saveOv(); follow(); for (const c of folders.keys()) trim(c);
    return files();
  }
  // ---- live recordings: an OBS stream this node received is written into the channel's folder and shared like any
  // video. The channel's owner then publishes it (a video block) or discards it (deleted here).
  async function recordingPath(chain, title, ext = '.mp4') {
    const f = await folderFor(chain), base = cleanName(title, 'Live');
    let name = base + ext;
    for (let i = 2; fs.existsSync(path.join(f.dir, name)); i++) name = `${base} (${i})${ext}`;
    return { full: path.join(f.dir, name), file: name };
  }
  async function seedRecording(chain, file, title, stream) {
    const f = await folderFor(chain), full = path.join(f.dir, file);
    return new Promise((res, rej) => {
      client.seed(full, { announce: trackers, name: file }, (t) => {
        torrents.set(t.infoHash, { chain, t });
        f.meta.videos[t.infoHash] = { file, name: file, title, size: t.length, complete: true, own: true, live: stream, ts: Date.now() };
        if (!ov.pinned.includes(t.infoHash)) { ov.pinned.push(t.infoHash); saveOv(); }   // kept until published or discarded
        fsp.writeFile(path.join(f.dir, '.opentube', t.infoHash + '.torrent'), t.torrentFile).catch(() => {});
        saveMeta(f); log(`[host] recorded “${title}” to ${f.name}/${file} (${(t.length / 1e6).toFixed(1)} MB), sharing it`);
        res({ infohash: t.infoHash, size: t.length, file });
      }).on?.('error', rej);
    });
  }
  function settleRecordings() {   // the owner discarded a recording: delete it; published: it's an ordinary video now
    for (const [chain, f] of folders) {
      const d = engine.derive(chain); if (!d?.liveEnds) continue;
      for (const [ih, e] of Object.entries(f.meta.videos)) {
        if (!e.live) continue;
        const outcome = d.liveEnds.get(e.live);
        if (outcome === 'discarded') { ov.pinned = ov.pinned.filter(x => x !== ih); saveOv(); remove(ih, 'the owner discarded this recording'); delete f.meta.videos[ih]; saveMeta(f); }
        else if (outcome === 'published') { delete e.live; saveMeta(f); }
      }
    }
  }
  setInterval(settleRecordings, 15000);
  // "keep this video": someone allowed to use this computer uploaded from a browser without a video folder. This
  // computer downloads it from their browser right away (no waiting for the posting fee to be confirmed) and keeps it.
  async function storeVideo(chain, ih) {
    const c = engine.openChain(chain); if (c) engine.requestChain(c);
    for (let i = 0; i < 60; i++) {
      const b = engine.derive(chain)?.videos?.find(v => v.payload.infohash === ih);
      if (b) {
        if (!ov.videos.some(v => v.ih === ih)) ov.videos.push({ chain, ih });
        if (!ov.pinned.includes(ih)) ov.pinned.push(ih);
        ov.dropped = ov.dropped.filter(x => x !== ih); saveOv();
        await ensure(chain, b); return true;
      }
      await new Promise(r => setTimeout(r, 500));
    }
    throw new Error('this computer hasn’t received the video’s block yet: try again in a moment');
  }
  const progressOf = (ih) => { const t = torrents.get(ih)?.t; return t ? { progress: t.progress, peers: t.numPeers, done: !!t.done, size: t.length || null } : null; };
  // live chunks (see liveChunkPreimage in the core): a few seconds of a stream as one small torrent, shared for 6 hours
  function seedChunk(buf, name) {
    return new Promise((res, rej) => {
      const t = client.seed(buf, { announce: trackers, name }, (t) => { setTimeout(() => t.destroy(), 6 * 3600000); res(t.infoHash); });
      t?.on?.('error', rej);
    });
  }
  return {
    recordingPath, seedRecording, seedChunk, storeVideo, progressOf,
    follow, piece, complete, command, files,
    noteProof(ih, key, ok) { const m = proven.get(ih) || proven.set(ih, new Map()).get(ih); if (ok) m.set(key, Date.now()); else m.delete(key); },
    status: () => {
      const list = [...torrents.values()].map(x => x.t);
      return { at: Date.now(), videoDir, channels: wanted().size, found: !!membership(), hosting: list.filter(t => t.done).length,
        hostedBytes: list.filter(t => t.done).reduce((n, t) => n + t.length, 0), downloading: list.filter(t => !t.done).length,
        peers: list.reduce((n, t) => n + t.numPeers, 0), up: Math.round(client.uploadSpeed), down: Math.round(client.downloadSpeed),
        overCap: [...folders.values()].some(f => f.overCap) };
    }
  };
}
