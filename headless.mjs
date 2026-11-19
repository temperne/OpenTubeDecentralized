#!/usr/bin/env node
// Runs the OpenTube page itself on a server, in headless Chromium, around the clock.
// It's the same index.html everyone else opens, in mirror mode: it acts as YOU. Give it your user ID
// (My node → Your user ID) and it joins exactly the channels you join, downloads and seeds their
// videos (WebRTC, like any viewer), and leaves channels when you leave them. It never needs your private key.
//
// Videos go into a real folder on this machine, the same layout a browser uses:
//   <video folder>/<Channel name>/channel.json   which channel (chain ID, owner key)
//   <video folder>/<Channel name>/<video files>   the real files
// Chains and settings stay in a persistent browser profile. Both survive restarts.
//
// Setup (once):   npm install && npx playwright install --with-deps chromium
// Run:            node headless.mjs https://your-site.example/ <your user ID> <video folder>
//
// Options (environment variables):
//   VIDEO_DIR=./videos               the video folder, if not given as the third argument
//   PROFILE_DIR=./browser-profile   where the browser keeps chains and settings
//   CAP_GB=50                        storage limit per joined channel
import { chromium } from 'playwright';
import fsp from 'node:fs/promises';
import path from 'node:path';

const target = process.argv[2] || process.env.SITE_URL;
const userId = process.argv[3] || process.env.USER_ID;
const VIDEO_DIR = path.resolve(process.argv[4] || process.env.VIDEO_DIR || './videos');
if (!target || !/^[A-Za-z0-9_-]{43}$/.test(userId || '')) {
  console.error('usage: node headless.mjs <site url> <your user ID> <video folder>\n(find your user ID on the site under My node → Your user ID)');
  process.exit(1);
}
await fsp.mkdir(VIDEO_DIR, { recursive: true });

// The page asks for files through this one function; every path stays inside VIDEO_DIR.
const inside = (rel) => {
  const p = path.resolve(VIDEO_DIR, String(rel || '.'));
  if (p !== VIDEO_DIR && !p.startsWith(VIDEO_DIR + path.sep)) throw new Error('path outside the video folder');
  return p;
};
async function folderOp(op, rel, a, b) {
  const p = inside(rel);
  switch (op) {
    case 'list': return (await fsp.readdir(p, { withFileTypes: true })).filter(d => d.isFile() || d.isDirectory()).map(d => [d.name, d.isDirectory() ? 'directory' : 'file']);
    case 'stat': try { const st = await fsp.stat(p); return [st.isDirectory() ? 'directory' : 'file', st.size]; } catch { return null; }
    case 'mkdir': await fsp.mkdir(p, { recursive: true }); return true;
    case 'create': await (await fsp.open(p, a ? 'w' : 'a')).close(); return true;   // a: start empty
    case 'read': { const fh = await fsp.open(p, 'r'); try { const buf = Buffer.alloc(b); const { bytesRead } = await fh.read(buf, 0, b, a); return buf.subarray(0, bytesRead).toString('base64'); } finally { await fh.close(); } }
    case 'write': { const fh = await fsp.open(p, 'r+'); try { const d = Buffer.from(b, 'base64'); await fh.write(d, 0, d.length, a); return true; } finally { await fh.close(); } }
    case 'remove': await fsp.rm(p, { recursive: !!a, force: true }); return true;
  }
  throw new Error('unknown op ' + op);
}
// In the page: FileSystemDirectoryHandle / FileSystemFileHandle look-alikes backed by folderOp, so the page uses
// this folder exactly as if you had picked it in Chrome.
function folderShim(rootName) {
  const call = (...a) => window.__openTubeFs(...a);
  const b64e = (u8) => { let s = ''; for (let i = 0; i < u8.length; i += 0x8000) s += String.fromCharCode.apply(null, u8.subarray(i, i + 0x8000)); return btoa(s); };
  const b64d = (s) => Uint8Array.from(atob(s), c => c.charCodeAt(0));
  const bytes = async (d) => d instanceof Uint8Array ? d : d instanceof ArrayBuffer ? new Uint8Array(d) : ArrayBuffer.isView(d) ? new Uint8Array(d.buffer, d.byteOffset, d.byteLength)
    : d instanceof Blob ? new Uint8Array(await d.arrayBuffer()) : new TextEncoder().encode(String(d));
  const missing = (n) => new DOMException(`“${n}” not found`, 'NotFoundError');
  const join = (dir, name) => { if (!name || name === '.' || name === '..' || /[\\/]/.test(name)) throw new TypeError('bad name'); return dir ? dir + '/' + name : name; };
  const readAt = async (p, start, len) => { const out = new Uint8Array(len); let off = 0;
    while (off < len) { const part = b64d(await call('read', p, start + off, Math.min(4 << 20, len - off))); if (!part.length) break; out.set(part, off); off += part.length; }
    return off < len ? out.slice(0, off) : out; };
  class DiskBlob {  // a Blob-like view of a large file that's read on demand
    constructor(p, name, start, end) { Object.assign(this, { p, name, start, end, type: '' }); }
    get size() { return this.end - this.start; }
    slice(a = 0, b = this.size) { const n = this.size; a = a < 0 ? Math.max(n + a, 0) : Math.min(a, n); b = b < 0 ? Math.max(n + b, 0) : Math.min(b, n); return new DiskBlob(this.p, this.name, this.start + a, this.start + Math.max(a, b)); }
    async arrayBuffer() { return (await readAt(this.p, this.start, this.size)).buffer; }
    async text() { return new TextDecoder().decode(await this.arrayBuffer()); }
    stream() { let off = 0; return new ReadableStream({ pull: async (c) => { if (off >= this.size) return c.close(); const part = await readAt(this.p, this.start + off, Math.min(1 << 20, this.size - off)); off += part.length || this.size; if (part.length) c.enqueue(part); } }); }
  }
  const granted = { queryPermission: async () => 'granted', requestPermission: async () => 'granted' };
  class FileHandle {
    constructor(p, name) { Object.assign(this, { kind: 'file', p, name }, granted); }
    async getFile() {
      const st = await call('stat', this.p); if (!st) throw missing(this.name);
      return st[1] <= 16 << 20 ? new File([await readAt(this.p, 0, st[1])], this.name) : new DiskBlob(this.p, this.name, 0, st[1]);
    }
    async createWritable(opts = {}) {
      await call('create', this.p, !opts.keepExistingData);
      let pos = 0;
      const write = async (chunk) => { const d = await bytes(chunk); await call('write', this.p, pos, b64e(d)); pos += d.length; };
      const w = new WritableStream({ write });
      w.write = async (arg) => {
        if (arg && typeof arg === 'object' && arg.type) { if (arg.type === 'seek') { pos = arg.position; return; } if (arg.type === 'write') { if (arg.position != null) pos = arg.position; return write(arg.data); } }
        return write(arg);
      };
      w.seek = async (n) => { pos = n; };
      w.close = async () => {};  // writes are already on disk
      return w;
    }
  }
  class DirHandle {
    constructor(p, name) { Object.assign(this, { kind: 'directory', p, name }, granted); }
    async getDirectoryHandle(name, opts = {}) {
      const p = join(this.p, name), st = await call('stat', p);
      if (st && st[0] !== 'directory') throw new DOMException('not a folder', 'TypeMismatchError');
      if (!st) { if (!opts.create) throw missing(name); await call('mkdir', p); }
      return new DirHandle(p, name);
    }
    async getFileHandle(name, opts = {}) {
      const p = join(this.p, name), st = await call('stat', p);
      if (st && st[0] !== 'file') throw new DOMException('not a file', 'TypeMismatchError');
      if (!st) { if (!opts.create) throw missing(name); await call('create', p, false); }
      return new FileHandle(p, name);
    }
    async removeEntry(name, opts = {}) { const p = join(this.p, name); if (!(await call('stat', p))) throw missing(name); await call('remove', p, !!opts.recursive); }
    async *entries() { for (const [n, k] of await call('list', this.p)) yield [n, k === 'directory' ? new DirHandle(join(this.p, n), n) : new FileHandle(join(this.p, n), n)]; }
    async *values() { for await (const [, h] of this.entries()) yield h; }
    async *keys() { for await (const [n] of this.entries()) yield n; }
  }
  window.__openTubeFolder = new DirHandle('', rootName);
}
const PROFILE_DIR = process.env.PROFILE_DIR || './browser-profile';
const log = (...a) => console.log(new Date().toISOString().slice(0, 19).replace('T', ' '), ...a);

