// Random transaction histories through the JavaScript ledger (checkTx trusted + Ledger.apply + endBlock), recording
// every verdict and the whole state after every block, for the C# port to replay. Run: node dotnet/OpenTube.Tests/gen-ledger.mjs
import fs from 'node:fs'; import path from 'node:path'; import crypto from 'node:crypto'; import { fileURLToPath } from 'node:url';
const here = path.dirname(fileURLToPath(import.meta.url));
new Function(/<script id="opentube-core">([\s\S]*?)<\/script>/.exec(fs.readFileSync(path.join(here, '../../index.html'), 'utf8'))[1])();
const C = globalThis.OpenTubeCore;
let seed = 7; const rnd = () => (seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648;
const pick = (a) => a[Math.floor(rnd() * a.length)], int = (a, b) => a + Math.floor(rnd() * (b - a + 1));
const ids = []; for (let i = 0; i < 8; i++) ids.push(await C.newIdentity());
const keys = ids.map(i => i.pub), signers = await Promise.all(ids.map(i => C.signer(i)));
const hex = (n, len = 64) => (n.toString(16)).padStart(len, '0').slice(-len);
let txn = 0; const txHash = (starter) => '0'.repeat(starter ? 6 : 3) + hex(++txn, starter ? 58 : 61);
const snap = (L) => C.canon({
  bal: Object.fromEntries(L.bal), seq: Object.fromEntries(L.seq), starters: [...L.starters].sort(), paid: Object.fromEntries(L.paid),
  hosts: Object.fromEntries([...L.hosts].map(([k, v]) => [k, [...v].sort()])), prices: L.prices, minted: L.minted, burned: L.burned, txCount: L.txCount, clock: L.clock,
  claimed: [...L.claimed].sort(), slotUsed: [...L.slotUsed].sort(), adViews: Object.fromEntries(L.adViews), adPaid: Object.fromEntries(L.adPaid), adBusy: Object.fromEntries(L.adBusy), views: Object.fromEntries(L.views),
  served: Object.fromEntries([...L.served].map(([k, m]) => [k, Object.fromEntries(m)])), receipts: [...L.receipts].sort(), rcptUse: Object.fromEntries(L.rcptUse),
  poolFees: Object.fromEntries([...L.poolFees].map(([k, v]) => [String(k), v])), settled: [...L.settled].sort((a, b) => a - b), earned: Object.fromEntries(L.earned),
  names: Object.fromEntries(L.names), nameOf: Object.fromEntries(L.nameOf), htlcs: Object.fromEntries(L.htlcs), offers: Object.fromEntries(L.offers), domains: Object.fromEntries(L.domains),
  linkVotes: [...L.linkVotes].sort(), appealDays: Object.fromEntries(L.appealDays), msgkeys: Object.fromEntries(L.msgkeys), superior: Object.fromEntries(L.superior),
  judges: Object.fromEntries([...L.judges].map(([k, v]) => [k, [...v].sort()])), owners: Object.fromEntries(L.owners), retired: [...L.retired].sort(),
  tabsPaid: Object.fromEntries(L.tabsPaid), viewers: [...L.viewers].sort(), certs: Object.fromEntries(L.certs), listings: Object.fromEntries(L.listings) });

const runs = [];
for (let run = 0; run < 30; run++) {
  let L = new C.Ledger(); const seq = new Map(); let ts = Date.UTC(2026, 10, 19, 6) + run * 3600000;
  const steps = [], chains = [hex(1), hex(2), hex(3)], videos = [hex(11), hex(12), hex(13)];
  const preimages = [], preOf = new Map(), refs = {}; let scripted = [];
  // a scripted opening that walks every multi-step flow once: escrow → dispute → ruling → higher court → appeal → final
  // ruling; a swap unlocked with its secret and one refunded after its deadline; an ad view → certificate → transfer →
  // redeem; an auction with bids settled when it ends; a held sale released by its buyer
  {
    const K = keys, script = [], send = (fi, act, x, h) => script.push({ fi, act, x, h });
    for (let i = 0; i < K.length; i++) send(i, 'starter', true);
    send(1, 'list', { kind: 'fixed', title: 'Desk', price: 50, fee: 30, arbiter: K[3], hold: 1 }, 'L1');
    send(2, 'buy', { id: '@L1' });
    send(2, 'dispute', { id: '@L1', case: hex(70) });
    send(3, 'rule', { id: '@L1', buyerShare: 0.5 });
    send(3, 'court', { parent: K[4], sig: 's' });
    send(1, 'appeal', { id: '@L1', case: hex(71) });
    send(4, 'rule', { id: '@L1', buyerShare: 1 });
    send(1, 'list', { kind: 'fixed', title: 'Lamp', price: 20, fee: 30, arbiter: K[5] }, 'L2');
    send(6, 'buy', { id: '@L2' });
    send(6, 'release', { id: '@L2' });
    send(1, 'list', { kind: 'auction', title: 'Car', price: 10, fee: 30, ends: ts + 30 * 60000 }, 'L3');
    send(2, 'bid', { id: '@L3', amt: 12 }); send(6, 'bid', { id: '@L3', amt: 20 });
    const pre = hex(424242), hh = crypto.createHash('sha256').update(Buffer.from(pre, 'hex')).digest('hex');
    send(0, 'htlc', { to: K[1], amt: 40, hash: hh, expires: ts + 20 * 60000 }, 'H1');
    send(1, 'unlock', { id: '@H1', preimage: pre });
    send(0, 'htlc', { to: K[2], amt: 30, hash: hex(9), expires: ts + 11 * 60000 }, 'H2');
    for (const t2 of script) t2.ts = ts;
    scripted = script;
  }
  for (let blk = 0; blk < 40; blk++) {
    const txs = [];
    if (scripted.length && blk < 3) {   // the scripted opening, in its first blocks (refund after the deadline in block 2)
      const now = blk === 2 ? scripted.filter(x => x.act === 'refund') : scripted.filter(x => x.act !== 'refund' && !x.done);
      if (blk === 1) { const h2 = refs.H2; if (h2) scripted.push({ fi: 0, act: 'refund', x: { id: h2 } }); }
      for (const st of blk === 0 ? scripted.filter(x => x.act !== 'refund') : blk === 2 ? scripted.filter(x => x.act === 'refund') : []) {
        const from = keys[st.fi], x = JSON.parse(JSON.stringify(st.x).replace(/"@(\w+)"/g, (m, r) => JSON.stringify(refs[r] || hex(1))));
        const t = { v: 2, kind: 'tx', from, seq: (seq.get(from) || 0) + 1, ts, nonce: 0, sig: 's', [st.act]: x, hash: txHash(st.act === 'starter') };
        const checked = await C.checkTx(t, true), why = checked ? L.apply(t) : 'checkTx';
        if (checked && why === null) { seq.set(from, t.seq); if (st.h) refs[st.h] = t.hash; }
        txs.push({ t, why: why ?? null }); ts += 1000;
      }
      if (blk === 2) scripted = [];
    }
    for (let i = 0; i < int(2, 9); i++) {
      const fi = int(0, keys.length - 1), from = keys[fi], other = pick(keys.filter(k => k !== from));
      const s = (seq.get(from) || 0) + 1;
      const base = { v: 2, kind: 'tx', from, seq: rnd() < 0.05 ? s + 1 : s, ts, nonce: 0, sig: 's' };
      const listing = pick([...L.listings.keys(), hex(99)]), htlc = pick([...L.htlcs.keys(), hex(98)]), offer = pick([...L.offers.keys(), hex(97)]);
      const certIds = [...L.certs.keys()].slice(0, int(1, 3));
      const kinds = ['starter', 'starter', 'pay', 'post', 'view', 'vote', 'host', 'name', 'bind', 'retire', 'list', 'list', 'buy', 'bid', 'unlist', 'release', 'dispute', 'rule', 'appeal', 'court', 'judge', 'courtcfg',
        'htlc', 'unlock', 'refund', 'offer', 'endoffer', 'linkvote', 'msgkey', 'serve', 'tabs', 'claim', 'redeem', 'certs'];
      const k = pick(kinds); let x;
      switch (k) {
        case 'starter': x = true; break;
        case 'pay': x = [{ to: other, amt: int(1, 400) }]; break;
        case 'post': x = { chain: pick(chains), video: pick(videos), size: int(1, 3e9), amt: int(1, 300) }; break;
        case 'view': x = { chain: pick(chains), video: pick(videos), to: other, amt: int(1, 10) }; break;
        case 'vote': x = { chain: pick(chains), target: pick(videos), to: other, v: pick([1, -1]), amt: int(1, 20) }; break;
        case 'host': x = { chain: pick(chains), on: rnd() < 0.7 }; break;
        case 'name': x = { space: pick(['@', '~']), chain: pick(chains), handle: pick(['cooking', 'news', null, 'cooking']) }; break;
        case 'bind': x = { key: other, sig: 's' }; break;
        case 'retire': if (rnd() > 0.15) { x = { to: other, sig: 's' }; } else x = { to: other, sig: 's' }; break;
        case 'list': x = { kind: pick(['fixed', 'auction', 'fixed']), title: 'Bike', price: int(5, 200), fee: int(0, 40), ...(rnd() < 0.5 ? { arbiter: other, hold: pick([3, undefined, '2']) } : {}), ...(rnd() < 0.3 ? { qty: int(1, 3) } : {}),
          ends: ts + int(-1, 3) * 3600000, ...(rnd() < 0.2 && certIds.length ? { certs: certIds } : {}) }; if (x.qty > 1) { delete x.arbiter; x.kind = 'fixed'; } break;
        case 'buy': case 'unlist': case 'release': x = { id: listing }; break;
        case 'bid': x = { id: listing, amt: int(5, 400) }; break;
        case 'dispute': case 'appeal': x = { id: listing, ...(rnd() < 0.5 ? { case: hex(77) } : {}) }; break;
        case 'rule': x = { id: listing, buyerShare: pick([0, 0.5, 1]) }; break;
        case 'court': x = { parent: other, sig: 's' }; break;
        case 'judge': x = { key: other, on: rnd() < 0.7 }; break;
        case 'courtcfg': x = { appealDays: int(0, 3) }; break;
        case 'htlc': { const pre = hex(int(1, 1e9)) ; preimages.push(pre); const hh = crypto.createHash('sha256').update(Buffer.from(pre, 'hex')).digest('hex'); preOf.set(hh, pre); x = { to: other, amt: int(1, 200), hash: hh, expires: ts + pick([5, 30, 120, 60 * 24 * 40]) * 60000 }; break; }
        case 'unlock': x = { id: htlc, preimage: pick([...preimages, hex(5)]) }; break;
        case 'refund': case 'endoffer': x = { id: k === 'refund' ? htlc : offer }; break;
        case 'offer': x = { side: 'sell', asset: 'BTC', amt: int(1, 100), rate: '0.00001', methods: ['lightning'] }; break;
        case 'linkvote': { const url = pick(['https://www.example.com/a', 'https://news.example.org/b', 'ftp://x.example/c']); x = { url, domain: C.domainOf(url), v: pick([1, -1]), ...(rnd() < 0.6 ? { to: other } : {}), amt: int(1, 20) }; break; }
        case 'msgkey': x = { key: pick(['m'.repeat(87), 'n'.repeat(87)]), on: rnd() < 0.7 }; break;
        case 'serve': { const e = C.epochOf(ts) - int(0, 3); x = { epoch: e, receipts: [{ v: 1, kind: 'receipt', res: pick(['bytes', 'uptime', 'tracker', 'storage']), provider: from, by: other, epoch: e, amt: int(1, 5000), sig: 's' }],
          ...(rnd() < 0.3 ? { grant: { v: 1, kind: 'nodegrant', user: other, at: 1, sig: 's' } } : {}) }; break; }
        case 'tabs': x = [{ v: 1, kind: 'tab', payer: other, payee: from, chain: pick(chains), video: pick(videos), creator: pick(keys), n: int(1, 50), amt: int(1, 60), at: ts, sig: 's' }]; break;
        case 'claim': {   // real advertiser-signed terms, no witnesses required
          const ai = int(0, keys.length - 1), terms = { v: 1, kind: 'adterms', advertiser: keys[ai], channel: pick(chains), infohash: 'ab'.repeat(20), size: 1000, duration: int(5, 30), perView: int(2, 20), maxViews: int(1, 4), created: 1 };
          terms.sig = await signers[ai].sign(C.preimage ? 'opentube-adterms:' + C.canon(terms) : ''); x = { terms, slot: Math.floor(ts / 10000) - int(0, 2), video: pick(videos), creator: pick(keys), witnesses: [], ...(rnd() < 0.3 ? { bytes: int(1, 1500) } : {}) }; break; }
        case 'redeem': x = { ids: certIds.length ? certIds : [hex(55)] }; break;
        case 'certs': x = { to: other, ids: certIds.length ? certIds : [hex(55)] }; break;
      }
      // aim at the one party allowed to act, most of the time (otherwise nearly everything is refused)
      let by = null;
      if (rnd() < 0.8) {
        const Ls = [...L.listings.entries()], E = (l) => l[1].escrow;
        const one = (arr, f) => { const c = arr.filter(f); return c.length ? pick(c) : null; };
        if (k === 'bid') { const l = one(Ls, ([, v]) => v.kind === 'auction' && v.state === 'open' && v.ends > ts); if (l) { by = pick(keys.filter(q => q !== l[1].seller)); x = { id: l[0], amt: (l[1].high ? l[1].high.amt + int(1, 30) : l[1].price + int(0, 30)) }; } }
        if (k === 'buy') { const l = one(Ls, ([, v]) => v.kind === 'fixed' && v.state === 'open'); if (l) { by = pick(keys.filter(q => q !== l[1].seller)); x = { id: l[0] }; } }
        if (k === 'unlist') { const l = one(Ls, ([, v]) => v.state === 'open'); if (l) { by = l[1].seller; x = { id: l[0] }; } }
        if (k === 'release') { const l = one(Ls, (l) => E(l)?.state === 'held'); if (l) { by = E(l).buyer; x = { id: l[0] }; } }
        if (k === 'dispute') { const l = one(Ls, (l) => E(l)?.state === 'held'); if (l) { by = pick([E(l).buyer, l[1].seller]); x = { id: l[0], ...(rnd() < 0.5 ? { case: hex(77) } : {}) }; } }
        if (k === 'rule') { const l = one(Ls, (l) => E(l)?.state === 'disputed'); if (l) { const court = E(l).court || l[1].arbiter; by = pick([court, ...(L.judges.get(court) || [])]); x = { id: l[0], buyerShare: pick([0, 0.5, 1]) }; } }
        if (k === 'appeal') { const l = one(Ls, (l) => E(l)?.state === 'ruled'); if (l) { by = pick([E(l).buyer, l[1].seller]); x = { id: l[0], ...(rnd() < 0.5 ? { case: hex(78) } : {}) }; } }
        if (k === 'court') { const l = one(Ls, (l) => !!l[1].arbiter); if (l) { by = l[1].arbiter; x = { parent: pick(keys.filter(q => q !== by)), sig: 's' }; } }
        if (k === 'unlock') { const h = one([...L.htlcs.entries()], ([, v]) => v.state === 'locked'); if (h) { by = h[1].to; x = { id: h[0], preimage: preOf.get(h[1].hash) || hex(5) }; } }
        if (k === 'refund') { const h = one([...L.htlcs.entries()], ([, v]) => v.state === 'locked'); if (h) { by = h[1].from; x = { id: h[0] }; } }
        if (k === 'endoffer') { const o = one([...L.offers.entries()], ([, v]) => v.state === 'open'); if (o) { by = o[1].from; x = { id: o[0] }; } }
        if (k === 'redeem' || k === 'certs') { const c = one([...L.certs.entries()], ([, v]) => keys.includes(v.owner)); if (c) { by = c[1].owner; x = k === 'redeem' ? { ids: [c[0]] } : { to: pick(keys.filter(q => q !== by)), ids: [c[0]] }; } }
      }
      if (by && keys.includes(by)) { base.from = by; base.seq = (seq.get(by) || 0) + 1; }
      const t = { ...base, [k]: x, hash: txHash(k === 'starter') };
      const checked = await C.checkTx(t, true);
      const why = checked ? L.apply(t) : 'checkTx';
      if (checked && why === null) seq.set(t.from, t.seq);
      txs.push({ t, why: why ?? null });
      ts += int(1, 90) * 1000;
    }
    const miner = rnd() < 0.9 ? pick(keys) : null;
    L.endBlock(miner, ts); L = new C.Ledger(L);
    steps.push({ txs, miner, ts, state: snap(L) });
    ts += int(1, 40) * 60000;
  }
  runs.push(steps);
}
fs.writeFileSync(path.join(here, 'data/ledger.json'), JSON.stringify({ runs }));
const all = runs.flat().flatMap(s => s.txs), ok = all.filter(x => x.why === null).length;
console.log(`ledger: ${runs.length} histories, ${runs.flat().length} blocks, ${all.length} transactions (${ok} applied, ${all.filter(x => x.why === 'checkTx').length} rejected by checkTx, ${all.length - ok - all.filter(x => x.why === 'checkTx').length} refused by the ledger)`);
