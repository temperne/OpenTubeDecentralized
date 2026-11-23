// Run: dotnet build dotnet && node dotnet/OpenTube.Tests/test-flash.cjs
// Flash computers on the .NET node, with the "local" provider (the flash computer is another .NET node on this
// machine): settings sealed in the browser to the node's key (ECDH + AES-GCM), start one, it comes online as the
// person's computer (a flash computer of this one), goes idle, and is deleted.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-flash-')), N = 8906, BASE = `http://localhost:${N}/`;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return ((Date.now() - t0) / 1000).toFixed(1); await new Promise(r => setTimeout(r, 500)); } throw new Error('timeout'); };
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let P, lines = []; const start = (grant, env = {}) => { P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, '--no-host', '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', '--no-obs', '--site-dir=' + SITE, ...(grant ? ['--grant=' + grant] : [])], { env: { ...process.env, ...env } });
  P.stdout.on('data', d => { for (const l of String(d).split('\n')) { lines.push(l); if (/\[flash\]|rror/.test(l)) log('[C#]', l.trim().replace(/^\d\d:\d\d:\d\d /, '').slice(0, 170)); } });
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); return; } catch { execSync('sleep 0.5'); } } };
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
(async () => {
  start();
  const ctx = await chromium.launchPersistentContext(W + '/b', { headless: true }); const A = ctx.pages()[0];
  await A.goto(BASE + '?folder=browser#/'); await A.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 });
  const grant = await A.evaluate(async () => { const g = { v: 1, kind: 'nodegrant', user: opentube.me.pub, at: Date.now(), label: '' }; g.sig = await opentube.me.sign(OpenTubeCore.nodeGrantPreimage(g)); return btoa(JSON.stringify(g)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); });
  const g0 = new Promise(r => P.on('exit', r)); P.kill('SIGKILL'); await g0; execSync('sleep 1');
  start(grant, { FLASH_PROVIDER: 'local', FLASH_IDLE_MIN: '0.5' });   // (the owner's login from the command line; a short idle time for the test)
  const nodeKey = JSON.parse(execSync(`curl -s localhost:${N}/status`)).node;
  await until(A, (k) => opentube.engine.nodes.has(k), nodeKey, 90000);
  const ctl = (cmd, args) => A.evaluate(async ({ k, cmd, args }) => { try { return (await opentube.nodeCtl(opentube.engine.nodes.get(k), cmd, args)).data; } catch (e) { return 'refused: ' + e.message; } }, { k: nodeKey, cmd, args });
  let st = await ctl('flash', {});
  check(typeof st === 'object' && st.enc && st.mine?.provider === 'local' && st.mine.ready, `flash settings: provider ${st.mine?.provider}, ready ${st.mine?.ready}, key ${String(st.enc).slice(0, 12)}…`);
  // settings sealed in the browser to this computer's key
  st = await A.evaluate(async ({ k, enc }) => {
    // the site's sealFor (WebCrypto): ECDH P-256 with an ephemeral key, SHA-256(secret + label) → AES-256-GCM
    const b64u = (b) => btoa(String.fromCharCode(...new Uint8Array(b))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    const unb = (x) => Uint8Array.from(atob(x.replace(/-/g, '+').replace(/_/g, '/') + '==='.slice((x.length + 3) % 4)), c => c.charCodeAt(0));
    const their = await crypto.subtle.importKey('raw', unb(enc), { name: 'ECDH', namedCurve: 'P-256' }, false, []);
    const eph = await crypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, true, ['deriveBits']);
    const bits = new Uint8Array(await crypto.subtle.deriveBits({ name: 'ECDH', public: their }, eph.privateKey, 256)), label = new TextEncoder().encode('opentube-seal-v1');
    const kin = new Uint8Array(bits.length + label.length); kin.set(bits); kin.set(label, bits.length);
    const key = await crypto.subtle.importKey('raw', await crypto.subtle.digest('SHA-256', kin), 'AES-GCM', false, ['encrypt']);
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const ct = await crypto.subtle.encrypt({ name: 'AES-GCM', iv }, key, new TextEncoder().encode(JSON.stringify({ provider: 'local', maxHours: 2, billing: 'second' })));
    const sealed = { epk: b64u(await crypto.subtle.exportKey('raw', eph.publicKey)), iv: b64u(iv), ct: b64u(ct) };
    return (await opentube.nodeCtl(opentube.engine.nodes.get(k), 'flash.setup', { sealed })).data;
  }, { k: nodeKey, enc: st.enc });
  check(st.mine?.maxHours === 2 && st.mine.billing === 'second', 'sealed settings opened on the node (browser WebCrypto → C#)');
  st = await A.evaluate(async (k) => { const g = { v: 1, kind: 'nodegrant', user: opentube.me.pub, at: Date.now(), label: 'flash' }; g.sig = await opentube.me.sign(OpenTubeCore.nodeGrantPreimage(g));
    const grant = btoa(JSON.stringify(g)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    try { return (await opentube.nodeCtl(opentube.engine.nodes.get(k), 'flash.start', { grant })).data; } catch (e) { return 'refused: ' + e.message; } }, nodeKey);
  if (typeof st === 'string') log(st);
  const tag = st.children?.[0]?.tag; check(!!tag, `started ${tag} (${st.children?.[0]?.state})`);
  const t0 = Date.now(); let ch;
  while (Date.now() - t0 < 120000) { ch = (await ctl('flash', {})).children?.find(x => x.tag === tag); if (ch?.state === 'running') break; await new Promise(r => setTimeout(r, 2000)); }
  check(ch?.state === 'running' && ch.node, `the flash computer came online as yours after ${((Date.now() - t0) / 1000).toFixed(0)} s (node ${String(ch?.node).slice(0, 8)})`);
  const onDevices = await A.evaluate((k) => opentube.engine.nodes.get(k)?.info?.flash, ch?.node);
  check(onDevices?.parent === nodeKey && onDevices?.tag === tag, 'it shows on your Devices as a flash computer of this one');
  const t1 = Date.now();
  while (Date.now() - t1 < 150000) { ch = (await ctl('flash', {})).children?.find(x => x.tag === tag); if (ch?.state === 'deleted') break; await new Promise(r => setTimeout(r, 3000)); }
  check(ch?.state === 'deleted', `idle, so it was deleted: ${ch?.why}`);
  await ctx.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); try { P.kill(); } catch {} process.exit(1); });
