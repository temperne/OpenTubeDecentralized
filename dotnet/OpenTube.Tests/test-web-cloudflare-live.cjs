// Run: (wrangler dev --local --port 8920 in the repo root, after node cloudflare/build.mjs) then
//      node dotnet/OpenTube.Tests/test-web-cloudflare-live.cjs http://127.0.0.1:8920/
// Against the Cloudflare site itself (its worker: static files, the fallback node at /ot and the fallback live hub at
// /live): one browser goes live from its camera on the C# site, another browser opens the stream and it plays.
const { chromium } = require('playwright'); const path = require('path'), os = require('os'), fs = require('fs');
const W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-cf-')); const BASE = (process.argv[2] || 'http://127.0.0.1:8920/').replace(/\/?$/, '/') + 'app/';
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
const S = -1;
const waitText = async (p, re, ms = 60000, sel = 'main') => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (re.test(await p.textContent(sel).catch(() => '') || '')) return true; await p.waitForTimeout(1000); } return false; };
(async () => {
  const errs = [];
  const ca = await chromium.launchPersistentContext(W + '/a', { headless: true, args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] }); const A = ca.pages()[0]; A.on('pageerror', e => errs.push(e.message));
  A.on('response', r => { if (r.status() >= 400) log('[A] HTTP', r.status(), r.url()); }); A.on('requestfailed', r => log('[A] failed', r.url(), r.failure()?.errorText));
  A.on('console', m => { if (/opentube|rror/i.test(m.text())) log('[A]', m.text().slice(0, 140)); });
  await A.goto(BASE + 'you/channels'); if (!await A.waitForSelector('input[placeholder=Name]', { timeout: 60000 }).then(() => true, () => false)) { log('PAGE:', (await A.textContent('body')).slice(0, 400)); log('URL', A.url()); }
  check(await waitText(A, /./, 1000, 'header a.net') && await A.waitForFunction(() => /Online|Limited/.test(document.querySelector('header a.net')?.textContent || ''), null, { timeout: 60000 }).then(() => true, () => false), 'the site links to the network: ' + (await A.textContent('header a.net').catch(() => '')).trim());
  await A.fill('input[placeholder=Name]', 'Static Channel'); await A.click('button:has-text("Create & register")');
  await A.waitForURL(/\/app\/c\//, { timeout: 240000 }); const chain = A.url().split('/c/')[1].split(/[/?#]/)[0];
  check(await A.waitForFunction(() => !/being registered/.test(document.body.textContent), null, { timeout: 240000 }).then(() => true, () => false), 'channel registered');
  await A.goto(BASE + 'golive/' + chain); await A.waitForSelector('button:has-text("● Go live")', { timeout: 60000 });
  await A.fill('input[maxlength="140"]', 'Static live'); await A.click('button:has-text("● Go live")');
  check(await waitText(A, /Live · .* sent/, 60000), 'live: ' + (await A.textContent('#golive-status').catch(() => '')));
  // another person, another browser
  const cb = await chromium.launchPersistentContext(W + '/b', { headless: true }); const B = cb.pages()[0]; B.on('pageerror', e => errs.push(e.message));
  await B.goto(BASE + 'c/' + chain);
  const href = await B.waitForSelector(`a[href^="live/${chain}/"]`, { timeout: 120000 }).then(e => e.getAttribute('href'), () => null);
  check(!!href, 'the viewer sees the stream live on the channel');
  if (href) await B.goto(BASE + href); else await B.goto(BASE + 'explore');
  const played = await B.waitForFunction(() => { const v = document.querySelector('video'); return v && v.readyState >= 2 && v.currentTime > 1; }, null, { timeout: 90000 }).then(() => true, () => false);
  check(played, 'it plays for the viewer: ' + (await B.textContent('#live-status').catch(() => '')));
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await ca.close(); await cb.close(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
