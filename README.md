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

### On Cloudflare (Workers + Durable Objects)

A plain static deployment has no node, so browsers have to find each other through public trackers, which often fails. Deploy with the included config instead:

```sh
npx wrangler deploy
```

`wrangler.jsonc` builds the page (`cloudflare/build.mjs`) and deploys `cloudflare/worker.mjs`. The Worker serves `index.html` and routes `/ot` and `/status` to a single Durable Object, which runs the full node. It stores every chain in its built-in SQLite storage and uses WebSocket hibernation, so idle connections cost nothing. Durable Objects with SQLite storage are available on the Workers free plan. If you deploy through Cloudflare's Git integration, pushing this repository is enough: the build step runs automatically.

## How browsers connect

1. **Home node first.** The page connects to `wss://<the host it was loaded from>/ot`. That node keeps the main chain (and by default every channel chain) and relays between all connected browsers. `server.mjs` also mines pending registrations; the Cloudflare node only mines its own ranking list. The same host runs a **video tracker** at `/tracker`, which introduces browsers that have the same video so they can connect over WebRTC. Public WebTorrent trackers are only a fallback.
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
- **How the network enforces comment rules:** there's no central server deciding. Every node, whether a browser, `server.mjs` or the Cloudflare node, runs the same rules on every block it receives. A comment is accepted only if, according to the owner's records in that comment's own history, its author may comment on that video and hasn't been revoked. A comment that fails is rejected: never stored, never relayed. A modified client can still create such a block, but no honest node will keep or forward it. Commenters could try to dodge a newer "off" or revoke by attaching to older blocks, so nodes also re-apply the owner's latest settings and revokes when they display a channel.
- **Several writers:** a block may have up to 8 parents, so blocks written at the same time by different people merge instead of conflicting.
- **Nothing is deleted.** Hiding a video or comment, or revoking a commenter, appends a signed record. Every node applies these records when it displays the channel, so the full history stays verifiable.
- **Keys:** every channel and list gets **its own Ed25519 key pair** when you create it, and whoever holds the private key controls it. Each browser also has a **personal key** that identifies you when you comment. Channels made before per-channel keys are controlled by the personal key. Everything is managed on *My node*:
  - **Back up all keys:** one file with your personal key and every channel and list key.
  - **Export key:** on each channel row, for just that channel. For older channels, this exports the personal key file, labelled with what it controls.
  - **Import keys:** accepts any of these files on another browser.

  Channel pages warn you until the key controlling that channel has been backed up.

## Votes and rankings

- **Votes:** 👍/👎 on any video is a signed `vote` block on that channel's chain (value 1, -1, or 0 to withdraw). The latest vote per key per video counts. Anyone who hasn't been revoked by the channel can vote, even when comments are off. Like comments, votes need a small proof of work.
- **Ranking lists:** a list created with `ranking: true` publishes rankings instead of hand-picked channels. A ranking is a **signed snapshot** (top videos with scores and vote counts) gossiped between nodes. Only the newest snapshot per list is kept, so rankings never bloat chain history, and any node can verify the list's key signed it.
- **Who does the work, and why:** every home node runs its own ranking list automatically ("Trending on <host>"). It already stores every chain, so ranking costs almost nothing, and it's that site's front page. It reranks about 20 seconds after new videos or votes, and every 5 minutes regardless (`RANK_EVERY_SECONDS`). The built-in algorithm `hot-v1` is net votes with time decay. Anyone can run a different ranking list with their own algorithm, and viewers pick which one they trust in Explore ("Ranked by").
- **Explore** has Trending, New, Channels and Lists tabs, driven by the chosen ranking. Without one, each browser ranks the channels it has loaded.
- **Known weakness:** keys are free, so one person can cast many votes. Proof of work only slows that down. Making votes resist fakes is the ranker's job, for example by weighting older or invested keys, and is the main open design question.

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
