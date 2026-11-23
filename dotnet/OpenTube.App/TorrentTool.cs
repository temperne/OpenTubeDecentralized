using OpenTube.Core.BitTorrent;

namespace OpenTube.App;

/// <summary>
/// opentube torrent seed FILE [--port=N]            seed a file; prints its infohash (the same WebTorrent gives it)
/// opentube torrent get INFOHASH OUT HOST:PORT…      download from peers (only the infohash needed: the rest comes from them)
/// opentube torrent hash FILE                       just the infohash
/// opentube torrent dht [--dht-port=N]              run a DHT node only (a bootstrap node)
/// opentube torrent serve INFOHASH OUT HOST:PORT…    download and serve it at http://127.0.0.1:N/INFOHASH as it arrives (the phone's player)
/// --tracker=wss://…   (seed, get) also on this WebSocket tracker, with browsers over WebRTC (repeatable)
/// --dht               (seed, get) find peers on the BitTorrent DHT and announce there (get needs no HOST:PORT then)
/// --dht-port=N        its UDP port   --bootstrap=HOST:PORT   join through these nodes instead of the public routers
/// </summary>
static class TorrentTool
{
    public static int Run(string[] a)
    {
        if (a.Length < 2 && a.FirstOrDefault() != "dht") { Console.Error.WriteLine("opentube torrent seed FILE [--port=N] | get INFOHASH OUT HOST:PORT… | hash FILE"); return 2; }
        var port = int.TryParse(a.FirstOrDefault(x => x.StartsWith("--port="))?[7..], out var pp) ? pp : 0;
        var rest = a.Where(x => !x.StartsWith("--")).ToArray();
        var trackers = a.Where(x => x.StartsWith("--tracker=")).Select(x => x[10..]).ToList();   // WebSocket trackers: browsers (WebRTC)
        var useDht = a.Contains("--dht") || a.Any(x => x.StartsWith("--bootstrap="));
        var dhtPort = int.TryParse(a.FirstOrDefault(x => x.StartsWith("--dht-port="))?[11..], out var dp) ? dp : 0;
        var boot = a.Where(x => x.StartsWith("--bootstrap=")).Select(x => x[12..]).ToList();
        void Dht(BtClient bt)
        {
            if (!useDht) return;
            var d = new Dht { Log = Console.Error.WriteLine }; d.Start(dhtPort); Console.Error.WriteLine($"dht on UDP port {d.Port}");
            d.Bootstrap(boot.Count > 0 ? boot : null).Wait(); bt.UseDht(d);
        }
        WebTorrentPeers? Web(BtClient bt, TorrentData t) { if (trackers.Count == 0) return null; var w = new WebTorrentPeers(bt, new OpenTube.Net.SipRtc(), Console.Error.WriteLine); w.Announce(t, trackers); return w; }
        switch (rest[0])
        {
            case "hash": Console.WriteLine(TorrentMeta.Create(rest[1]).InfoHash); return 0;
            case "dht":   // a DHT node and nothing else (a bootstrap node for a private network, tests)
            {
                var d = new Dht { Log = Console.Error.WriteLine }; d.Start(dhtPort); Console.WriteLine($"dht {d.Port}");
                if (boot.Count > 0) d.Bootstrap(boot).Wait();
                while (true) { Thread.Sleep(10000); Console.Error.WriteLine($"dht: {d.NodeCount} nodes"); }
            }
            case "seed":
            {
                var bt = new BtClient { Log = Console.Error.WriteLine }; var t = bt.Add(TorrentData.Seed(rest[1]));
                bt.Listen(port); var web = Web(bt, t); Dht(bt);
                Console.WriteLine($"{t.InfoHashHex} {bt.Port}");
                while (true) { Thread.Sleep(5000); Console.Error.WriteLine($"peers {bt.PeerCount} uploaded {t.Uploaded}"); }
            }
            case "serve":
            {
                var bt = new BtClient { Log = Console.Error.WriteLine }; var t = bt.Add(TorrentData.Fetch(rest[1], rest[2]));
                using var http = new OpenTube.Net.TorrentHttp(ih => bt.Torrents.GetValueOrDefault(ih));
                bt.Listen(port); var web = Web(bt, t); Dht(bt);
                foreach (var hp in rest[3..]) { var i = hp.LastIndexOf(':'); _ = bt.Connect(t, hp[..i], int.Parse(hp[(i + 1)..])); }
                Console.WriteLine(http.UrlFor(t));
                while (true) Thread.Sleep(5000);
            }
            case "get":
            {
                var bt = new BtClient { Log = Console.Error.WriteLine }; var t = bt.Add(TorrentData.Fetch(rest[1], rest[2]));
                var done = new TaskCompletionSource(); t.Done += _ => done.TrySetResult();
                bt.Listen(port); var web = Web(bt, t); Dht(bt);
                foreach (var hp in rest[3..]) { var i = hp.LastIndexOf(':'); _ = bt.Connect(t, hp[..i], int.Parse(hp[(i + 1)..])); }
                var t0 = DateTime.UtcNow;
                if (!done.Task.Wait(TimeSpan.FromMinutes(5))) { Console.Error.WriteLine($"timed out: {t.HaveCount} pieces"); return 1; }
                Console.WriteLine($"done {t.Meta!.Length} bytes in {(DateTime.UtcNow - t0).TotalSeconds:0.00}s");
                return 0;
            }
        }
        return 2;
    }
}
