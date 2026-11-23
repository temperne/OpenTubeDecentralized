// mode "leech": WebTorrent downloads infohash from C# at port. mode "seed": WebTorrent seeds file, prints port.
import WebTorrent from 'webtorrent'; import fs from 'fs';
const [mode, a, b] = process.argv.slice(2);
const client = new WebTorrent({ dht: false, lsd: false, tracker: false, utp: false, webSeeds: false, secure: Number(process.env.SECURE ?? 1) });
if (mode === 'leech') {
  const t0 = Date.now(); fs.rmSync('out', { recursive: true, force: true });
  const t = client.add(a, { path: 'out', announce: [] });
  t.on('infoHash', () => t.addPeer('127.0.0.1:' + b));
  t.on('metadata', () => console.error('metadata from C# after', Date.now() - t0, 'ms'));
  t.on('done', () => { console.log('done', t.length, 'bytes in', ((Date.now() - t0) / 1000).toFixed(2), 's', t.files[0].path); client.destroy(); process.exit(0); });
  setTimeout(() => { console.log('TIMEOUT', t.progress); process.exit(1); }, 120000);
} else {
  client.seed(a, { announce: [] }, (t) => { console.log(t.infoHash, client.torrentPort); });
}
