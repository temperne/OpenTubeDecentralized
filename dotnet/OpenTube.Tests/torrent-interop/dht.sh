#!/usr/bin/env bash
# The DHT (BEP 5): peers found from the infohash alone, no tracker, no address. C# with C#, and both ways with
# WebTorrent's DHT (bittorrent-dht), each time through a bootstrap node of the other kind.
# Once: (cd torrent-interop && npm install webtorrent@3 bittorrent-dht); dotnet build ../OpenTube.App
set -u; cd "$(dirname "$0")"; D=../../OpenTube.App/bin/Debug/net10.0/opentube.dll; mkdir -p files out; fail=0
[ -f files/mid.bin ] || head -c 123457 /dev/urandom > files/mid.bin
[ -f files/dht.bin ] || head -c 5000000 /dev/urandom > files/dht.bin
waitfor() { for i in $(seq 100); do [ -s "$1" ] && return; sleep 0.2; done; }
pids=(); trap 'kill ${pids[@]} 2>/dev/null' EXIT
# bootstrap nodes: one C#, one JavaScript
: > b-cs.out; dotnet $D torrent dht --dht-port=17100 > b-cs.out 2>/dev/null & pids+=($!); waitfor b-cs.out
: > b-js.out; node dht.mjs node 17200 > b-js.out 2>/dev/null & pids+=($!); waitfor b-js.out

# 1. C# seeds, C# downloads (through the C# bootstrap node)
: > s1.out; dotnet $D torrent seed files/dht.bin --dht --bootstrap=127.0.0.1:17100 > s1.out 2>s1.err & SP=$!; pids+=($SP); waitfor s1.out; read IH PORT < s1.out
rm -f out/dht1.bin; timeout 90 dotnet $D torrent get $IH out/dht1.bin --dht --bootstrap=127.0.0.1:17100 > g1.out 2>g1.err
cmp -s files/dht.bin out/dht1.bin && echo "ok   C# <- C#, found on the DHT: $(cat g1.out)" || { echo "FAIL C# <- C# over the DHT"; tail -5 g1.err; fail=1; }
kill $SP; wait $SP 2>/dev/null

# 2. C# seeds and announces through the JavaScript DHT node; WebTorrent finds it there and downloads
: > s2.out; dotnet $D torrent seed files/dht.bin --dht --bootstrap=127.0.0.1:17200 > s2.out 2>s2.err & SP=$!; pids+=($SP); waitfor s2.out; read IH PORT < s2.out
sleep 3; timeout 100 node dht.mjs leech $IH 127.0.0.1:17200 > g2.out 2>g2.err
cmp -s files/dht.bin out-dht/dht.bin && echo "ok   WebTorrent <- C#, found on the DHT: $(cat g2.out)" || { echo "FAIL WebTorrent <- C# over the DHT"; tail -5 g2.err s2.err; fail=1; }
kill $SP; wait $SP 2>/dev/null

# 3. WebTorrent seeds and announces through the C# DHT node; C# finds it there and downloads
: > s3.out; node dht.mjs seed files/mid.bin 127.0.0.1:17100 > s3.out 2>s3.err & SP=$!; pids+=($SP); waitfor s3.out; read IH PORT < s3.out
sleep 3; rm -f out/dht3.bin; timeout 90 dotnet $D torrent get $IH out/dht3.bin --dht --bootstrap=127.0.0.1:17100 > g3.out 2>g3.err
cmp -s files/mid.bin out/dht3.bin && echo "ok   C# <- WebTorrent, found on the DHT: $(cat g3.out)" || { echo "FAIL C# <- WebTorrent over the DHT"; tail -5 g3.err; fail=1; }
exit $fail
