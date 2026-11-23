using OpenTube.Core.BitTorrent;

namespace OpenTube.App;

/// <summary>
/// opentube torrent seed FILE [--port=N]            seed a file; prints its infohash (the same WebTorrent gives it)
/// opentube torrent get INFOHASH OUT HOST:PORT…      download from peers (only the infohash needed: the rest comes from them)
/// opentube torrent hash FILE                       just the infohash
/// </summary>
static class TorrentTool
{
    public static int Run(string[] a)
    {
        if (a.Length < 2) { Console.Error.WriteLine("opentube torrent seed FILE [--port=N] | get INFOHASH OUT HOST:PORT… | hash FILE"); return 2; }
        var port = int.TryParse(a.FirstOrDefault(x => x.StartsWith("--port="))?[7..], out var pp) ? pp : 0;
        var rest = a.Where(x => !x.StartsWith("--")).ToArray();
        switch (rest[0])
        {
            case "hash": Console.WriteLine(TorrentMeta.Create(rest[1]).InfoHash); return 0;
            case "seed":
            {
                var bt = new BtClient { Log = Console.Error.WriteLine }; var t = bt.Add(TorrentData.Seed(rest[1]));
                bt.Listen(port);
                Console.WriteLine($"{t.InfoHashHex} {bt.Port}");
                while (true) { Thread.Sleep(5000); Console.Error.WriteLine($"peers {bt.PeerCount} uploaded {t.Uploaded}"); }
            }
            case "get":
            {
                var bt = new BtClient { Log = Console.Error.WriteLine }; var t = bt.Add(TorrentData.Fetch(rest[1], rest[2]));
                var done = new TaskCompletionSource(); t.Done += _ => done.TrySetResult();
                bt.Listen(port);
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
