// Run: node dotnet/OpenTube.Tests/test-dns-zone.cjs   (needs Xvfb for the desktop app, and a built OpenTube.App and OpenTube.Uno desktop)
// Domains (the setting saved by the node's window): the app starts a zone for example.test and approves names; a node answering DNS for it (--dns) gives the
// zone's names, the wildcard namespaces, the zone's TXT and NXDOMAIN for the rest; and a subdomain's home page on the
// node opens the page its record names.
const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os'), dns = require('dns'), http = require('http');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-dns-')), N = 8997, DP = 5353;
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
const procs = []; process.on('exit', () => { for (const p of procs) try { p.kill(); } catch {} });
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f));
// the domain setting as the node's window saves it (Domain… → Save): DATA/domain.json
fs.mkdirSync(W + '/node', { recursive: true }); fs.writeFileSync(W + '/node/domain.json', JSON.stringify({ domain: 'example.test', port: DP, ip: '1.2.3.4' }));
const P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, `--videos=${W}/videos`, '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh',
  '--site-dir=' + SITE]); procs.push(P);
let nodeLog = ''; P.stdout.on('data', d => { nodeLog += d; for (const l of String(d).split('\n')) if (/\[dns\]|rror/.test(l)) log('[node]', l.trim().slice(0, 160)); });
for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); break; } catch { execSync('sleep 0.5'); } }
// the app (desktop, on a virtual screen): its test setup makes a channel, then the zone with two names
const X = spawn('Xvfb', [':93', '-screen', '0', '412x900x24']); procs.push(X); execSync('sleep 1.5');
const APP = REPO + '/dotnet/OpenTube.Uno/OpenTube.Uno/bin/Debug/net10.0-desktop';
const A = spawn('dotnet', ['OpenTube.Uno.dll'], { cwd: APP, env: { ...process.env, DISPLAY: ':93', OPENTUBE_DATA: W + '/app', OPENTUBE_SITE: `http://localhost:${N}`, OPENTUBE_TEST: 'setup,zone', OPENTUBE_START: 'go:domains/example.test' } }); procs.push(A);
let appLog = ''; A.stdout.on('data', d => appLog += d);
const r = new dns.promises.Resolver(); r.setServers([`127.0.0.1:${DP}`]);
const q = (f, name) => f.call(r, name).then(x => x, e => e.code);
(async () => {
  const t0 = Date.now(); while (!/\[test\] zone [0-9a-f]{64}/.test(appLog) && Date.now() - t0 < 300000) await new Promise(s => setTimeout(s, 1000));
  const zone = (/\[test\] zone ([0-9a-f]{64})/.exec(appLog) || [])[1]; const chain = (/\[test\] setup ready: ([0-9a-f]{64})/.exec(appLog) || [])[1];
  check(!!zone, 'the app starts a zone for example.test and approves two names'); if (!zone) { log(appLog.split('\n').filter(l => /test|rror/.test(l)).slice(-4).join(' | ')); process.exit(1); }
  // the node hears of the zone and its records (registration needs a main-chain block)
  let tucson; for (let i = 0; i < 90 && !Array.isArray(tucson); i++) { tucson = await q(r.resolve4, 'tucson.example.test'); if (!Array.isArray(tucson)) await new Promise(s => setTimeout(s, 1000)); }
  check(JSON.stringify(tucson) === '["5.6.7.8"]', 'an approved name answers with the address it names: ' + JSON.stringify(tucson));
  check(JSON.stringify(await q(r.resolve4, 'home.example.test')) === '["1.2.3.4"]', 'a name without an address answers with the domain’s node');
  check(JSON.stringify(await q(r.resolve4, 'example.test')) === '["1.2.3.4"]', 'the domain itself answers with its node');
  check(JSON.stringify(await q(r.resolve4, 'alice.c.example.test')) === '["1.2.3.4"]', 'a wildcard namespace (alice.c) answers with the domain’s node');
  check(JSON.stringify(await q(r.resolve4, 'bob.u.example.test')) === '["1.2.3.4"]', 'and people (bob.u) too');
  check(await q(r.resolve4, 'nobody.example.test') === 'ENOTFOUND', 'an unknown name: no such domain');
  const txt = await q(r.resolveTxt, '_opentuber.example.test');
  check(Array.isArray(txt) && txt.flat().join('') === 'zone=' + zone, 'TXT _opentuber names the zone chain');
  const ns = await q(r.resolveNs, 'example.test');
  check(Array.isArray(ns) && ns.includes('ns1.example.test'), 'NS: ns1/ns2 of the domain ' + JSON.stringify(ns));
  check(await q(r.resolve4, 'example.com') === 'EREFUSED', 'names outside the domain are refused');
  // the subdomain's home page on the node opens the page its record names
  const loc = await new Promise(res => http.get({ host: '127.0.0.1', port: N, path: '/', headers: { host: 'tucson.example.test' } }, x => { res(x.statusCode + ' ' + (x.headers.location || '')); x.resume(); }).on('error', e => res(e.message)));
  check(loc === `302 /app/c/${chain}`, 'tucson.example.test/ opens the page it names: ' + loc);
  const loc2 = await new Promise(res => http.get({ host: '127.0.0.1', port: N, path: '/', headers: { host: 'example.test' } }, x => { res(String(x.statusCode)); x.resume(); }).on('error', e => res(e.message)));
  check(loc2 === '200', 'the domain itself serves the site: ' + loc2);
  process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
