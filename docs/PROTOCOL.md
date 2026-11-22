# OpenTube protocol

What a node or browser must do to take part in the network. The reference is the shared core in `index.html`
(`<script id="opentube-core">`), which browsers, the JavaScript node (`server.mjs`) and the .NET node
(`dotnet/`, via `JsCore`) all run. Where this document and the core disagree, the core wins; fix the document.

Test data for checking another implementation: `dotnet/OpenTube.Tests/data/vectors.json`. Those tests run against the
real core and natively in C#.

## 1. Primitives

| What | Rule |
|---|---|
| Hash | SHA-256, written as 64 lowercase hex characters |
| Signatures | Ed25519 (RFC 8032) |
| Public key | the 32-byte raw key, base64url without padding: 43 characters `[A-Za-z0-9_-]` |
| Private key | PKCS#8 (RFC 8410), base64url. The bytes are `302e020100300506032b657004220420` followed by the 32-byte seed |
| Identity file (`key.json`) | `{ "pub": "<public key>", "priv": "<private key>" }` |
| Text to bytes | UTF-8 |
| Canonical JSON (`canon`) | `JSON.stringify` semantics with object keys sorted by UTF-16 code unit, no spaces, and keys whose value is `undefined` left out. Strings are escaped exactly as JavaScript does: `"` `\` and control characters, while U+2028 and U+2029 stay raw. Numbers are written as JavaScript does; blocks only use safe integers |

## 2. Blocks and chains

A block:

```json
{ "v": 1, "chain": "<chain id | null>", "parents": ["<hash>", …], "height": 3, "ts": 1794960000000,
  "author": "<public key | null>", "payload": { "type": "…", … }, "nonce": 12345, "hash": "…", "sig": "…" }
```

- **Preimage:** `canon(block without hash, sig, nonce) + ":"`
- **Hash:** `sha256(preimage + String(nonce))`, as hex
- **Signature:** Ed25519 by `author` over the UTF-8 bytes of the hash string. Genesis blocks have no author.
- **Proof of work:** the count of leading zero bits of the hash, which must reach the difficulty the chain requires. The main chain needs 18 (`MAIN_GENESIS.payload.difficulty`). Blocks written by someone other than a chain's owner need `commentDifficulty` (10).
- **Limits** (`LIMITS`): 300,000 bytes per block, up to 8 parents, and more.

**The network ID** is the hash of `MAIN_GENESIS`:
`abdcfc1c8f660f01f103f93ead8f0a6b66be3f9136ebb7190e77ea6944c5e00c`. Changing anything in it creates a different
network.

**Chains:**
- **The main chain:** registrations of names and channels, up to 16 per block.
- **Other chains:** each channel, list, organization, and so on is its own chain.
  - It's identified by the hash of its genesis block, which is registered on the main chain.
  - The latest heads win (it's a DAG of `parents`).
- **Validation:** `validGenesis`, `checkSigned`, `checkTx` and `derive` in the core define what each payload type may contain and who may write it.

## 3. Node messages (WebSocket `/ot`)

Every message is one JSON text frame with a string field `t`. Messages with an unknown `t` are ignored, as are malformed ones.

| `t` | Fields | Meaning |
|---|---|---|
| `heads` | `c` chain, `h` [hash…] | "these are my newest blocks of chain c" |
| `rq` | `c`, `w` [wanted hash…], `h` [my heads] | request blocks; an empty `w` asks for `heads` |
| `bk` | `c`, `b` [block…] (up to 5000) | blocks, in reply to `rq` or pushed when new |
| `tx` | `x` | a transaction for the mempool (e.g. coins) |
| `mp` | `e` [entry…] | main-chain mempool entries (registrations waiting to be mined) |
| `mem` / `memrq` | `m` membership / `a` key | someone's signed list of joined channels / ask for it |
| `node` | `n` nodeinfo | a node's signed status (below) |
| `nodehi` | `node` | sent by a node when a peer connects: "I am this node key" |
| `rank` | `r` | a signed ranking ("Trending on …") |
| `route` | `id`, `dst`, `ttl`, `body` / `re` | a message for one key, passed hop by hop; replies go back along the same path |
| `watch` | … | watch-group traffic; nodes pass it on to everyone |
| `lwant` / `lseg` | `stream`, `ttl` / `seg` | live segments over the mesh |
| `rcpt` | `r` | a usage receipt on its way to the node it pays |

**Gossip:** on connect, and every 60 s as a repair pass, a node sends `heads` for its chains. A peer that lacks a head asks with `rq`, and gets `bk`.

## 4. Node status (`nodeinfo`)

```json
{ "v": 1, "kind": "nodeinfo", "node": "<node key>", "at": <ms>, "grant": <nodegrant>, "roles": ["node", "entry"],
  "addrs": ["https://…", "http://192.168.1.5:8080"], "info": { … under 4000 bytes … }, "sig": "…" }
```

- **Signature:** by the node key, over `"opentube-nodeinfo:" + canon(status without sig)`.
- **`grant`:** the owner's signed `nodegrant` (`{ v: 1, kind: "nodegrant", user, at, label, sig }`). It's signed over `"opentube-nodegrant:" + canon(…)` by the owner's personal key, so a node shows up as theirs.
- **Lifetime:** a node announces itself every 30 s; statuses older than 30 minutes are forgotten.
- **`entry` role:** an `entry` node has a public https address and is a way into the network for browsers (`/entries.json`).

## 5. Other endpoints a node serves

| Path | What |
|---|---|
| `/` | the website (`index.html` and its files), so any node is a way in |
| `/tracker` | a WebTorrent tracker (WebSocket): announces with WebRTC offers, relays answers, scrape (`createTracker`) |
| `/live` | the live hub (WebSocket): broadcasters send signed segments, viewers watch (`createLiveHub`) |
| `/status` | JSON: network, height, tip, peers, chains… |
| `/entries.json` | the entry nodes seen online in the last 5 minutes: `{ network, at, entry: [{ node, url, at }] }` |
| `/seeds.json` | the static list of entry nodes |

## 6. Compatibility checks for a new implementation

1. `hash(MAIN_GENESIS)` equals the network ID above.
2. `canon` and the block hash agree with the vectors.
3. Ed25519 signatures interoperate both ways, and the same key gives the same signature (Ed25519 is deterministic).
4. Mined blocks are accepted by a JavaScript node, and the other way round. See `dotnet/README.md` for the test that runs both.
