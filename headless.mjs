#!/usr/bin/env node
// Runs the OpenTube page itself on a server, in headless Chromium, around the clock.
// It's the same index.html everyone else opens, in mirror mode: it acts as YOU. Give it your user ID
// (My node → Your user ID) and it joins exactly the channels you join, downloads and seeds their
// videos (WebRTC, like any viewer), and leaves channels when you leave them. Everything is kept in a
// persistent browser profile, so it survives restarts. It never needs your private key.
//
// Setup (once):   npm install && npx playwright install --with-deps chromium
// Run:            node headless.mjs https://your-site.example/ <your user ID>
//
// Options (environment variables):
//   PROFILE_DIR=./browser-profile   where the browser keeps its data (chains, videos)
//   CAP_GB=50                        storage limit per joined channel
import { chromium } from 'playwright';

const target = process.argv[2] || process.env.SITE_URL;
const userId = process.argv[3] || process.env.USER_ID;
if (!target || !/^[A-Za-z0-9_-]{43}$/.test(userId || '')) {
  console.error('usage: node headless.mjs <site url> <your user ID>\n(find your user ID on the site under My node → Your user ID)');
  process.exit(1);
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
  log(`opening ${url.href} (profile: ${PROFILE_DIR})`);
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
