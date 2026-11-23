// Run: dotnet publish dotnet/OpenTube.Web -c Release -o <dir> && node dotnet/OpenTube.Tests/test-web-keys.cjs <dir>/wwwroot
// Key management in the C# pages (website and phone app), checked by a JavaScript page elsewhere on the network:
// back up all keys (the JavaScript page's file format), channel keys bound to you, messaging keys added and retired,
// a channel given a new key, the backup imported on another device (same person, same channel), and the personal key
// retired (the network hands everything to the new one).
const { chromium } = require('playwright'); const { spawn, execSync } = require('child_process'); const fs = require('fs'), path = require('path'), os = require('os');
const REPO = path.resolve(__dirname, '../..'), W = fs.mkdtempSync(path.join(os.tmpdir(), 'opentube-keys-')), N = 8881, APP = path.resolve(process.argv[2]);
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const SITE = W + '/site'; fs.mkdirSync(SITE); for (const f of ['index.html', 'sw.js']) fs.copyFileSync(path.join(REPO, f), path.join(SITE, f)); fs.writeFileSync(SITE + '/seeds.json', '{"entry":[]}');
const nodeCrypto = require('crypto');
// open a password-locked key file the way the format says (PBKDF2-SHA256 → AES-256-GCM): null on a wrong password
const unlock = (f, pw) => { try { const key = nodeCrypto.pbkdf2Sync(Buffer.from(pw.normalize('NFC')), Buffer.from(f.salt, 'base64url'), f.iterations, 32, 'sha256');
  const ct = Buffer.from(f.ct, 'base64url'), d = nodeCrypto.createDecipheriv('aes-256-gcm', key, Buffer.from(f.iv, 'base64url')); d.setAuthTag(ct.subarray(-16));
  return JSON.parse(Buffer.concat([d.update(ct.subarray(0, -16)), d.final()]).toString()); } catch { return null; } };
