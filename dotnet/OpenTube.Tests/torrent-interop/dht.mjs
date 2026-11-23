// The DHT against WebTorrent's (bittorrent-dht, what WebTorrent and many clients use).
// "node PORT": a DHT node only (the bootstrap node), prints its port. "leech IH BOOT": WebTorrent finds peers on the DHT
// (through BOOT) and downloads. "seed FILE BOOT": WebTorrent seeds and announces on the DHT, prints "infohash port".
import WebTorrent from 'webtorrent'; import DHT from 'bittorrent-dht'; import fs from 'fs';
const [mode, a, b] = process.argv.slice(2);
if (mode === 'node') {
  const d = new DHT({ bootstrap: false }); d.listen(Number(a), () => console.log('dht', d.address().port));
  setInterval(() => console.error('js dht nodes:', d.toJSON().nodes.length), 5000);
} else {
  const client = new WebTorrent({ dht: { bootstrap: [b] }, lsd: false, tracker: false, utp: false, webSeeds: false, natUpnp: false, natPmp: false });
  if (mode === 'leech') {
    const t0 = Date.now(); fs.rmSync('out-dht', { recursive: true, force: true });
    const t = client.add(a, { path: 'out-dht', announce: [] });
    t.on('wire', (w, addr) => console.error('peer', addr));
    t.on('done', () => { console.log('done', t.length, 'bytes in', ((Date.now() - t0) / 1000).toFixed(2), 's', t.files[0].path); client.destroy(); process.exit(0); });
    setTimeout(() => { console.log('TIMEOUT', t.progress); process.exit(1); }, 90000);
  } else {
    client.seed(a, { announce: [] }, (t) => { console.log(t.infoHash, client.torrentPort); t.on('wire', (w, addr) => console.error('peer', addr)); });
  }
}
