using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Node;

/// <summary>
/// Controlling this computer from the site (Devices): commands signed by the owner's key (or by someone the owner shared
/// the computer with), routed to this node over the network, answered with a reply signed by this node. Same protocol as
/// the JavaScript node (nodectl / nodectlr): the command is checked (this node, a fresh nonce, at most 2 minutes old,
/// the sender's signature) before anything runs.
/// </summary>
public sealed class NodeControl(OpenTubeNode node)
{
    readonly Dictionary<string, double> seen = [];   // nonce -> when (replay protection)
    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    JsonObject Users => (JsonObject)node.Obs!.Store["users"]!;
    /// <summary>may(user, what): the owner may do everything; others what the owner shared (until their access ends).</summary>
    bool May(string user, string what) => user == node.OwnerUser || (Users[user] is JsonObject u && u["can"] is JsonArray can && can.Any(x => (string?)x == what) && !(Js.Num(u["until"]) < Now()));
    bool OwnsChannel(string user, string chain) => node.Engine.Read(e =>
    {
        var owner = e.Store.Chains.TryGetValue(chain, out var c) ? c.Owner : e.Store.Directory.TryGetValue(chain, out var d) ? (string?)d.Genesis["author"] : null;
        return owner != null && (owner == user || e.Store.Ledger.Resolve(owner) == e.Store.Ledger.Resolve(user));
    });

    JsonObject Signed(JsonObject x) { x["sig"] = node.Identity!.Sign("opentube-nodectl:" + Blocks.Canon(x)); return x; }
    JsonObject Reply(JsonObject c, bool ok, JsonNode? data = null, string? error = null)
    {
        var r = new JsonObject { ["v"] = 1, ["kind"] = "nodectlr", ["node"] = node.NodeKey, ["re"] = c["nonce"]?.DeepClone(), ["at"] = Now(), ["ok"] = ok };
        if (ok) r["data"] = data; else r["error"] = error;
        return Signed(r);
    }

