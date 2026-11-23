#!/usr/bin/env bash
# The C# BitTorrent client against WebTorrent 3 (Node.js), both directions, plain and encrypted, from the infohash alone.
# Once: (cd torrent-interop && npm install webtorrent@3 create-torrent parse-torrent); dotnet build ../OpenTube.App
set -u; cd "$(dirname "$0")"; D=../../OpenTube.App/bin/Debug/net10.0/opentube.dll; mkdir -p files; fail=0
[ -f files/big.bin ] || { head -c 31457000 /dev/urandom > files/big.bin; head -c 700 /dev/urandom > files/tiny.bin; head -c 123457 /dev/urandom > files/mid.bin; }
for f in tiny mid big; do
  [ "$(node hash.mjs files/$f.bin | cut -d' ' -f1)" = "$(dotnet $D torrent hash files/$f.bin)" ] && echo "ok   infohash $f" || { echo "FAIL infohash $f"; fail=1; }
  for SEC in 0 1 2; do
    : > seed.out; dotnet $D torrent seed files/$f.bin > seed.out 2>/dev/null & SP=$!; while [ ! -s seed.out ]; do sleep 0.3; done; read IH PORT < seed.out
    SECURE=$SEC timeout 60 node wire.mjs leech $IH $PORT >/dev/null 2>&1; cmp -s files/$f.bin out/$f.bin && echo "ok   WebTorrent(secure=$SEC) <- C#  $f" || { echo "FAIL WebTorrent(secure=$SEC) <- C#  $f"; fail=1; }
    kill $SP; wait $SP 2>/dev/null
    : > js.out; SECURE=$SEC node wire.mjs seed files/$f.bin > js.out 2>/dev/null & SP=$!; while [ ! -s js.out ]; do sleep 0.3; done; read IH PORT < js.out
    rm -f out/cs-$f.bin; timeout 60 dotnet $D torrent get $IH out/cs-$f.bin 127.0.0.1:$PORT >/dev/null 2>&1; cmp -s files/$f.bin out/cs-$f.bin && echo "ok   C# <- WebTorrent(secure=$SEC)  $f" || { echo "FAIL C# <- WebTorrent(secure=$SEC)  $f"; fail=1; }
    kill $SP; wait $SP 2>/dev/null
  done
done
exit $fail
