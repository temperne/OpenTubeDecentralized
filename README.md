# OpenTube

A decentralized video platform with its own blockchain. Every browser that opens `index.html` becomes a full node: it verifies blocks, stores the chains it cares about, and hosts videos for other viewers.

## Running it

**Viewers** just open the page. Nothing to install.

**Operators (optional but recommended)** run a home node on the machine that serves the page:

```sh
node server.mjs            # serves index.html on http://localhost:8080 and runs a node at /ot
```

You need Node 20 or newer, and there's no `npm install`. For HTTPS, put it behind a TLS proxy (e.g. `caddy reverse-proxy --to :8080`).

| Variable | Default | Meaning |
|---|---|---|
| `PORT` | 8080 | HTTP port |
| `DATA_DIR` | `./data` | Where the node stores blocks and its key |
| `MAIN_ONLY=1` | off | Keep only the main chain, not channel chains |
| `MINE=0` | mining on | Don't spend CPU confirming registrations |
| `PEERS` | none | Comma-separated `wss://host/ot` URLs of other home nodes to sync with (Node 22+) |

`GET /status` returns the node's height, tip and peer count as JSON.

## How browsers connect

1. **Home node first.** The page connects to `wss://<the host it was loaded from>/ot`. That node keeps the main chain (and by default every channel chain), relays between all connected browsers, and mines pending registrations.
2. **Fallback: direct P2P.** If no node answers (a static host, `file://`, or the node is down), browsers find each other through public WebTorrent trackers using [Trystero](https://github.com/dmotz/trystero) and sync over WebRTC.

URL options: `?node=wss://host/ot` (use a specific node), `?net=p2p` (skip the home node), `?net=local` (tabs of one browser only, for development), `?trackers=ws://localhost:8000` (override trackers, for testing).

## The chains

```
MAIN CHAIN (proof of work, every node keeps it)
  genesis: network config (difficulty, trackers, limits). Its hash is the network ID.
  block 1: register [ full signed genesis of channel A, list B, … ]
  block 2: register [ … ]
        │
        ├── CHANNEL CHAIN A  (the channel's own key controls it)
        │     genesis: name, about, avatar, channel public key, default comment setting
        │     video (+ optional comment setting), settings (change a video's comments),
        │     post, profile edit, allow/revoke commenter, hide, comment
        │
        └── LIST CHAIN B     (curation)
              genesis: name, about, owner key
              add channel X, hide (remove)
```

- **Main chain:** blocks need proof of work (18 leading zero bits by default; a browser takes a few seconds, a server well under one). The highest chain wins. Each block embeds the **complete signed genesis blocks** of the channels and lists it registers, so any node can open a channel from the main chain alone.
- **Channel chains:** only the channel key can post videos, posts and settings. Other people can write `comment` and `hide` (their own comments) blocks when that video's comment setting allows it. Each video either uses the channel default or overrides it: *anyone*, *approved users only*, or *off*. The owner can change a video's setting at any time; the change is appended as a `settings` record. Other people's blocks also need a small proof of work (10 bits) to deter spam.
- **Several writers:** a block may have up to 8 parents, so blocks written at the same time by different people merge instead of conflicting.
- **Nothing is deleted.** Hiding a video or comment, or revoking a commenter, appends a signed record. Every node applies these records when it displays the channel, so the full history stays verifiable.
- **Keys:** every channel and list gets **its own Ed25519 key pair** when you create it. Whoever holds the private key controls the channel. Each channel's *Manage* tab has **Export channel key**, and the channel page warns you until you've exported it. *My node → Import channel key* moves control to another browser, or shares it with a co-owner. Separately, each browser has a **personal key** that identifies you when you comment; back it up from *My node*.

## Subscribe vs. Join

- **Subscribe:** your node keeps that channel's chain in sync and shows its videos in your feed. Videos download only when you watch them.
- **Join:** subscribe **and** host. Every new video the channel posts downloads automatically and is shared with other viewers. Each joined channel has a storage limit (default 5 GB, oldest evicted first).

Video files travel via WebTorrent. The chains carry only the infohash, title and thumbnail.

## One source of rules

The blockchain rules live in the `<script id="opentube-core">` block of `index.html`. `server.mjs` extracts and runs that exact block, so browser and server nodes always validate identically. If you change anything in `MAIN_GENESIS`, you start a different network.

## Limitations
- A browser hosts only while its tab is open. Home nodes keep chains available, but they don't store video files.
- Proof of work on a small network is weak: someone with much more CPU than everyone else could rewrite main-chain history. Difficulty is fixed in the genesis block and doesn't adjust.
- Browsers limit storage per site, typically to a share of free disk.
- Sync gossips every chain to every peer, which is fine for thousands of channels but needs per-chain rooms to go much further.
