// OpenTube home node on Cloudflare: the page is served as a static asset, and
// a single Durable Object runs the node behind wss://<your-worker>/ot.
// Deploy with `npx wrangler deploy` from the repository root.
import core from './core.gen.mjs';

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === '/ot' || url.pathname === '/status') {
      // One node for the whole network: every browser talks to the same Durable Object.
      return env.NODE.get(env.NODE.idFromName('main')).fetch(request);
    }
    return env.ASSETS.fetch(request);
  }
};

export class OpenTubeNode {
  constructor(ctx, env) {
    this.ctx = ctx;
    this.env = env;
    this.ready = this.ctx.blockConcurrencyWhile(() => this.init());
  }

  async init() {
    const sql = this.ctx.storage.sql;
    sql.exec('CREATE TABLE IF NOT EXISTS blocks (hash TEXT PRIMARY KEY, chain TEXT NOT NULL, body TEXT NOT NULL)');
    sql.exec('CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT NOT NULL)');
    let identity = sql.exec("SELECT v FROM meta WHERE k = 'identity'").toArray()[0]?.v;
    if (!identity) {
      identity = JSON.stringify(await core.newIdentity());
      sql.exec("INSERT INTO meta (k, v) VALUES ('identity', ?)", identity);
    }
    this.engine = await core.createEngine({
      signer: await core.signer(JSON.parse(identity)),
      send: (msg, to) => this.send(msg, to),
      mine: null,          // Workers bill CPU time; browsers mine their own registrations
      trackAll: true,      // keep every channel chain, not just the main chain
      persist: (chain, b) => sql.exec('INSERT OR IGNORE INTO blocks (hash, chain, body) VALUES (?, ?, ?)', b.hash, chain, JSON.stringify(b))
    });
    // Replay stored blocks: main chain first, since it holds the other chains' genesis blocks.
    const byChain = new Map();
    for (const row of sql.exec('SELECT chain, body FROM blocks ORDER BY rowid')) {
      (byChain.get(row.chain) || byChain.set(row.chain, []).get(row.chain)).push(JSON.parse(row.body));
    }
    const E = this.engine;
    await E.insert(E.MAIN_ID, byChain.get(E.MAIN_ID) || [], null, false);
    for (const [chain, blocks] of byChain) if (chain !== E.MAIN_ID && E.openChain(chain)) await E.insert(chain, blocks, null, false);
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
    const E = this.engine;
    if (new URL(request.url).pathname === '/status') {
      return Response.json({
        network: E.MAIN_ID, height: E.main.best.height, tip: E.main.best.hash, registered: E.directory.size,
        pending: E.mempool.size, chains: E.chains.size, peers: this.ctx.getWebSockets().length
      }, { headers: { 'access-control-allow-origin': '*' } });
    }
    if (request.headers.get('upgrade') !== 'websocket') return new Response('expected a WebSocket', { status: 426 });
    const { 0: client, 1: server } = new WebSocketPair();
    // Hibernation API: Cloudflare keeps the sockets open without keeping this object awake.
    this.ctx.acceptWebSocket(server);
    const id = crypto.randomUUID();
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
