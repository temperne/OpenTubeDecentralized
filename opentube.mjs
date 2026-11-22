#!/usr/bin/env node
// OpenTube on your computer. Started by the command on the site's My node page ("Run OpenTube on your computer"),
// which downloads this file from the site and runs it. Each start it downloads the site's current
// server.mjs, headless.mjs, rtmp.mjs and index.html into ~/.opentube/app, then runs:
//
//   node     a full node that syncs with the site and shows up under "Your nodes" on your My node page
//   host     keeps your joined and own channels' videos in a folder and shares them with viewers (WebTorrent,
//            installed into ~/.opentube on first run; --browser-host uses headless Chromium instead)
//   obs      an OBS receiver: OBS → Settings → Stream → Custom, server rtmp://127.0.0.1/live (or this computer's address),
//            stream key from the site (Devices → 🎥 OBS & streaming),
//            then pick this computer under Go live on the site
//
// Options (after `--` in the command):
//   --site=URL        the site (set by the command)
//   --grant=...       your signed grant: this node is yours (set by the command; keep it private)
//   --videos=PATH     the video folder (default: ~/OpenTube Videos)
//   --name=TEXT       what to call this computer on My node (default: its hostname)
//   --port=8090       the local node's HTTP port (default: 8090, or the next free one)
//   --obs-port=1935   the port OBS streams to (use one your server's firewall lets in, e.g. 80 if nothing else uses it)
//   --no-host --no-obs --no-node   leave a part out
//   --mine            also spend CPU confirming everyone's registrations (by default the node confirms only its own
//                     transactions, such as the receipts it's paid for)
//   --cap-gb=5        storage limit per channel you host (your own channels are never trimmed)
//   --tunnel          a public HTTPS address through Cloudflare Tunnel: no open ports, certificate or account needed
//                     (https://<name>.trycloudflare.com; it changes when the tunnel restarts). With a Cloudflare account:
//                     --tunnel-token=<token> --public-url=https://your.host for a fixed address. OBS (RTMP) can't go
//                     through a tunnel: it still needs its port.
//   --public          make this node public over HTTPS as <your-ip>.sslip.io with a Let's Encrypt certificate, so browsers
//                     can use it as a node and tracker (needs ports 80 and 443; on Linux run as root). --public=your.domain
//                     uses your own domain. --email=you@example.com for Let's Encrypt notices.
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import net from 'node:net';
import { spawn, spawnSync } from 'node:child_process';

const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const args = Object.fromEntries(process.argv.slice(1).filter(a => a.startsWith('--')).map(a => { const i = a.indexOf('='); return i < 0 ? [a.slice(2), true] : [a.slice(2, i), a.slice(i + 1)]; }));
const major = Number(process.versions.node.split('.')[0]);
if (major < 22) { console.error(`OpenTube needs Node.js 22 or newer (you have ${process.version}). Get it from https://nodejs.org`); process.exit(1); }
// --obs-via=<https address of an OpenTube computer>: only a bridge for OBS. It listens on 127.0.0.1 (port 1935, or
// --obs-port) and carries the RTMP stream inside a WebSocket to that computer's public address, so OBS reaches a
// computer behind a Cloudflare Tunnel (which carries web traffic only) or a firewall that blocks port 1935.
if (args['obs-via']) {
  const net = await import('node:net');
  let to; try { to = new URL(String(args['obs-via'])); to.protocol = to.protocol === 'http:' ? 'ws:' : 'wss:'; to.pathname = to.pathname.replace(/\/+$/, '') + '/rtmp'; to.search = ''; }
  catch { console.error('--obs-via needs the computer’s address, like --obs-via=https://example.trycloudflare.com'); process.exit(1); }
  const port = Number(args['obs-port']) || 1935;
  const srv = net.createServer((c) => {
    c.pause(); const ws = new WebSocket(to); ws.binaryType = 'arraybuffer';
    ws.onopen = () => { log('[obs-via] OBS connected: forwarding to', to.host); c.on('data', (d) => ws.send(d)); c.resume(); };
    ws.onmessage = (e) => c.write(Buffer.from(e.data));
    ws.onclose = () => c.destroy(); ws.onerror = () => { log('[obs-via] couldn’t reach', to.href, '(is OBS receiving switched on there, and is it updated?)'); c.destroy(); };
    c.on('close', () => { try { ws.close(); } catch {} }); c.on('error', () => {});
  });
  srv.on('error', (e) => { console.error(e.code === 'EADDRINUSE' ? `Port ${port} is in use here (an OpenTube receiving OBS?). Use --obs-port=1936 and put rtmp://127.0.0.1:1936/live in OBS.` : e.message); process.exit(1); });
  srv.listen(port, '127.0.0.1', () => log(`OBS bridge ready. In OBS: Settings → Stream → Custom…, Server rtmp://127.0.0.1${port === 1935 ? '' : ':' + port}/live and your stream key from that computer. Forwarding to ${to.host}. Ctrl+C stops.`));
  await new Promise(() => {});
}
let site;
try { site = new URL(args.site); } catch { console.error('Missing --site. Copy the command from the site: My node → Run OpenTube on your computer.'); process.exit(1); }
let grant = null;
try { grant = JSON.parse(Buffer.from(String(args.grant), 'base64url').toString('utf8')); } catch {}
if (!grant?.user) { console.error('Missing or broken --grant. Copy the command again from My node → Run OpenTube on your computer.'); process.exit(1); }

