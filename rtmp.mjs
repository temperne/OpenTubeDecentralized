// OBS "Start Streaming" receiver for OpenTube: a small RTMP server (no dependencies) that turns the incoming
// H.264 + AAC stream into fragmented MP4, the format browsers can play while it arrives (Media Source Extensions).
//
//   const rtmp = startRtmp({ port: 1935, host: '127.0.0.1', onPublish: (key) => sink | string, log });
//
// onPublish is called when OBS starts streaming; it returns a sink { init(bytes, mime), fragment(bytes, isKey), end() }
// or a string explaining why the stream is refused (OBS shows an error). init() gets the MP4 header (ftyp + moov) once;
// each fragment() is one moof + mdat, starting at a keyframe whenever possible. Header + fragments in order form a
// normal MP4 file.
//
// In OBS: Settings → Stream → Service "Custom…", Server rtmp://localhost/live, any stream key. Video encoder x264 or
// any H.264 encoder (not HEVC/AV1), audio AAC. A keyframe interval of 1–2 s gives the quickest start for viewers.
import net from 'node:net';
import crypto from 'node:crypto';

// ---------------------------------------------------------------- AMF0 (RTMP command encoding)
function amfRead(buf, pos) {
  const t = buf[pos++];
  switch (t) {
    case 0: return [buf.readDoubleBE(pos), pos + 8];
    case 1: return [buf[pos] !== 0, pos + 1];
    case 2: { const n = buf.readUInt16BE(pos); return [buf.toString('utf8', pos + 2, pos + 2 + n), pos + 2 + n]; }
    case 12: { const n = buf.readUInt32BE(pos); return [buf.toString('utf8', pos + 4, pos + 4 + n), pos + 4 + n]; }
    case 3: case 8: {
      if (t === 8) pos += 4;
      const o = {};
      for (;;) {
        const n = buf.readUInt16BE(pos); pos += 2;
        if (n === 0 && buf[pos] === 9) return [o, pos + 1];
        const k = buf.toString('utf8', pos, pos + n); pos += n;
        const [v, p2] = amfRead(buf, pos); o[k] = v; pos = p2;
      }
    }
    case 10: { const n = buf.readUInt32BE(pos); pos += 4; const a = []; for (let i = 0; i < n; i++) { const [v, p2] = amfRead(buf, pos); a.push(v); pos = p2; } return [a, pos]; }
    case 11: return [buf.readDoubleBE(pos), pos + 10];
    case 5: case 6: return [null, pos];
    default: throw new Error('AMF0 type ' + t);
  }
}
function amfReadAll(buf) { const out = []; let pos = 0; while (pos < buf.length) { const [v, p] = amfRead(buf, pos); out.push(v); pos = p; } return out; }
function amfWrite(v) {
  if (v === null || v === undefined) return Buffer.from([5]);
  if (typeof v === 'number') { const b = Buffer.alloc(9); b[0] = 0; b.writeDoubleBE(v, 1); return b; }
  if (typeof v === 'boolean') return Buffer.from([1, v ? 1 : 0]);
  if (typeof v === 'string') { const s = Buffer.from(v); const b = Buffer.alloc(3); b[0] = 2; b.writeUInt16BE(s.length, 1); return Buffer.concat([b, s]); }
  const parts = [Buffer.from([3])];
  for (const [k, x] of Object.entries(v)) { const kb = Buffer.from(k); const l = Buffer.alloc(2); l.writeUInt16BE(kb.length); parts.push(l, kb, amfWrite(x)); }
  parts.push(Buffer.from([0, 0, 9]));
  return Buffer.concat(parts);
}

