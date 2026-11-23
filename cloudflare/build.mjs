// Build step for the Cloudflare deployment (wrangler runs this automatically).
// Workers can't eval code, so instead of reading the rules out of index.html at
// runtime like server.mjs does, this copies the exact same <script id="opentube-core">
// block into an ES module. One source of truth, still.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const html = fs.readFileSync(path.join(here, '..', 'index.html'), 'utf8');
const core = /<script id="opentube-core">([\s\S]*?)<\/script>/.exec(html)?.[1];
if (!core) throw new Error('index.html has no opentube-core script');

fs.writeFileSync(path.join(here, 'core.gen.mjs'), '// GENERATED from index.html by build.mjs. Do not edit.\n' + core + '\nexport default globalThis.OpenTubeCore;\n');
fs.mkdirSync(path.join(here, 'dist'), { recursive: true });
fs.writeFileSync(path.join(here, 'dist', 'index.html'), html);
fs.copyFileSync(path.join(here, '..', 'sw.js'), path.join(here, 'dist', 'sw.js'));  // streaming service worker
if (fs.existsSync(path.join(here, '..', 'seeds.json'))) fs.copyFileSync(path.join(here, '..', 'seeds.json'), path.join(here, 'dist', 'seeds.json'));  // entry nodes browsers try first (static: costs the Worker nothing)
// The programs the My node command downloads and runs on people's computers.
for (const f of ['opentube.mjs', 'server.mjs', 'headless.mjs', 'rtmp.mjs', 'host.mjs', 'tls.mjs', 'opentube-dotnet.sh', 'opentube-dotnet.ps1']) if (fs.existsSync(path.join(here, '..', f))) fs.copyFileSync(path.join(here, '..', f), path.join(here, 'dist', f));
console.log('opentube: built cloudflare/core.gen.mjs, cloudflare/dist/index.html and cloudflare/dist/sw.js');