const HOME = path.join(os.homedir(), '.opentube');
// Choices made on the site (Devices → Make public…) are saved here by the node and apply on every start; options on
// the command line win.
const LAUNCH_FILE = path.join(HOME, 'launch.json');
try { const saved = JSON.parse(fs.readFileSync(LAUNCH_FILE, 'utf8')); for (const [k, v] of Object.entries(saved)) if (!(k in args) && v !== false && v != null) args[k] = v; } catch {}
const APP = path.join(HOME, 'app');
const VIDEOS = path.resolve(String(args.videos || path.join(os.homedir(), 'OpenTube Videos')));
const STATUS = path.join(HOME, 'host-status.json');
const want = { node: !args['no-node'], host: !args['no-host'], obs: !args['no-obs'] && !args['no-node'] };
await fsp.mkdir(APP, { recursive: true });

// The site's current code. If the site can't be reached, the copy from last time is used.
// It says so when it can't update, since an old copy can behave differently (an old OBS receiver listened on localhost only).
const stale = [];
for (const f of ['index.html', 'server.mjs', 'headless.mjs', 'rtmp.mjs', 'host.mjs', 'tls.mjs', 'sw.js', 'seeds.json']) {
  try {
    const r = await fetch(new URL(f, site) + '?v=' + Date.now(), { cache: 'no-store' });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    await fsp.writeFile(path.join(APP, f), Buffer.from(await r.arrayBuffer()));
  } catch (e) {
    if (!fs.existsSync(path.join(APP, f))) log(`couldn’t download ${f} from ${site.origin} (${e.message})`);
    else stale.push(`${f} (${e.message}; using the copy from ${fs.statSync(path.join(APP, f)).mtime.toISOString().slice(0, 16).replace('T', ' ')})`);
  }
}
if (stale.length) log('⚠ Couldn’t update: ' + stale.join(', ') + '. OpenTube may be out of date.');
else log(`Up to date with ${site.origin} (code in ${APP})`);

// Only one OpenTube per computer (they'd share ~/.opentube, and an older one keeps ports such as OBS's 1935, so the
// new one can't listen there). Stop any earlier one first: the one in running.json, and on Linux any process
// running our code from this folder (an older version left behind).
const RUNNING = path.join(HOME, 'running.json');
const alive = (pid) => { try { process.kill(pid, 0); return true; } catch (e) { return e.code === 'EPERM'; } };
async function stopEarlier() {
  const pids = new Set();
  try { for (const pid of JSON.parse(fs.readFileSync(RUNNING, 'utf8')).pids || []) if (pid !== process.pid && alive(pid)) pids.add(pid); } catch {}
  if (process.platform === 'linux') {
    for (const d of fs.readdirSync('/proc').filter(x => /^\d+$/.test(x))) {
      const pid = Number(d); if (pid === process.pid || pid === process.ppid) continue;
      try { const cmd = fs.readFileSync(`/proc/${d}/cmdline`, 'utf8').split('\0'); if (cmd.some(a => a.startsWith(HOME + path.sep) && /\.(mjs)$/.test(a))) pids.add(pid); } catch {}
    }
    // and the launcher that started them (it runs as "node -e …opentube.mjs…" and would restart them)
    for (const pid of [...pids]) try {
      const pp = Number(/PPid:\s*(\d+)/.exec(fs.readFileSync(`/proc/${pid}/status`, 'utf8'))[1]);
      if (pp > 1 && pp !== process.pid && pp !== process.ppid && fs.readFileSync(`/proc/${pp}/cmdline`, 'utf8').includes('opentube.mjs')) pids.add(pp);
    } catch {}
  }
  if (!pids.size) return;
  log(`Stopping the OpenTube that was already running (process ${[...pids].join(', ')}), so this one can use its ports…`);
  for (const pid of pids) try { process.kill(pid, 'SIGTERM'); } catch {}
  for (let i = 0; i < 50 && [...pids].some(alive); i++) await new Promise(r => setTimeout(r, 100));
  for (const pid of pids) if (alive(pid)) try { process.kill(pid, 'SIGKILL'); } catch {}
  await new Promise(r => setTimeout(r, 300));
}
await stopEarlier();
const noteRunning = () => { try { fs.writeFileSync(RUNNING, JSON.stringify({ pids: [process.pid, ...[...children].map(c => c.pid)], at: Date.now() })); } catch {} };

