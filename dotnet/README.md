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

**Native C#:**
- networking, HTTP and WebSockets;
- the website;
- crypto: SHA-256, Ed25519 and random numbers;
- proof of work, on every core;
- storage: keys and the block log, which uses the JavaScript node's format, so data carries over;
- the UIs and the service.

**The rules themselves** (what makes a block or transaction valid) still run as the shared JavaScript core inside
.NET, through the Jint interpreter. That keeps a .NET node in exact agreement with browsers while the rules are
ported. `docs/PROTOCOL.md` describes them; the tests compare both.

**Not in the .NET node yet** (the JavaScript node has them):
- video hosting (BitTorrent seeding);
- the OBS receiver;
- the group studio;
- recordings;
- YouTube forwarding;
- flash computers;
- node control from the site (`nodectl`);
- remembering the tunnel name across restarts.
