using System.Text;
using System.Text.Json.Nodes;
using OpenTube.Core;
using OpenTube.Core.BitTorrent;

namespace OpenTube.Node;

/// <summary>
/// Pay per piece (core: PIECE, createPieceTabs), the "ot_pay" BitTorrent extension: both ends say who they are
/// ({hello: key}); the downloader keeps a running tab per peer and video ("I've received n pieces from you; I owe you
/// amt units in total"), signs it and sends it ({tab}) as pieces arrive. This node, as the sender, stops serving a peer
/// whose tab falls 4 pieces behind or who can't cover it; its tabs are cashed in by <see cref="Earnings"/>. As the
/// downloader it pays from its own key, except where it's free: videos from before the coin, the channel's owner, the
/// channel's hosts (registered, or by their published list), and channels this node's owner hosts.
/// </summary>
public sealed class PieceTabs
{
    readonly OpenTubeNode node;
    readonly Dictionary<string, JsonObject> tabs = [];   // 'payer|video' -> latest tab received (we are the payee)
    readonly Dictionary<string, (double N, double Amt)> sent = [];   // 'payee|video' -> what we've signed (we are the payer)
    readonly object gate = new();
    (double At, Dictionary<string, (string Chain, JsonObject Block)> Map) index = (0, []);
    static readonly bool Debug = Environment.GetEnvironmentVariable("PAY_DEBUG") == "1";
    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public PieceTabs(OpenTubeNode node) => this.node = node;
    public List<JsonObject> Pending() { lock (gate) return tabs.Values.Select(x => (JsonObject)x.DeepClone()).ToList(); }

    sealed class Ext(PieceTabs o, PeerSession wire) : IWireExtension
    {
        public string Name => "ot_pay";
        public bool Ready; public string? PeerKey;
        public void OnExtendedHandshake(Dictionary<string, object?> h) { if (h.GetValueOrDefault("m") is Dictionary<string, object?> m && m.ContainsKey("ot_pay")) { Ready = true; Send(new JsonObject { ["hello"] = o.node.NodeKey }); } }
        public void Send(JsonObject msg) => wire.SendExtended("ot_pay", Encoding.UTF8.GetBytes(msg.ToJsonString()));
        public void OnMessage(byte[] payload)
        {
            JsonObject? m; try { m = JsonNode.Parse(payload) as JsonObject; } catch { return; }
            if (m == null) return;
            if (Debug) o.node.Log("[pay-debug] got " + m.ToJsonString()[..Math.Min(300, m.ToJsonString().Length)] + $" (peer {PeerKey?[..8]}, shape {(m["tab"] is JsonObject tt ? TabShape(tt) : (bool?)null)})");
            if (Js.IsKey(m["hello"]) && (string?)m["hello"] != o.node.NodeKey) PeerKey = (string)m["hello"]!;
            else if (m["tab"] is JsonObject tab && TabShape(tab) && (string?)tab["payee"] == o.node.NodeKey && (string?)tab["payer"] == PeerKey) o.Take(tab);
        }
    }
    /// <summary>Every connection gets the extension; a peer that owes too much isn't unchoked.</summary>
    public void Attach(PeerSession wire)
    {
        var e = new Ext(this, wire); wire.Use(e);
        wire.MayServe = w => { var info = w.Torrent == null ? null : InfoFor(w.Torrent.InfoHashHex); return info == null || e.PeerKey == null || info.Value.Free(e.PeerKey) || Owed(w, e, info.Value).N <= 4; };
    }

    static bool TabShape(JsonObject x) => Js.NumIs(x["v"], 1) && (string?)x["kind"] == "tab" && Js.IsKey(x["payer"]) && Js.IsKey(x["payee"]) && (string?)x["payer"] != (string?)x["payee"]
        && Js.IsHex(x["chain"], 64) && Js.IsHex(x["video"], 64) && Js.IsKey(x["creator"]) && Js.IsSafeInteger(x["n"]) && Js.Num(x["n"]) > 0 && Coin.AmtOk(x["amt"]) && Js.IsSafeInteger(x["at"]) && x["sig"] is JsonValue sv && sv.TryGetValue<string>(out _);
    static string TabPreimage(JsonObject x) { var r = (JsonObject)x.DeepClone(); r.Remove("sig"); return "opentube-tab:" + Blocks.Canon(r); }
    void Take(JsonObject x)
    {
        if (!Rules.VerifySig((string)x["payer"]!, TabPreimage(x), x["sig"])) { if (Debug) node.Log("[pay-debug] tab signature fails: " + TabPreimage(x)); return; }
        var k = $"{x["payer"]}|{x["video"]}";
        lock (gate) { if (tabs.TryGetValue(k, out var cur) && Js.Num(cur["amt"]) >= Js.Num(x["amt"])) return; tabs[k] = x; }
    }