const freePort = (p) => new Promise((res) => { const s = net.createServer().once('error', () => res(freePort(p + 1))).once('listening', () => s.close(() => res(p))).listen(p); });
const port = await freePort(Number(args.port) || 8090);
const ws = site.origin.replace(/^http/, 'ws');

// Run a part as a child process, restart it if it stops, prefix its output. Stopping this program stops the parts.
const children = new Set();
let stopping = false;
function run(label, file, argv, env) {
  const start = () => {
    if (stopping) return;
    const child = spawn(process.execPath, [path.join(APP, file), ...argv], { env: { ...process.env, ...env, OPENTUBE_LAUNCHER: '1' }, stdio: ['ignore', 'pipe', 'pipe'] });
    children.add(child); noteRunning();
    const out = (d) => { for (const line of String(d).split('\n')) if (line.trim()) console.log(`[${label}] ${line}`); };
    child.stdout.on('data', out); child.stderr.on('data', out);
    child.on('exit', (code) => { children.delete(child); if (stopping) return; if (code === UPDATE_EXIT) return relaunch(); log(`[${label}] stopped (${code}); restarting in 5 s`); setTimeout(start, 5000); });
  };
  start();
}
const stopAll = () => { stopping = true; for (const c of children) c.kill(); try { fs.rmSync(RUNNING, { force: true }); } catch {} };
// "Update" on the site (Devices): the node exits with UPDATE_EXIT; this program stops the other parts and starts itself
// again the same way it was started, which downloads the site's current code (this launcher included). It stays as a
// thin parent so the terminal, Ctrl+C and service managers keep working.
const UPDATE_EXIT = 75;
let next = null;
async function relaunch() {
  if (next || stopping) return;
  log('Updating: downloading the site’s current OpenTube and restarting…');
  stopAll();
  for (let i = 0; i < 50 && children.size; i++) await new Promise(r => setTimeout(r, 100));
  const viaEval = process.execArgv.some(a => a === '-e' || a === '--eval');   // the one-line command: node -e "…" -- --site=… --grant=…
  next = spawn(process.execPath, [...process.execArgv, ...(viaEval ? ['--'] : []), ...process.argv.slice(1)], { stdio: 'inherit', env: process.env });
  next.on('exit', (code) => process.exit(code ?? 0));
}
process.on('exit', stopAll);
for (const sig of ['SIGINT', 'SIGTERM', 'SIGHUP']) process.on(sig, () => { if (next) { try { next.kill(sig); } catch {} return; } stopAll(); process.exit(0); });

const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm', npx = process.platform === 'win32' ? 'npx.cmd' : 'npx';
const installed = (pkg) => fs.existsSync(path.join(HOME, 'node_modules', pkg, 'package.json'));
const install = (spec) => spawnSync(npm, ['install', '--prefix', HOME, '--no-audit', '--no-fund', spec], { stdio: 'inherit', shell: process.platform === 'win32' });