    /// <summary>A routed message for this node: a control command gets a signed answer.</summary>
    public async Task<JsonObject?> OnRoute(JsonNode? body)
    {
        if (body is JsonObject rq && (string?)rq["kind"] == "reach") return node.Coord != null ? await node.Coord.ReachProbe(rq) : null;   // can the internet reach that computer?
        if (body is JsonObject pq && (string?)pq["kind"] == "pad") return node.Coord?.PadTake(pq);   // another entry node's live streams
        if (body is JsonObject q && (string?)q["kind"] == "pchal") return node.Earn?.Answer(q) ?? new JsonObject { ["kind"] = "pproof", ["has"] = false };   // a storage proof challenge
        if (body is not JsonObject c || (string?)c["kind"] != "nodectl") return null;
        JsonObject Fail(string why) => Reply(c, false, null, why);
        if (!Js.NumIs(c["v"], 1) || (string?)c["node"] != node.NodeKey || Js.Str(c["nonce"]) is not { Length: <= 40 } nonce || Js.Str(c["cmd"]) is not { } cmd) return Fail("bad command");
        var user = Js.Str(c["user"]) ?? "";
        if (node.OwnerUser == null || (user != node.OwnerUser && !(Users[user] is JsonObject su && !(Js.Num(su["until"]) < Now())))) return Fail("this computer isn’t yours, and it isn’t shared with you (or your invitation ended)");
        lock (seen)
        {
            if (Math.Abs(Now() - (Js.Num(c["at"]) ?? 0)) > 2 * 60000 || seen.ContainsKey(nonce)) return Fail("stale or repeated command");
        }
        var unsigned = (JsonObject)c.DeepClone(); unsigned.Remove("sig");
        if (!Rules.VerifySig(c["user"], "opentube-nodectl:" + Blocks.Canon(unsigned), c["sig"])) return Fail("bad signature");
        lock (seen) { seen[nonce] = Now(); foreach (var (k, at) in seen.ToList()) if (Now() - at > 5 * 60000) seen.Remove(k); }
        var owner = user == node.OwnerUser; var a = c["args"] as JsonObject ?? [];
        var who = owner ? "the owner" : (string?)Users[user]?["name"] is { Length: > 0 } nm ? nm : user[..8];
        try
        {
            JsonNode? data;
            switch (cmd)
            {
                case "logs":
                    if (!owner) return Fail("only the owner can read this computer’s log");
                    var n = (int)Math.Min(400, Js.Num(a["n"]) is double d && d > 0 ? d : 200);
                    return Reply(c, true, new JsonObject { ["lines"] = new JsonArray(node.Logs.TakeLast(n).Select(l => (JsonNode?)l).ToArray()) });
                case "public.set":
                {   // auto: its own HTTPS when directly on the internet, else a tunnel; tunnel: a tunnel always (too); off
                    if (!owner) return Fail("only the owner can change this");
                    var mode = Js.Str(a["mode"]); if (mode is not ("auto" or "tunnel" or "off")) return Fail("bad mode");
                    if (node.SetPublic == null) return Fail("not available");
                    node.Log($"[control] public: {mode}, from the owner's console");
                    _ = Task.Run(async () => { await Task.Delay(500); try { await node.SetPublic(mode); } catch (Exception e) { node.Log("[internet] " + e.Message); } });
                    return Reply(c, true, new JsonObject { ["applying"] = mode });
                }
                case "relay":
                {   // "I just went live: help serve it" (from the owner's or a sharing person's browser)
                    if (!May(user, "host")) return Fail("you may not use this computer for relaying");
                    if (!Js.IsHex(a["stream"], 64) || !Js.IsHex(a["chain"], 64)) return Fail("bad stream");
                    node.Relays?.Ask((string)a["stream"]!, (string)a["chain"]!, Js.IsTrue(a["record"]), Js.Str(a["title"]));
                    return Reply(c, true, new JsonObject { ["relaying"] = true });
                }
                case "update":
                {   // download the newest version and restart with it (the launcher or the service manager starts it again)
                    if (!owner && !May(user, "manage")) return Fail("only the owner can update this computer");
                    if (node.Update is not { Enabled: true } up) return Fail("this computer wasn’t started with the OpenTube command, so it can’t update itself: stop it and run the command again");
                    node.Log($"[control] update from {who}’s console");
                    try { var restarting = await up.Check(true); return Reply(c, true, new JsonObject { ["restarting"] = restarting, ["commit"] = up.Commit }); }
                    catch (Exception e) { return Fail(e.Message); }
                }
                case "status":
                    data = node.Status.DeepClone(); if (data is JsonObject so) so["host"] = node.Hosting?.Status(); break;
                case "store": case "store.status":
                {   // keep a video someone uploaded from a browser (their storage is this computer)
                    if (!May(user, "host")) return Fail("you may not store videos on this computer");
                    if (node.Hosting == null) return Fail("video hosting is off on this computer");
                    if (!Js.IsHex(a["ih"], 40)) return Fail("bad video");
                    var ih = (string)a["ih"]!;
                    if (cmd == "store")
                    {
                        if (!Js.IsHex(a["chain"], 64)) return Fail("bad channel");
                        var ch = (string)a["chain"]!; bool ok = owner || OwnsChannel(user, ch);
                        if (!ok && Js.Str(a["channelSig"]) is { } cs2)
                        {
                            var key = node.Engine.Read(e => e.Store.Chains.TryGetValue(ch, out var cc) ? cc.Owner : e.Store.Directory.TryGetValue(ch, out var dd) ? (string?)dd.Genesis["author"] : null);
                            ok = key != null && Rules.VerifySig(key, $"opentube-obs-channel:{node.NodeKey}:{user}:{ch}", cs2);
                        }
                        if (!ok) return Fail("that channel isn’t yours");
                        try { await node.Hosting.StoreVideo(ch, ih); } catch (Exception e) { return Fail(e.Message); }
                        node.Log($"[host] storing a video for {who}");
                    }
                    return Reply(c, true, node.Hosting.ProgressOf(ih));
                }
                case "users": case "users.set":
                    if (!owner) return Fail("only the owner can share this computer");
                    if (cmd == "users.set")
                    {
                        var key = Js.Str(a["key"]);
                        if (key == null || !Js.IsKey(a["key"]) || key == node.OwnerUser) return Fail("not a user ID");
                        var can = (a["can"] as JsonArray ?? []).Select(x => Js.Str(x)).Where(x => x is "obs" or "host" or "manage").Distinct().ToList();
                        if (can.Count == 0) { Users.Remove(key); ((JsonObject)node.Obs!.Store["profiles"]!).Remove(key); }
                        else Users[key] = new JsonObject { ["can"] = new JsonArray(can.Select(x => (JsonNode?)x).ToArray()), ["name"] = (Js.Str(a["name"]) ?? "")[..Math.Min(60, (Js.Str(a["name"]) ?? "").Length)] };
                        node.Obs!.Save();
                    }
                    node.Log($"[control] {cmd}: shared with {Users.Count} people");
                    return Reply(c, true, new JsonObject { ["users"] = Users.DeepClone() });
                case "obs": case "obs.set": case "obs.rotate":
                {
                    if (node.Obs == null) return Fail("OBS is off on this computer (started with --no-obs)");
                    if (!May(user, "obs")) return Fail("you may not stream through this computer");
                    if (cmd == "obs" && Js.IsTrue(a["test"]) && node.Coord != null) return Reply(c, true, await node.Coord.ObsTest());   // can OBS reach this computer from the internet?
                    var p = node.Obs.ProfileOf(user);
                    if (cmd == "obs.rotate") p["key"] = "ot-" + B64u.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
                    if (cmd == "obs.set")
                    {
                        var ch = Js.Str(a["channel"]);
                        if (ch != null && Js.IsHex(a["channel"], 64) && node.Engine.Read(e => !e.Store.Chains.ContainsKey(ch)))
                        {   // not loaded here yet: fetch it first
                            if (node.Engine.OpenChain(ch) is { } chain) node.Engine.RequestChain(chain);
                            for (int i = 0; i < 40 && node.Engine.Read(e => !e.Store.Chains.TryGetValue(ch, out var cc) || cc.Blocks.Count == 0); i++) await Task.Delay(250);
                        }
                        if (a.ContainsKey("channel"))
                        {
                            bool proved = false;
                            if (ch != null && Js.Str(a["channelSig"]) is { } cs)
                            {   // signed by the channel's key itself
                                var key = node.Engine.Read(e => e.Store.Chains.TryGetValue(ch, out var cc) ? cc.Owner : e.Store.Directory.TryGetValue(ch, out var dd) ? (string?)dd.Genesis["author"] : null);
                                proved = key != null && Rules.VerifySig(key, $"opentube-obs-channel:{node.NodeKey}:{user}:{ch}", cs);
                            }
                            if (!string.IsNullOrEmpty(ch) && !proved && !OwnsChannel(user, ch)) return Fail("that channel isn’t yours (or this computer couldn’t load it yet: try again)");
                            p["channelBy"] = !string.IsNullOrEmpty(ch) ? user : null;
                            p["channel"] = ch != null && Js.IsHex(a["channel"], 64) ? ch : null;
                        }
                        if (Js.Str(a["title"]) is { } t) p["title"] = t.Length == 0 ? "Live" : t[..Math.Min(140, t.Length)];
                        if (a["youtube"] is JsonObject yt) { var y = (JsonObject)p["youtube"]!; if (Js.Str(yt["key"]) is { } yk) y["key"] = yk.Trim()[..Math.Min(200, yk.Trim().Length)]; if (Js.IsBool(yt["on"])) y["on"] = Js.IsTrue(yt["on"]); }
                        if (a["tile"] is JsonObject tl) { var tt = (JsonObject)p["tile"]!; if (Js.IsBool(tl["on"])) tt["on"] = Js.IsTrue(tl["on"]); if (Js.Num(tl["n"]) is 2 or 3 or 4) tt["n"] = Js.Num(tl["n"]); }
                    }
                    node.Obs.Save();
                    var chan = (string?)p["channel"];
                    data = new JsonObject
                    {
                        ["key"] = p["key"]?.DeepClone(), ["port"] = node.Rtmp?.Port, ["ips"] = new JsonArray(LocalIps().Select(x => (JsonNode?)x).ToArray()), ["publicIp"] = null,
                        ["channel"] = chan, ["title"] = p["title"]?.DeepClone(),
                        ["linked"] = chan != null && node.Obs.LinkedChannels().Contains(chan),
                        ["youtube"] = new JsonObject { ["on"] = Js.IsTrue(p["youtube"]?["on"]), ["set"] = !string.IsNullOrEmpty((string?)p["youtube"]?["key"]) }, ["tile"] = p["tile"]?.DeepClone(),
                        ["ffmpeg"] = Media.HasFfmpeg, ["node"] = node.NodeKey,
                        ["live"] = node.Rtmp == null ? null : new JsonObject { ["port"] = node.Rtmp.Port, ["listening"] = node.Rtmp.Error == null, ["error"] = node.Rtmp.Error, ["live"] = node.Rtmp.Live > 0, ["bytes"] = node.Rtmp.Bytes, ["keyInterval"] = node.Rtmp.KeyInterval, ["lastConn"] = node.Rtmp.LastStage == null ? null : new JsonObject { ["stage"] = node.Rtmp.LastStage } },
                        ["version"] = new JsonObject { ["name"] = node.Version, ["build"] = node.Build, ["impl"] = "dotnet" },
                    };
                    break;
                }
                case "group" or "group.set" or "group.invite" or "group.uninvite" or "group.studio" or "group.air" or "group.mute" or "group.logo" or "group.kick.all" or "group.kick" or "group.join":
                {   // the group stream: yours (or, for a guest, the one you were invited to; or another user's you share this computer with)
                    if (node.Obs == null) return Fail("OBS is off on this computer (started with --no-obs)");
                    var me = Users[user] as JsonObject; var guestOf = (string?)me?["guestOf"];
                    var hostArg = Js.Str(a["host"]);
                    var joinHost = guestOf == null && hostArg != null && hostArg != user && (hostArg == node.OwnerUser || (Users[hostArg] is JsonObject hu && hu["guestOf"] == null)) ? hostArg : null;
                    var hostUser = guestOf ?? joinHost ?? user;
                    if (!May(user, "obs")) return Fail("you may not stream through this computer");
                    if (joinHost != null && cmd is not ("group" or "group.join" or "group.kick")) return Fail("only the host can change their group stream");
                    try { data = await node.Obs.GroupOf(hostUser).Control(cmd, a, user, guestOf == null && joinHost == null, owner); }
                    catch (Exception e) { return Fail(e.Message); }
                    break;
                }
                case "flash" or "flash.setup" or "flash.start" or "flash.stop" or "flash.scale" or "flash.keep":
                    if (node.Flashes == null) return Fail("flash computers are unavailable on this computer");
                    try { return Reply(c, true, await node.Flashes.Control(cmd, a, user, owner)); } catch (Exception e) { return Fail(e.Message); }
                case var fm when Host.Commands.Contains(fm):   // the file manager (Manage files on the site)
                    if (!owner && !May(user, "manage")) return Fail("you may not manage this computer’s files");
                    if (node.Hosting == null) return Fail("video hosting is off on this computer (started with --no-host, or without --grant)");
                    data = node.Hosting.Command(cmd, a); break;
                default:
                    return Fail("this computer doesn’t know that command (it runs the .NET version of OpenTube)");
            }
            node.Log($"[control] {cmd} from {who}’s console");
            return Reply(c, true, data);
        }
        catch (Exception e) { return Fail(e.Message); }
    }

    static IEnumerable<string> LocalIps() => NetworkInterface.GetAllNetworkInterfaces().Where(i => i.OperationalStatus == OperationalStatus.Up)
        .SelectMany(i => i.GetIPProperties().UnicastAddresses).Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(x.Address)).Select(x => x.Address.ToString());
}
