// Refreshes seeds.json, the entry nodes a first-time visitor's browser tries before the site's own node.
// Asks every known entry node which entry nodes it knows (/entries.json, from signed node statuses), keeps the ones
// that answer over HTTPS right now, and writes the list. Run hourly by .github/workflows/seeds.yml; the site
// redeploys on the commit. Nothing here is trusted beyond "this address answers": browsers check everything else.
import fs from 'node:fs';
const FILE = new URL('../seeds.json', import.meta.url);
const cur = JSON.parse(fs.readFileSync(FILE, 'utf8'));
// an address that redirects elsewhere isn't a node: redirects count as failures
const get = async (u, ms = 8000) => { const r = await fetch(u, { signal: AbortSignal.timeout(ms), redirect: 'manual' }); if (!r.ok) throw new Error(String(r.status)); return r.json(); };
// the home domain first; the Cloudflare address until the domain's DNS is live
const SITES = (process.env.SITE ? [process.env.SITE] : ['https://opentuber.com', 'https://opentubedecentralized.jasonajensen.workers.dev']).map(u => u.replace(/\/+$/, ''));
let SITE = SITES[0];
const known = new Set((cur.entry || []).map(u => u.replace(/\/+$/, '')));
let network = cur.network || null;
// the site's list too: the entry nodes its node hears of right now (a computer behind a tunnel gets a new address
// each time it starts, so the old addresses alone would never lead to it)
let siteUp = false;
for (const s of SITES) try { const j = await get(s + '/entries.json'); SITE = s; siteUp = true; network ||= j.network; for (const e of j.entry || []) if (e?.node && /^https:\/\//.test(e.url || '')) known.add(e.url.replace(/\/+$/, '')); }
catch (e) { console.log(`${s}: no entry list (${e.message})`); }
for (const u of [...known]) {
  try { const j = await get(u + '/entries.json'); network ||= j.network; if (j.network && network && j.network !== network) continue; for (const e of j.entry || []) if (/^https:\/\//.test(e?.url || '')) known.add(e.url.replace(/\/+$/, '')); }
  catch (e) { console.log(`${u}: no entry list (${e.message})`); }
}
const alive = [];
for (const u of known) {
  try { const s = await get(u + '/status'); if (network && s.network && s.network !== network) { console.log(`${u}: another network`); continue; } alive.push(u); console.log(`${u}: up (height ${s.height})`); }
  catch (e) { console.log(`${u}: down (${e.message})`); }
}
// only what's up now. When nothing answers and the site couldn't be asked either, this machine is probably offline:
// keep the old list then. (With no entry nodes, browsers use the site's own node.)
const entry = alive.length || siteUp ? alive.slice(0, 40) : [...(cur.entry || [])];
const next = { ...cur, ...(network ? { network } : {}), updated: alive.length ? new Date().toISOString() : cur.updated || null, entry };
if (JSON.stringify(next.entry) !== JSON.stringify(cur.entry) || (next.network && next.network !== cur.network)) {
  fs.writeFileSync(FILE, JSON.stringify(next, null, 2) + '\n'); console.log('seeds.json updated:', entry.length, 'entry node(s)');
} else console.log('seeds.json unchanged');