// ---------------------------------------------------------------- MP4 boxes
const u32 = (n) => { const b = Buffer.alloc(4); b.writeUInt32BE(n >>> 0); return b; };
const u16 = (n) => { const b = Buffer.alloc(2); b.writeUInt16BE(n & 0xffff); return b; };
const u64 = (n) => { const b = Buffer.alloc(8); b.writeBigUInt64BE(BigInt(Math.max(0, Math.round(n)))); return b; };
const i32 = (n) => { const b = Buffer.alloc(4); b.writeInt32BE(n | 0); return b; };
function box(type, ...parts) { const body = Buffer.concat(parts.flat()); return Buffer.concat([u32(body.length + 8), Buffer.from(type, 'latin1'), body]); }
const full = (type, version, flags, ...parts) => box(type, Buffer.from([version, (flags >> 16) & 255, (flags >> 8) & 255, flags & 255]), ...parts);
const MATRIX = Buffer.concat([u32(0x10000), u32(0), u32(0), u32(0), u32(0x10000), u32(0), u32(0), u32(0), u32(0x40000000)]);

function sps(avcC) {   // width/height from the first SPS in an AVCDecoderConfigurationRecord
  try {
    const len = avcC.readUInt16BE(6); let nal = avcC.subarray(8, 8 + len);
    const rbsp = []; for (let i = 0; i < nal.length; i++) { if (i > 1 && nal[i] === 3 && nal[i - 1] === 0 && nal[i - 2] === 0) continue; rbsp.push(nal[i]); }
    let bit = 8;   // skip the NAL header byte
    const read = (n) => { let v = 0; for (let i = 0; i < n; i++) { v = (v << 1) | ((rbsp[bit >> 3] >> (7 - (bit & 7))) & 1); bit++; } return v; };
    const ue = () => { let z = 0; while (read(1) === 0 && z < 32) z++; return (1 << z) - 1 + read(z); };
    const se = () => { const k = ue(); return k & 1 ? (k + 1) / 2 : -k / 2; };
    const profile = read(8); read(16); ue();
    let chroma = 1;
    if ([100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135].includes(profile)) {
      chroma = ue(); if (chroma === 3) read(1); ue(); ue(); read(1);
      if (read(1)) for (let i = 0; i < (chroma !== 3 ? 8 : 12); i++) if (read(1)) { let last = 8, next = 8; for (let j = 0; j < (i < 6 ? 16 : 64); j++) { if (next) next = (last + se() + 256) % 256; last = next || last; } }
    }
    ue(); const poc = ue();
    if (poc === 0) ue(); else if (poc === 1) { read(1); se(); se(); const n = ue(); for (let i = 0; i < n; i++) se(); }
    ue(); read(1);
    const wMbs = ue() + 1, hMap = ue() + 1, frameMbsOnly = read(1);
    if (!frameMbsOnly) read(1);
    read(1);
    let cl = 0, cr = 0, ct = 0, cb = 0;
    if (read(1)) { cl = ue(); cr = ue(); ct = ue(); cb = ue(); }
    const cx = chroma === 0 ? 1 : 2, cy = (chroma === 1 ? 2 : 1) * (2 - frameMbsOnly);
    return { width: wMbs * 16 - (cl + cr) * cx, height: (2 - frameMbsOnly) * hMap * 16 - (ct + cb) * cy };
  } catch { return null; }
}
function aacConfig(asc) {
  const ot = asc[0] >> 3, fi = ((asc[0] & 7) << 1) | (asc[1] >> 7), ch = (asc[1] >> 3) & 15;
  const rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
  return { objectType: ot, sampleRate: rates[fi] || 44100, channels: ch || 2 };
}
function initSegment(v, a) {
  const traks = [], trex = [];
  if (v) {
    const avc1 = box('avc1', Buffer.alloc(6), u16(1), Buffer.alloc(16), u16(v.width), u16(v.height), u32(0x480000), u32(0x480000), u32(0), u16(1), Buffer.alloc(32), u16(0x18), u16(0xffff), box('avcC', v.avcC));
    traks.push(box('trak', full('tkhd', 0, 3, u32(0), u32(0), u32(1), u32(0), u32(0), Buffer.alloc(8), u16(0), u16(0), u16(0), u16(0), MATRIX, u32(v.width << 16), u32(v.height << 16)),
      box('mdia', full('mdhd', 0, 0, u32(0), u32(0), u32(1000), u32(0), u16(0x55c4), u16(0)), full('hdlr', 0, 0, u32(0), Buffer.from('vide'), Buffer.alloc(12), Buffer.from('VideoHandler\0')),
        box('minf', full('vmhd', 0, 1, Buffer.alloc(8)), box('dinf', full('dref', 0, 0, u32(1), full('url ', 0, 1))),
          box('stbl', full('stsd', 0, 0, u32(1), avc1), full('stts', 0, 0, u32(0)), full('stsc', 0, 0, u32(0)), full('stsz', 0, 0, u32(0), u32(0)), full('stco', 0, 0, u32(0)))))));
    trex.push(full('trex', 0, 0, u32(1), u32(1), u32(0), u32(0), u32(0)));
  }
  if (a) {
    const dsi = Buffer.concat([Buffer.from([5, a.asc.length]), a.asc]);
    const dcd = Buffer.concat([Buffer.from([4, 13 + dsi.length, 0x40, 0x15, 0, 0, 0]), u32(0), u32(0), dsi]);
    const es = Buffer.concat([Buffer.from([3, 3 + dcd.length + 3, 0, 2, 0]), dcd, Buffer.from([6, 1, 2])]);
    const mp4a = box('mp4a', Buffer.alloc(6), u16(1), Buffer.alloc(8), u16(a.channels), u16(16), u16(0), u16(0), u32(a.sampleRate << 16), full('esds', 0, 0, es));
    traks.push(box('trak', full('tkhd', 0, 3, u32(0), u32(0), u32(2), u32(0), u32(0), Buffer.alloc(8), u16(0), u16(1), u16(0x100), u16(0), MATRIX, u32(0), u32(0)),
      box('mdia', full('mdhd', 0, 0, u32(0), u32(0), u32(a.sampleRate), u32(0), u16(0x55c4), u16(0)), full('hdlr', 0, 0, u32(0), Buffer.from('soun'), Buffer.alloc(12), Buffer.from('SoundHandler\0')),
        box('minf', full('smhd', 0, 0, u32(0)), box('dinf', full('dref', 0, 0, u32(1), full('url ', 0, 1))),
          box('stbl', full('stsd', 0, 0, u32(1), mp4a), full('stts', 0, 0, u32(0)), full('stsc', 0, 0, u32(0)), full('stsz', 0, 0, u32(0), u32(0)), full('stco', 0, 0, u32(0)))))));
    trex.push(full('trex', 0, 0, u32(2), u32(1), u32(0), u32(0), u32(0)));
  }
  const moov = box('moov', full('mvhd', 0, 0, u32(0), u32(0), u32(1000), u32(0), u32(0x10000), u16(0x100), Buffer.alloc(10), MATRIX, Buffer.alloc(24), u32(3)), ...traks, box('mvex', ...trex));
  return Buffer.concat([box('ftyp', Buffer.from('isom'), u32(0x200), Buffer.from('isomiso6avc1mp41')), moov]);
}
// One fragment: video samples (track 1, ms) and audio samples (track 2, sample-rate units).
function fragment(seq, vs, as) {
  const trafs = [], datas = [];
  const make = (track, samples, video, offsetFix) => {
    const flags = 0x1 | 0x100 | 0x200 | (video ? 0x400 | 0x800 : 0);
    const per = samples.map(s => Buffer.concat([u32(s.dur), u32(s.data.length), ...(video ? [u32(s.key ? 0x02000000 : 0x01010000), i32(s.cto)] : [])]));
    const trun = (off) => full('trun', 1, flags, u32(samples.length), i32(off), ...per);
    return { track, samples, trun, base: samples[0].dts, size: samples.reduce((n, s) => n + s.data.length, 0) };
  };
  if (vs.length) trafs.push(make(1, vs, true));
  if (as.length) trafs.push(make(2, as, false));
  const build = (offsets) => box('moof', full('mfhd', 0, 0, u32(seq)), ...trafs.map((t, i) => box('traf', full('tfhd', 0, 0x020000, u32(t.track)), full('tfdt', 1, 0, u64(t.base)), t.trun(offsets[i]))));
  const sizeOnly = build(trafs.map(() => 0)).length;
  let off = sizeOnly + 8; const offsets = [];
  for (const t of trafs) { offsets.push(off); off += t.size; }
  for (const t of trafs) for (const s of t.samples) datas.push(s.data);
  const mdat = Buffer.concat(datas);
  return Buffer.concat([build(offsets), u32(mdat.length + 8), Buffer.from('mdat'), mdat]);
}

