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

**A public address, by itself.** At startup the node works out how it's on the internet:
- **Directly** (a server with a public IP, or ports 80/443 forwarded to it): it serves HTTPS itself as
  `https://<ip, dashed>.sslip.io` (or `--public=your.domain`) with a Let's Encrypt certificate it gets and renews by itself.
  Ports 80/443 need admin rights on Linux (root, or `setcap cap_net_bind_service`).
- **Behind a router, or ports 80/443 already used by another program** (the log names it, e.g. Caddy): a Cloudflare
  quick tunnel (`https://….trycloudflare.com`); cloudflared is downloaded into the data folder if it isn't installed.

`--tunnel` keeps a tunnel as well, `--no-tunnel` never starts one, `--public=off` never serves HTTPS itself.

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

**Control from the site** (Devices): the site's signed commands (`nodectl`) are answered by the .NET node with signed
replies. It serves:
- OBS settings: the stream key (and a new one), the linked channel (proved by the channel's key or by your identity
  owning it), the title;
- sharing the computer with others;
- the log;
- status.

Commands for features it doesn't have yet are refused with a clear message. `test-control.cjs` drives it from a real
page on the site, then streams with the key the site received.

**WebRTC** (SIPSorcery):
- **the peer mesh:** the node joins the network's WebRTC room as browsers do (Trystero's wire, in C#), so browsers
  connect to it directly through any tracker (`test-mesh.cjs`);
- **the segment tree:** a stream from OBS on this computer, sent to another computer's hub, goes to viewers over data
  channels from here (`test-tree.cjs`);
- **relaying:** when you (or someone you share the computer with) go live from a browser, the computer watches the
  stream as a member, serves viewers itself, and records streams on your channels (`test-relay.cjs`).

**Video hosting** (with `--grant`; `--videos=DIR`, default `~/OpenTube Videos`; `--cap-gb=5`; `--no-host`): the
node keeps the videos of the channels you and the people you share it with joined or own, in the browser's folder
layout, and shares them over BitTorrent with browsers (WebRTC, WebTorrent's trackers) and computers (TCP, plain or
encrypted). Manage files and "store" work from the site; OBS and relayed recordings are kept in the channel's folder
and offered for publishing; live streams are also shared in small torrent chunks (`test-host.cjs`).

**Getting paid:** receipts submitted in `serve` transactions, pay per piece (`ot_pay`: signed tabs both ways, cashed in
every minute; peers that don't pay stop getting pieces), storage proofs with other hosts (`test-proof.cjs`), earnings
passed to the owner. The C# engine makes its own transactions (proof of work, signed).

**Not in the .NET node yet** (the JavaScript node has them):
- the DHT (finding ordinary BitTorrent apps without a tracker);
- the group studio;

- flash computers;
- self-update and public/tunnel switching from the site.
