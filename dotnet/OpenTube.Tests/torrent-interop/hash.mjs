import createTorrent from 'create-torrent'; import parse from 'parse-torrent';
for (const f of process.argv.slice(2)) { const buf = await new Promise((r, j) => createTorrent(f, (e, t) => e ? j(e) : r(t))); console.log((await parse(buf)).infoHash, f); }
