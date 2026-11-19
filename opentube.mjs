#!/usr/bin/env node
// OpenTube on your computer. Started by the command on the site's My node page ("Run OpenTube on your computer"),
// which downloads this file from the site and runs it. Each start it downloads the site's current
// server.mjs, headless.mjs, rtmp.mjs and index.html into ~/.opentube/app, then runs:
//
//   node     a full node that syncs with the site and shows up under "Your nodes" on your My node page
//   host     the headless host: keeps your joined channels' videos in a folder and shares them (Chromium,
//            installed into ~/.opentube on first run)
//   obs      an OBS receiver: OBS → Settings → Stream → Custom, server rtmp://localhost/live, any stream key,
//            then pick this computer under Go live on the site
//
// Options (after `--` in the command):
//   --site=URL        the site (set by the command)
//   --grant=...       your signed grant: this node is yours (set by the command; keep it private)
//   --videos=PATH     the video folder (default: ~/OpenTube Videos)
//   --name=TEXT       what to call this computer on My node (default: its hostname)
//   --port=8090       the local node's HTTP port (default: 8090, or the next free one)
//   --no-host --no-obs --no-node   leave a part out
//   --mine            also spend CPU confirming registrations (off by default)
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
let site;
try { site = new URL(args.site); } catch { console.error('Missing --site. Copy the command from the site: My node → Run OpenTube on your computer.'); process.exit(1); }
let grant = null;
try { grant = JSON.parse(Buffer.from(String(args.grant), 'base64url').toString('utf8')); } catch {}
if (!grant?.user) { console.error('Missing or broken --grant. Copy the command again from My node → Run OpenTube on your computer.'); process.exit(1); }

const HOME = path.join(os.homedir(), '.opentube');
const APP = path.join(HOME, 'app');
const VIDEOS = path.resolve(String(args.videos || path.join(os.homedir(), 'OpenTube Videos')));
const STATUS = path.join(HOME, 'host-status.json');
const want = { node: !args['no-node'], host: !args['no-host'], obs: !args['no-obs'] && !args['no-node'] };
await fsp.mkdir(APP, { recursive: true });

// The site's current code. If the site can't be reached, the copy from last time is used.
for (const f of ['index.html', 'server.mjs', 'headless.mjs', 'rtmp.mjs', 'sw.js']) {
  try {
    const r = await fetch(new URL(f, site), { cache: 'no-store' });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    await fsp.writeFile(path.join(APP, f), Buffer.from(await r.arrayBuffer()));
  } catch (e) { if (!fs.existsSync(path.join(APP, f))) log(`couldn’t download ${f} from ${site.origin} (${e.message})`); }
}

const freePort = (p) => new Promise((res) => { const s = net.createServer().once('error', () => res(freePort(p + 1))).once('listening', () => s.close(() => res(p))).listen(p); });
const port = await freePort(Number(args.port) || 8090);
const ws = site.origin.replace(/^http/, 'ws');

// Run a part as a child process, restart it if it stops, prefix its output.
function run(label, file, argv, env) {
  const start = () => {
    const child = spawn(process.execPath, [path.join(APP, file), ...argv], { env: { ...process.env, ...env }, stdio: ['ignore', 'pipe', 'pipe'] });
    const out = (d) => { for (const line of String(d).split('\n')) if (line.trim()) console.log(`[${label}] ${line}`); };
    child.stdout.on('data', out); child.stderr.on('data', out);
    child.on('exit', (code) => { log(`[${label}] stopped (${code}); restarting in 5 s`); setTimeout(start, 5000); });
  };
  start();
}

if (want.node) {
  const roles = ['node', ...(want.host ? ['host'] : []), ...(want.obs ? ['obs'] : [])];
  run('node', 'server.mjs', [], {
    PORT: String(port), DATA_DIR: path.join(HOME, 'node-data'), PEERS: ws + '/ot', MINE: args.mine ? '1' : '0', RANKER: '0',
    NODE_GRANT: String(args.grant), NODE_SITE: site.origin, NODE_ROLES: roles.join(','), NODE_LABEL: String(args.name || os.hostname()),
    HOST_STATUS_FILE: STATUS, RTMP_PORT: want.obs ? '1935' : ''
  });
}

if (want.host) {
  // Chromium for the headless host lives in ~/.opentube (npm and npx come with Node.js).
  const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm', npx = process.platform === 'win32' ? 'npx.cmd' : 'npx';
  const has = () => fs.existsSync(path.join(HOME, 'node_modules', 'playwright', 'package.json'));
  if (!has()) {
    log('first run: installing Chromium for the headless host (a few hundred MB, once)…');
    spawnSync(npm, ['install', '--prefix', HOME, '--no-audit', '--no-fund', 'playwright@1.64.1'], { stdio: 'inherit', shell: process.platform === 'win32' });
  }
  if (has()) {
    spawnSync(npx, ['--prefix', HOME, 'playwright', 'install', 'chromium'], { stdio: 'inherit', cwd: HOME, shell: process.platform === 'win32' });
    await fsp.mkdir(VIDEOS, { recursive: true });
    run('host', 'headless.mjs', [site.origin + '/', grant.user, VIDEOS], { PROFILE_DIR: path.join(HOME, 'browser-profile'), HOST_STATUS_FILE: STATUS });
  } else log('couldn’t install Chromium, so the headless host is off. The node and OBS receiver still run.');
}

log(`OpenTube is running for user ${grant.user.slice(0, 8)}…`);
if (want.node) log(`  node:  http://localhost:${port}  (syncs with ${site.origin}; see it under My node → Your nodes)`);
if (want.host) log(`  host:  videos in ${VIDEOS}`);
if (want.obs) log('  OBS:   Settings → Stream → Service “Custom…”, Server rtmp://localhost/live, any stream key. Then Go live on the site and pick this computer.');
log('Keep this window open. Ctrl+C stops everything.');
