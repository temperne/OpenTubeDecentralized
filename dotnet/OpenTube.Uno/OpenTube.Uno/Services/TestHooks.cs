using System.Text.Json.Nodes;
using OpenTube.Client;

namespace OpenTube.Uno.Services;

/// <summary>
/// (tests only, when OPENTUBE_TEST is set) Getting an app ready for an end-to-end test without tapping through it:
/// OPENTUBE_TEST=setup makes a channel ("Uno Test Channel") and earns the starter coins, then writes test-setup.json
/// into the data folder: {grant, chain, user} (the grant lets a local computer run as this person).
/// OPENTUBE_TEST=setup,market also lists an item for sale once the coins have arrived ("[test] listed {id}").
/// </summary>
public static class TestHooks
{
    public static void Start()
    {
        var opts = (Environment.GetEnvironmentVariable("OPENTUBE_TEST") ?? Svc.QueryOption("test") ?? "").Split(',');
        if (!opts.Contains("setup")) return;
        _ = Task.Run(async () =>
        {
            var S = Svc.S;
            while (!S.Ready) await Task.Delay(500);
            try
            {
                var chain = S.Publish.MyChannels().FirstOrDefault()?.Id;
                if (chain == null)
                {
                    var g = await S.CreateChannel(new JsonObject { ["type"] = "genesis", ["kind"] = "channel", ["name"] = "Uno Test Channel", ["about"] = "", ["avatar"] = "", ["comments"] = "anyone" });
                    chain = (string)g["hash"]!;
                }
                if (!S.Engine.Read(e => e.Store.Ledger.Starters.Contains(S.Me.pub))) await S.Engine.SubmitTx(new JsonObject { ["starter"] = true }, S.Me);
                var outp = new JsonObject { ["grant"] = S.Trade.Grant("test computer"), ["chain"] = chain, ["user"] = S.Me.pub };
                try { File.WriteAllText(Path.Combine(Svc.DataFolder, "test-setup.json"), outp.ToJsonString()); } catch { }
                Console.WriteLine("[test] setup ready: " + chain);
                if (opts.Contains("place") && !S.All().Any(x => x.Kind == "org"))
                {
                    while (S.Trade.Balance < 500) await Task.Delay(1000);
                    var pid = await S.AddPlace("Test Café", "Café", "Coffee and cake.", 40.7128, -74.006, "1 Main St", "Mon–Fri 8–18", "555-0100", "https://example.com");
                    while (S.Chain(pid)?.Profile["place"] == null) await Task.Delay(1000);
                    await S.AddReview(pid, 4, "Good coffee, friendly staff.");
                    // a photo, and the place's answer to the review
                    using (var bmp = new SkiaSharp.SKBitmap(320, 200)) using (var cv = new SkiaSharp.SKCanvas(bmp))
                    {
                        cv.Clear(new SkiaSharp.SKColor(150, 90, 40)); cv.DrawCircle(160, 100, 60, new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White });
                        using var jpg = SkiaSharp.SKImage.FromBitmap(bmp).Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80);
                        await S.AddPhoto(pid, "data:image/jpeg;base64," + Convert.ToBase64String(jpg.ToArray()), "Our espresso bar");
                    }
                    while (S.Reviews(pid).Count == 0) await Task.Delay(500);
                    await S.ReplyToReview(pid, S.Reviews(pid)[0].Hash, "Thank you, come again!");
                    Console.WriteLine("[test] place " + pid);
                }
                if (opts.Contains("zone") && S.ZoneOf("example.test") == null)
                {
                    var zid = await S.CreateZone("example.test", "test zone");
                    while (S.Chain(zid) == null) await Task.Delay(1000);
                    await S.SetRecord(zid, "tucson", "5.6.7.8", "c/" + chain);
                    await S.SetRecord(zid, "home", "", "");
                    Console.WriteLine("[test] zone " + zid);
                }
                if (opts.Contains("market") && !S.Trade.Listings().Any(l => S.IsMine(l.Seller)))
                {
                    while (S.Trade.Balance < S.Trade.Price("list") + 500) await Task.Delay(1000);
                    var id = await S.ListDraft(new Market.Draft { Type = "goods", Title = "Test bike", About = "A blue bike, barely used.", Price = "5", Contact = "ask in messages", Category = "", Tags = "bike, test", Fields = { ["condition"] = "used" } });
                    Console.WriteLine("[test] listed " + id);
                }
            }
            catch (Exception e) { Console.WriteLine("[test] setup failed: " + e); }
        });
    }
}
