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
// ---- validPayload: a chain of every kind with an owner history, then candidate blocks of every type by every kind
// of author (owner, a member, a linked computer, a clerk, a moderator, a stranger), with and without anti-spam work
const ids = { owner: me.pub, A: (await C.newIdentity()).pub, B: (await C.newIdentity()).pub, R: (await C.newIdentity()).pub, Cl: (await C.newIdentity()).pub, M: (await C.newIdentity()).pub };
const hashWith = (zeros, tag) => { const h = [...C.canon([tag, rnd()])].reduce((a, c) => (a * 31 + c.charCodeAt(0)) >>> 0, 7).toString(16).padStart(8, '0'); return ('0'.repeat(zeros) + 'f' + h.repeat(8)).slice(0, 64); };
const hexOf = (n) => (n.toString(16) + 'c'.repeat(64)).slice(0, 64);
const kinds = {
  channel: { avatar: '', comments: 'approved' }, list: {}, group: { avatar: '', join: 'approved' }, circle: { enc: 'e'.repeat(87) },
  org: { avatar: '' }, case: { parent: hex64 }, square: null, mail: null,
};
const payloadChains = [], payloadCases = [];
for (const [kind, extra] of Object.entries(kinds)) {
  const g = extra ? base({ kind, ...extra }) : { v: 1, chain: null, parents: [], height: 0, ts: Date.UTC(2026, 10, 1), author: 'A'.repeat(43), payload: { type: 'genesis', kind, name: (kind === 'mail' ? 'Messages ' : 'Public posts ') + '2026-11', about: '', month: '2026-11' }, nonce: 0, sig: 'square' };
  g.hash = await C.hashBlock(g);
  const ch = new C.Chain(g.hash, g), hist = [], refs = {};
  let tip = g.hash, h = 0;
  const add = (author, payload, name, zeros = 12) => { const b = { v: 1, chain: g.hash, parents: [tip], height: ++h, ts: Date.UTC(2026, 10, 2) + h, author, payload, nonce: 0, hash: hashWith(zeros, name + h), sig: 's' }; ch.add(b); hist.push(b); tip = b.hash; if (name) refs[name] = b.hash; return b; };
  const own = g.author;
  if (!['square', 'mail'].includes(kind)) {
    add(own, { type: 'profile', comments: 'approved' }, 'profile');
    add(own, { type: 'video', title: 'V1', desc: '', infohash: 'ab'.repeat(20), file: 'v.mp4', size: 10, thumb: '', comments: 'approved' }, 'V1');
    add(own, { type: 'video', title: 'V2', desc: '', infohash: 'cd'.repeat(20), file: 'w.mp4', size: 10, thumb: '' }, 'V2');
    add(own, { type: 'live', title: 'L1', stream: hexOf(1), mime: 'video/webm' }, 'L1');
    add(own, { type: 'relay', key: ids.R, on: true }, 'relayOn');
    add(ids.R, { type: 'live', title: 'L2', stream: hexOf(2), mime: 'video/mp4' }, 'L2');
    add(own, { type: 'allow', key: ids.A }, 'allowA');
    add(own, { type: 'revoke', key: ids.B }, 'revokeB');
    add(own, { type: 'settings', target: refs.V1, comments: 'anyone' }, 'setV1');
    add(own, { type: 'role', key: ids.Cl, role: 'clerk', on: true }, 'roleC');
    add(own, { type: 'role', key: ids.M, role: 'moderator', on: true }, 'roleM');
    add(own, { type: 'post', text: 'hello' }, 'P1');
    add(ids.A, { type: 'comment', target: refs.V1, text: 'nice' }, 'C1');
    add(ids.A, { type: 'post', text: 'member post' }, 'P2');
  } else add(ids.A, kind === 'square' ? { type: 'post', text: 'first' } : { type: 'msg', tag: hex64, salt: 'ab'.repeat(16), eph: 'x'.repeat(87), iv: 'iv', ct: 'ct' }, 'P1');
  const T = (r) => refs[r] || hex64;
  const templates = {
    rekey: { type: 'rekey', key: ids.A, proof: 'p' }, child: { type: 'child', genesis: { ...base({ kind: 'case', parent: g.hash }), hash: hex64, sig: 's' } },
    class: { type: 'class', id: 'spam', name: 'Spam' }, role: { type: 'role', key: ids.B, role: 'judge', on: true }, flag: { type: 'flag', ref: { chain: hex64, block: hex64 }, cls: 'spam', on: true },
    submit: { type: 'submit', what: 'complaint', title: 'T', text: 'x' }, report: { type: 'report', ref: { chain: hex64, block: hex64 }, cls: 'spam' }, petition: { type: 'petition', id: 'more-parks', name: 'Parks' },
    filing: { type: 'filing', what: 'motion', title: 'T', text: 'x', by: ids.A }, status: { type: 'status', state: 'open' }, order: { type: 'order', title: 'T', text: 'x', judge: ids.A, sig: 's', buyerShare: 0.5 },
    sealed: { type: 'sealed', iv: 'iv', ct: 'ct', keys: [{ t: 't', iv: 'i', w: 'w' }], target: T('P1') }, msg: { type: 'msg', tag: hex64, salt: 'ab'.repeat(16), eph: 'x'.repeat(87), iv: 'iv', ct: 'ct' },
    post: { type: 'post', text: 'hi', media: [{ ih: 'ab'.repeat(20), name: 'a.png', mime: 'image/png', size: 5 }] }, comment: { type: 'comment', target: T('V1'), text: 'hey', name: 'Ann' },
    commentP: { type: 'comment', target: T('P1'), text: 'hey' }, commentL2: { type: 'comment', target: T('L2'), text: 'hey' }, commentV2: { type: 'comment', target: T('V2'), text: 'hey' },
    hide: { type: 'hide', target: T('C1') }, vote: { type: 'vote', target: T('V1'), v: 1 }, voteC: { type: 'vote', target: T('C1'), v: -1 }, voteP: { type: 'vote', target: T('P1'), v: 0 },
    video: { type: 'video', title: 'New', desc: 'd', infohash: 'ef'.repeat(20), file: 'f.mp4', size: 3, thumb: '', tags: ['cooking', 'pc-games'], stream: hexOf(3) },
    live: { type: 'live', title: 'Now', stream: hexOf(4), mime: 'video/webm' }, liveend: { type: 'liveend', stream: hexOf(4), outcome: 'ended', stats: { peak: 3, unique: 5, secs: 100, top: [[ids.A, 50]] }, next: hexOf(5) },
    settings: { type: 'settings', target: T('V2'), comments: 'off' }, allow: { type: 'allow', key: ids.B }, revoke: { type: 'revoke', key: ids.A },
    profile: { type: 'profile', name: 'New name', about: 'a', links: [{ label: 'Site', url: 'https://x.example/a' }], place: { lat: 1, lon: 2, label: 'Here' }, comments: 'off', location: 'Paris' },
    profileOrg: { type: 'profile', orgType: 'llc', details: { founded: 2020 }, people: [{ key: ids.A, role: 'CEO' }], jurisdiction: 'US' }, profileGroup: { type: 'profile', join: 'open' },
    add: { type: 'add', target: hex64, video: hex64, note: 'good' },
    ad: { type: 'ad', title: 'Buy', file: 'ad.mp4', thumb: '', url: 'https://shop.example', terms: { v: 1, kind: 'adterms', advertiser: own, channel: g.hash, infohash: 'ab'.repeat(20), size: 5, duration: 30, perView: 10, maxViews: 100, created: 1, sig: 's' }, tags: ['x'], category: 'games' },
    sponsor: { type: 'sponsor', advertiser: hex64 }, relay: { type: 'relay', key: ids.B, on: false },
  };
  const fields = ['type', 'text', 'target', 'title', 'key', 'on', 'v', 'name', 'media', 'stream', 'mime', 'relay', 'outcome', 'stats', 'next', 'comments', 'tags', 'image', 'id', 'cls', 'role', 'what', 'judge', 'buyerShare', 'sig', 'links', 'place', 'join', 'url', 'terms', 'genesis', 'by', 'state', 'keys', 'eph', 'ct', 'iv', 'note', 'size', 'thumb', 'about', 'details', 'people', 'orgType', 'jurisdiction', 'video', 'advertiser', 'ref', 'proof', 'category', 'location', 'avatar'];
  const values = [undefined, null, '', ' ', 'x', 'spam', 12345, 1, 0, -1, -0, 0.5, 2, 1e15, true, false, [], {}, ['a'], hex64, ids.A, ids.owner, 'https://ok.example', 'https://bad﻿url', 'http://x', 'y'.repeat(141), 'z'.repeat(1001), 'w'.repeat(5001), T('V1'), T('C1'), T('P1'), T('L2'), T('V2'), g.hash, 'default', 'anyone', 'off', 'ended', 'clerk', 'moderator', 'complaint', 'open'];
  const authors = [own, ids.A, ids.B, ids.R, ids.Cl, ids.M, (await C.newIdentity()).pub];
  payloadChains.push({ kind, genesis: g, blocks: hist });
  const ci = payloadChains.length - 1;
  for (let i = 0; i < 2500; i++) {
    const p = clone(pick(Object.values(templates)));
    if (rnd() < 0.55) { set(p, pick(fields), pick(values)); if (rnd() < 0.3) set(p, pick(fields), pick(values)); }
    const b = { v: 1, chain: g.hash, parents: [rnd() < 0.8 ? tip : pick([g.hash, ...hist.map(x => x.hash)])], height: 0, ts: rnd() < 0.9 ? Date.UTC(2026, 10, 3) : pick([Date.UTC(2026, 9, 31, 23, 55), Date.UTC(2026, 11, 1, 0, 5), Date.UTC(2027, 0, 1)]), author: pick(authors), payload: p, nonce: 0, hash: hashWith(pick([0, 4, 9, 10, 12]), 'cand' + i), sig: 's' };
    b.height = ch.blocks.get(b.parents[0]).height + 1;
    if (rnd() < 0.05) b.hash = 'xz' + b.hash.slice(2);   // not hex: zeroBits must still agree
    let ok; try { ok = !!C.validPayload(ch, clone(b), { commentDifficulty: 10 }); } catch (e) { ok = 'throws'; }
    payloadCases.push({ c: ci, b, ok });
  }
}
fs.writeFileSync(path.join(here, 'data/rules.json'), JSON.stringify({ generatedAt: Date.now(), genesis: genesisCases, signed: signedCases, numbers: numberCases, chains: chainCases }));
fs.writeFileSync(path.join(here, 'data/payload.json'), JSON.stringify({ chains: payloadChains, cases: payloadCases }));
console.log(`validPayload: ${payloadCases.length} cases on ${payloadChains.length} chains (${payloadCases.filter(x => x.ok === true).length} valid, ${payloadCases.filter(x => x.ok === 'throws').length} throw)`);
console.log(`genesis ${genesisCases.length} (${genesisCases.filter(x => x.ok).length} valid) · signed ${signedCases.length} (${signedCases.filter(x => x.ok).length} valid) · numbers ${numberCases.length} · chains ${chainCases.length}`);
