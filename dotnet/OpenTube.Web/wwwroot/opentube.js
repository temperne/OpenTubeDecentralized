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
    set: (k, v) => { let o = v; try { o = JSON.parse(v); } catch {} return tx('kv', 'readwrite', s => s.put(o, k)); },   // JSON stays an object (as the JavaScript page keeps it)
    blocks: async () => JSON.stringify(await tx('blocks', 'readonly', s => s.getAll()) || []),
    put: (chainId, blockJson) => { const b = JSON.parse(blockJson); return tx('blocks', 'readwrite', s => s.put({ hash: b.hash, chainId, block: b })); }
  };
  // WebRTC: the browser's data channels, for the C# BitTorrent client (BrowserRtc). Complete SDP (no trickle), as
  // WebTorrent's trackers expect; bytes cross as Uint8Array.
  const ICE = [{ urls: 'stun:stun.l.google.com:19302' }, { urls: 'stun:global.stun.twilio.com:3478' }];
  const pcs = new Map(); let rtcHub = null, nextId = 1;
  const gather = (pc) => new Promise((res) => {
    if (pc.iceGatheringState === 'complete') return res();
    const t = setTimeout(res, 2000);
    pc.addEventListener('icegatheringstatechange', () => { if (pc.iceGatheringState === 'complete') { clearTimeout(t); res(); } });
  });
  const wire = (id, ch) => {
    const p = pcs.get(id); if (!p) return; p.ch = ch; ch.binaryType = 'arraybuffer';
    ch.onopen = () => rtcHub.invokeMethodAsync('RtcOpen', id);
    ch.onmessage = (e) => rtcHub.invokeMethodAsync('RtcData', id, new Uint8Array(e.data));
    ch.onclose = () => rtcHub.invokeMethodAsync('RtcClosed', id);
    if (ch.readyState === 'open') rtcHub.invokeMethodAsync('RtcOpen', id);
  };
  const make = () => {
    const id = nextId++, pc = new RTCPeerConnection({ iceServers: ICE }); pcs.set(id, { pc });
    pc.onconnectionstatechange = () => { if (pc.connectionState === 'failed' || pc.connectionState === 'closed') rtcHub.invokeMethodAsync('RtcClosed', id); };
    return [id, pc];
  };
  window.otRtc = {
    init: (hub) => { rtcHub = hub; },
    offer: async () => { const [id, pc] = make(); wire(id, pc.createDataChannel('opentube')); await pc.setLocalDescription(await pc.createOffer()); await gather(pc); return { id, sdp: pc.localDescription.sdp }; },
    accept: (id, sdp) => pcs.get(id)?.pc.setRemoteDescription({ type: 'answer', sdp }).catch(() => otRtc.close(id)),
    answer: async (sdp) => { const [id, pc] = make(); pc.ondatachannel = (e) => wire(id, e.channel); await pc.setRemoteDescription({ type: 'offer', sdp }); await pc.setLocalDescription(await pc.createAnswer()); await gather(pc); return { id, sdp: pc.localDescription.sdp }; },
    send: (id, bytes) => { const c = pcs.get(id)?.ch; if (!c || c.readyState !== 'open') return -1; try { c.send(bytes); } catch { return -1; } return c.bufferedAmount; },
    close: (id) => { const p = pcs.get(id); pcs.delete(id); try { p?.ch?.close(); } catch {} try { p?.pc.close(); } catch {} },
  };

  // Playing: the video element asks /webtorrent/<infohash>/<name>; the service worker (sw.js, the same one the
  // JavaScript page uses) passes each request here, and C# (BrowserTorrents) answers with the bytes.
  let streamHub = null, swOk = null;
  async function onSw(event) {
    const d = event.data; if (d?.type !== 'webtorrent' || !streamHub) return;
    const port = event.ports[0];
    const lm = /\/webtorrent\/live\/([0-9a-f]{64})\//.exec(d.url);
    if (lm) {   // a live stream: one continuous response, read from C# a chunk at a time
      const r = await streamHub.invokeMethodAsync('LiveOpen', lm[1]);
      if (!r) return port.postMessage({ status: 404, headers: {}, body: '' });
      port.postMessage({ status: 200, headers: { 'Content-Type': r.type, 'Cache-Control': 'no-store' }, body: 'STREAM' });
      port.onmessage = async ({ data }) => {
        if (!data) { port.onmessage = null; streamHub.invokeMethodAsync('LiveClose', r.id); return; }
        const chunk = await streamHub.invokeMethodAsync('LiveRead', r.id);
        port.postMessage(chunk === null ? null : chunk);
      };
      return;
    }
    const m = /\/webtorrent\/([0-9a-f]{40})\//.exec(d.url);
    if (!m) return port.postMessage({ status: 404, headers: {}, body: '' });
    const info = await streamHub.invokeMethodAsync('Info', m[1]);
    if (!info) return port.postMessage({ status: 504, headers: {}, body: '' });
    const L = info.length; let start = 0, end = L - 1, status = 200;
    const r = /bytes=(\d*)-(\d*)/.exec(d.headers.range || '');
    if (r) { status = 206; if (r[1]) { start = +r[1]; if (r[2]) end = Math.min(+r[2], L - 1); } else start = Math.max(0, L - +r[2]); }
    if (start >= L) return port.postMessage({ status: 416, headers: { 'Content-Range': `bytes */${L}` }, body: '' });
    const headers = { 'Content-Type': info.type, 'Accept-Ranges': 'bytes', 'Content-Length': String(end - start + 1) };
    if (status === 206) headers['Content-Range'] = `bytes ${start}-${end}/${L}`;
    if (d.method === 'HEAD') return port.postMessage({ status, headers, body: '' });
    port.postMessage({ status, headers, body: 'STREAM' });
    let pos = start;
    port.onmessage = async ({ data }) => {
      if (!data) { port.onmessage = null; return; }   // the player let go (seeking, leaving)
      if (pos > end) return port.postMessage(null);
      const chunk = await streamHub.invokeMethodAsync('Read', m[1], pos, Math.min(end - pos + 1, 131072));
      if (chunk === null) return port.postMessage(null);   // (an empty chunk: the piece is still coming; the pull stays alive)
      pos += chunk.length; port.postMessage(chunk);
    };
  }
  window.otStream = {
    init: async (hub) => {
      streamHub = hub;
      if (swOk !== null) return swOk;
      if (!('serviceWorker' in navigator)) return (swOk = false);
      try { navigator.serviceWorker.addEventListener('message', onSw); await navigator.serviceWorker.register('/sw.js', { scope: '/' }); swOk = true; } catch { swOk = false; }
      return swOk;
    },
    ready: async () => {
      if (navigator.serviceWorker?.controller) return true;
      await navigator.serviceWorker.ready;
      for (let i = 0; i < 40 && !navigator.serviceWorker.controller; i++) await new Promise((r) => setTimeout(r, 100));
      return !!navigator.serviceWorker.controller;
    },
  };
})();
