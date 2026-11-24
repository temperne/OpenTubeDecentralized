using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>
/// Your computers as every UI shows them: what each one says about itself (in plain words), its roles, whether it's
/// yours, and what your computers earned for what they provided to the network.
/// </summary>
public static class Devices
{
    public static JsonObject? Info(this ClientSession s, string node) => s.Engine.Read(e => e.Nodes.TryGetValue(node, out var x) ? x["info"]?.DeepClone() as JsonObject : null);
    public static List<string> Roles(this ClientSession s, string node) => s.Engine.Read(e => e.Nodes.TryGetValue(node, out var x) ? (x["roles"] as JsonArray ?? []).Select(r => (string?)r).OfType<string>().ToList() : []);
    public static bool OwnsNode(this ClientSession s, string node) => s.Engine.Read(e => e.Nodes.TryGetValue(node, out var x) && (string?)x["grant"]?["user"] == s.Me.pub);
    public static List<string> Addresses(this ClientSession s, string node) => s.Engine.Read(e => e.Nodes.TryGetValue(node, out var x) ? (x["addrs"] as JsonArray ?? []).Select(a => (string?)a).OfType<string>().ToList() : []);
    public static string RoleName(string r) => r switch { "node" => "full node", "host" => "host", "obs" => "OBS receiver", "entry" => "entry node", _ => r };
    static string Bytes(double n) => n < 1e6 ? $"{n / 1e3:0} KB" : n < 1e9 ? $"{n / 1e6:0.0} MB" : $"{n / 1e9:0.00} GB";

    /// <summary>"Linux · Node .NET 10 · main #680 · 3 peer(s) · 21 chains · build 1a2b3c4d"</summary>
    public static string InfoLine(JsonObject? i) => i == null ? "" : string.Join(" · ", new[] { (string?)i["os"], (string?)i["node"] is { } nv ? "Node " + nv : null, Js.Num(i["height"]) is { } h ? $"main #{h}" : null, Js.Num(i["peers"]) is { } p ? $"{p} peer(s)" : null, Js.Num(i["chains"]) is { } c ? $"{c} chains" : null, (string?)i["version"]?["build"] is { } b ? "build " + b[..Math.Min(8, b.Length)] : null }.Where(x => x != null));
    public static string? HostLine(JsonObject? i) => i?["host"] is JsonObject ho ? $"Hosting {Js.Num(ho["hosting"]) ?? 0} videos ({Bytes(Js.Num(ho["hostedBytes"]) ?? 0)}) for {Js.Num(ho["channels"]) ?? 0} joined channels in {(string?)ho["videoDir"] ?? "its folder"}" + (Js.Num(ho["downloading"]) is > 0 and var dl ? $" · downloading {dl}" : "") : null;
    public static string? ObsLine(JsonObject? i) => i?["obs"] is not JsonObject o ? null : (string?)o["error"] is { } er ? "⚠ OBS: " + er : o["listening"]?.GetValue<bool>() == false ? "⚠ OBS: not listening" : Js.IsTrue(o["live"]) ? $"OBS: streaming now ({Bytes(Js.Num(o["bytes"]) ?? 0)})" : Js.IsTrue(o["armed"]) ? "OBS: ready, waiting for Start Streaming" : $"OBS: listening on port {Js.Num(o["port"])} (stream key and server under Go live)";

    public sealed record Earn(string Key, double Epoch, string Res, double Units, double Amount);
    public static string ResName(string r) => r switch { "bytes" => "video served", "uptime" => "time connected", "tracker" => "tracker service", "storage" => "storage kept", _ => r };
    public static string ResAmount(string r, double units) => r == "bytes" ? Bytes(units) : r == "storage" ? Bytes(units * 1e6) : $"{Math.Round(units / 60)} min";
    /// <summary>The last payouts to your accounts for what your computers provided, newest first.</summary>
    public static List<Earn> Earnings(this ClientSession s, int max = 30)
    {
        var keys = s.Directory().Where(c => s.IsMine(c.Owner)).Select(c => c.Owner!).Append(s.Me.pub).ToHashSet();
        return s.Engine.Read(e => keys.Where(k => k == s.Me.pub || (e.Store.Ledger.Resolve(k) != s.Me.pub && !e.Store.Ledger.Retired.Contains(k)))
            .SelectMany(k => e.Store.Ledger.Earned.TryGetValue(k, out var l) ? l.Select(x => new Earn(k, Js.Num(x["e"]) ?? 0, (string?)x["res"] ?? "", Js.Num(x["units"]) ?? 0, Js.Num(x["amt"]) ?? 0)).ToList() : [])
            .OrderByDescending(x => x.Epoch).ThenBy(x => x.Res).Take(max).ToList());
    }
    /// <summary>The one command that adds a computer (Linux/Mac, or Windows PowerShell).</summary>
    public static (string Unix, string Windows) AddCommands(this ClientSession s, string grant) =>
        ($"curl -fsSL {s.Site}/opentube-dotnet.sh | bash -s -- --site={s.Site} --grant={grant}",
         $"powershell -NoProfile -ExecutionPolicy Bypass -Command \"& ([scriptblock]::Create((irm {s.Site}/opentube-dotnet.ps1))) --site={s.Site} --grant={grant}\"");
}
