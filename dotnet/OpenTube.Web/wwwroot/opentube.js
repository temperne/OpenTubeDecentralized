// The thin bindings OpenTube's C# website needs from the browser. Everything else is C# (WebAssembly).
// Storage: the same IndexedDB the JavaScript page used, so a browser keeps its key and its chains.
(() => {
  const open = () => new Promise((res, rej) => {
    const r = indexedDB.open('opentube-chain', 1);
    r.onupgradeneeded = () => { const d = r.result; d.createObjectStore('blocks', { keyPath: 'hash' }).createIndex('chain', 'chainId'); d.createObjectStore('kv'); d.createObjectStore('files', { keyPath: 'infohash' }); };
    r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error);
  });
  let dbp = null; const db = () => dbp ||= open();
  const tx = async (store, mode, fn) => { const d = await db(); return new Promise((res, rej) => { const t = d.transaction(store, mode); const r = fn(t.objectStore(store)); t.oncomplete = () => res(r?.result); t.onerror = () => rej(t.error); }); };
  window.otStore = {
    // the identity ({pub, priv}) and channelKeys are kept as objects (the JavaScript page's format); C# passes JSON text
    get: async (k) => { const v = await tx('kv', 'readonly', s => s.get(k)); return v == null ? null : typeof v === 'string' ? v : JSON.stringify(v); },
    set: (k, v) => tx('kv', 'readwrite', s => s.put(k === 'identity' || k === 'channelKeys' ? JSON.parse(v) : v, k)),
    blocks: async () => JSON.stringify(await tx('blocks', 'readonly', s => s.getAll()) || []),
    put: (chainId, blockJson) => { const b = JSON.parse(blockJson); return tx('blocks', 'readwrite', s => s.put({ hash: b.hash, chainId, block: b })); }
  };
  // Video: the browser plays BitTorrent over WebRTC (WebTorrent) into a <video>; the next step moves the torrent
  // logic to C# (it exists: OpenTube.Core/BitTorrent) with only WebRTC and MediaSource bound here.
  let wt = null;
  const loadWt = () => new Promise((res, rej) => { if (window.WebTorrent) return res(); const s = document.createElement('script'); s.src = 'https://cdn.jsdelivr.net/npm/webtorrent@3.0.21/dist/webtorrent.min.js'; s.onload = res; s.onerror = rej; document.head.append(s); });
  window.otPlayer = {
    play: async (elementId, infoHash, file, trackersJson) => {
      await loadWt(); wt ||= new window.WebTorrent();
      const el = document.getElementById(elementId); if (!el) return 'no player';
      const announce = JSON.parse(trackersJson);
      const t = wt.get(infoHash) || wt.add(`magnet:?xt=urn:btih:${infoHash}&dn=${encodeURIComponent(file)}`, { announce });
      return await new Promise((res) => {
        const go = () => { const f = t.files.find(x => /\.(mp4|webm|mkv|mov|m4v)$/i.test(x.name)) || t.files[0]; const v = document.createElement('video'); v.controls = true; v.autoplay = true; v.style.width = '100%'; el.replaceChildren(v);
          f.getBlobURL ? f.getBlobURL((e, u) => { if (!e) v.src = u; }) : f.blob?.().then(b => { v.src = URL.createObjectURL(b); });
          if (f.streamTo) try { f.streamTo(v); } catch {}
          res(`Playing over BitTorrent from ${t.numPeers} peer(s)`); };
        if (t.ready) go(); else t.once('ready', go);
        setTimeout(() => res(t.ready ? null : 'No one is sharing this video right now (it may come back when a host is online)'), 30000);
      });
    }
  };
})();
