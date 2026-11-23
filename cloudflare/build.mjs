// Build step for the Cloudflare deployment (wrangler runs this automatically).
// Workers can't eval code, so instead of reading the rules out of index.html at
// runtime, this copies the exact same <script id="opentube-core">
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
for (const f of ['opentube-dotnet.sh', 'opentube-dotnet.ps1']) if (fs.existsSync(path.join(here, '..', f))) fs.copyFileSync(path.join(here, '..', f), path.join(here, 'dist', f));
// The C# website (Blazor WebAssembly, dotnet/OpenTube.Web) at /app/. The build machine may not have .NET: then it's
// installed here first (into a cache folder). If that fails, the site deploys without /app/ (the JavaScript page still works).
try {
  const { execSync } = await import('node:child_process');
  const out = path.join(here, 'web-out'), dist = path.join(here, 'dist', 'app');
  const has = (cmd) => { try { execSync(cmd + ' --list-sdks', { stdio: 'pipe' }).toString().match(/^10\./m) || (() => { throw 0; })(); return true; } catch { return false; } };
  let dotnet = 'dotnet';
  if (!has(dotnet)) {
    const dir = path.join(process.env.HOME || here, '.dotnet'); dotnet = path.join(dir, 'dotnet');
    if (!has(dotnet)) { console.log('opentube: installing .NET 10 to build the C# website…'); execSync(`curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --install-dir "${dir}"`, { stdio: 'inherit' }); }
  }
  execSync(`"${dotnet}" publish "${path.join(here, '..', 'dotnet', 'OpenTube.Web')}" -c Release -o "${out}" --nologo`, { stdio: 'inherit', env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' } });
  fs.rmSync(dist, { recursive: true, force: true });
  fs.cpSync(path.join(out, 'wwwroot', 'app'), dist, { recursive: true, filter: (f) => !/\.(br|gz)$/.test(f) });   // Cloudflare compresses by itself
  console.log('opentube: built the C# website into cloudflare/dist/app');
} catch (e) { console.log('opentube: skipped the C# website (/app/): ' + (e.message || e)); }
console.log('opentube: built cloudflare/core.gen.mjs, cloudflare/dist/index.html and cloudflare/dist/sw.js');
