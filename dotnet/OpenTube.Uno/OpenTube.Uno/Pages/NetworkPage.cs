using OpenTube.Client;
using OpenTube.Core;
using OpenTube.Uno.Controls;
using OpenTube.Uno.Services;
using static OpenTube.Uno.Controls.Ui;

namespace OpenTube.Uno.Pages;

/// <summary>
/// Network &amp; diagnostics: an overall verdict, each check in plain words with what to try, the connection, the main
/// chain, every chain this device keeps, the video trackers, and the recent events.
/// </summary>
public sealed partial class NetworkPage : LivePage
{
    readonly Dictionary<string, (bool Ok, string Text)> trackers = []; bool tested;
    public NetworkPage() : base("Network") { }
    static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    protected override string Key()
    {
        if (!tested) { tested = true; Retest(); }
        var S = Svc.S; return $"{S.Peers}|{S.Engine.MsgsIn / 20}|{S.Engine.Read(e => e.Store.Main.Blocks.Count)}|{string.Join(",", trackers.Select(t => t.Value.Ok))}|{S.Engine.Events().Count}";
    }
    void Retest()
    {
        foreach (var u in Svc.S.TrackerUrls())
            _ = Task.Run(async () =>
            {
                var t0 = DateTime.UtcNow;
                try { using var ws = new System.Net.WebSockets.ClientWebSocket(); using var cts = new CancellationTokenSource(8000); await ws.ConnectAsync(new Uri(u), cts.Token); trackers[u] = (true, $"reachable · {(DateTime.UtcNow - t0).TotalMilliseconds:0} ms"); }
                catch (Exception e) { trackers[u] = (false, e is OperationCanceledException ? "timed out" : "unreachable"); }
            });
    }
    sealed record Check(string Level, string Name, string Text, string? Fix = null);
    List<Check> Checks()
    {
        var S = Svc.S; var o = new List<Check>();
        o.Add(S.Peers == 0 ? new("error", "Connection", "Not linked to any node: nothing new arrives.", "Nobody may be reachable, or this network blocks it. Running OpenTube on a computer (Devices) gives the network an always-on peer.")
            : new(S.Peers >= 2 ? "ok" : "warn", "Connection", $"Connected to {S.Peers} node(s). Every connection is an equal peer; none is required.", S.Peers < 2 ? "With only one connection, losing it pauses syncing until another is found." : null));
        var (lvl, _, text, height, dir) = S.Engine.Read(e => { var s = e.SyncState(e.Store.Main); return (s.Level, s.State, s.Text, Js.Num(e.Store.MainBest["height"]) ?? 0, e.Store.Directory.Count); });
        o.Add(new(lvl, "Main chain", $"Block #{height}, {dir} registered. {text}", lvl == "ok" ? null : "Stay connected; missing blocks are requested automatically."));
        var followed = S.Engine.Read(e => e.Store.Chains.Values.Where(c => c != e.Store.Main && (S.Notes.Subscription(c.Id) != null || S.IsMine(c.Owner))).Select(c => e.SyncState(c)).ToList());
        var stuck = followed.Count(x => x.Level == "error"); var lag = followed.Count(x => x.Level == "warn" && x.State != "unconfirmed");
        o.Add(stuck > 0 ? new("error", "Your chains", $"{stuck} of {followed.Count} chains are not syncing.", "See the chains below.") : lag > 0 ? new("warn", "Your chains", $"{lag} of {followed.Count} chains are catching up.", "Usually resolves within seconds.") : new("ok", "Your chains", followed.Count > 0 ? $"All {followed.Count} chains you follow are in sync." : "You don’t follow any chains yet."));
        var up = trackers.Values.Count(t => t.Ok);
        o.Add(trackers.Count == 0 ? new("warn", "Video (P2P)", "Testing trackers…") : up == 0 ? new("error", "Video (P2P)", "No video tracker is reachable, so viewers can’t find whoever has a video.", "Check your network, or run OpenTube on a computer with a public address.") : new("ok", "Video (P2P)", $"{up} of {trackers.Count} trackers reachable."));
        var nodes = S.MyNodes(); var online = nodes.Count(x => Now - x.At < 10 * 60000);
        o.Add(nodes.Count == 0 ? new("warn", "Your computers", "None yet, so your videos are online only while this device is.", "Devices → Add a computer: one command on any computer keeps them online.") : online > 0 ? new("ok", "Your computers", $"{online} of {nodes.Count} online.") : new("warn", "Your computers", "None online right now.", "Check that OpenTube is running on them (Devices)."));
        return o;
    }
    protected override void Build()
    {
        var S = Svc.S; var checks = Checks();
        var worst = checks.Any(c => c.Level == "error") ? "error" : checks.Any(c => c.Level == "warn") ? "warn" : "ok";
        Col.Children.Add(Row(H("Network & diagnostics", 22), Btn("Re-test", () => { trackers.Clear(); Retest(); Redraw(); })));
        Col.Children.Add(Text(worst == "ok" ? "✅ Everything working" : worst == "warn" ? "⚠️ Partly working" : "❌ Something is broken", 18));
        var cl = new StackPanel { Spacing = 8 };
        foreach (var c in checks) { var sp = new StackPanel { Spacing = 2 }; sp.Children.Add(Text($"{(c.Level == "ok" ? "✅" : c.Level == "warn" ? "⚠️" : "❌")} {c.Name} — {c.Text}", 14)); if (c.Fix != null) sp.Children.Add(Sub("→ " + c.Fix)); cl.Children.Add(sp); }
        Col.Children.Add(Card(cl));
        var main = S.Engine.Read(e => (Tip: (string?)e.Store.MainBest["hash"] ?? "", Blocks: e.Store.Main.Blocks.Count, Orphans: e.Store.Main.Orphans.Count, Pending: e.Store.Mempool.Count, Txs: e.Store.TxPool.Count, Mining: e.Mining, Hashes: e.MinedHashes, Rejected: e.Store.Rejected.ToList(), Sync: e.SyncState(e.Store.Main)));
        var mst = S.Engine.Stat(S.Engine.Read(e => e.Store.Main.Id));
        Col.Children.Add(Card(H("Connection"), Text($"Linked to {S.Peers} node(s): " + string.Join(", ", S.LinkedUrls.Select(u => u.Replace("wss://", "").Replace("ws://", "").Replace("/ot", ""))), 14),
            Sub($"Last message in: {(S.Engine.LastMsgIn > 0 ? Svc.Ago(S.Engine.LastMsgIn) : "never")} · {S.Engine.MsgsIn:N0} in · {S.Engine.MsgsOut:N0} out"), Sub("The site: " + Svc.Site), Sub("Network ID"), Mono(Network.MainId[..32] + "…")));
        Col.Children.Add(Card(H("Main chain"), Text($"{main.Sync.State}: {main.Sync.Text}", 14), Sub($"Tip {main.Tip[..Math.Min(16, main.Tip.Length)]}… · {main.Blocks} blocks stored ({main.Orphans} waiting for parents) · last new block {(mst.LastBlock > 0 ? Svc.Ago(mst.LastBlock) : "never")}"),
            Sub($"Mining: {(main.Mining ? $"yes, {main.Hashes:N0} hashes" : "idle")} · pending: {main.Pending} registrations, {main.Txs} transactions · rejected: {(main.Rejected.Count == 0 ? "none" : string.Join(", ", main.Rejected.Select(kv => $"{kv.Value}× {kv.Key}")))}")));
        var chains = S.Engine.Read(e => e.Store.Chains.Values.Where(c => c != e.Store.Main).Select(c => (c.Id, Name: (string?)c.Genesis["payload"]?["name"] ?? (string?)c.Genesis["payload"]?["month"] ?? c.Id[..8], Kind: (string?)c.Genesis["payload"]?["kind"] ?? "", Blocks: c.Blocks.Count, c.Owner, Sync: e.SyncState(c))).ToList());
        var ch = new StackPanel { Spacing = 6 };
        foreach (var c in chains.OrderByDescending(c => S.IsMine(c.Owner) || S.Notes.Subscription(c.Id) != null).ThenByDescending(c => c.Blocks).Take(60))
        {
            var st = S.Engine.Stat(c.Id);
            ch.Children.Add(new StackPanel { Children = { Text($"{c.Name} ({c.Kind}) · {c.Blocks} blocks · {c.Sync.State}", 13), Sub($"{(S.IsMine(c.Owner) ? "yours" : S.Notes.Subscription(c.Id) ?? "viewed")} · last block {(st.LastBlock > 0 ? Svc.Ago(st.LastBlock) : "never")} · last heard {(st.LastAnnounce > 0 ? Svc.Ago(st.LastAnnounce) : "never")}") } });
        }
        Col.Children.Add(Card(H($"Chains ({chains.Count})"), ch));
        var tl = new StackPanel { Spacing = 4 };
        foreach (var u in S.TrackerUrls()) tl.Children.Add(Text($"{(trackers.TryGetValue(u, out var r) ? (r.Ok ? "✅ " : "⚠️ ") + r.Text : "testing…")} · {u}", 12));
        Col.Children.Add(Card(H("Video trackers"), tl));
        var ev = S.Engine.Events();
        Col.Children.Add(Card(H("Recent events"), ev.Count == 0 ? Sub("Nothing yet.") : Mono(string.Join("\n", ev.Take(60).Select(e => $"{DateTimeOffset.FromUnixTimeMilliseconds((long)e.Ts).ToLocalTime():HH:mm:ss}  {e.Kind,-6} {e.Text}")))));
    }
}
