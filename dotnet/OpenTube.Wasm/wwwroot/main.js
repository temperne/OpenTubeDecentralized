// Loads the C# rules (WebAssembly) and exposes them as globalThis.OpenTubeWasm.
import { dotnet } from './_framework/dotnet.js';
const { getAssemblyExports, getConfig } = await dotnet.create();
const x = (await getAssemblyExports(getConfig().mainAssemblyName)).OpenTubeWasm;
globalThis.OpenTubeWasm = {
  checkSigned: (b, now = Date.now()) => x.CheckSigned(JSON.stringify(b), now),
  checkSignedMany: (bs, now = Date.now()) => JSON.parse(x.CheckSignedMany(JSON.stringify(bs), now)),
  validGenesis: (g) => x.ValidGenesis(JSON.stringify(g)),
  canon: (v) => x.Canon(JSON.stringify(v)),
  hashBlock: (b) => x.HashBlock(JSON.stringify(b)),
};
globalThis.dispatchEvent?.(new Event('opentube-wasm-ready'));
