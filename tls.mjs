// Automatic HTTPS for an OpenTube node, so browsers (which only accept secure connections) can use it as a node and a
// tracker. No domain needed: the node uses <its public IP, dashed>.sslip.io (e.g. 203-0-113-7.sslip.io), a public DNS
// name that resolves to that IP for everyone, and gets a Let's Encrypt certificate for it by itself (renewed
// automatically). Or give it your own domain. Needs ports 80 and 443 open (on Linux, run as root or allow Node to bind
// them), and the acme-client package (opentube.mjs installs it).
//
//   const pub = await autoHttps({ handler, upgrade, dataDir, domain, email, log });   // -> { url: 'https://…' } or null
import http from 'node:http';
import https from 'node:https';
import fs from 'node:fs';
import path from 'node:path';

async function publicIPv4() {
  for (const u of ['https://api.ipify.org', 'https://ifconfig.me/ip', 'https://icanhazip.com']) {
    try { const ip = (await (await fetch(u, { signal: AbortSignal.timeout(5000) })).text()).trim(); if (/^\d{1,3}(\.\d{1,3}){3}$/.test(ip)) return ip; } catch {}
  }
  return null;
}
export const sslipName = (ip) => ip.replace(/\./g, '-') + '.sslip.io';

export async function autoHttps({ handler, upgrade, dataDir, domain = null, email = null, httpPort = Number(process.env.PUBLIC_HTTP_PORT) || 80, httpsPort = Number(process.env.PUBLIC_HTTPS_PORT) || 443, staging = !!process.env.PUBLIC_STAGING, log = console.log }) {
  const acme = (await import('acme-client')).default;
  let host = domain;
  if (!host) {
    const ip = process.env.PUBLIC_IP || await publicIPv4();
    if (!ip) { log('[https] couldn’t find this computer’s public IP; HTTPS is off'); return null; }
    host = sslipName(ip);
  }
  const dir = path.join(dataDir, 'tls'); fs.mkdirSync(dir, { recursive: true });
  const files = { account: path.join(dir, 'account.pem'), key: path.join(dir, host + '.key'), cert: path.join(dir, host + '.crt') };
  const tokens = new Map();   // ACME http-01 challenges

  // Port 80: Let's Encrypt's check, everything else is the node itself (so plain http:// works too).
  const plain = http.createServer((req, res) => {
    const m = /^\/\.well-known\/acme-challenge\/([\w-]+)$/.exec(req.url || '');
    if (m) { const k = tokens.get(m[1]); res.writeHead(k ? 200 : 404, { 'content-type': 'text/plain' }); return res.end(k || 'not found'); }
    handler(req, res);
  });
  plain.on('upgrade', upgrade);
  await new Promise((res, rej) => plain.once('error', rej).listen(httpPort, res)).catch((e) => { throw new Error(`port ${httpPort} isn’t available (${e.code}); HTTPS needs ports 80 and 443`); });

  const expires = () => { try { return new crypto.X509Certificate(fs.readFileSync(files.cert)).validToDate.getTime(); } catch { return 0; } };
  async function issue() {
    const accountKey = fs.existsSync(files.account) ? fs.readFileSync(files.account) : await acme.crypto.createPrivateKey();
    fs.writeFileSync(files.account, accountKey, { mode: 0o600 });
    const client = new acme.Client({ directoryUrl: staging ? acme.directory.letsencrypt.staging : acme.directory.letsencrypt.production, accountKey });
    const [key, csr] = await acme.crypto.createCsr({ commonName: host });
    const cert = await client.auto({
      csr, email: email || undefined, termsOfServiceAgreed: true, challengePriority: ['http-01'],
      challengeCreateFn: async (authz, challenge, keyAuthorization) => { tokens.set(challenge.token, keyAuthorization); },
      challengeRemoveFn: async (authz, challenge) => { tokens.delete(challenge.token); }
    });
    fs.writeFileSync(files.key, key, { mode: 0o600 }); fs.writeFileSync(files.cert, cert);
    log(`[https] certificate for ${host} from Let's Encrypt, valid until ${new Date(expires()).toISOString().slice(0, 10)}`);
  }
  if (expires() < Date.now() + 30 * 86400000) {
    log(`[https] getting a certificate for ${host} from Let's Encrypt…`);
    try { await issue(); } catch (e) { log('[https] couldn’t get a certificate:', e.message, `(is port ${httpPort} reachable from the internet?)`); return null; }
  }
  const secure = https.createServer({ key: fs.readFileSync(files.key), cert: fs.readFileSync(files.cert) }, handler);
  secure.on('upgrade', upgrade);
  await new Promise((res, rej) => secure.once('error', rej).listen(httpsPort, res)).catch((e) => { throw new Error(`port ${httpsPort} isn’t available (${e.code})`); });
  // Renew a month before it runs out, without restarting.
  setInterval(async () => {
    if (expires() > Date.now() + 30 * 86400000) return;
    try { await issue(); secure.setSecureContext({ key: fs.readFileSync(files.key), cert: fs.readFileSync(files.cert) }); } catch (e) { log('[https] renewal failed:', e.message); }
  }, 12 * 3600000);
  log(`[https] this node is public at https://${host} (tracker wss://${host}/tracker)`);
  return { url: 'https://' + host, host };
}
import crypto from 'node:crypto';
