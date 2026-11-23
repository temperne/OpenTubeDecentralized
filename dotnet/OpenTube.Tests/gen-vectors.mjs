// Makes test cases for the C# port from the JavaScript core itself: valid inputs, then many broken variants, each with
// the answer the JavaScript gives. Run: node dotnet/OpenTube.Tests/gen-vectors.mjs  (writes data/rules.json)
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const here = path.dirname(fileURLToPath(import.meta.url));
const html = fs.readFileSync(path.join(here, '../../index.html'), 'utf8');
new Function(/<script id="opentube-core">([\s\S]*?)<\/script>/.exec(html)[1])();
const C = globalThis.OpenTubeCore;
let seed = 42; const rnd = () => (seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648;
const pick = (a) => a[Math.floor(rnd() * a.length)];
const clone = (x) => JSON.parse(JSON.stringify(x));

const me = await C.newIdentity(), other = await C.newIdentity();
const key = me.pub, hex64 = 'ab'.repeat(32), img = 'data:image/png;base64,iVBORw0KGgo=';
// ---- validGenesis: a valid genesis of every kind, then broken variants
const base = (payload) => ({ v: 1, chain: null, parents: [], height: 0, ts: 1795000000000, author: key, payload: { type: 'genesis', name: 'Name', about: 'About', ...payload }, nonce: 5 });
const valid = [
  base({ kind: 'channel', avatar: '', comments: 'anyone' }), base({ kind: 'channel', avatar: img, comments: 'off' }),
  base({ kind: 'list' }), base({ kind: 'list', ranking: true }),
  base({ kind: 'group', avatar: '', join: 'open' }),
  base({ kind: 'circle', enc: 'x'.repeat(87) }),
  base({ kind: 'org', avatar: '', jurisdiction: 'United States › California', orgType: 'llc', details: { founded: 2020, open: true, city: 'Paris' }, people: [{ key, role: 'CEO', share: 50 }] }),
  base({ kind: 'org', avatar: '', parent: hex64 }),
  base({ kind: 'case', parent: hex64, listing: hex64 }),
  { v: 1, chain: null, parents: [], height: 0, ts: Date.UTC(2026, 10, 1), author: 'A'.repeat(43), payload: { type: 'genesis', kind: 'square', name: 'Public posts 2026-11', about: '', month: '2026-11' }, nonce: 0, sig: 'square' },
  { v: 1, chain: null, parents: [], height: 0, ts: Date.UTC(2027, 0, 1), author: 'A'.repeat(43), payload: { type: 'genesis', kind: 'mail', name: 'Messages 2026-13', about: '', month: '2026-13' }, nonce: 0, sig: 'square' },
];
const weird = [undefined, null, 0, 1, -1, 1.5, '', ' ', '﻿', '\u0085x', 'x', 'X'.repeat(61), 'y'.repeat(2001), true, false, [], {}, [1], { a: 1 }, hex64, key, img, 'data:image/gif;base64,AA==', 'Name ', 1e15, 1e21, -0, NaN];
const paths = ['v', 'chain', 'parents', 'height', 'author', 'nonce', 'ts', 'sig', 'payload', 'payload.type', 'payload.kind', 'payload.name', 'payload.about', 'payload.avatar', 'payload.comments',
  'payload.ranking', 'payload.join', 'payload.enc', 'payload.parent', 'payload.listing', 'payload.jurisdiction', 'payload.orgType', 'payload.details', 'payload.details.founded', 'payload.details.Bad',
  'payload.details.city', 'payload.people', 'payload.people.0', 'payload.people.0.share', 'payload.people.0.key', 'payload.people.0.role', 'payload.month'];
const set = (o, p, v) => { const ks = p.split('.'); let x = o; for (const k of ks.slice(0, -1)) { if (x[k] === null || typeof x[k] !== 'object') x[k] = {}; x = x[k]; } if (v === undefined) delete x[ks.at(-1)]; else x[ks.at(-1)] = v; };
const genesis = [];
for (const g of valid) genesis.push(g);
for (let i = 0; i < 6000; i++) { const g = clone(pick(valid)); set(g, pick(paths), pick(weird)); if (rnd() < 0.3) set(g, pick(paths), pick(weird)); genesis.push(g); }
const genesisCases = genesis.map(g => ({ g: JSON.parse(JSON.stringify(g) ?? 'null'), ok: C.validGenesis(JSON.parse(JSON.stringify(g) ?? 'null')) }));

// ---- checkSigned: signed blocks (numbers of every shape in the payload), then tampered ones
const signer = await C.signer(me);
const signed = [];
const nums = [0, -0, 1, -1, 0.1, 1.5e-7, 123456789012345680000, 1e21, 2 ** 53, -(2 ** 53) + 1, 1e-7, 5e-324, 1.7976931348623157e308, 0.000001, 1 / 3, 1234.5678];
for (let i = 0; i < 300; i++) {
  const b = { v: 1, chain: hex64, parents: [hex64], height: 1 + i, ts: Date.now() - 1000, author: key, payload: { type: 'post', text: pick(['hi', 'é😀', 'a b', '"q"\\', '\u0001']), n: pick(nums), list: [pick(nums), 'x', null, true] }, nonce: Math.floor(rnd() * 1e9) };
  b.hash = await C.hashBlock(b); b.sig = await signer.sign(b.hash); signed.push(b);
}
const tamper = [
  (b) => { b.payload.text += '!'; }, (b) => { b.nonce += 1; }, (b) => { b.sig = b.sig.slice(0, -2) + (b.sig.at(-2) === 'A' ? 'B' : 'A') + b.sig.at(-1); },
  (b) => { b.author = other.pub; }, (b) => { b.hash = 'z' + b.hash.slice(1); }, (b) => { b.ts = Date.now() + 3600000; }, (b) => { b.nonce = 1.5; }, (b) => { b.height = '2'; },
  (b) => { delete b.sig; }, (b) => { b.sig = 12; }, (b) => { b.author = 'short'; }, (b) => { b.sig = '!!!' + b.sig.slice(3); },
];
const blocks = [...signed.slice(0, 150)];
for (const b of signed.slice(150)) { const t = clone(b); pick(tamper)(t); blocks.push(t); }
{ const t = clone(signed[0]); t.payload.big = 'x'.repeat(300000); blocks.push(t); }   // over the size limit (one is enough)
const signedCases = [];
for (const b of blocks) signedCases.push({ b, ok: await C.checkSigned(clone(b)) });

// ---- numbers, as canon prints them
const numberCases = [...nums, 100, 1e20, 1e-6, 1e-5, 123e-20, 2 ** 31, 0.5, -1e-7, 9007199254740993, 4.35, 0.1 + 0.2].map(n => ({ n, s: C.canon(n) }));

// ---- Chain: random DAGs, then heads, ancestors and missingFor
const chainCases = [];
for (let t = 0; t < 40; t++) {
  const g = { hash: 'g' + t, parents: [], height: 0, ts: 0, payload: {} };
  const ch = new C.Chain(g.hash, g), all = [g], adds = [];
  for (let i = 0; i < 30; i++) {
    const ps = [...new Set([pick(all), ...(rnd() < 0.4 ? [pick(all)] : [])].map(x => x.hash))];
    const b = { hash: `b${t}-${i}`, parents: ps, height: Math.max(...ps.map(p => ch.blocks.get(p).height)) + 1, ts: Math.floor(rnd() * 3), payload: { i } };
    ch.add(b); all.push(b); adds.push(b);
  }
  const want = [pick(all).hash, pick(all).hash, 'nope'], have = [pick(all).hash, 'nope2'], ancQ = [pick(all).hash, pick(all).hash];
  chainCases.push({ genesis: g, adds, heads: [...ch.heads], ancQ, anc: [...ch.ancestors(ancQ)], want, have, missing: ch.missingFor(want, have).map(b => b.hash) });
}
fs.writeFileSync(path.join(here, 'data/rules.json'), JSON.stringify({ generatedAt: Date.now(), genesis: genesisCases, signed: signedCases, numbers: numberCases, chains: chainCases }));
console.log(`genesis ${genesisCases.length} (${genesisCases.filter(x => x.ok).length} valid) · signed ${signedCases.length} (${signedCases.filter(x => x.ok).length} valid) · numbers ${numberCases.length} · chains ${chainCases.length}`);