// ---------------------------------------------------------------- RTMP server
// Linux: which processes listen on a TCP port (from /proc), so a port taken by an older OpenTube can be named or freed.
import fs from 'node:fs';
const cmdOf = (pid) => { try { return fs.readFileSync(`/proc/${pid}/cmdline`, 'utf8').split('\0').join(' ').trim(); } catch { return ''; } };
export function portHolders(port) {
  if (process.platform !== 'linux') return [];
  const want = new Map();   // socket inode -> address
  for (const f of ['/proc/net/tcp', '/proc/net/tcp6']) {
    let t = ''; try { t = fs.readFileSync(f, 'utf8'); } catch { continue; }
    for (const line of t.split('\n').slice(1)) {
      const c = line.trim().split(/\s+/); if (c.length < 10 || c[3] !== '0A') continue;   // 0A = LISTEN
      const [hex, ph] = c[1].split(':'); if (parseInt(ph, 16) !== port) continue;
      const addr = hex.length === 8 ? hex.match(/../g).reverse().map(x => parseInt(x, 16)).join('.') : (/^0+$/.test(hex) ? '::' : hex === '00000000000000000000000001000000' ? '::1' : 'IPv6 ' + hex);
      want.set(c[9], addr);
    }
  }
  const out = [];
  if (!want.size) return out;
  for (const d of fs.readdirSync('/proc').filter(x => /^\d+$/.test(x))) {
    let fds = []; try { fds = fs.readdirSync(`/proc/${d}/fd`); } catch { continue; }
    for (const fd of fds) {
      let l = ''; try { l = fs.readlinkSync(`/proc/${d}/fd/${fd}`); } catch { continue; }
      const m = /^socket:\[(\d+)\]$/.exec(l); if (!m || !want.has(m[1])) continue;
      let ppid = 0; try { ppid = Number(/PPid:\s*(\d+)/.exec(fs.readFileSync(`/proc/${d}/status`, 'utf8'))[1]); } catch {}
      out.push({ pid: Number(d), ppid, cmd: cmdOf(d), addr: want.get(m[1]) }); break;
    }
  }
  return out;
}

