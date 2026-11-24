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
// ---- the player in the browser: the browser's own <video>, placed over the spot the app keeps for it (its controls,
// full screen and picture-in-picture are the browser's)
(() => {
  const vids = new Map();
  globalThis.otVideo = {
    show: (id, src) => {
      let v = vids.get(id);
      if (!v) { v = document.createElement('video'); v.controls = true; v.playsInline = true; v.autoplay = true; v.style.cssText = 'position:fixed;z-index:10;background:#000;object-fit:contain;left:0;top:0;width:0;height:0'; document.body.appendChild(v); vids.set(id, v); }
      if (v.src !== src) { v.src = src; v.play().catch(() => {}); }
    },
    place: (id, x, y, w, h, visible) => { const v = vids.get(id); if (!v) return; Object.assign(v.style, { left: x + 'px', top: y + 'px', width: w + 'px', height: h + 'px', display: visible ? 'block' : 'none' }); },
    remove: (id) => { const v = vids.get(id); vids.delete(id); if (v) { try { v.pause(); } catch {} v.removeAttribute('src'); v.load(); v.remove(); } },
    // "state position duration" for the app's status and tests
    state: (id) => { const v = vids.get(id); if (!v) return 'None 0 0'; const st = v.ended ? 'Ended' : v.paused ? (v.readyState < 2 ? 'Opening' : 'Paused') : v.readyState < 3 ? 'Buffering' : 'Playing'; return `${st} ${v.currentTime || 0} ${isFinite(v.duration) ? v.duration : 0}`; },
  };
})();
// a still from a video (a frame a second in) as a small JPEG data: URI; '' when it can't
globalThis.otThumb = (src) => new Promise((resolve) => {
  const v = document.createElement('video'); v.muted = true; v.playsInline = true; v.preload = 'auto'; v.src = src;
  const done = (r) => { clearTimeout(t); try { v.removeAttribute('src'); v.load(); } catch {} resolve(r); };
  const t = setTimeout(() => done(''), 20000);
  v.onloadedmetadata = () => { v.currentTime = Math.min(1, (v.duration || 2) / 2); };
  v.onseeked = () => { try { const s = Math.min(1, 480 / Math.max(v.videoWidth, v.videoHeight)); const c = document.createElement('canvas'); c.width = Math.max(1, v.videoWidth * s | 0); c.height = Math.max(1, v.videoHeight * s | 0); c.getContext('2d').drawImage(v, 0, 0, c.width, c.height); done(c.toDataURL('image/jpeg', 0.8)); } catch { done(''); } };
  v.onerror = () => done('');
});
// ---- the camera in the browser (Go live): the preview is drawn by the app itself from small frames sent here (an
// HTML video over the app's canvas leaves a blank patch elsewhere on it in Chrome), the cameras and microphones to
// choose from, and the recorder whose chunks go to C# (LiveBroadcast)
(() => {
  let media = null, rec = null, cam = null, timer = 0, q = Promise.resolve(), exp = null;
  const cs = async () => { const rt = await globalThis.getDotnetRuntime(0); let ex; try { ex = await rt.getAssemblyExports('OpenTube.Uno.dll'); } catch { ex = await rt.getAssemblyExports('OpenTube.Uno'); } return ex.OpenTube.Uno.Services.CameraBridge; };
  const FW = 384, FH = 216, frame = document.createElement('canvas'); frame.width = FW; frame.height = FH;
  const g = frame.getContext('2d', { willReadFrequently: true });
  const send = async () => {
    if (!cam || !cam.videoWidth) return;
    g.fillStyle = '#000'; g.fillRect(0, 0, FW, FH);
    const k = Math.min(FW / cam.videoWidth, FH / cam.videoHeight), dw = cam.videoWidth * k, dh = cam.videoHeight * k;
    g.drawImage(cam, (FW - dw) / 2, (FH - dh) / 2, dw, dh);
    const px = g.getImageData(0, 0, FW, FH).data;
    for (let i = 0; i < px.length; i += 4) { const r = px[i]; px[i] = px[i + 2]; px[i + 2] = r; }   // RGBA → BGRA
    (exp ||= await cs()).Frame(new Uint8Array(px.buffer), FW, FH);
  };
  const ensureView = () => {
    if (!cam) { cam = document.createElement('video'); cam.muted = true; cam.playsInline = true; cam.autoplay = true; }
    if (!timer) timer = setInterval(() => { send().catch(() => {}); }, 80); return cam;
  };
  globalThis.otCam = {
    // "id\tlabel" lines: cameras, then a blank line, then microphones (labels show once the browser has allowed them)
    devices: async () => { const d = await navigator.mediaDevices?.enumerateDevices?.() || []; const f = (k, n) => d.filter(x => x.kind === k).map((x, i) => `${x.deviceId}\t${x.label || n + ' ' + (i + 1)}`).join('\n'); return f('videoinput', 'Camera') + '\n\n' + f('audioinput', 'Microphone'); },
    // open a source for the preview: cam '' (default), a camera id, or 'screen'; mic '', an id, or 'none'; '' or the error
    preview: async (cam, mic) => {
      if (rec) return 'the camera is streaming: end the stream to change it';
      try {
        if (media) media.getTracks().forEach(t => t.stop());
        const audio = mic === 'none' ? null : await navigator.mediaDevices.getUserMedia({ audio: mic ? { deviceId: { exact: mic } } : true }).catch(() => null);
        const video = cam === 'screen' ? await navigator.mediaDevices.getDisplayMedia({ video: { frameRate: 30 }, audio: false })
          : await navigator.mediaDevices.getUserMedia({ video: cam ? { deviceId: { exact: cam }, width: { ideal: 1280 }, height: { ideal: 720 } } : { width: { ideal: 1280 }, height: { ideal: 720 } } });
        media = new MediaStream([...video.getVideoTracks(), ...(audio?.getAudioTracks() || [])]);
        const v = ensureView(); v.srcObject = media; v.play().catch(() => {}); return '';
      } catch (e) { return e.message || String(e); }
    },
    // start recording: the mime type, or 'error:…'; each second a chunk goes to C#
    start: async () => {
      const mime = ['video/webm;codecs=vp8,opus', 'video/webm;codecs=vp9,opus', 'video/webm'].find(m => window.MediaRecorder?.isTypeSupported?.(m));
      if (!mime) return 'error:This browser can’t record video for streaming. Use Chrome, Edge or Firefox.';
      if (!media) { const e = await otCam.preview('', ''); if (e) return 'error:' + e; }
      const c = await cs();
      rec = new MediaRecorder(media, { mimeType: mime, videoBitsPerSecond: 2_000_000, audioBitsPerSecond: 128_000 });
      rec.ondataavailable = (e) => { if (e.data.size) q = q.then(async () => c.Chunk(new Uint8Array(await e.data.arrayBuffer()))).catch(() => {}); };
      rec.start(1000); return mime;
    },
    stop: async () => {
      if (rec) await new Promise((r) => { rec.onstop = r; try { rec.stop(); } catch { r(); } }); rec = null;
      if (media) media.getTracks().forEach(t => t.stop()); media = null;
      if (cam) { cam.srcObject = null; cam = null; } clearInterval(timer); timer = 0;
    },
  };
})();
