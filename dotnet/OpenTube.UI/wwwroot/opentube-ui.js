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
  camStart: async (hub, videoId) => {
    const mime = ['video/webm;codecs=vp8,opus', 'video/webm;codecs=vp9,opus', 'video/webm'].find(m => window.MediaRecorder?.isTypeSupported?.(m));
    if (!mime) return { error: 'This device can’t record video here.' };
    let media;
    try { media = await navigator.mediaDevices.getUserMedia({ video: { width: { ideal: 1280 }, height: { ideal: 720 } }, audio: true }); }
    catch (e) { return { error: 'The camera or microphone isn’t available: ' + e.message }; }
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
