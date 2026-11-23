// A scenario of real (mined, signed) blocks through the JavaScript engine's insert, recording after each step the best
// main block, the directory, the pools, the ledger, rejections and every chain's heads. Run: node dotnet/OpenTube.Tests/gen-engine.mjs
import fs from 'node:fs'; import path from 'node:path'; import crypto from 'node:crypto'; import { fileURLToPath } from 'node:url';
const here = path.dirname(fileURLToPath(import.meta.url));
new Function(/<script id="opentube-core">([\s\S]*?)<\/script>/.exec(fs.readFileSync(path.join(here, '../../index.html'), 'utf8'))[1])();
const C = globalThis.OpenTubeCore;
const lz = (d) => { let z = 0; for (const b of d) { if (b === 0) { z += 8; continue; } return z + Math.clz32(b) - 24; } return z; };
const mineN = (body, bits, start = 0) => { const pre = C.preimage(body); for (let n = start; ; n++) if (lz(crypto.createHash('sha256').update(pre + n).digest()) >= bits) return n; };
const ids = { M: await C.newIdentity(), A: await C.newIdentity(), B: await C.newIdentity(), Cc: await C.newIdentity() };
const S = {}; for (const [k, v] of Object.entries(ids)) S[k] = await C.signer(v);
const finish = async (b, who, bits) => { b.nonce = bits ? mineN(b, bits) : 0; b.hash = await C.hashBlock(b); b.sig = who ? await S[who].sign(b.hash) : 's'; return b; };
const tx = async (who, seq, act, x, ts, bits = 10) => finish({ v: 2, kind: 'tx', from: ids[who].pub, seq, ts, [act]: x }, who, act === 'starter' ? 20 : bits);
const engine = await C.createEngine({ signer: S.M, send: () => {}, peers: () => [], trackAll: true });
const MAIN = engine.MAIN_ID, T0 = Date.UTC(2026, 10, 21, 12);
const snapL = (L) => C.canon({ bal: Object.fromEntries(L.bal), seq: Object.fromEntries(L.seq), starters: [...L.starters].sort(), names: Object.fromEntries(L.names), nameOf: Object.fromEntries(L.nameOf), minted: L.minted, burned: L.burned, txCount: L.txCount, prices: L.prices });
const observe = () => ({ best: engine.main.best.hash, height: engine.main.best.height, directory: [...engine.directory.keys()].sort(), mempool: [...engine.mempool.keys()].sort(), txpool: [...engine.txpool.keys()].sort(),
  ledger: snapL(engine.ledger), rejected: { ...engine.stats.rejected },
  chains: Object.fromEntries([...engine.chains].map(([id, c]) => [id, { heads: [...c.heads].sort(), n: c.blocks.size, orphans: [...c.orphans.keys()].sort() }])) });
const steps = [];
const op = async (label, chain, blocks) => { await engine.insert(chain, JSON.parse(JSON.stringify(blocks))); steps.push({ label, chain, blocks, after: observe() }); };
const mainBlock = async (parent, height, ts, entries, txs, who = 'M', bits = 18) => finish({ v: 1, chain: MAIN, parents: [parent], height, ts, author: ids[who].pub, payload: { type: 'register', entries, ...(txs ? { txs } : {}) } }, who, bits);
const genesis = async (who, payload, ts) => finish({ v: 1, chain: null, parents: [], height: 0, ts, author: ids[who].pub, payload: { type: 'genesis', name: 'G ' + who, about: '', ...payload } }, who, 0);

