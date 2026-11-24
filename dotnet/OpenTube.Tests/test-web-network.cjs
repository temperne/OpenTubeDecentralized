// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-network.cjs <dir>/wwwroot
// The network page in the C# pages: checks (linked to the node, main chain, meet places tested over WebSocket, your
// computers), the connection, the main chain, chains this device keeps, transfers and storage.
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-net-')), N = 8896, APP = path.resolve(process.argv[2]);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
const waitText = async (p, re, ms = 60000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (re.test(await p.textContent('main').catch(() => '') || '')) return true; await p.waitForTimeout(1000); } return false; };
let P; process.on('exit', () => { try { P?.kill(); } catch {} });
(async () => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, '--no-host', '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', '--no-obs', '--site-dir=' + SITE, '--app-dir=' + APP]);
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); break; } catch { execSync('sleep 0.5'); } }
  const BASE = `http://localhost:${N}/`; const errs = [];
  const b = await chromium.launch(); const X = await b.newPage(); X.on('pageerror', e => errs.push(e.message));
  await X.goto(BASE + '#/network'); await X.waitForURL(/\/app\/network/, { timeout: 60000 }).catch(() => {});
  check(/\/app\/network/.test(X.url()), 'the old #/network link opens the C# network page');
  check(await waitText(X, /Network — linked to 1 node/, 60000), 'linked to the node');
  check(await waitText(X, /Video meet places — [1-9]\d* of \d+ reachable/, 60000), 'meet places tested over WebSocket: ' + ((await X.textContent('main')).match(/Video meet places — [^\n]*?reachable/) || [''])[0]);
  check(await X.isVisible('td.mono:has-text("/tracker")') && await waitText(X, /✓ reachable/, 10000), 'the node’s tracker is listed as reachable');
  check(await waitText(X, /Height \/ tip#\d+/, 10000), 'the main chain');
  check(await waitText(X, /Used\d/, 20000), 'storage used');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await b.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
