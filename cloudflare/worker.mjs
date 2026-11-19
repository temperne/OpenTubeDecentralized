// OpenTube home node on Cloudflare: the page is served as a static asset, and
// a single Durable Object runs the node:
//   /ot       blockchain node (WebSocket)
//   /tracker  WebTorrent tracker, so browsers find each other for video
//   /status   JSON status
// Deploy with `npx wrangler deploy` from the repository root.
import core from './core.gen.mjs';

// How often the node republishes its ranking (seconds). Override with a RANK_EVERY_SECONDS variable.
const rankEvery = (env) => Math.max(10, Number(env.RANK_EVERY_SECONDS) || 300) * 1000;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === '/ot' || url.pathname === '/tracker' || url.pathname === '/status') {
      // One node for the whole network: every browser talks to the same Durable Object.
      return env.NODE.get(env.NODE.idFromName('main')).fetch(request);
    }
    return env.ASSETS.fetch(request);
  }
};

// Proof of work with WebCrypto, used only to register this node's own ranking list once.
function mine(body, bits, onProgress) {
  let cancelled = false;
  const enc = new TextEncoder(), prefix = core.preimage(body);
  const lz = (d) => { let z = 0; for (const b of d) { if (b === 0) { z += 8; continue; } return z + Math.clz32(b) - 24; } return z; };
  const promise = (async () => {
    for (let n = Math.floor(Math.random() * 1e9), count = 0; !cancelled; n++, count++) {
      if (lz(new Uint8Array(await crypto.subtle.digest('SHA-256', enc.encode(prefix + n)))) >= bits) return n;
      if (count % 20000 === 0) onProgress?.(count);
    }
    return new Promise(() => {});
  })();
  return { promise, cancel: () => { cancelled = true; } };
}

export class OpenTubeNode {
  constructor(ctx, env) {
    this.ctx = ctx;
    this.env = env;
    this.tracker = core.createTracker();
    this.ready = this.ctx.blockConcurrencyWhile(() => this.init());
  }

  meta(k, v) {
    const sql = this.ctx.storage.sql;
    if (v === undefined) { const r = sql.exec('SELECT v FROM meta WHERE k = ?', k).toArray()[0]; return r ? JSON.parse(r.v) : null; }
    sql.exec('INSERT OR REPLACE INTO meta (k, v) VALUES (?, ?)', k, JSON.stringify(v));
  }

  async init() {
    const sql = this.ctx.storage.sql;
    sql.exec('CREATE TABLE IF NOT EXISTS blocks (hash TEXT PRIMARY KEY, chain TEXT NOT NULL, body TEXT NOT NULL)');
    sql.exec('CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT NOT NULL)');
    sql.exec('CREATE TABLE IF NOT EXISTS memberships (author TEXT PRIMARY KEY, body TEXT NOT NULL)');
    let identity = this.meta('identity');
    if (!identity) { identity = await core.newIdentity(); this.meta('identity', identity); }
    let ranker = this.meta('ranker');
    if (!ranker) { ranker = { key: await core.newIdentity() }; this.meta('ranker', ranker); }
    this.ranker = ranker;
    const rankerSigner = await core.signer(ranker.key);
    this.engine = await core.createEngine({
      signer: await core.signer(identity),
      keyFor: (pub) => pub === rankerSigner.pub ? rankerSigner : null,
      send: (msg, to) => this.send(msg, to),
      mine,                // shouldMine defaults to this node's own keys: only its ranking list
      trackAll: true,      // keep every channel chain, not just the main chain
      relayWatch: true,    // pass ad watch-group messages between browsers
      // Keep everyone's latest joined-channel list so headless hosts can follow their owner.
      onMembership: (m) => sql.exec('INSERT OR REPLACE INTO memberships (author, body) VALUES (?, ?)', m.author, JSON.stringify(m)),
      persist: (chain, b) => sql.exec('INSERT OR IGNORE INTO blocks (hash, chain, body) VALUES (?, ?, ?)', b.hash, chain, JSON.stringify(b)),
      // New videos/votes: rerank in ~20 s instead of waiting for the periodic alarm.
      onBlock: (chain, b) => { if (this.loaded && chain !== this.engine?.main && ['video', 'vote', 'hide'].includes(b.payload.type)) this.soonRank(); }
    });
    // Replay stored blocks: main chain first, since it holds the other chains' genesis blocks.
    const byChain = new Map();
    for (const row of sql.exec('SELECT chain, body FROM blocks ORDER BY rowid')) {
      (byChain.get(row.chain) || byChain.set(row.chain, []).get(row.chain)).push(JSON.parse(row.body));
    }
    const E = this.engine;
    await E.insert(E.MAIN_ID, byChain.get(E.MAIN_ID) || [], null, false);
    for (const [chain, blocks] of byChain) if (chain !== E.MAIN_ID && E.openChain(chain)) await E.insert(chain, blocks, null, false);
    for (const row of sql.exec('SELECT body FROM memberships')) await E.acceptMembership(JSON.parse(row.body));
    if (ranker.genesis) {
      if (!E.directory.has(ranker.genesis.hash)) { await E.addToMempool(ranker.genesis); E.openChain(ranker.genesis.hash); E.maybeMine(); }
      const last = this.meta('ranking'); if (last) await E.acceptRanking(last, false);
    }
    this.loaded = true;
  }