const chA = await genesis('A', { kind: 'channel', avatar: '', comments: 'anyone' }, T0);
const listB = await genesis('B', { kind: 'list' }, T0 + 1);
const m1 = await mainBlock(MAIN, 1, T0 + 1000, [chA, listB], [await tx('A', 1, 'starter', true, T0 + 500), await tx('B', 1, 'starter', true, T0 + 600)]);
await op('block 1: two registrations and two starter claims', MAIN, [m1]);
const m2 = await mainBlock(m1.hash, 2, T0 + 2000, [], [await tx('A', 2, 'pay', [{ to: ids.B.pub, amt: 100 }], T0 + 1500), await tx('A', 3, 'name', { space: '@', chain: chA.hash, handle: 'cooking' }, T0 + 1600)]);
const m2b = await mainBlock(m1.hash, 2, T0 + 2100, [], [await tx('B', 2, 'pay', [{ to: ids.A.pub, amt: 7 }], T0 + 1700)]);
await op('block 2 and a competing block 2', MAIN, [m2, m2b]);
const top = engine.main.best.hash === m2.hash ? m2b : m2;   // build on the losing branch: a reorganisation
const m3 = await mainBlock(top.hash, 3, T0 + 3000, [await genesis('Cc', { kind: 'group', avatar: '', join: 'open' }, T0 + 2500)], null);
await op('block 3 on the other branch (reorg)', MAIN, [m3]);
await op('not enough work', MAIN, [await mainBlock(m3.hash, 4, T0 + 4000, [], [await tx('Cc', 1, 'starter', true, T0 + 3500)], 'M', 4)]);
await op('a second starter claim', MAIN, [await mainBlock(m3.hash, 4, T0 + 4100, [], [await tx('A', engine.ledger.seq.get(ids.A.pub) + 1, 'starter', true, T0 + 3600)])]);
const many = []; for (let i = 0; i < 17; i++) many.push(await genesis('Cc', { kind: 'list' }, T0 + 10 + i));
await op('17 registrations (16 allowed)', MAIN, [await mainBlock(m3.hash, 4, T0 + 4200, many, null)]);
const badSig = await mainBlock(m3.hash, 4, T0 + 4300, [], [await tx('Cc', 1, 'starter', true, T0 + 3700)]); badSig.sig = badSig.sig.replace(/^./, c => c === 'A' ? 'B' : 'A');
await op('bad signature', MAIN, [badSig]);
await op('a name for a list under @ (wrong kind)', MAIN, [await mainBlock(m3.hash, 4, T0 + 4400, [], [await tx('B', engine.ledger.seq.get(ids.B.pub) + 1, 'name', { space: '@', chain: listB.hash, handle: 'news' }, T0 + 3800)])]);
const m4 = await mainBlock(m3.hash, 4, T0 + 5000, [], [await tx('Cc', 1, 'starter', true, T0 + 4500)]);
await op('block 4', MAIN, [m4]);
// the channel: a video by its owner, comments by others (with and without work), orphans, a wrong height, a key handover
const cb = async (who, parents, height, payload, ts, bits = 0) => finish({ v: 1, chain: chA.hash, parents, height, ts, author: ids[who].pub, payload }, who, bits);
const v1 = await cb('A', [chA.hash], 1, { type: 'video', title: 'Soup', desc: '', infohash: 'ab'.repeat(20), file: 's.mp4', size: 5, thumb: '' }, T0 + 6000);
const c1 = await cb('B', [v1.hash], 2, { type: 'comment', target: v1.hash, text: 'yum' }, T0 + 6100, 10);
const c2 = await cb('B', [v1.hash], 2, { type: 'comment', target: v1.hash, text: 'no work' }, T0 + 6200, 0);
await op('a comment before its video (orphan), then the video', chA.hash, [c1]);
await op('the video arrives: the comment follows', chA.hash, [v1]);
await op('a comment without anti-spam work', chA.hash, [c2]);
await op('wrong height', chA.hash, [await cb('A', [v1.hash], 5, { type: 'post', text: 'hi' }, T0 + 6300)]);
await op('malformed (no parents)', chA.hash, [{ ...(await cb('A', [v1.hash], 2, { type: 'post', text: 'x' }, T0 + 6400)), parents: [] }]);
const rk = await cb('A', [c1.hash], 3, { type: 'rekey', key: ids.Cc.pub, proof: await S.Cc.sign(`opentube-rekey:${chA.hash}:${ids.Cc.pub}`) }, T0 + 6500);
const rkBad = await cb('A', [c1.hash], 3, { type: 'rekey', key: ids.B.pub, proof: await S.A.sign('nope') }, T0 + 6600);
await op('a key handover without the new key agreeing', chA.hash, [rkBad]);
await op('a key handover', chA.hash, [rk]);
await op('the old key posts (not the owner any more)', chA.hash, [await cb('A', [rk.hash], 4, { type: 'post', text: 'still me?' }, T0 + 6700)]);
await op('the new key posts', chA.hash, [await cb('Cc', [rk.hash], 4, { type: 'post', text: 'new owner' }, T0 + 6800)]);
fs.writeFileSync(path.join(here, 'data/engine.json'), JSON.stringify({ steps, now: Date.now(), mainGenesis: C.MAIN_GENESIS }));
console.log(steps.map(s => `${s.label}: main #${s.after.height}, rejected ${JSON.stringify(s.after.rejected)}`).join('\n'));