    // ---- which video an infohash is, who made it, and who gets it free
    record struct Info(string Chain, string Video, string Creator, Func<string, bool> Free);
    Info? InfoFor(string ih)
    {
        (string Chain, JsonObject Block) x;
        lock (gate)
        {
            if (Now() - index.At > 10000 || !index.Map.ContainsKey(ih))
            {
                var map = node.Engine.Read(e =>
                {
                    var d = new Dictionary<string, (string, JsonObject)>();
                    foreach (var c in e.Store.Chains.Values)
                        if (c != e.Store.Main && (string?)c.Genesis["payload"]?["kind"] == "channel")
                            foreach (var b in Derived.Of(c).Videos) if ((string?)b["payload"]?["infohash"] is { } vih) d[vih] = (c.Id, b);
                    return d;
                });
                index = (Now(), map);
            }
            if (!index.Map.TryGetValue(ih, out x)) return null;
        }
        var (owner, hostsSet) = node.Engine.Read(e => (e.Store.Chains.TryGetValue(x.Chain, out var c) ? c.Owner : null, e.Store.Ledger.Hosts.TryGetValue(x.Chain, out var h) ? new HashSet<string>(h) : []));
        if (owner == null) return null;
        bool Hosts(string? k)
        {
            if (k == null) return false;
            var users = new[] { k, node.Engine.Read(e => e.Nodes.TryGetValue(k, out var n) ? (string?)n["grant"]?["user"] : null) }.Where(u => u != null).Select(u => u!);
            return users.Any(u => hostsSet.Contains(u) || node.Engine.Read(e => e.Memberships.TryGetValue(u, out var m) && (m["own"] as JsonArray ?? []).Any(v => (string?)v == x.Chain)));
        }
        var ts = Js.Num(x.Block["ts"]) ?? 0; var user = node.OwnerUser;
        return new Info(x.Chain, (string)x.Block["hash"]!, owner,
            k => ts < Coin.PieceStart || k == owner || Hosts(k) || (k == node.NodeKey && (user == owner || Hosts(user))));
    }
    (double N, double Amt) Owed(PeerSession w, Ext x, Info info)
    {
        var pl = w.Torrent?.Meta?.PieceLength ?? 16384; var servedN = Math.Floor(w.Uploaded / (double)pl);
        JsonObject? tab; lock (gate) tabs.TryGetValue(x.PeerKey + "|" + info.Video, out tab);
        var owedN = servedN - (Js.Num(tab?["n"]) ?? 0);
        return (owedN, (Js.Num(tab?["amt"]) ?? 0) + Math.Max(0, owedN) * Price());
    }
    double Price() => node.Engine.Read(e => e.Store.Ledger.Prices.GetValueOrDefault("piece", 2));

    /// <summary>Every few seconds: sign tabs for what we've received; stop serving peers whose tab is behind or unfunded.</summary>
    public void Tick(IEnumerable<PeerSession> wires)
    {
        foreach (var w in wires)
        {
            if (w.Torrent?.Meta is not { } meta) continue;
            var x = w.Extensions.OfType<Ext>().FirstOrDefault(); if (x is not { Ready: true, PeerKey: not null }) continue;
            var info = InfoFor(w.Torrent.InfoHashHex); if (info is not { } inf) continue;
            if (!inf.Free(node.NodeKey))
            {   // as the downloader: owe the peer for each piece's worth it sent us
                var n = Math.Floor(w.Downloaded / (double)meta.PieceLength); var k = x.PeerKey + "|" + inf.Video;
                (double N, double Amt) prev; lock (gate) prev = sent.GetValueOrDefault(k);
                if (n > prev.N)
                {
                    var amt = prev.Amt + Math.Max(1, Math.Ceiling((n - prev.N) * Price()));
                    var tab = new JsonObject { ["v"] = 1, ["kind"] = "tab", ["payer"] = node.NodeKey, ["payee"] = x.PeerKey, ["chain"] = inf.Chain, ["video"] = inf.Video, ["creator"] = inf.Creator, ["n"] = n, ["amt"] = amt, ["at"] = Now() };
                    tab["sig"] = node.Identity!.Sign(TabPreimage(tab));
                    lock (gate) sent[k] = (n, amt);
                    x.Send(new JsonObject { ["tab"] = tab });
                }
            }
            if (!inf.Free(x.PeerKey))
            {   // as the sender: a peer that owes more than it can pay, or sends no tabs, stops getting pieces
                var (owedN, owedAmt) = Owed(w, x, inf);
                JsonObject? tab; lock (gate) tabs.TryGetValue(x.PeerKey + "|" + inf.Video, out tab);
                var bal = node.Engine.Read(e => e.Store.Ledger.Balance(x.PeerKey));
                if (owedN > 4 || (tab != null && bal < owedAmt * Coin.CreatorShare))
                    if (!w.AmChoking) { w.Choke(); node.Log($"[pay] stopped sending to {x.PeerKey[..8]}: {(owedN > 4 ? $"no payment for {owedN} pieces" : "not enough coins")}"); }
            }
            else if (w.AmChoking && !w.PeerChoking) w.Unchoke();
        }
    }
}
