# OpenTube

A decentralized video platform with its own blockchain.

**Live demo:** https://opentubedecentralized.jasonajensen.workers.dev/ (a Cloudflare Worker + Durable Object home node, deployed from this repository).

Every browser that opens `index.html` becomes a full node: it verifies blocks, stores the chains it cares about, and hosts videos for other viewers.

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

### Run OpenTube on your computer with one command

*My node → Your computers → Run OpenTube on a computer → Create my command* gives you one line to paste into a terminal (Windows Command Prompt or PowerShell, Mac Terminal, Linux). It needs Node.js 22+. The line downloads `opentube.mjs` from your site and runs it. Each start it fetches the site's current `server.mjs`, `headless.mjs`, `rtmp.mjs` and `index.html` into `~/.opentube/app`, so it stays up to date, and runs:

- **a full node** that syncs with the site (`PEERS=wss://site/ot`, no ranking list, no mining unless you tick it);
- **video hosting** for your joined channels and your own channels: the node downloads their videos into a folder (`--videos=…`, default `~/OpenTube Videos`, same `Channel/channel.json` layout) and shares them with viewers' browsers over WebRTC, announcing on your site's tracker. It needs no browser: `host.mjs` uses WebTorrent, installed into `~/.opentube` on first run (a few MB). After a restart it shares straight from the files. `--cap-gb=5` limits each channel you joined (your own channels are never trimmed). `--browser-host` uses the older headless-Chromium host instead;
- **an OBS receiver** on `rtmp://localhost/live`.

The command carries a **grant signed by your personal key**. The node makes its own key and announces itself every 30 s, signed and carrying your grant. Your *My node* page lists your computers with what they run, online / last seen, their status (height, peers, hosted videos, folder, OBS) and addresses, plus a *Test connection* button. Whoever runs your command runs a node that is yours, so don't share it. Options: `--no-host`, `--no-obs`, `--no-node`, `--mine`, `--name=…`, `--port=…`.

### Always-on hosting: the page itself, headless, acting as you

Browsers host videos only while their tabs are open. To keep your joined channels online around the clock, run **the same page** on a server in headless Chromium and give it your user ID (My node → *Your user ID*):

```sh
npm install && npx playwright install --with-deps chromium   # once
node headless.mjs https://your-site.example/ <your user ID> <video folder>
```

It mirrors you: your browser publishes a signed list of the channels you've joined (latest version only, stored by home nodes), and the headless page follows it. When you join a channel it joins and starts hosting; when you leave, it leaves and deletes those videos. It never needs your private key. Videos go into the `<video folder>` you give it (or `VIDEO_DIR`, default `./videos`), as real files in the same channel-folder layout a browser uses (see *The video folder* below). Chains and settings live in a persistent browser profile (`PROFILE_DIR`, default `./browser-profile`), and `CAP_GB` sets the storage limit per channel. It logs a status line every 30 seconds and restarts the page if it crashes. Any VPS, home PC or Raspberry Pi works. Cloudflare can't keep a browser running, so on a Cloudflare deployment the Worker handles chains, tracker and rankings, and headless hosts handle video.

Note: the joined-channel list is public, so anyone with your user ID can see which channels you host.

## How peers connect (no central node)

OpenTube works like Bitcoin and BitTorrent: every browser and every command-line node is an equal peer, and none is required.

