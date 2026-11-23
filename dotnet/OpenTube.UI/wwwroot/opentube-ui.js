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