  async soonRank() {
    const at = Date.now() + 20000, cur = await this.ctx.storage.getAlarm();
    if (!cur || cur > at) await this.ctx.storage.setAlarm(at);
  }

  // Create this node's ranking list the first time someone visits (we need the host name for it).
  async ensureRanker(host) {
    if (this.ranker.genesis) return;
    const signer = await core.signer(this.ranker.key);
    this.ranker.genesis = await this.engine.createGenesis({ type: 'genesis', kind: 'list', name: `Trending on ${host}`.slice(0, 60), about: 'Videos ranked by votes and freshness, computed by this home node.', ranking: true }, signer);
    this.meta('ranker', this.ranker);
    await this.ctx.storage.setAlarm(Date.now() + 10000);
  }

  // Publishes this node's ranking. Never throws: Cloudflare stops retrying alarms that keep failing,
  // which would silence the ranking for good, so errors are recorded and the next run is always scheduled.
  async alarm() {
    await this.ready;
    const id = this.ranker.genesis?.hash;
    let next = 30000;
    try {
      if (id && this.engine.directory.has(id)) {
        const r = await this.engine.publishRanking(id, this.engine.rank());
        this.meta('ranking', r);
        this.meta('rankingStatus', { at: Date.now(), items: r.items.length, error: null });
        next = rankEvery(this.env);
      } else if (id) {
        this.engine.maybeMine();  // registration still pending (e.g. after an eviction)
      }
    } catch (e) {
      this.meta('rankingStatus', { ...(this.meta('rankingStatus') || {}), errorAt: Date.now(), error: String(e?.message || e) });
    } finally {
      await this.ctx.storage.setAlarm(Date.now() + next);
    }
  }
  // Self-healing: if the ranking timer is missing for any reason, start it again.
  async ensureAlarm() {
    if (this.ranker.genesis && !(await this.ctx.storage.getAlarm())) await this.ctx.storage.setAlarm(Date.now() + 5000);
  }

  send(msg, to) {
    const data = JSON.stringify(msg);
    for (const ws of this.ctx.getWebSockets()) {
      if (to && ws.deserializeAttachment()?.id !== to) continue;
      try { ws.send(data); } catch {}
    }
  }

  async fetch(request) {
    await this.ready;
    const E = this.engine, url = new URL(request.url);
    await this.ensureRanker(url.host);
    await this.ensureAlarm();
    if (url.pathname === '/status') {
      return Response.json({
        network: E.MAIN_ID, now: Date.now(), height: E.main.best.height, tip: E.main.best.hash, registered: E.directory.size,
        pending: E.mempool.size, chains: E.chains.size, peers: this.ctx.getWebSockets().length,
        ranker: this.ranker.genesis?.hash || null, ranking: this.meta('rankingStatus'), nextAlarm: await this.ctx.storage.getAlarm(),
        tracker: this.tracker.stats(), memberships: E.memberships.size
      }, { headers: { 'access-control-allow-origin': '*' } });
    }
    if (request.headers.get('upgrade') !== 'websocket') return new Response('expected a WebSocket', { status: 426 });
    const { 0: client, 1: server } = new WebSocketPair();
    const id = crypto.randomUUID();
    if (url.pathname === '/tracker') {
      // Tracker sockets stay in memory (no hibernation): swarm membership lives here.
      server.accept();
      this.tracker.attach(id, (s) => server.send(s));
      server.addEventListener('message', (e) => { try { this.tracker.message(id, JSON.parse(e.data)); } catch {} });
      const bye = () => this.tracker.detach(id);
      server.addEventListener('close', bye); server.addEventListener('error', bye);
      return new Response(null, { status: 101, webSocket: client });
    }
    // Blockchain sockets use the hibernation API: idle connections cost nothing.
    this.ctx.acceptWebSocket(server);
    server.serializeAttachment({ id });
    E.peerJoined(id);
    return new Response(null, { status: 101, webSocket: client });
  }

  async webSocketMessage(ws, message) {
    await this.ready;
    if (typeof message !== 'string' || message.length > 16 * 1024 * 1024) return;
    let msg; try { msg = JSON.parse(message); } catch { return; }
    await this.engine.handle(msg, ws.deserializeAttachment()?.id);
  }

  webSocketClose(ws, code) { try { ws.close(code === 1005 ? 1000 : code, 'bye'); } catch {} }
  webSocketError() {}
}