export function startRtmp({ port = 1935, host = '::', onPublish, log = () => {} }) {   // all addresses: OBS may be on another machine
  const state = { port, live: 0, bytes: 0, sessions: new Set() };
  const server = net.createServer((sock) => {
    sock.setNoDelay(true);
    const who = String(sock.remoteAddress || '').replace(/^::ffff:/, '');
    log(`connection from ${who}`); state.lastConn = { at: Date.now(), from: who, stage: 'connected' };
    const shake = setTimeout(() => { if (stage < 2) { log(`${who}: no RTMP handshake within 10 s (is this OBS, with Service “Custom…” and a server starting rtmp://?)`); sock.destroy(); } }, 10000);
    let buf = Buffer.alloc(0), stage = 0, inChunk = 128, outChunk = 4096, sink = null, published = false;
    const streams = new Map();   // chunk stream id -> { ts, len, type, sid, body: [], got }
    let video = null, audio = null, vq = [], aq = [], seq = 1, sentInit = false, lastV = null, lastA = null, fragStart = null;
    const write = (csid, type, sid, payload, ts = 0) => {
      const out = [];
      for (let off = 0, first = true; off < payload.length || first; off += outChunk, first = false) {
        let h;
        if (first) { h = Buffer.alloc(12); h[0] = csid & 63; h.writeUIntBE(Math.min(ts, 0xffffff), 1, 3); h.writeUIntBE(payload.length, 4, 3); h[7] = type; h.writeUInt32LE(sid, 8); }
        else h = Buffer.from([0xc0 | (csid & 63)]);
        out.push(h, payload.subarray(off, off + outChunk));
        if (!payload.length) break;
      }
      sock.write(Buffer.concat(out));
    };
    const control = (type, n) => write(2, type, 0, u32(n));
    const command = (sid, ...vals) => write(3, 20, sid, Buffer.concat(vals.map(amfWrite)));
    const status = (sid, level, code, description) => command(sid, 'onStatus', 0, null, { level, code, description });

    // nextKey: the dts of a keyframe about to start the next fragment (so every video frame before it goes out now).
    function flush(final = false, nextKey = null) {
      if (!sink || !video && !audio) return;
      if (!sentInit) { sink.init(initSegment(video, audio), mime()); sentInit = true; }
      // otherwise keep the last sample of each track: its duration comes from the next one
      const vs = final || nextKey !== null ? vq.splice(0) : vq.splice(0, Math.max(0, vq.length - 1));
      const as = final ? aq.splice(0) : aq.splice(0, Math.max(0, aq.length - 1));
      const dur = (list, next, def) => list.forEach((s, i) => { s.dur = Math.max(1, ((list[i + 1] || next)?.dts ?? s.dts + def) - s.dts); });
      dur(vs, nextKey !== null ? { dts: nextKey } : vq[0], lastV?.dur || 33); dur(as, aq[0], 1024);
      if (vs.length) lastV = vs[vs.length - 1];
      if (!vs.length && !as.length) return;
      const frag = fragment(seq++, vs, as);
      state.bytes += frag.length;
      sink.fragment(frag, vs.length ? vs[0].key : false);
      fragStart = null;
    }
    const mime = () => `video/mp4; codecs="${[video && video.codec, audio && 'mp4a.40.' + audio.objectType].filter(Boolean).join(',')}"`;
    function media(type, ts, body) {
      if (!published || !body.length) return;
      if (type === 9) {
        if ((body[0] & 0x0f) !== 7 || body[0] & 0x80) { if (!video?.warned) { log('OBS stream: only H.264 video works here; pick x264 or another H.264 encoder'); video = { ...(video || {}), warned: true }; } return; }
        const pt = body[1], cto = body.readIntBE(2, 3), key = (body[0] >> 4) === 1;
        if (pt === 0) { const avcC = Buffer.from(body.subarray(5)); const dim = sps(avcC) || { width: 1280, height: 720 }; video = { avcC, ...dim, codec: 'avc1.' + avcC.subarray(1, 4).toString('hex') }; return; }
        if (pt !== 1 || !video?.avcC) return;
        if (key && fragStart !== null && ts - fragStart >= 500) flush(false, ts);   // start fragments at keyframes
        if (fragStart === null) fragStart = ts;
        vq.push({ dts: ts, cto, key, data: Buffer.from(body.subarray(5)) });
      } else {
        if ((body[0] >> 4) !== 10) { if (!audio?.warned) { log('OBS stream: audio must be AAC'); audio = { warned: true }; } return; }
        if (body[1] === 0) { const asc = Buffer.from(body.subarray(2)); audio = { asc, ...aacConfig(asc) }; return; }
        if (!audio?.asc) return;
        aq.push({ dts: Math.round(ts * audio.sampleRate / 1000), data: Buffer.from(body.subarray(2)) });
      }
      if (fragStart !== null && ts - fragStart >= 2000) flush();   // long keyframe intervals: cut anyway
    }
    async function onCommand(sid, vals) {
      const [name, txn] = vals;
      if (name === 'connect') {
        log(`${who}: OBS connected (app “${vals[2]?.app ?? '?'}”)`); state.lastConn.stage = 'connected to app ' + (vals[2]?.app ?? '?');
        control(5, 2500000); write(2, 6, 0, Buffer.concat([u32(2500000), Buffer.from([2])])); control(1, outChunk);
        command(0, '_result', txn, { fmsVer: 'FMS/3,0,1,123', capabilities: 31 }, { level: 'status', code: 'NetConnection.Connect.Success', description: 'Connection succeeded.', objectEncoding: 0 });
      } else if (name === 'createStream') command(0, '_result', txn, null, 1);
      else if (name === 'releaseStream' || name === 'FCPublish') command(0, '_result', txn, null, null);
      else if (name === 'publish') {
        const key = String(vals[3] || '');
        log(`${who}: publish with stream key ${key ? key.slice(0, 4) + '…' : '(empty: put the key in OBS’s Stream Key box)'}`); state.lastConn.stage = 'publish';
        const r = await onPublish?.(key);
        if (!r || typeof r === 'string') { status(sid, 'error', 'NetStream.Publish.BadName', r || 'refused'); log('OBS stream refused: ' + (r || 'no handler')); setTimeout(() => sock.destroy(), 500); return; }
        sink = r; published = true; state.live++;
        status(sid, 'status', 'NetStream.Publish.Start', 'Publishing to OpenTube');
        log('OBS is streaming');
      } else if (name === 'FCUnpublish' || name === 'deleteStream') end();
    }
    function end() {
      if (!published) return;
      published = false; state.live = Math.max(0, state.live - 1);
      try { flush(true); } catch {}
      sink?.end(); sink = null;
      log(`OBS stream ended (${(state.bytes / 1e6).toFixed(1)} MB)`);
    }
    function onMessage(type, sid, ts, body) {
      if (type === 1) inChunk = body.readUInt32BE(0) & 0x7fffffff;
      else if (type === 20 || type === 17) { try { onCommand(sid, amfReadAll(type === 17 ? body.subarray(1) : body)); } catch (e) { log('rtmp command error', e.message); } }
      else if (type === 8 || type === 9) { try { media(type, ts, body); } catch (e) { log('rtmp media error', e.message); } }
    }
    sock.on('data', (d) => {
      buf = Buffer.concat([buf, d]);
      if (stage === 0) {   // C0 + C1 → S0 + S1 + S2
        if (buf.length < 1537) return;
        const c1 = buf.subarray(1, 1537);
        const s1 = Buffer.concat([u32(Math.floor(Date.now() / 1000)), u32(0), crypto.randomBytes(1528)]);
        sock.write(Buffer.concat([Buffer.from([3]), s1, c1]));
        buf = buf.subarray(1537); stage = 1; state.lastConn.stage = 'handshake';
      }
      if (stage === 1) { if (buf.length < 1536) return; buf = buf.subarray(1536); stage = 2; clearTimeout(shake); state.lastConn.stage = 'handshake done'; }   // C2
      while (stage === 2 && buf.length) {
        let pos = 0;
        const b0 = buf[pos++], fmt = b0 >> 6; let csid = b0 & 63;
        if (csid === 0) { if (buf.length < 2) return; csid = 64 + buf[pos++]; }
        else if (csid === 1) { if (buf.length < 3) return; csid = 64 + buf[pos] + buf[pos + 1] * 256; pos += 2; }
        const hl = [11, 7, 3, 0][fmt];
        if (buf.length < pos + hl) return;
        const st = streams.get(csid) || streams.set(csid, { ts: 0, delta: 0, len: 0, type: 0, sid: 0, body: [], got: 0, ext: false }).get(csid);
        let tsField = 0;
        if (fmt <= 2) tsField = buf.readUIntBE(pos, 3);
        if (fmt <= 1) { st.len = buf.readUIntBE(pos + 3, 3); st.type = buf[pos + 6]; }
        if (fmt === 0) st.sid = buf.readUInt32LE(pos + 7);
        pos += hl;
        let ext = fmt <= 2 ? tsField === 0xffffff : st.ext;
        let tsVal = tsField;
        if (ext) { if (buf.length < pos + 4) return; tsVal = buf.readUInt32BE(pos); pos += 4; }
        const take = Math.min(inChunk, st.len - st.got);
        if (buf.length < pos + take) return;
        if (st.got === 0) {   // a new message: work out its timestamp
          if (fmt === 0) st.ts = tsVal; else if (fmt === 1 || fmt === 2) { st.delta = tsVal; st.ts += tsVal; } else st.ts += st.delta;
          st.ext = ext;
        }
        st.body.push(buf.subarray(pos, pos + take)); st.got += take;
        buf = buf.subarray(pos + take);
        if (st.got >= st.len) { const body = Buffer.concat(st.body); st.body = []; st.got = 0; onMessage(st.type, st.sid, st.ts, body); }
      }
    });
    const bye = () => { clearTimeout(shake); end(); };
    sock.on('close', bye); sock.on('error', bye);
  });
  server.on('error', (e) => log(`OBS receiver couldn’t listen on ${host}:${port}: ${e.message}`));
  // Listen on IPv6 and IPv4 together ('::'): on Windows and macOS "localhost" means ::1 first, and OBS only tries the
  // first address, so an IPv4-only receiver leaves OBS stuck at "Connecting…". Falls back to IPv4 where IPv6 is off.
  const listen = (h) => { state.trying = h; server.removeAllListeners('listening'); server.listen({ port, host: h, ipv6Only: false }); server.once('listening', () => { state.listening = true; state.error = null; state.host = h; setTimeout(() => { const b = portHolders(port); if (b.length) log(`the system says port ${port} is bound on: ${b.map(x => x.addr + (x.pid === process.pid ? ' (this OpenTube)' : ` (process ${x.pid}: ${x.cmd.slice(0, 60)})`)).join(', ')}`); }, 200); log(`OBS receiver on rtmp://${h === '127.0.0.1' || h === '::1' ? 'localhost' : 'this-computer'}:${port}/live (listening on ${h === '::' ? 'all addresses, IPv4 and IPv6' : h === '0.0.0.0' ? 'all IPv4 addresses' : h})`); }); };
  server.on('error', async (e) => {
    if (host === '::' && ['EAFNOSUPPORT', 'EADDRNOTAVAIL', 'EINVAL'].includes(e.code) && !state.fellBack) { state.fellBack = true; return listen('0.0.0.0'); }
    if (e.code === 'EADDRINUSE') {
      // Someone else has the port, often only on 127.0.0.1 (an older OpenTube), which also stops us listening on all
      // addresses: OBS on this computer then reaches the old one and OBS elsewhere reaches nothing. If it's an older
      // OpenTube, stop it and take the port.
      const holders = portHolders(port);
      const ours = holders.filter(x => x.pid !== process.pid && x.pid !== process.ppid && /server\.mjs|opentube/i.test(x.cmd));
      if (ours.length && !state.reclaimed) {
        state.reclaimed = true;
        log(`port ${port} is held by an older OpenTube (process ${ours.map(x => `${x.pid} on ${x.addr}`).join(', ')}): stopping it and taking the port on all addresses`);
        for (const x of ours) { for (const pid of [x.pid, x.ppid]) if (pid > 1 && pid !== process.pid && pid !== process.ppid && (pid === x.pid || /opentube/i.test(cmdOf(pid)))) try { process.kill(pid, 'SIGTERM'); } catch {} }
        await new Promise(r => setTimeout(r, 2500));
        for (const x of ours) try { process.kill(x.pid, 'SIGKILL'); } catch {}
        await new Promise(r => setTimeout(r, 500));
        return listen(state.trying || host);
      }
      state.error = `port ${port} is already taken by another program` + (holders.length ? ` (${holders.map(x => `process ${x.pid} “${x.cmd.slice(0, 80)}” on ${x.addr}`).join('; ')})` : '') + `, so OBS can’t reach this OpenTube. Stop that program, then start OpenTube again. To see it: sudo ss -ltnp | grep :${port}`;
    } else state.error = e.message;
    log('OBS receiver: ' + state.error);
  });
  listen(host);
  return { status: () => ({ port, host: state.host || host, listening: !!state.listening, error: state.error || null, live: state.live > 0, bytes: state.bytes, lastConn: state.lastConn || null }), close: () => server.close() };
}
