# OpenTube Decentralized

A peer-to-peer video platform in a single HTML file. It needs no backend server, database, or video hosting.

## How it works

| Layer | Technology | What it does |
|---|---|---|
| Video transport | [WebTorrent](https://webtorrent.io) (WebRTC) | Uploading a video makes your browser a seed. Viewers stream it straight from you and pass it on to other viewers while they watch. |
| Catalog & comments | [Gun.js](https://gun.eco) | A small graph database that syncs titles, magnet links, thumbnails and comments between browsers through any public relay. |
| Your uploads | IndexedDB | Saves the videos you upload so your browser seeds them again after a reload. |

Video bytes never pass through a server. Relays and trackers only help peers find each other.

## Run it

Open `index.html` in a modern browser, or host the file anywhere static (GitHub Pages, IPFS, Netlify, a USB stick…).

```sh
npx serve .        # or: python3 -m http.server
```

## Features
- Upload by drag and drop, with an automatic thumbnail
- Videos stream while they download (MP4 H.264/AAC or WebM)
- Search, "My videos", comments
- Share links (`#watch=<infohash>&t=<title>`) work even before the catalog syncs
- Watch any magnet link
- Settings to change your display name, catalog relays and trackers

## Important limitations
- A video plays only while at least one peer that has it is online. Keep your tab open, or run a long-lived seeder such as `webtorrent-hybrid` or a browser left open on a spare machine.
- Public Gun relays come and go. For something reliable, run your own (`npx gun`) and add it in Settings.
- The catalog is open and has no moderation or authentication. Anyone can publish to it.
