using System.Text.Json.Nodes;
using OpenTube.Client;

namespace OpenTube.Uno.Services;

/// <summary>
/// (tests only, when OPENTUBE_TEST is set) Getting an app ready for an end-to-end test without tapping through it:
/// OPENTUBE_TEST=setup makes a channel ("Uno Test Channel") and earns the starter coins, then writes test-setup.json
/// into the data folder: {grant, chain, user} (the grant lets a local computer run as this person).
/// </summary>
public static class TestHooks
{
    public static void Start()
    {
        if ((Environment.GetEnvironmentVariable("OPENTUBE_TEST") ?? Svc.QueryOption("test")) != "setup") return;
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
            }
            catch (Exception e) { Console.WriteLine("[test] setup failed: " + e); }
        });
    }
}