const url = new URL(target);
url.searchParams.set('mirror', userId);
if (process.env.CAP_GB) url.searchParams.set('cap', process.env.CAP_GB);

const fmt = (b) => b > 1e9 ? (b / 1e9).toFixed(1) + ' GB' : b > 1e6 ? (b / 1e6).toFixed(1) + ' MB' : Math.round(b / 1e3) + ' KB';

async function run() {
  const ctx = await chromium.launchPersistentContext(PROFILE_DIR, {
    headless: true,
    args: ['--autoplay-policy=no-user-gesture-required', '--disable-background-timer-throttling', '--disable-renderer-backgrounding']
  });
  await ctx.exposeFunction('__openTubeFs', folderOp).catch(() => {});
  await ctx.addInitScript(folderShim, path.basename(VIDEO_DIR));
  const page = ctx.pages()[0] || await ctx.newPage();
  page.on('console', (m) => {
    const t = m.text();
    if (t.startsWith('opentube-status ')) {
      try {
        const s = JSON.parse(t.slice(16));
        log(`${s.net} · main #${s.height} · ${s.found ? '' : "waiting for this user's channel list · "}${s.channels} channels joined · hosting ${s.hosting} videos (${fmt(s.hostedBytes)})` +
          `${s.downloading ? ` · downloading ${s.downloading}` : ''} · ${s.peers} video peers · ↑${fmt(s.up)}/s ↓${fmt(s.down)}/s`);
      } catch {}
    } else if (t.startsWith('opentube-mirror ')) {
      log(t.slice(16));
    } else if (m.type() === 'error' && !/WebSocket connection to .*(tracker|announce)/.test(t)) log('page error:', t.slice(0, 200));  // dead public trackers are normal
  });
  page.on('pageerror', (e) => log('page exception:', e.message));
  page.on('dialog', (d) => d.dismiss().catch(() => {}));
  log(`opening ${url.href} (videos: ${VIDEO_DIR}, profile: ${PROFILE_DIR})`);
  await page.goto(url.href, { waitUntil: 'load' });
  // If the page crashes or closes, start over; the profile keeps all data.
  await new Promise((resolve) => { page.on('crash', resolve); page.on('close', resolve); ctx.on('close', resolve); });
  log('page stopped; restarting in 5 s');
  await ctx.close().catch(() => {});
}

for (;;) {
  try { await run(); } catch (e) { log('error:', e.message); }
  await new Promise((r) => setTimeout(r, 5000));
}