const PW = 'correct horse battery';
let fail = 0; const check = (ok, what) => { log(ok ? 'ok  ' : 'FAIL', what); if (!ok) fail = 1; };
let P; process.on('exit', () => { try { P?.kill(); } catch {} });
const until = async (p, fn, arg, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (await p.evaluate(fn, arg).catch(() => false)) return true; await new Promise(r => setTimeout(r, 1000)); } return false; };
const waitText = async (p, re, ms = 120000) => { const t0 = Date.now(); while (Date.now() - t0 < ms) { if (re.test(await p.textContent('main').catch(() => '') || '')) return true; await p.waitForTimeout(1000); } return false; };
(async () => {
  P = spawn('dotnet', [REPO + '/dotnet/OpenTube.App/bin/Debug/net10.0/opentube.dll', '--service', `--port=${N}`, `--data=${W}/node`, '--no-host', '--mine=all', '--no-tunnel', '--public=off', '--no-ranking', '--no-mesh', '--no-obs', '--site-dir=' + SITE, '--app-dir=' + APP]);
  for (let i = 0; i < 60; i++) { try { execSync(`curl -sf localhost:${N}/status`); break; } catch { execSync('sleep 0.5'); } }
  const BASE = `http://localhost:${N}/`;
  // O: someone else on the network (the JavaScript page), who checks what the network says
  const co = await chromium.launchPersistentContext(W + '/o', { headless: true }); const O = co.pages()[0];
  await O.goto(BASE + '?folder=browser#/'); await O.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 });
  // X: the C# pages
  const cx = await chromium.launchPersistentContext(W + '/x', { headless: true, acceptDownloads: true }); const X = cx.pages()[0]; const errs = [];
  X.on('pageerror', e => errs.push(e.message));
  await X.goto(BASE + 'app/you'); await X.waitForSelector('.mono', { timeout: 60000 }); const x = (await X.textContent('.mono')).trim();
  await X.goto(BASE + 'app/you/channels'); await X.waitForSelector('input[placeholder=Name]', { timeout: 60000 });
  await X.fill('input[placeholder=Name]', 'Keyed Channel'); await X.click('text=Create & register');
  await X.waitForURL(/\/app\/c\//, { timeout: 180000 }); const chain = X.url().split('/c/')[1];
  const oldOwner = await (async () => { for (let i = 0; i < 60; i++) { const o = await O.evaluate((c) => opentube.engine.directory.get(c)?.genesis.author, chain); if (o) return o; await new Promise(r => setTimeout(r, 1000)); } return null; })();
  await X.goto(BASE + 'app/you/keys'); await X.waitForSelector('text=Back up all keys', { timeout: 30000 });
  check(await waitText(X, /2 keys not backed up/, 20000), 'Keys: the page warns that 2 keys (personal, channel) aren’t backed up');
  await X.click('button:has-text("Back up all keys")');
  check(await waitText(X, /choose a password/i, 10000), 'backing up needs a password first');
  await X.fill('#keys-pass', PW); await X.fill('#keys-pass2', PW + 'x'); await X.click('button:has-text("Back up all keys")');
  check(await waitText(X, /don’t match/, 10000), 'the two passwords must match');
  await X.fill('#keys-pass2', PW);
  const [dl] = await Promise.all([X.waitForEvent('download'), X.click('button:has-text("Back up all keys")')]);
  const locked = JSON.parse(fs.readFileSync(await dl.path(), 'utf8')); const file = W + '/backup.json'; fs.writeFileSync(file, JSON.stringify(locked));
  check(locked.format === 'opentube-encrypted' && locked.kdf === 'pbkdf2-sha256' && !JSON.stringify(locked).includes(x), 'the backup file is locked (no keys readable in it)');
  check(unlock(locked, 'wrong password') === null, 'a wrong password doesn’t open it');
  const backup = unlock(locked, PW) || {};
  check(backup.format === 'opentube-keys' && backup.personal?.pub === x && backup.personal?.priv && backup.personal?.msg?.length > 0 && backup.channels?.some(c => c.chain === chain && c.pub === oldOwner && c.priv), `the backup file: the JavaScript page’s format, your personal key (with messaging keys) and the channel’s key (${dl.suggestedFilename()})`);
  check(await waitText(X, /all keys backed up/, 10000), 'after the backup: “all keys backed up”');
  check(await until(O, ({ k, me }) => opentube.engine.ledger.resolve(k) === me, { k: oldOwner, me: x }, 180000), 'the channel key is bound to your identity (the network says so)');
  // messaging keys: a new one, then retire one
  check(await until(O, (me) => (opentube.engine.ledger.msgkeys.get(me) || []).length === 1, x, 120000), 'your messaging key is published');
  await X.click('button:has-text("New messaging key")');
  check(await until(O, (me) => (opentube.engine.ledger.msgkeys.get(me) || []).length === 2, x, 120000), 'a new messaging key is published (2)');
  await X.waitForTimeout(2000); await X.click('.row-item:has-text("published") button:has-text("Retire")');
  check(await until(O, (me) => (opentube.engine.ledger.msgkeys.get(me) || []).length === 1, x, 120000), 'retiring one takes it off the network (1)');
  // the channel gets a new key
  await X.click('button:has-text("New key…")'); await X.click('button:has-text("Replace its key")');
  await O.goto(BASE + '?folder=browser#/c/' + chain); await O.waitForTimeout(1500);
  const newOwner = await (async () => { for (let i = 0; i < 90; i++) { const o = await O.evaluate((c) => opentube.engine.derive(c)?.owner, chain).catch(() => null); if (o && o !== oldOwner) return o; await new Promise(r => setTimeout(r, 1000)); } return null; })();
  check(!!newOwner, 'the channel has a new key (the chain’s owner changed)');
  // Y: another device imports the backup
  const cy = await chromium.launchPersistentContext(W + '/y', { headless: true }); const Y = cy.pages()[0]; Y.on('pageerror', e => errs.push(e.message));
  await Y.goto(BASE + 'app/you/keys'); await Y.waitForSelector('text=Import keys', { timeout: 60000 });
  await Y.setInputFiles('input[type=file]', file); await Y.waitForSelector('#keys-unlock', { timeout: 20000 });
  await Y.fill('#keys-unlock', 'not the password'); await Y.click('button:has-text("Unlock and import")');
  check(await waitText(Y, /Wrong password/, 30000), 'importing it with a wrong password: “Wrong password”');
  await Y.fill('#keys-unlock', PW); await Y.click('button:has-text("Unlock and import")');
  await Y.waitForSelector('text=Use the key from the file', { timeout: 30000 });
  await Y.click('button:has-text("Use the key from the file")');
  await Y.waitForTimeout(3000); await Y.goto(BASE + 'app/you'); await Y.waitForSelector('.mono', { timeout: 60000 });
  check((await Y.textContent('.mono')).trim() === x, 'another device imports the backup: the same person (same user ID) after the restart');
  await Y.goto(BASE + 'app/you/keys'); check(await waitText(Y, /Keyed Channel/, 60000), 'and it holds the channel’s key');
  const J = await cy.newPage(); await J.goto(BASE + '?folder=browser#/you'); await J.waitForFunction(() => window.opentube?.engine, null, { timeout: 60000 });
  check(await J.evaluate(() => opentube.me.pub) === x, 'the JavaScript page on that device uses the imported key too');
  // the JavaScript page reads the C#-made locked file, and makes one the C# format reads
  await J.goto(BASE + '?folder=browser#/you/keys'); await J.waitForSelector('button:has-text("Back up all keys")', { timeout: 60000 });
  await J.setInputFiles('.panel:has(h3:text-is("Keys & backup")) input[type=file]', file); await J.waitForSelector('#pw-dialog #pw1', { timeout: 20000 });
  await J.fill('#pw1', PW); await J.click('#pw-ok');
  check(await J.waitForFunction(() => /Imported 1 channel/.test(document.querySelector('#toast')?.textContent || ''), null, { timeout: 30000 }).then(() => true, () => false), 'the JavaScript page imports the C#-made locked file with its password');
  await J.goto(BASE + '?folder=browser#/you/keys'); await J.waitForSelector('button:has-text("Back up all keys")', { timeout: 60000 });
  const [jdl] = await Promise.all([J.waitForEvent('download'), (async () => { await J.click('button:has-text("Back up all keys")'); await J.fill('#pw1', 'js side pass'); await J.fill('#pw2', 'js side pass'); await J.click('#pw-ok'); })()]);
  const jfile = W + '/js-backup.json'; fs.copyFileSync(await jdl.path(), jfile); const jl = JSON.parse(fs.readFileSync(jfile, 'utf8'));
  check(jl.format === 'opentube-encrypted' && unlock(jl, 'js side pass')?.personal?.pub === x, 'the JavaScript page’s backup is locked in the same format');
  await J.close();
  // the C# page reads the JavaScript-made file (same person, so only the channel key is new or already there)
  await Y.goto(BASE + 'app/you/keys'); await Y.waitForSelector('text=Import keys', { timeout: 60000 });
  await Y.setInputFiles('input[type=file]', jfile); await Y.waitForSelector('#keys-unlock', { timeout: 20000 });
  await Y.fill('#keys-unlock', 'js side pass'); await Y.click('button:has-text("Unlock and import")');
  check(await waitText(Y, /Imported 1 channel|already here/, 30000), 'the C# page imports the JavaScript-made locked file');
  // retire the personal key (on Y)
  await Y.click('button:has-text("Retire my personal key")'); await Y.click('button:has-text("Yes: make a new key")');
  let y2 = x; for (let i = 0; i < 30 && y2 === x; i++) { await Y.waitForTimeout(3000); await Y.goto(BASE + 'app/you').catch(() => {}); await Y.waitForSelector('.mono', { timeout: 60000 }).catch(() => {}); y2 = (await Y.textContent('.mono').catch(() => x)).trim(); }
  check(y2 !== x, 'retiring: this device now uses a new personal key');
  check(await until(O, ({ old, nu }) => opentube.engine.ledger.retired.has(old) && opentube.engine.ledger.resolve(old) === nu, { old: x, nu: y2 }, 180000), 'the network retired the old key and hands what it had to the new one');
  check(errs.length === 0, 'no page errors' + (errs.length ? ': ' + errs.slice(0, 3).join(' | ') : ''));
  await co.close(); await cx.close(); await cy.close(); P.kill(); process.exit(fail);
})().catch(e => { console.error(e); process.exit(1); });