- **The mesh:** browsers and command-line nodes join the network's WebRTC room and connect directly to each other. Blocks, transactions and introductions spread over those connections; a peer that receives something new passes it on. Losing any peer, including the site the page came from, changes nothing for the others.
- **Trackers only introduce peers:** the site's own tracker, every node's tracker, the network's defaults, and the current public WebSocket trackers from [ngosang/trackerslist](https://github.com/ngosang/trackerslist), fetched on every start (and remembered in case GitHub can't be reached). Any one of them is enough. Command-line nodes also use the list's UDP/HTTP trackers and the DHT for videos, so ordinary BitTorrent apps can share them too.
- **Command-line nodes** are the always-on backbone, like bitcoind: they keep everything, join the same mesh (`@trystero-p2p/torrent` with WebTorrent's WebRTC, installed by `opentube.mjs`), and host videos.
- **Public nodes with automatic HTTPS:** browsers only accept secure connections, so a node started with `--public` gets a public HTTPS name by itself: `<its IP, dashed>.sslip.io` (e.g. `203-0-113-7.sslip.io`, which resolves to that IP for everyone, so no domain is needed) with a Let's Encrypt certificate it obtains and renews itself (`tls.mjs`). `--public=your.domain` uses your own domain. It needs ports 80 and 443 (on Linux, run as root). The node announces its address in its check-ins; browsers then connect to it and use its tracker, and remember public nodes' trackers for the next start.
- **WebSocket links to nodes are only a backup:** a browser connects to the site's node (or `?nodes=wss://a/ot,wss://b/ot`, or nodes announcing a public address) only while it has fewer than 3 direct peers.
- **Trending** is computed by each browser for itself; picking a ranking list is optional.

URL options: `?net=p2p` (mesh only, no WebSocket links), `?net=local` (tabs of one browser, for development), `?trackers=wss://…` (use only these trackers, for testing).

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

## Coins (the ledger)

OpenTube runs on **one shared coin**. 100 units = 1 coin. Transactions ride in main-chain blocks, so proof of work orders them and nothing can be spent twice; every node replays them and gets the same balances.

- **Where coins come from:** only from running the network. Each main-chain block pays **1 coin** to whoever mined it (home nodes, your computers, or your browser when it confirms its own transaction). Every new key may claim **10 starter coins** once, which needs a few seconds of extra proof of work.
- **What costs coins:**
  - **Watching** someone else's video pays its creator. The page won't play it until the view is paid. It's free for your own videos, videos you host, and videos from before the coin.
  - **Likes** pay the author of the video or comment. **Dislikes** cost the same but are burned. Only paid votes count in rankings.
  - **Posting** a video pays the members registered as **hosts** of that channel, split evenly (joining a channel registers you). If nobody hosts it yet, the block's miner gets it. A video doesn't show up, rank or get hosted until its fee is paid; its owner sees a *Publish* button.
  - **Tips** and transfers: *Wallet → Send coins*, and the *Tip* button on channels.
- **Ads pay you back:** watching an ad is what earns coins. A completed ad view pays the ad's price from the advertiser channel's coins, half to the viewer and half to the creator of the video it ran with. When a video costs coins and an ad is available, the ad plays first, and the view is then paid from what you just earned, so you can watch with an empty wallet. Watching the same ad again pays again; ads that overlap in time don't (one ad at a time per viewer). A channel's own ads always run before its own videos. Sponsoring another channel (*Manage → sponsors*), or a list doing so, runs its ads too. When creating an ad, the advertiser chooses whether a view pays only when another viewer watched it in sync and confirms it (safer against bots, but rare on a small network) or always. Ads that don't say pay always, up to their maximum views.
- **Prices set themselves:** after every block, the watch, like and post-per-GB prices each move up to 12.5% toward a target load (`COIN.target`). Busy means dearer and quiet means cheaper, so storage and attention are priced by the network itself. A payment may be up to 25% under the current price, because prices move while it waits for a block.
- **Before the coin:** videos and votes from before 2026-11-19 09:00 UTC stay free. Transactions in the old per-person tokens stay valid in history but no longer affect balances.
- **Known weaknesses:** keys are free, so starter coins and block rewards can be farmed with CPU (each costs proof of work, like mining). Host registration isn't checked against actually storing the videos yet (storage proofs are not built). A modified page could watch without paying, because video travels peer to peer.

## Network & diagnostics

The *Network* page (and the health dot in the header) checks each part of the system and explains it in plain language. Each check gets a verdict (✅ / ⚠️ / ❌), the likely cause, and what to try:

- **Connection:** home node or peer-to-peer, when the network last spoke, and the node's own `/status` compared with yours.
- **Main chain:** in sync or not, mining, pending registrations and transactions, and rejected blocks with reasons.
- **Every chain you follow:** synced, catching up, or nobody answering, with missing blocks, last block and last announcement.
- **Video P2P:** WebRTC support, whether your browser can discover its public address (needed across networks), a live test of each tracker, and every transfer with a verdict like "no online peer has this video".
- **Storage:** quota, and whether the browser may evict your data, with a button to keep it.
- **Hosting:** joined channels and your published list. Also a recent-events log.

## Votes and rankings

- **Votes:** 👍/👎 on any video is a signed `vote` block on that channel's chain (value 1, -1, or 0 to withdraw). The latest vote per key per video counts. Anyone who hasn't been revoked by the channel can vote, even when comments are off. Like comments, votes need a small proof of work.
- **Ranking lists:** a list created with `ranking: true` publishes rankings instead of hand-picked channels. A ranking is a **signed snapshot** (top videos with scores and vote counts) gossiped between nodes. Only the newest snapshot per list is kept, so rankings never bloat chain history, and any node can verify the list's key signed it.
- **Who does the work, and why:** every home node runs its own ranking list automatically ("Trending on <host>"). It already stores every chain, so ranking costs almost nothing, and it's that site's front page. It reranks about 20 seconds after new videos or votes, and every 5 minutes regardless (`RANK_EVERY_SECONDS`). The built-in algorithm `waves-v1` lets every like and dislike fade with time, at three speeds: *Rising* (votes from the last minutes count most), *Today* and *This week*, plus *All time* (net votes). New uploads get a starting boost that fades. *Hot*, the default, blends the three speeds, so videos rise while votes keep coming and sink when they stop: the front page moves in waves. Explore has a chip for each view, and your feed can be ordered by Hot or Newest. Anyone can run a different ranking list with their own algorithm, and viewers pick which one they trust in Explore ("Ranked by").
- **Explore** has Trending, New, Channels and Lists tabs, driven by the chosen ranking. Without one, each browser ranks the channels it has loaded.
- **Known weakness:** keys are free, so one person can cast many votes. Proof of work only slows that down. Making votes resist fakes is the ranker's job, for example by weighting older or invested keys, and is the main open design question.

## Usernames and links

Channels can claim an **@name** and lists a **~name** (*Manage → Username*). Then `https://your-site/@cooking` opens the channel and `https://your-site/~science` the list. (`#` can't be used: browsers never send what follows it to the server.)

- A claim is a small signed transaction in a main-chain block, made with the channel's or list's own key. Nodes reject claims by anyone else, and `@` names only go to channels, `~` names only to lists.
- One name per channel or list. Claiming a different one, or releasing it, frees the old name for anyone.
- If two people claim the same name, the claim that lands in the main chain first wins.
- Names are 3–30 characters: `a–z`, `0–9`, and `. _ -` in the middle. Capital letters are lowercased.
- `server.mjs` and the Cloudflare worker serve the page for `/@…` and `/~…` paths; the page looks the name up.

## Live streaming

On your channel's Videos tab, **Go live**: pick a camera, a microphone, or *Share my screen*. **OBS Studio:** click *Start Virtual Camera* in OBS and pick *OBS Virtual Camera*, which brings your scenes and overlays. Going live needs a video folder (the stream is recorded there) and a home node (the site's own server).

- **Start:** the page appends a signed `live` block to the channel, so subscribers see a *LIVE* banner on the channel and at the top of their feed. Chat is comments on that block.
- **WebRTC first:** the home node's live hub (`/live`) arranges viewers in a tree. **Members who joined the channel get the slots right under the broadcaster**; other viewers connect under members, and every viewer can pass the stream on to a few more. The hub only introduces peers; video goes browser to browser. About a second of delay.
- **Segments as the fallback:** the broadcaster also cuts the stream into ~1 s chunks, each signed with the channel key and carrying its byte position in the recording. Viewers whose WebRTC connection fails (strict networks, no TURN relay) play these through the home node instead, a few seconds behind. `?live=segments` forces this.
- **Members save the stream:** members' browsers (and headless hosts) join their channels' streams in the background, relay them, and write every chunk at its position into a file in the channel's folder. Their copy is the broadcaster's recording byte for byte.
- **When it ends, the streamer chooses:** *Publish recording* hashes the recording and posts it as a normal video. Members already have it, so they only fetch what they missed. *Discard* deletes the recording, and members delete their copies.
- Every node checks the rules: only the channel's key can start or end a stream or sign its chunks, and viewers verify every chunk's signature and hash.

**OBS "Start Streaming" (recommended for OBS):** run OpenTube on the computer OBS runs on (*My node → Your computers → Run OpenTube on a computer*). It listens for OBS on `rtmp://localhost/live` (`rtmp.mjs`: RTMP in plain JavaScript, no ffmpeg needed). On the site, *Go live → Stream from OBS*, pick that computer, then in OBS set *Settings → Stream → Service “Custom…”*, server `rtmp://localhost/live`, any stream key, an H.264 video encoder (x264 or your GPU's H.264, not HEVC/AV1), AAC audio, and a keyframe interval of 2 s. Click *Start Streaming*. The computer repackages the stream as fragmented MP4, signs each fragment with its node key (your signed `live` block names it as the stream's `relay`), and relays it to the site's live hub. Viewers watch through segments, a few seconds behind. Your browser and members save the recording as an `.mp4`, and *Stop Streaming* leads to the usual publish-or-discard step. If you click Start Streaming before Go live, OBS shows an error saying so. This works with the Cloudflare site too.

**OBS with a stream key (server.mjs home nodes only):** under *Go live*, open *Or send the stream from OBS with a stream key* and click *Get a stream key*. The page shows the exact OBS settings: Settings → Output → Advanced → Recording → *Custom Output (FFmpeg)* → *Output to URL*, the URL shown, container `webm`, video `libvpx` (VP8), audio `libvorbis` (browsers can't stream ffmpeg's Opus-in-WebM). Then *Start Recording* in OBS; stopping it ends the stream. The node signs the incoming bytes with its own key (your browser names the node as the stream's `relay` in the signed `live` block) and relays them as segments, a few seconds behind. Your browser records the stream, so keep the page open, and then asks publish or discard as usual. The Cloudflare node can't receive OBS streams; use the Virtual Camera there.

Known limits: the recording is WebM straight from the browser's recorder, so some players can't seek in it. The hub keeps the last minute of chunks, so a member who joins later than that fetches the gap after the stream is published.

## Subscribe vs. Join

- **Subscribe:** your node keeps that channel's chain in sync and shows its videos in your feed. Videos download only when you watch them.
- **Join:** subscribe **and** host. Every new video the channel posts downloads automatically and is shared with other viewers. Each joined channel has a storage limit (default 5 GB, oldest evicted first).

Video files travel via WebTorrent (v3). The chains carry only the infohash, title and thumbnail.

**Playback while downloading:** a small service worker (`sw.js`, served next to the page) answers the video player's range requests from the torrent, so videos start within seconds and can be seeked, MP4 included. It needs https or localhost; elsewhere a video plays once it's fully downloaded. Videos you host play straight from the file in your folder. Watch-only copies are kept in the browser's private storage while you watch and deleted when you leave the page.

## The video folder (hosting uses real files on your disk)

To upload or join, you choose a folder once (*My node → Video folder*). OpenTube manages it like this:

```
<your folder>/
  Cooking With Sam/              one readable folder per channel
    channel.json                 which channel this is: chain ID, owner public key, network, and its videos
    pasta night.mp4              the real video files, playable in any player
    .opentube/<infohash>.torrent piece hashes, so reopening the page never re-hashes anything
```

- **Upload** copies your file into the channel's folder, hashes it once and shares it from there.
- **Join** creates the channel's folder and downloads each video straight into it. Nothing is held in memory: pieces are written to the file as they arrive, and the file is saved in steps, so a closed tab loses little.
- **Reopening the page** shares everything in the folder again within a second. Videos you host also play straight from the file on disk.
- **Browser support:** the folder uses the File System Access API, which Chrome and Edge on a computer provide. Firefox, Safari and phones can still **watch**, but not upload or join. After a restart Chrome may ask you to allow the folder again; a bar at the top of the page has the button. Choose "Allow on every visit" to stop the question.
- **Headless hosts** (`headless.mjs`) use the folder given on the command line: Node does the file access and hands it to the page, so the files are real files on that machine. `?folder=browser` uses the browser's private storage with the same layout, for testing.
- Videos stored inside the browser by older versions keep being shared, and they move into the folder when you choose one.

## One source of rules

The blockchain rules live in the `<script id="opentube-core">` block of `index.html`. `server.mjs` extracts and runs that exact block, so browser and server nodes always validate identically. If you change anything in `MAIN_GENESIS`, you start a different network.

## Limitations
- A browser hosts only while its tab is open. Home nodes keep chains available, but they don't store video files.
- Browsers can't connect to each other on some networks (mobile data, strict NATs) because there's no TURN relay yet.
- Proof of work on a small network is weak: someone with much more CPU than everyone else could rewrite main-chain history. Difficulty is fixed in the genesis block and doesn't adjust.
- Browsers limit storage per site, typically to a share of free disk.
- Sync gossips every chain to every peer, which is fine for thousands of channels but needs per-chain rooms to go much further.
