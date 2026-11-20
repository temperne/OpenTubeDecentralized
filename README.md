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
  - **Watching** is paid **per piece**, to whoever sends you each piece (see *Paying per piece*). There's no pay button, and a partial watch costs only what you downloaded. Free: your own videos, channels you host, and videos from before the coin.
  - **Likes** pay the author of the video or comment. **Dislikes** cost the same but are burned. Only paid votes count in rankings.
  - **Posting** a video pays the members registered as **hosts** of that channel, split evenly (joining a channel registers you). If nobody hosts it yet, the block's miner gets it. A video doesn't show up, rank or get hosted until its fee is paid; its owner sees a *Publish* button.
  - **Tips** and transfers: *Wallet → Send coins*, and the *Tip* button on channels.
- **Ads pay you back:** watching an ad is what earns coins. A verified ad view takes the ad's price from the advertiser channel's coins. Half goes to the creator of the video it ran with. The other half becomes an **ad-view certificate** owned by the viewer (see *Ad views*). A skipped ad pays for the share you actually watched. Watching the same ad again pays again; ads that overlap in time don't (one ad at a time per viewer). A channel's own ads always run before its own videos. Sponsoring another channel (*Manage → sponsors*, or *Market → Ad board → Run on my channel*), or a list doing so, runs its ads too. An advertiser can require that a view only pays when another viewer watched in sync and confirms it (safer against bots), or let every view pay. Ads name a **category** (what product they're for) and tags.
- **Nodes are paid for what they provide** (`SERVE` in the core). The network counts resources in 10-minute periods. Whoever *receives* something signs a receipt for the node that gave it:
  - **Video bytes:** bytes downloaded from the node's WebTorrent peer. Its peer ID is derived from the node's key, so browsers know whose bytes they got.
  - **Time connected:** seconds the browser was linked to the node.
  - **Tracker service:** seconds the node's public `wss://…/tracker` answered.
  - **Storage:** MB of video a node proved it holds, signed by the node that challenged it (see Storage proofs).

  Browsers send the receipts to the node right after the period. The node submits them in a `serve` transaction and confirms its own transactions (personal nodes run with `MINE=own`), so it depends on nobody else. An hour after the period, each resource has a pool that is split among nodes by their receipted amounts:
  - **bytes:** 2 coins
  - **time connected:** 1 coin
  - **tracker service:** 0.5 coin
  - **storage:** 1 coin

  Each pool also gets a share of **10% of every watch, like and post payment** made in that period (50/15/15/20). A busier network means bigger pools, and a share nobody claimed is burned. With your grant the coins go to *your* account (Wallet → *Earned by your nodes*). Mining blocks still pays separately. Receipts count once each, only from keys that did the starter proof of work, and only up to a per-key cap per period (20 GB of bytes, 10 minutes of connection time, 10 minutes of tracker service, 1 TB of storage).
- **Prices set themselves:** after every block, the watch, like and post-per-GB prices each move up to 12.5% toward a target load (`COIN.target`). Busy means dearer and quiet means cheaper, so storage and attention are priced by the network itself. A payment may be up to 25% under the current price, because prices move while it waits for a block.
- **Before the coin:** videos and votes from before 2026-11-19 09:00 UTC stay free. Transactions in the old per-person tokens stay valid in history but no longer affect balances.
- **Known weaknesses:** keys are free, so starter coins and block rewards can be farmed with CPU (each costs proof of work, like mining). Host registration isn't checked against actually storing the videos yet (storage proofs are not built). Resource receipts are only as honest as the keys signing them: someone with many keys (each one needs the starter work) could receipt their own node, up to the per-key caps. A modified page could watch without paying, because video travels peer to peer.

## Identities own channels (and keys can be replaced)

Your **identity** is your personal key. Every channel or list you create has its own key, and that key is **bound** to your identity by a `bind` transaction. The channel key signs "this identity owns me" and your identity sends it. From then on:

- every coin paid to the channel (views, likes, tips, the creator's share of pieces) lands in your identity's balance;
- the channel's ads are paid from that balance;
- the wallet shows one account.

Binding happens automatically for the keys your browser holds. A channel key can be re-bound to another identity only with its own signature, which is how a channel changes hands.

- **Replace a channel key** (*Manage → Channel key → Replace with a new key*): the current key appends a `rekey` block that the new key also signs. From that point the new key controls the chain. Earlier blocks by the old key still count as the owner's.
- **Retire your personal key** (*My node → Retire my personal key*): a `retire` transaction, signed by the new key too, moves your balance to a new key and makes every channel bound to you resolve to it. Your personal chains (friends page, old channels) are handed over with `rekey` blocks, and the old key can't send transactions any more. Your encryption key stays, so friends can still read you. Back up your keys again afterwards.

Channels also have a richer **About** page: an avatar (changeable under *Manage*), a location, and links added one at a time (Twitter, LinkedIn, a shop…). *Block* (on a comment, or under *Manage → Commenters*) hides everything that person writes on your channel from then on, and *Unblock* shows it again.

## Paying per piece

A video torrent is split into pieces, each checked against the hash in the torrent. OpenTube adds one message type, `ot_pay`, to the BitTorrent connection itself (BitTorrent's extension protocol, the same mechanism WebTorrent uses for metadata), so payment follows the exact exchange between the peer that gave a piece and the peer that got it:

1. When two peers connect they say who they are (`{hello: key}`).
2. As pieces arrive, the downloader signs a running **tab** for that peer and video: "you've sent me n pieces; I owe you amt units in total". amt grows by the network's **piece price** per piece; it started at 0.02 coins and adjusts every block like the other prices. It goes to the peer over the same connection.
3. The peer keeps the latest tab and cashes it in once a minute with a `tabs` transaction. The ledger moves the difference since last time from the payer, as much as they have, and splits it:
   - 10% to the network's resource pools;
   - of the rest, **half to the video's creator** (the channel's key) and half to the peer.

   The first tab from a viewer counts as one view.
4. A peer stops sending to anyone who is more than 4 pieces behind on tabs, or whose balance can't cover them.

**Free:** your own videos, channels you host (joining registers you), peers who host the channel (or your own node), videos from before the coin, and ads. A node earns tabs under its own key and passes what it earns to its owner every 10 minutes, keeping 20 coins for its own downloads.

## Ad views (certificates)

The viewer's half of a verified ad view is held in a **certificate**: a ledger record of who watched which advertiser's ad, holding the coins taken from the advertiser at that moment. *Wallet → Ad views* groups yours by advertiser. A certificate can be:

- **cashed in:** you get the coins it holds;
- **given:** for example, to the creator whose fans you are;
- **sold** on the market, alone or in bundles.

A single 2-cent view is too small to sell, but a bundle of thousands of views of one advertiser's ads is something a creator can sell back to that advertiser for whatever they agree, in coins on the market or off the network (cash, PayPal, gift cards). The network only guarantees that the views are real and each one is cashed in once. An advertiser who buys back its own certificates gets its coins back by cashing them in.

## Market

*Market* holds listings on the main chain:

- **For sale:** a fixed price. *Buy* pays the seller (10% to the resource pools).
- **Auctions:** a starting bid and an end time, up to 30 days. Each bid must beat the high bid by 5%. The high bid is held by the ledger, an outbid bidder is refunded at once, and the auction settles automatically at its end.
- **Ad views:** listings that carry ad-view certificates. They move to the buyer by themselves.
- **Ad board:** every ad the browser knows, with its category, price and length, and a *Run on my channel* button.

Anything that isn't coins or certificates (goods, gift cards, services) is delivered by the seller off the network, using the contact in the listing.

## Groups

A **group** is a chain kind where members post. Create one under *My node* and choose whether anyone can post or only members you approve (*Manage → approve*). Posts, which can include a picture, plus replies and likes are blocks on the group's chain, so anyone who subscribes keeps a copy. Likes pay the author. Find groups under *Explore → Groups*.

## Friends (private, encrypted)

*Friends* is your own page that only your friends can read:

- **Starting it:** *Start my friends page* registers a chain under your personal key. Its genesis carries your public **encryption key** (ECDH P-256), which lives with your identity and is in your key backup.
- **Adding friends:** add a friend by their user ID. They see your posts once they add you too. The list stays in your browser.
- **Posting:** each post (text and an optional picture) is **sealed**. The content is encrypted with a fresh key, and that key is wrapped for every friend using the ECDH secret between your key and theirs. Only the people it was written for can open it.
- **Replies:** a reply goes on the same chain, sealed to the same people.
- **Backups:** anyone can keep a copy of the chain, so friends and every node back up your page, but nobody else can read it. A non-friend sees only "🔒 not shared with you".
- **What's public:** nothing in a post is readable; still public are that a post exists, when it was posted, and how many people it was sealed to.

## Private messages

*Messages* are end-to-end encrypted.

- **Messaging keys:** your identity publishes **messaging keys** (ECDH P-256) with `msgkey` transactions. You can add new ones and retire old ones (*Messages → Messaging keys*), and they're in your key backup.
- **Sending:** the sender makes a one-time key pair, does ECDH with the recipient's newest messaging key, and encrypts the message with that shared secret. The message's **tag** is the shared secret hashed with a random salt. The sender's identity and signature sit inside the encrypted part, and the block is signed by a throwaway key, so the network sees only `{ tag, salt, one-time key, ciphertext }`: neither who sent it nor who it's for.
- **Receiving:** messages travel on one shared mail chain per month. Your browser computes the tag for each message with each of your messaging keys and opens only the ones that match. Someone who knows your public key can't do that, because they don't have the secret.
- **Sent messages:** your copies stay in your browser.

## Public posts (a feed for everyone)

*Posts* is a public feed, like a microblog:

- **One chain per month:** the genesis is built from the month alone, so every node derives the same chain ID without registering it, and nobody owns it.
- **Posting:** anyone posts (with anti-spam work). A **new post** goes on the current month's chain. A **reply** goes on the chain of the post it answers, however old, so a thread stays together.
- **Syncing:** nodes keep every month. A browser syncs only the months it opens (*older ›*).
- **Likes** pay the author, as elsewhere.

## Organizations: moderation, courts and the tree of chains

An **organization** is a chain (create one under *Orgs*). Its owner can do three things:

- give people **roles**: moderator, clerk or judge;
- name **classifications**, such as `adult`;
- add **courts below it**.

Courts below an org are **child chains**: they're registered by a `child` block on their parent's chain, not on the main chain, so jurisdictions nest like the places they serve (United States › California › Los Angeles County). A browser syncs a child chain only when you open it; nodes keep them all.

**Moderation**

- **Reporting:** anyone can report a video to an org (*⚑ Report* on the video).
- **Flagging:** moderators review reports and **flag** what fits a classification.
- **New classifications:** anyone can **petition** for one, others support it, and the owner adopts it.
- **Following:** when you follow an org, you choose for each classification whether its flags **label** a video, **list it but disable opening it**, or **hide** it. That choice is kept in your browser.

**Courts and cases**

- **Filing:** you file with a court's clerk (complaint, motion, response, evidence, appeal). The filing is a public, signed block on the court's chain.
- **Cases:** a clerk opens a **case**, a child chain of the court with its own key pair, which the clerk holds and can hand over by exporting it. The clerk dockets filings, sets the status (open, hearing, stayed, decided, appealed, closed) and enters **orders**. Each order carries its judge's signature, which every node checks.
- **Roles:** an identity's public page lists its roles.

**Escrow, rulings and appeals** (on the ledger)

- **Escrow:** a market listing can name a court. The buyer's coins are then held: released when the buyer confirms delivery, or automatically after 14 days unless disputed.
- **Disputes and rulings:** a dispute freezes the coins, and the court's key or the judges it named (`judge` transaction) rule how to split them.
- **Appeals:** either side may appeal within 14 days. The dispute then goes to the court above (`court` transaction, signed by both courts), whose ruling replaces the earlier one. A ruling pays out when its appeal window passes, or at once at the top court.
- **The clause:** the site asks every visitor to agree to resolve disputes this way (*Terms*).

## Tags and "not interested"

Videos and ads carry up to 8 tags, and ads a product category. Click a tag to see its videos (`#/tag/name`). Click ✕ next to a tag on a video, or set categories under *My node → Not interested*, and videos and ads with those tags or categories are hidden for you. That list stays in your browser; it's never published.

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

## Managing your nodes from any console

Every computer you run OpenTube on appears under *My node → Your computers*. Computers that host have a **Manage files** button. It lists each channel and video on that node: size, download progress, peers, and how many **other copies** exist (proven by storage proofs, or seeding in the swarm). You can:

- **Pin** a video. It is never trimmed.
- **Drop** a video. It is deleted and not downloaded again; *Allow again* undoes this. You're warned when no other copy is known.
- **Host it here:** keep another channel on this node by ID or @name, even if you haven't joined it.
- **Stop hosting here:** stop a channel on this node while staying a member, then delete its files if you want.
- **Change the size limit** per channel.

These overrides live in `<video folder>/.opentube-node.json`. When a channel is over its limit, the node removes the videos the most other hosts have proven they hold first. It never removes a video with fewer than 2 other proven copies; it keeps it and flags the channel instead.

Commands are signed by your personal key, and the node obeys only the user in its grant. It refuses stale or repeated commands, and its answers are signed by the node. The page first connects **directly** to the node's addresses. That works for a public node with HTTPS, or a node on the same computer (localhost); a secure page can't open plain `ws://` to other IP addresses, and home computers sit behind routers. Otherwise the same signed command is passed node to node over the mesh, addressed to the node's key, and the answer comes back the same way.

## Storage proofs (proving a node really has the files)

A torrent's metadata lists a hash for every piece of the video. Every couple of minutes, each hosting node does the following:

1. It picks a video it holds completely, a random piece of it, and a fresh random nonce.
2. It sends the challenge to other nodes that should have the video: nodes it sees in that video's swarm (recognized by peer IDs derived from their keys) and other hosting nodes.
3. Each challenged node must answer within 20 s with `sha256("opentube-proof:" + nonce + ":" + its key + ":" + piece bytes)`, signed.

Only a node holding that piece can compute the answer. The challenger checks it against its own copy, so the hashes have to meet up. Because the answering node's key is inside the hash, a node can't pass off another node's answer as its own. A wrong answer, or no answer in time, fails.

Passed proofs count as copies for trimming. They also earn **storage** receipts: the challenger signs "node X proved it holds N MB" for the period, paid from the resource pools like bandwidth. Nodes claim starter coins for their own key so the receipts they sign count. Not done yet: browsers that host don't answer or send challenges, and a challenge only proves the node had the data at that moment.

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