// Hosting: the node itself keeps and shares the videos (WebTorrent over WebRTC, no browser). Needs the webtorrent
// package, installed into ~/.opentube once (a few MB). --browser-host runs the older headless-Chromium host instead.
// The peer mesh (browsers and nodes connect to this node directly over WebRTC) needs Trystero and WebTorrent's WebRTC.
if (want.node && (!installed('@trystero-p2p/torrent') || !installed('webtorrent'))) {
  log('first run: installing the peer-to-peer libraries (a few MB, once)…');
  if (!installed('webtorrent')) install('webtorrent@3.0.21');
  if (!installed('@trystero-p2p/torrent')) install('@trystero-p2p/torrent@0.26.0');
}
if (want.node && args.public && !installed('acme-client')) { log('installing Let\'s Encrypt support (once)…'); install('acme-client@5.4.0'); }
let nodeHosts = false;
if (want.host && want.node && !args['browser-host']) {
  if (!installed('webtorrent')) { log('first run: installing WebTorrent for video hosting (a few MB, once)…'); install('webtorrent@3.0.21'); }
  nodeHosts = installed('webtorrent');
  if (nodeHosts) await fsp.mkdir(VIDEOS, { recursive: true });
  else log('couldn’t install WebTorrent, so video hosting is off. The node and OBS receiver still run.');
}

// ---- Cloudflare Tunnel (--tunnel): a public HTTPS address with no open ports, no certificate and no router setup.
// cloudflared connects out to Cloudflare, which forwards https://<name>.trycloudflare.com (WebSockets included) to
// this node. No account needed (the address changes when the tunnel restarts; the node announces the new one).
// With an account: --tunnel-token=<token> --public-url=https://your.host (a named tunnel, a fixed address).
async function cloudflaredPath() {
  const found = spawnSync(process.platform === 'win32' ? 'where' : 'which', ['cloudflared']);
  if (found.status === 0) return String(found.stdout).split(/\r?\n/)[0].trim();
  const dir = path.join(HOME, 'bin'); fs.mkdirSync(dir, { recursive: true });
  const arch = process.arch === 'arm64' ? 'arm64' : process.arch === 'arm' ? 'arm' : 'amd64';
  const plat = process.platform, file = path.join(dir, plat === 'win32' ? 'cloudflared.exe' : 'cloudflared');
  if (fs.existsSync(file)) return file;
  const name = plat === 'win32' ? 'cloudflared-windows-amd64.exe' : plat === 'darwin' ? `cloudflared-darwin-${arch === 'arm64' ? 'arm64' : 'amd64'}.tgz` : `cloudflared-linux-${arch}`;
  log('downloading Cloudflare’s tunnel program (cloudflared, once)…');
  const r = await fetch('https://github.com/cloudflare/cloudflared/releases/latest/download/' + name); if (!r.ok) throw new Error('download failed: HTTP ' + r.status);
  const buf = Buffer.from(await r.arrayBuffer());
  if (name.endsWith('.tgz')) { const tgz = file + '.tgz'; fs.writeFileSync(tgz, buf); spawnSync('tar', ['-xzf', tgz, '-C', dir]); fs.rmSync(tgz, { force: true }); }
  else fs.writeFileSync(file, buf);
  fs.chmodSync(file, 0o755); return file;
}
let tunnelUrl = null, tunnelState = null;
if (want.node && (args.tunnel || args['tunnel-token'])) {
  try {
    const bin = await cloudflaredPath(), token = args['tunnel-token'] ? String(args['tunnel-token']) : null;
    const argv = token ? ['tunnel', '--no-autoupdate', 'run', '--token', token] : ['tunnel', '--no-autoupdate', '--url', `http://localhost:${port}`];
    if (token && !args['public-url']) log('⚠ --tunnel-token needs --public-url=https://your.host (the hostname routed to this tunnel in Cloudflare)');
    const tunnel = spawn(bin, argv, { stdio: ['ignore', 'pipe', 'pipe'] }); children.add(tunnel);
    tunnel.on('exit', (code) => { children.delete(tunnel); if (!stopping) { log(`tunnel stopped (${code}); restarting OpenTube for a new one`); relaunch(); } });
    tunnelUrl = args['public-url'] ? String(args['public-url']).replace(/\/+$/, '') : await new Promise((res) => {
      const t = setTimeout(() => res(null), 45000);
      const look = (d) => { const m = /https:\/\/[a-z0-9-]+\.trycloudflare\.com/.exec(String(d)); if (m) { clearTimeout(t); res(m[0]); } };
      tunnel.stdout.on('data', look); tunnel.stderr.on('data', look);
    });
    if (tunnelUrl) { tunnelState = 'on'; log(`  public: ${tunnelUrl} (through Cloudflare Tunnel; browsers use it as a way into the network, tracker and live hub)`); }
    else { tunnelState = 'no address from cloudflared within 45 s'; log('the tunnel didn’t give an address within 45 s; the node runs without a public address'); }
  } catch (e) { tunnelState = e.message; log('tunnel off:', e.message); }
}

