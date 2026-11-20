// Video hosting for a Node home node, no browser needed: keeps the videos of the channels a user follows in a folder
// and shares them with viewers' browsers over WebRTC (WebTorrent), announcing on the site's tracker.
//
// Started by server.mjs when VIDEO_DIR and HOST_USER are set (opentube.mjs does that). It follows HOST_USER's signed
// list of joined channels (published from their browser; their own channels are included), downloads every paid video
// of those channels into <VIDEO_DIR>/<Channel name>/ (same layout as the browser's video folder, with channel.json), and
// keeps sharing them. Needs the `webtorrent` package (opentube.mjs installs it into ~/.opentube).
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
export async function startHost({ engine, nodeKey, user, videoDir, site, capGB = 5, log = console.log }) {
  const { default: WebTorrent } = await import('webtorrent');
  // DHT and TCP/UDP trackers too: ordinary BitTorrent apps can share these videos with this node, and it bridges them to browsers.
  const { createHash } = await import('node:crypto');
  const peerId = nodeKey ? createHash('sha1').update('opentube-node:' + nodeKey).digest('hex') : undefined;   // browsers recognize this node's bytes
  const client = new WebTorrent({ dht: true, lsd: false, natUpnp: false, natPmp: false, peerId });
  client.on('error', (e) => log('[host] webtorrent:', e.message));
  const listed = await publicTrackers();
  const trackers = [...new Set([site ? site.replace(/^http/, 'ws') + '/tracker' : null, ...(engine.CFG.trackers || []), ...listed].filter(Boolean))];
  await fsp.mkdir(videoDir, { recursive: true });
  const folders = new Map();   // chain -> { dir, name, meta }
  const torrents = new Map();  // infohash -> { chain, t }

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
    if (torrents.has(ih)) return;
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
  // Keep each channel under the size limit, newest first (nothing is trimmed from the user's own channels).
  function trim(chain) {
    const f = folders.get(chain); if (!f) return;
    if (engine.derive(chain)?.owner && ownChannels().has(chain)) return;
    let total = 0;
    for (const [ih, e] of Object.entries(f.meta.videos).sort((a, b) => (b[1].ts || 0) - (a[1].ts || 0))) {
      total += e.size || 0;
      if (total > capGB * 1e9) {
        const x = torrents.get(ih); if (x) { x.t.destroy(); torrents.delete(ih); }
        fsp.rm(path.join(f.dir, e.file), { force: true }).catch(() => {}); fsp.rm(path.join(f.dir, '.opentube', ih + '.torrent'), { force: true }).catch(() => {});
        delete f.meta.videos[ih]; saveMeta(f); log(`[host] removed “${e.title || e.file}” (over the ${capGB} GB limit for ${f.name})`);
      }
    }
  }
  const membership = () => engine.memberships.get(user);
  const ownChannels = () => new Set(membership()?.own || []);
  function follow() {
    const m = membership();
    if (!m) { engine.requestMembership?.(user); return; }
    const want = new Set(m.joined);
    for (const chain of want) {
      const c = engine.openChain(chain); if (!c) continue;
      if (!c.stats?.lastRequest || Date.now() - c.stats.lastRequest > 60000) engine.requestChain(c);
      const d = engine.derive(chain); if (!d || d.kind !== 'channel') continue;
      for (const b of d.videos) if (engine.postPaid(b)) ensure(chain, b).catch(e => log('[host]', e.message));
    }
    for (const [ih, x] of torrents) if (!want.has(x.chain)) { x.t.destroy(); torrents.delete(ih); }   // left: stop sharing (files stay)
  }
  setTimeout(follow, 3000); setInterval(follow, 30000);
  log(`[host] hosting for user ${user.slice(0, 8)}… in ${videoDir} (${trackers.length} trackers + DHT)`);
  return {
    follow,
    status: () => {
      const list = [...torrents.values()].map(x => x.t);
      return { at: Date.now(), videoDir, channels: membership()?.joined.length || 0, found: !!membership(), hosting: list.filter(t => t.done).length,
        hostedBytes: list.filter(t => t.done).reduce((n, t) => n + t.length, 0), downloading: list.filter(t => !t.done).length,
        peers: list.reduce((n, t) => n + t.numPeers, 0), up: Math.round(client.uploadSpeed), down: Math.round(client.downloadSpeed) };
    }
  };
}
