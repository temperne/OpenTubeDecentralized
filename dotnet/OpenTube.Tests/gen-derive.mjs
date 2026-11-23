// derive(): chains of every kind built from data/payload.json (the owner history plus every candidate the JavaScript
// judged valid), summarised the same way the C# test summarises its own Derived. Run: node dotnet/OpenTube.Tests/gen-derive.mjs
import fs from 'node:fs'; import path from 'node:path'; import { fileURLToPath } from 'node:url';
const here = path.dirname(fileURLToPath(import.meta.url));
new Function(/<script id="opentube-core">([\s\S]*?)<\/script>/.exec(fs.readFileSync(path.join(here, '../../index.html'), 'utf8'))[1])();
const C = globalThis.OpenTubeCore;
const P = JSON.parse(fs.readFileSync(path.join(here, 'data/payload.json'), 'utf8'));
const out = [];
for (const [ci, spec] of P.chains.entries()) {
  const ch = new C.Chain(spec.genesis.hash, spec.genesis);
  for (const b of spec.blocks) ch.add(b);
  for (const c of P.cases) if (c.c === ci && c.ok && !ch.blocks.has(c.b.hash) && ch.blocks.has(c.b.parents[0])) ch.add({ ...c.b, height: ch.blocks.get(c.b.parents[0]).height + 1 });
  // dated so the history ends two hours ago: streams in its last day are live now, older ones aren't
  const maxTs = Math.max(...[...ch.blocks.values()].filter(b => b.hash !== ch.id).map(b => b.ts)), shift = Date.now() - 2 * 3600000 - maxTs;
  for (const b of ch.blocks.values()) if (b.hash !== ch.id) b.ts = b.payload.type === 'live' && b.payload.title === 'L2' ? Date.now() - 3600000 : b.ts + shift - (b.payload.type === 'live' && b.payload.title === 'L1' ? 2 * 86400000 : 0);
  ch.version++;
  const now = Date.now(), d = C.derive(ch);
  const keys = [...new Set([spec.genesis.author, ...[...ch.blocks.values()].map(b => b.author)])].filter(Boolean).sort();
  const targets = [...new Set([...[...ch.blocks.values()].filter(b => ['video', 'live', 'post', 'comment'].includes(b.payload.type)).map(b => b.hash), 'ab'.repeat(32)])].sort();
  const summary = C.canon({
    kind: d.kind, owner: d.owner ?? null, byOwner: [...d.byOwner].sort(), relays: [...d.relays].sort(), profile: d.profile, allowed: [...d.allowed].sort(), revoked: [...d.revoked].sort(), hidden: [...d.hidden].sort(),
    videos: d.videos.map(b => b.hash), posts: d.posts.map(b => b.hash), sealed: d.sealed.map(b => b.hash), adds: d.adds.map(b => b.hash), ads: d.ads.map(b => b.hash), sponsors: d.sponsors.map(b => b.hash),
    streams: d.streams.map(b => b.hash), liveNow: d.liveNow.map(b => b.hash),
    liveEnds: Object.fromEntries(d.liveEnds), liveNext: Object.fromEntries(d.liveNext), liveStats: Object.fromEntries(d.liveStats), recordings: Object.fromEntries([...d.recordings].map(([k, b]) => [k, b.hash])), nodeRecs: Object.fromEntries(d.nodeRecs),
    votes: Object.fromEntries([...d.votes].map(([k, v]) => [k, { up: v.up, down: v.down, by: Object.fromEntries(v.by), recent: v.recent, blocks: v.blocks.map(b => b.hash) }])),
    policy: Object.fromEntries(targets.map(t => [t, d.policyFor(t) ?? null])),
    canComment: Object.fromEntries(keys.flatMap(k => targets.map(t => [k + '|' + t, !!d.canComment(k, t)]))), canPost: Object.fromEntries(keys.map(k => [k, !!d.canPost(k)])),
    comments: Object.fromEntries(targets.map(t => [t, d.commentsFor(t).map(b => b.hash)])), blockCount: d.blockCount });
  out.push({ chain: ci, now, keys, targets, blocks: [...ch.blocks.values()].filter(b => b.hash !== ch.id), summary });
}
fs.writeFileSync(path.join(here, 'data/derive.json'), JSON.stringify(out));
console.log(out.map(o => `${P.chains[o.chain].kind}: ${o.blocks.length} blocks`).join(' · '));
