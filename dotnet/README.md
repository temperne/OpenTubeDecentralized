# OpenTube for .NET (prototype)

One C# program that runs an OpenTube node. It shows a window when there's a screen, a terminal UI when there isn't,
and runs with no UI as a system service.

| Project | What |
|---|---|
| `OpenTube.Core` | Ed25519, SHA-256, canonical JSON, block hashing, multi-core proof of work, and `JsCore`, which runs the network's rules (the same script browsers run) inside .NET |
| `OpenTube.Node` | the node: peers (`/ot`), the website, the tracker (`/tracker`), the live hub (`/live`), `/status`, `/entries.json`, a Cloudflare tunnel, and announcing to your Devices page |
| `OpenTube.App` | the program `opentube`: window (Avalonia), terminal UI (Terminal.Gui), service mode and service installer |
| `OpenTube.Tests` | checks C# against the JavaScript core: network ID, canonical JSON, signatures both ways, mining |

## Run it

Needs the .NET 10 SDK.

```bash
cd dotnet
dotnet run --project OpenTube.App                      # a window (or the terminal UI over SSH)
dotnet run --project OpenTube.App -- --tui             # the terminal UI
dotnet run --project OpenTube.App -- --service         # no UI
dotnet run --project OpenTube.App -- --peers=1.2.3.4   # also connect to the node at that address
dotnet run --project OpenTube.App -- --help            # every option
```

It serves the website from the repository folder (the one with `index.html`). Open http://localhost:8080.

If `cloudflared` is installed, the node gets a public `https://….trycloudflare.com` address with no setup; `--no-tunnel` turns this off.

**Run at startup:**
- **From the app:** Service → Run at startup in the terminal UI, or the "Run at startup" button in the window.
- **From the command line:** `opentube --install-service`.
- **By hand:** `opentube --service-help` prints the instructions.

It installs the right thing for each system:

| System | What gets installed |
|---|---|
| Linux | a systemd unit (system-wide as root, otherwise a user service) |
| Windows | a Windows service |
| macOS | a launchd agent |

## A single file per platform

```bash
dotnet publish OpenTube.App -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
dotnet publish OpenTube.App -c Release -r win-x64   --self-contained -p:PublishSingleFile=true
dotnet publish OpenTube.App -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true
```

Copy the website (`index.html`, `sw.js`, `seeds.json`) next to it, or pass `--site-dir=`.

## What's native and what isn't yet

**All of the network runs in C#:**
- the rules: blocks, chains, every block type, the coin ledger, the main chain, inserting blocks, each chain's state;
- the engine: peer messages, catching up and gossip, the transaction pool, mining on every core, memberships, node
  statuses, rankings, routed messages and the live-segment mesh;
- the tracker and the live hub;
- networking, the website, crypto, storage, the UIs and the service.

Each part is checked against the JavaScript core on generated cases (`OpenTube.Tests`, data made by the `gen-*.mjs`
scripts from `index.html`). `compare-hub.cjs` runs the same live-hub and tracker script against a JavaScript node and
the C# node. `JsCore` (the core running in Jint) is kept only for tests.

**OBS:** the node receives OBS's Start Streaming (RTMP, port 1935; `--obs-port=`, `--no-obs`). Each person's stream key
is in `obs.json` (the JavaScript node's format); the owner's is printed at startup. The stream goes live on the channel
whose owner linked this computer, reaches viewers through this computer's live hub (or an entry node's), is recorded to
`recordings/`, and ends on the channel when OBS stops. `test-obs.cjs` checks all of it with ffmpeg playing OBS.

**Not in the .NET node yet** (the JavaScript node has them):
- video hosting (BitTorrent seeding), so recordings aren't offered for publishing yet;
- the group studio;
- YouTube forwarding;
- flash computers;
- node control from the site (`nodectl`);
- remembering the tunnel name across restarts.
