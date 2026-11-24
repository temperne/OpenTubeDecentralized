// The thin bindings the C# app needs from the browser, for videos: WebRTC data channels (the C# BitTorrent client's
// peers) and the service worker's relay (the player asks /webtorrent/<infohash>/<name>; sw.js passes each request
// here and C# answers with the bytes). Everything else is C# (WebAssembly). Calls into C# go through the app's
// [JSExport] methods (OpenTube.Uno.Services.BrowserBridge).
(() => {
  let cs = null;   // the C# exports
  const ready = (async () => {
    for (let i = 0; i < 600 && !globalThis.getDotnetRuntime; i++) await new Promise(r => setTimeout(r, 100));
    const rt = await globalThis.getDotnetRuntime(0);
    let ex; try { ex = await rt.getAssemblyExports('OpenTube.Uno.dll'); } catch { ex = await rt.getAssemblyExports('OpenTube.Uno'); }
    cs = ex.OpenTube.Uno.Services.BrowserBridge;
  })();
  const call = async (name, ...args) => { if (!cs) await ready; return cs[name](...args); };
  // bytes from C#: a ticket from the async call, then the bytes (-1: none)
  const bytes = async (name, ...args) => { const t = await call(name, ...args); return t < 0 ? null : new Uint8Array(cs.Take(t)); };

  // ---- WebRTC: complete SDP (no trickle), as WebTorrent's trackers expect; bytes cross as Uint8Array
  const ICE = [{ urls: 'stun:stun.l.google.com:19302' }, { urls: 'stun:global.stun.twilio.com:3478' }];
  const pcs = new Map(); let nextId = 1;
  const gather = (pc) => new Promise((res) => {
    if (pc.iceGatheringState === 'complete') return res();
    const t = setTimeout(res, 2000);
    pc.addEventListener('icegatheringstatechange', () => { if (pc.iceGatheringState === 'complete') { clearTimeout(t); res(); } });
  });
  const wire = (id, ch) => {
    const p = pcs.get(id); if (!p) return; p.ch = ch; ch.binaryType = 'arraybuffer';
    ch.onopen = () => call('RtcOpen', id);
    ch.onmessage = (e) => call('RtcData', id, new Uint8Array(e.data));
    ch.onclose = () => call('RtcClosed', id);
    if (ch.readyState === 'open') call('RtcOpen', id);
  };
  const make = () => {
    const id = nextId++, pc = new RTCPeerConnection({ iceServers: ICE }); pcs.set(id, { pc });
    pc.onconnectionstatechange = () => { if (pc.connectionState === 'failed' || pc.connectionState === 'closed') call('RtcClosed', id); };
    return [id, pc];
  };
  globalThis.otRtc = {
    offer: async () => { const [id, pc] = make(); wire(id, pc.createDataChannel('opentube')); await pc.setLocalDescription(await pc.createOffer()); await gather(pc); return id + ' ' + pc.localDescription.sdp; },
    accept: (id, sdp) => { pcs.get(id)?.pc.setRemoteDescription({ type: 'answer', sdp }).catch(() => otRtc.close(id)); },
    answer: async (sdp) => { const [id, pc] = make(); pc.ondatachannel = (e) => wire(id, e.channel); await pc.setRemoteDescription({ type: 'offer', sdp }); await pc.setLocalDescription(await pc.createAnswer()); await gather(pc); return id + ' ' + pc.localDescription.sdp; },
    // (bytes: a view of C# memory, copied before the call returns)
    send: (id, bytes) => { const c = pcs.get(id)?.ch; if (!c || c.readyState !== 'open') return -1; try { c.send(bytes.slice()); } catch { return -1; } return c.bufferedAmount; },
    close: (id) => { const p = pcs.get(id); pcs.delete(id); try { p?.ch?.close(); } catch {} try { p?.pc.close(); } catch {} },
  };

  // ---- the service worker's requests, answered by C#
  let swOk = null;
  async function onSw(event) {
    const d = event.data; if (d?.type !== 'webtorrent') return;
    const port = event.ports[0];
    const lm = /\/webtorrent\/live\/([0-9a-f]{64})\//.exec(d.url);
    if (lm) {   // a live stream: one continuous response, read from C# a chunk at a time
      const r = await call('LiveOpen', lm[1]);
      if (!r) return port.postMessage({ status: 404, headers: {}, body: '' });
      const [rid, type] = r.split(' ');
      port.postMessage({ status: 200, headers: { 'Content-Type': type, 'Cache-Control': 'no-store' }, body: 'STREAM' });
      port.onmessage = async ({ data }) => {
        if (!data) { port.onmessage = null; call('LiveClose', +rid); return; }
        const chunk = await bytes('LiveRead', +rid);
        port.postMessage(chunk ?? null);
      };
      return;
    }
    const m = /\/webtorrent\/([0-9a-f]{40})\//.exec(d.url);
    if (!m) return port.postMessage({ status: 404, headers: {}, body: '' });
    const info = await call('Info', m[1]);
    if (!info) return port.postMessage({ status: 504, headers: {}, body: '' });
    const [lenText, type] = info.split(' '); const L = +lenText;
    let start = 0, end = L - 1, status = 200;
    const r = /bytes=(\d*)-(\d*)/.exec(d.headers.range || '');
    if (r) { status = 206; if (r[1]) { start = +r[1]; if (r[2]) end = Math.min(+r[2], L - 1); } else start = Math.max(0, L - +r[2]); }
    if (start >= L) return port.postMessage({ status: 416, headers: { 'Content-Range': `bytes */${L}` }, body: '' });
    const headers = { 'Content-Type': type, 'Accept-Ranges': 'bytes', 'Content-Length': String(end - start + 1) };
    if (status === 206) headers['Content-Range'] = `bytes ${start}-${end}/${L}`;
    if (d.method === 'HEAD') return port.postMessage({ status, headers, body: '' });
    port.postMessage({ status, headers, body: 'STREAM' });
    let pos = start;
    port.onmessage = async ({ data }) => {
      if (!data) { port.onmessage = null; return; }   // the player let go (seeking, leaving)
      if (pos > end) return port.postMessage(null);
      const chunk = await bytes('Read', m[1], pos, Math.min(end - pos + 1, 131072));
      if (chunk == null) return port.postMessage(null);   // (an empty chunk: the piece is still coming; the pull stays alive)
      pos += chunk.length; port.postMessage(chunk);
    };
  }
  globalThis.otStream = {
    init: async () => {
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
