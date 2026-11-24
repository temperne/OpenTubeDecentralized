// What the C# pages need from the web view itself (the website and the phone app share this): a frame of a chosen
// video for its thumbnail, and the camera for going live (getUserMedia + MediaRecorder: WebM chunks each second,
// handed to C#, which signs and sends them). Everything else is C#.
window.otUi = {
  // a 320-wide JPEG from about a second into the chosen file (a data: URL), or '' if it can't be read
  thumb: (input) => new Promise((res) => {
    const f = input?.files?.[0]; if (!f) return res('');
    const v = document.createElement('video'); v.muted = true; v.playsInline = true; v.preload = 'auto'; v.src = URL.createObjectURL(f);
    const done = (x) => { URL.revokeObjectURL(v.src); res(x); };
    v.onloadeddata = () => { v.currentTime = Math.min(1, (v.duration || 2) / 2); };
    v.onseeked = () => { try { const c = document.createElement('canvas'), w = 320, h = Math.round(w * (v.videoHeight || 180) / (v.videoWidth || 320)); c.width = w; c.height = h; c.getContext('2d').drawImage(v, 0, 0, w, h); done(c.toDataURL('image/jpeg', 0.7)); } catch { done(''); } };
    v.onerror = () => done(''); setTimeout(() => done(''), 15000);
  }),
  // a file to keep (a key backup): downloaded by the browser
  download: (name, text) => { const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob([text], { type: 'application/json' })); a.download = name; document.body.append(a); a.click(); a.remove(); },
  // cameras and microphones (labels once allowed), for the Go live choices
  devices: async () => {
    const d = await navigator.mediaDevices?.enumerateDevices?.() || [];
    return { cams: d.filter(x => x.kind === 'videoinput').map((x, i) => ({ id: x.deviceId, label: x.label || `Camera ${i + 1}` })), mics: d.filter(x => x.kind === 'audioinput').map((x, i) => ({ id: x.deviceId, label: x.label || `Microphone ${i + 1}` })), screen: !!navigator.mediaDevices?.getDisplayMedia };
  },
  allowCam: async () => { try { const s = await navigator.mediaDevices.getUserMedia({ video: true, audio: true }).catch(() => navigator.mediaDevices.getUserMedia({ video: true })); s.getTracks().forEach(t => t.stop()); return ''; } catch (e) { return e.message; } },
  // a preview of the chosen source (cam: a camera's id, '' for the default, or 'screen'; mic: an id, '' default, 'none')
  camPreview: async (videoId, cam, mic) => {
    const old = window.otUi._preview; if (old) old.getTracks().forEach(t => t.stop()); window.otUi._preview = null;
    try {
      const audio = mic === 'none' ? null : await navigator.mediaDevices.getUserMedia({ audio: mic ? { deviceId: { exact: mic } } : true }).catch(() => null);
      const video = cam === 'screen' ? await navigator.mediaDevices.getDisplayMedia({ video: { frameRate: 30 }, audio: false })
        : await navigator.mediaDevices.getUserMedia({ video: cam ? { deviceId: { exact: cam }, width: { ideal: 1280 }, height: { ideal: 720 }, frameRate: { ideal: 30 } } : { width: { ideal: 1280 }, height: { ideal: 720 } } });
      const media = new MediaStream([...video.getVideoTracks(), ...(audio?.getAudioTracks() || [])]);
      window.otUi._preview = media;
      const v = document.getElementById(videoId); if (v) { v.srcObject = media; v.muted = true; v.play().catch(() => {}); }
      return '';
    } catch (e) { return 'Couldn’t open that source: ' + e.message; }
  },
  camStart: async (hub, videoId) => {
    const mime = ['video/webm;codecs=vp8,opus', 'video/webm;codecs=vp9,opus', 'video/webm'].find(m => window.MediaRecorder?.isTypeSupported?.(m));
    if (!mime) return { error: 'This device can’t record video here.' };
    let media = window.otUi._preview;
    if (!media) {
      try { media = await navigator.mediaDevices.getUserMedia({ video: { width: { ideal: 1280 }, height: { ideal: 720 } }, audio: true }); }
      catch (e) { return { error: 'The camera or microphone isn’t available: ' + e.message }; }
    }
    window.otUi._preview = null;
    const v = document.getElementById(videoId); if (v) { v.srcObject = media; v.muted = true; v.play().catch(() => {}); }
    const rec = new MediaRecorder(media, { mimeType: mime, videoBitsPerSecond: 2_000_000, audioBitsPerSecond: 128_000 });
    let q = Promise.resolve();   // in order
    rec.ondataavailable = (e) => { if (e.data.size) q = q.then(async () => hub.invokeMethodAsync('CamChunk', new Uint8Array(await e.data.arrayBuffer()))).catch(() => {}); };
    window.otUi._cam = { media, rec };
    rec.start(1000);
    return { mime };
  },
  camStop: async () => {
    const c = window.otUi._cam; if (!c) return; window.otUi._cam = null;
    await new Promise((r) => { c.rec.onstop = r; try { c.rec.stop(); } catch { r(); } });
    c.media.getTracks().forEach(t => t.stop());
  },
};
// The device's location ([lat, lon]), asked only when someone chooses "Use my location".
window.otUi = window.otUi || {};
window.otUi.locate = () => new Promise((res, rej) => {
  if (!navigator.geolocation) return rej(new Error('this device can’t tell its location'));
  navigator.geolocation.getCurrentPosition((p) => res([p.coords.latitude, p.coords.longitude]), (e) => rej(new Error(e.message || 'not allowed')), { timeout: 15000 });
});
// A chosen video file's length in seconds (for ads), from a hidden video element.
window.otUi.duration = (input) => new Promise((res) => {
  const f = input?.files?.[0]; if (!f) return res(0);
  const v = document.createElement('video'); v.preload = 'metadata'; v.muted = true;
  v.onloadedmetadata = () => { res(Math.ceil(v.duration || 0)); URL.revokeObjectURL(v.src); }; v.onerror = () => res(0);
  v.src = URL.createObjectURL(f);
});
// Is this video element playing (and the page visible) at second i of it? Starts it if it hasn't started (muted if it must).
window.otUi.playingAt = (id, i) => {
  const v = document.getElementById(id); if (!v) return false;
  if (v.paused && !v.ended && !v.dataset.tried) { v.dataset.tried = '1'; v.play().catch(() => { v.muted = true; v.play().catch(() => {}); }); }
  return !v.paused && v.currentTime >= i - 1.5 && document.visibilityState === 'visible';
};
window.otUi.pause = (id) => { const v = document.getElementById(id); if (v) v.pause(); };
// Bytes as an address a page element can show.
window.otUi.blobUrl = (bytes, mime) => URL.createObjectURL(new Blob([bytes], { type: mime }));
window.otUi.openBytes = (bytes, mime) => window.open(URL.createObjectURL(new Blob([bytes], { type: mime })), '_blank');
// A small thumbnail (JPEG data URL) of a picture or video's bytes, or '' if it can't make one.
window.otUi.thumbOf = (bytes, mime) => new Promise((res) => {
  const url = URL.createObjectURL(new Blob([bytes], { type: mime })), done = (t) => { URL.revokeObjectURL(url); res(t || ''); };
  const draw = (el, w, h) => { const scale = Math.min(1, 160 / Math.max(w, h)); const c = document.createElement('canvas'); c.width = Math.max(1, Math.round(w * scale)); c.height = Math.max(1, Math.round(h * scale)); c.getContext('2d').drawImage(el, 0, 0, c.width, c.height); return c.toDataURL('image/jpeg', 0.7); };
  if (mime.startsWith('image/')) { const i = new Image(); i.onload = () => done(draw(i, i.naturalWidth, i.naturalHeight)); i.onerror = () => done(''); i.src = url; return; }
  if (!mime.startsWith('video/')) return done('');
  const v = document.createElement('video'); v.muted = true; v.playsInline = true; v.preload = 'auto';
  v.onloadeddata = () => { v.currentTime = Math.min(1, (v.duration || 2) / 2); };
  v.onseeked = () => done(draw(v, v.videoWidth, v.videoHeight)); v.onerror = () => done(''); setTimeout(() => done(''), 8000);
  v.src = url;
});
// How much this site keeps on this device, and whether the browser keeps it for good.
window.otUi.storage = async () => {
  if (!navigator.storage?.estimate) return 'unknown';
  const e = await navigator.storage.estimate(), p = await navigator.storage.persisted?.();
  const mb = (n) => (n / 1e6).toFixed(n < 1e8 ? 1 : 0) + ' MB';
  return `${mb(e.usage || 0)} of ${mb(e.quota || 0)} · ${p ? 'kept for good' : 'the browser may clear it when space runs low'}`;
};
// Maps (OpenStreetMap through Leaflet, loaded only when a map is shown). Places are just coordinates on the chain.
(() => {
  let leafletP = null;
  const leaflet = () => leafletP ||= new Promise((res, rej) => {
    const css = document.createElement('link'); css.rel = 'stylesheet'; css.href = 'https://cdn.jsdelivr.net/npm/leaflet@1.9.4/dist/leaflet.css'; document.head.append(css);
    const js = document.createElement('script'); js.src = 'https://cdn.jsdelivr.net/npm/leaflet@1.9.4/dist/leaflet.js'; js.onload = () => res(window.L); js.onerror = () => { leafletP = null; rej(new Error('map library unavailable')); }; document.head.append(js);
  });
  const icon = (L, kind) => L.divIcon({ className: 'osm-pin', html: { listing: '🛍', housing: '🏠', org: '🏛', channel: '🏪', me: '📍' }[kind] || '📍', iconSize: [26, 26], iconAnchor: [13, 24] });
  const r5 = (x) => Math.round(x * 1e5) / 1e5;
  const maps = new Map();
  // show(id, points [{lat, lon, title, sub, href, kind}], {center, zoom, picked, pickRef}): pickRef gets OnPick(lat, lon) on a click
  window.otMap = {
    show: async (id, points, opts) => {
      const el = document.getElementById(id); if (!el) return;
      let L; try { L = await leaflet(); } catch { el.textContent = 'The map couldn’t load (no connection to the map library).'; return; }
      maps.get(id)?.remove();
      const map = L.map(el, { scrollWheelZoom: false }); maps.set(id, map);
      L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 19, attribution: '© <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a> contributors' }).addTo(map);
      const bounds = [];
      for (const pt of points || []) {
        const m = L.marker([pt.lat, pt.lon], { icon: icon(L, pt.kind) }).addTo(map);
        const pop = document.createElement('div'); const a = document.createElement(pt.href ? 'a' : 'b'); if (pt.href) a.href = pt.href; a.textContent = pt.title || ''; pop.append(a);
        if (pt.sub) { const s = document.createElement('div'); s.className = 'sub'; s.textContent = pt.sub; pop.append(s); }
        m.bindPopup(pop); bounds.push([pt.lat, pt.lon]);
      }
      let pin = opts?.picked ? L.marker([opts.picked.lat, opts.picked.lon], { icon: icon(L, 'me') }).addTo(map) : null;
      if (opts?.pickRef) map.on('click', (e) => { if (pin) pin.setLatLng(e.latlng); else pin = L.marker(e.latlng, { icon: icon(L, 'me') }).addTo(map); opts.pickRef.invokeMethodAsync('OnPick', r5(e.latlng.lat), r5(e.latlng.lng)); });
      map._pin = (lat, lon) => { if (pin) pin.setLatLng([lat, lon]); else pin = L.marker([lat, lon], { icon: icon(L, 'me') }).addTo(map); map.setView([lat, lon], 15); };
      if (opts?.center) map.setView([opts.center.lat, opts.center.lon], opts.zoom || 13);
      else if (bounds.length > 1) map.fitBounds(bounds, { padding: [30, 30], maxZoom: 14 });
      else if (bounds.length === 1) map.setView(bounds[0], opts?.zoom || 13);
      else map.setView([20, 0], 2);
      setTimeout(() => map.invalidateSize(), 50);
    },
    pin: (id, lat, lon) => maps.get(id)?._pin?.(lat, lon),
    // address search (OpenStreetMap's Nominatim): [{label, lat, lon}]
    geocode: async (q) => {
      const r = await fetch('https://nominatim.openstreetmap.org/search?format=json&limit=5&q=' + encodeURIComponent(q), { headers: { Accept: 'application/json' } });
      return (await r.json()).map(x => ({ label: x.display_name.slice(0, 120), lat: r5(Number(x.lat)), lon: r5(Number(x.lon)) }));
    }
  };
})();
// This site's build: a hash of its index.html, which names every fingerprinted file of the build.
window.otUi.buildId = async () => {
  const t = await (await fetch(document.baseURI + 'index.html', { cache: 'no-store' })).text();
  const h = new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(t)));
  return Array.from(h.slice(0, 6), b => b.toString(16).padStart(2, '0')).join('');
};
// Desktop notifications while the page is open (the browser asks you once).
window.otUi.askNotify = async () => { if (!window.Notification) return false; if (Notification.permission === 'granted') return true; return (await Notification.requestPermission()) === 'granted'; };
window.otUi.notify = (title, body, href) => { try { if (window.Notification?.permission !== 'granted') return; const n = new Notification(title, { body, icon: 'icon-192.png' }); n.onclick = () => { window.focus(); if (href) location.href = href; n.close(); }; } catch {} };
window.otUi.persist = async () => !!(await navigator.storage?.persist?.());
// a computer's /status, straight from this browser (Devices → Test connection)
window.otUi.status = async (url) => { const r = await fetch(url, { cache: 'no-store', signal: AbortSignal.timeout(4000) }); return await r.json(); };
// can this browser discover its public address (a STUN reply)? the ICE candidate types it gathers (Network page)
window.otUi.stun = () => new Promise((resolve) => {
  if (!window.RTCPeerConnection) return resolve({ error: 'no WebRTC in this browser', types: [] });
  const types = new Set(); let pc;
  try { pc = new RTCPeerConnection({ iceServers: [{ urls: ['stun:stun.l.google.com:19302', 'stun:global.stun.twilio.com:3478'] }] }); } catch (e) { return resolve({ error: e.message, types: [] }); }
  const done = () => { try { pc.close(); } catch {} resolve({ types: [...types] }); };
  pc.onicecandidate = (e) => { if (!e.candidate) return done(); const m = / typ (\w+)/.exec(e.candidate.candidate); if (m) types.add(m[1]); };
  pc.createDataChannel('probe'); pc.createOffer().then(o => pc.setLocalDescription(o)).catch(e => resolve({ error: e.message, types: [] }));
  setTimeout(done, 6000);
});
window.otUi.storageInfo = async () => { try { const e = await navigator.storage.estimate(); return { usage: e.usage || 0, quota: e.quota || 0, persisted: !!(await navigator.storage.persisted?.()) }; } catch { return null; } };