if (want.node) {
  const roles = ['node', ...(want.host ? ['host'] : []), ...(want.obs ? ['obs'] : [])];
  run('node', 'server.mjs', [], {
    PORT: String(port), DATA_DIR: path.join(HOME, 'node-data'), PEERS: ws + '/ot', MINE: args.mine ? '1' : 'own', RANKER: '0',
    NODE_GRANT: String(args.grant), NODE_SITE: site.origin, NODE_ROLES: roles.join(','), NODE_LABEL: String(args.name || os.hostname()),
    // flash computers: --flash-parent/--flash-tag on a flash computer; --flash-provider/--flash-token/… on the computer that starts them
    ...Object.fromEntries([['flash-parent', 'FLASH_PARENT'], ['flash-tag', 'FLASH_TAG'], ['flash-provider', 'FLASH_PROVIDER'], ['flash-token', 'FLASH_TOKEN'], ['flash-region', 'FLASH_REGION'], ['flash-plan', 'FLASH_PLAN'], ['flash-image', 'FLASH_IMAGE'], ['flash-max-hours', 'FLASH_MAX_HOURS'], ['flash-create-cmd', 'FLASH_CREATE_CMD'], ['flash-delete-cmd', 'FLASH_DELETE_CMD']].filter(([a]) => args[a] && args[a] !== true).map(([a, e]) => [e, String(args[a])])),
    HOST_STATUS_FILE: STATUS, RTMP_PORT: want.obs ? String(Number(args['obs-port']) || 1935) : '',
    ...(args.public ? { PUBLIC: args.public === true ? 'auto' : String(args.public), ...(args.email ? { PUBLIC_EMAIL: String(args.email) } : {}) } : {}),
    ...(tunnelUrl ? { PUBLIC_URL: tunnelUrl } : {}), LAUNCH_FILE, ...(tunnelState ? { TUNNEL_STATE: String(tunnelState).slice(0, 200) } : {}),
    ...(nodeHosts ? { VIDEO_DIR: VIDEOS, HOST_USER: grant.user, CAP_GB: String(args['cap-gb'] || 5) } : {})
  });
}

if (want.host && (args['browser-host'] || !want.node)) {
  // The older host: the site itself in headless Chromium (installed into ~/.opentube on first run).
  if (!installed('playwright')) { log('first run: installing Chromium for the headless host (a few hundred MB, once)…'); install('playwright@1.64.1'); }
  if (installed('playwright')) {
    spawnSync(npx, ['--prefix', HOME, 'playwright', 'install', 'chromium'], { stdio: 'inherit', cwd: HOME, shell: process.platform === 'win32' });
    await fsp.mkdir(VIDEOS, { recursive: true });
    run('host', 'headless.mjs', [site.origin + '/', grant.user, VIDEOS], { PROFILE_DIR: path.join(HOME, 'browser-profile'), HOST_STATUS_FILE: STATUS });
  } else log('couldn’t install Chromium, so the headless host is off. The node and OBS receiver still run.');
}

log(`OpenTube is running for user ${grant.user.slice(0, 8)}…`);
if (want.node) log(`  node:  http://localhost:${port}  (syncs with ${site.origin}; see it under My node → Your nodes)`);
if (want.host) log(`  host:  videos in ${VIDEOS}`);
const obsPort = Number(args['obs-port']) || 1935, obsSuffix = obsPort === 1935 ? '' : ':' + obsPort;
if (want.obs) log(`  OBS:   Settings → Stream → Service “Custom…”, Server rtmp://127.0.0.1${obsSuffix}/live (from another computer: this one’s address; its firewall must let in TCP port ${obsPort}), Stream Key: copy it on the site under Devices → 🎥 OBS & streaming.`);
log('Keep this window open. Ctrl+C stops everything.');
