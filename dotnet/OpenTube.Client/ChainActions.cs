using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>What you do on a channel's chain (as the JavaScript page does): comment, vote (paid), hide, block,
/// comment settings, tip, add to a list. Owner actions are signed with the channel's key, others with yours.</summary>
public static class ChainActions
{
    public record Tally(int Up, int Down, double Mine);
    public record Author(string Key, string Name, string Href, string? Pic, bool Owner);

    /// <summary>Votes on a target that count: from before the coin, or paid.</summary>
    public static Tally Votes(this ClientSession s, Derived d, string target)
    {
        if (!d.Votes.TryGetValue(target, out var t)) return new(0, 0, 0);
        int up = 0, down = 0;
        s.Engine.Read(e => { foreach (var b in t.Blocks) if ((Js.Num(b["ts"]) ?? 0) < Coin.Epoch || e.VotePaid(b)) { if ((Js.Num(b["payload"]?["v"]) ?? 0) > 0) up++; else down++; } return 0; });
        var me = s.IsMine(d.Owner) ? d.Owner! : s.Me.pub;
        return new(up, down, t.By.TryGetValue(me, out var v) ? v : 0);
    }
    public static double Views(this ClientSession s, string hash) => s.Engine.Read(e => e.Store.Ledger.Views.TryGetValue(hash, out var n) ? n : 0);
    public static double Price(this ClientSession s, string what) => s.Engine.Read(e => Math.Max(1, Math.Ceiling(e.Store.Ledger.Prices[what])));
    public static string Coins(double units) => (Math.Round(units) / 100).ToString("N2");

    /// <summary>Who wrote something on a chain: the channel itself for its owner, else the person (their bound identity).</summary>
    public static Author AuthorOf(this ClientSession s, string key, Derived? d, string fallback = "")
    {
        if (d != null && key == d.Owner) return new(key, (string?)d.Profile["name"] ?? "", "c/" + d.Id, (string?)d.Profile["avatar"], true);
        var who = s.Engine.Read(e => e.Store.Ledger.Resolve(key));
        var name = who == s.Me.pub ? "You" : s.NameOf(who);
        if (name.StartsWith("User ") && fallback.Length > 0) name = fallback;
        return new(who, name, "u/" + who, null, false);
    }

    static Task<JsonObject> Append(ClientSession s, string chain, JsonObject payload) => Task.Run(() => s.Engine.Append(chain, payload));

    public static async Task Comment(this ClientSession s, string chain, string target, string text)
    {
        text = text.Trim(); if (text.Length == 0) return;
        var d = s.Chain(chain) ?? throw new InvalidOperationException("This channel hasn’t loaded yet");
        if (!s.IsMine(d.Owner) && !d.CanComment(s.Me.pub, target)) throw new InvalidOperationException("Comments here are not open to you");
        var name = await s.Social.MyName();
        await Append(s, chain, new JsonObject { ["type"] = "comment", ["target"] = target, ["text"] = text[..Math.Min(text.Length, 2000)], ["name"] = name[..Math.Min(60, name.Length)] });
    }
    /// <summary>Vote up (1), down (-1) or take it back (same again: 0); paid to <paramref name="to"/> (the author).</summary>
    public static async Task Vote(this ClientSession s, string chain, string target, int dir, string to)
    {
        var d = s.Chain(chain) ?? throw new InvalidOperationException("This channel hasn’t loaded yet");
        var cur = s.Votes(d, target).Mine; var v = cur == dir ? 0 : dir;
        var vb = await Append(s, chain, new JsonObject { ["type"] = "vote", ["target"] = target, ["v"] = v });
        var author = (string)vb["author"]!;
        if (v == 0 || author == to) return;   // liking yourself pays nothing and doesn't count
        await s.Engine.SubmitTx(new JsonObject { ["vote"] = new JsonObject { ["chain"] = chain, ["target"] = (string)vb["hash"]!, ["to"] = to, ["v"] = v, ["amt"] = s.Price("like") } }, s.KeyOf(author) ?? s.Me);
    }
    public static Task Hide(this ClientSession s, string chain, string target) => Append(s, chain, new JsonObject { ["type"] = "hide", ["target"] = target });
    public static Task Block(this ClientSession s, string chain, string key) => Append(s, chain, new JsonObject { ["type"] = "revoke", ["key"] = key });
    public static Task Allow(this ClientSession s, string chain, string key) => Append(s, chain, new JsonObject { ["type"] = "allow", ["key"] = key });
    /// <summary>Who may comment on one video: default (the channel's), anyone, approved, off.</summary>
    public static Task CommentPolicy(this ClientSession s, string chain, string video, string policy) => Append(s, chain, new JsonObject { ["type"] = "settings", ["target"] = video, ["comments"] = policy });
    public static Task AddToList(this ClientSession s, string list, string chain, string video) => Append(s, list, new JsonObject { ["type"] = "add", ["target"] = chain, ["video"] = video });
    public static async Task Tip(this ClientSession s, string to, double units, string memo)
    {
        if (units <= 0) throw new ArgumentException("How many coins?");
        await s.Engine.SubmitTx(new JsonObject { ["pay"] = new JsonArray(new JsonObject { ["to"] = to, ["amt"] = units }), ["memo"] = memo[..Math.Min(memo.Length, 80)] }, s.Me);
    }
    /// <summary>Lists (not rankings) you own: where "Add to list" can put a video.</summary>
    public static List<(string Id, string Name)> MyLists(this ClientSession s) => s.Directory()
        .Where(c => c.Kind == "list" && s.IsMine(c.Owner) && s.Genesis(c.Id)?["payload"]?["ranking"] == null).Select(c => (c.Id, c.Name)).ToList();

    /// <summary>A post on a channel (its owner) or in a group (members; with your name).</summary>
    public static async Task Post(this ClientSession s, string chain, string text, string? image = null, JsonArray? media = null)
    {
        text = text.Trim(); if (text.Length == 0) throw new ArgumentException("Write something first");
        var d = s.Chain(chain) ?? throw new InvalidOperationException("This channel hasn’t loaded yet");
        var p = new JsonObject { ["type"] = "post", ["text"] = text };
        if (image != null) p["image"] = image;
        if (media is { Count: > 0 }) p["media"] = media.DeepClone();
        if (!s.IsMine(d.Owner)) { if (!d.CanPost(s.Me.pub)) throw new InvalidOperationException("You can’t post here"); var n = await s.Social.MyName(); p["name"] = n[..Math.Min(60, n.Length)]; }
        await Append(s, chain, p);
    }
    /// <summary>Profile changes (only the fields that changed), appended by the owner.</summary>
    public static Task UpdateProfile(this ClientSession s, string chain, JsonObject changes) { changes["type"] = "profile"; return Append(s, chain, changes); }
    public static Task AddChannel(this ClientSession s, string list, string channel, string note) { var p = new JsonObject { ["type"] = "add", ["target"] = channel }; if (note.Length > 0) p["note"] = note; return Append(s, list, p); }
    public static Task AddVideo(this ClientSession s, string list, string chain, string video, string note) { var p = new JsonObject { ["type"] = "add", ["target"] = chain, ["video"] = video }; if (note.Length > 0) p["note"] = note; return Append(s, list, p); }

    public static readonly System.Text.RegularExpressions.Regex NameRe = new("^[a-z0-9](?:[a-z0-9._-]{1,28}[a-z0-9])$");
    /// <summary>The registered name (@channel or ~list) of a chain, if any.</summary>
    public static string? NameOfChain(this ClientSession s, string chain) => s.Engine.Read(e => e.Store.Ledger.NameOf.GetValueOrDefault(chain));
    public static string? NameHolder(this ClientSession s, string full) => s.Engine.Read(e => (string?)e.Store.Ledger.Names.GetValueOrDefault(full)?["chain"]);
    /// <summary>Claim (or with null, release) this chain's name: a transaction signed by the chain's key.</summary>
    public static async Task ClaimName(this ClientSession s, string chain, string? handle)
    {
        var d = s.Chain(chain) ?? throw new InvalidOperationException("This channel hasn’t loaded yet");
        var space = d.Kind == "channel" ? "@" : d.Kind == "circle" ? "@@" : "~";
        if (handle != null && (!NameRe.IsMatch(handle) || handle.Contains("..") || handle.Contains("__") || handle.Contains("--"))) throw new ArgumentException("3–30 characters: a–z, 0–9, and . _ - in the middle");
        if (handle != null && s.NameHolder(space + handle) is { } h && h != chain) throw new ArgumentException($"{space}{handle} is taken");
        await s.Engine.SubmitTx(new JsonObject { ["name"] = new JsonObject { ["space"] = space, ["chain"] = chain, ["handle"] = handle } }, s.KeyOf(d.Owner!) ?? throw new InvalidOperationException("You don’t hold this channel’s key"));
    }

    /// <summary>A list's feed: videos added one by one, and the newest paid videos of its channels; newest first.</summary>
    public static List<(string Chain, JsonObject Video)> ListFeed(this ClientSession s, string list)
    {
        var d = s.Chain(list); if (d == null) return [];
        var outp = new Dictionary<string, (string, JsonObject)>();
        foreach (var b in d.Adds)
        {
            var target = (string?)b["payload"]?["target"]; if (target == null) continue;
            var c = s.Chain(target); if (c == null) { s.Engine.Read(e => e.OpenChain(target)); continue; }
            if ((string?)b["payload"]?["video"] is { } vid) { var v = c.Videos.FirstOrDefault(x => (string?)x["hash"] == vid); if (v != null) outp[vid] = (target, v); }
            else foreach (var v in c.Videos.Where(x => s.Engine.PostPaid(x)).OrderByDescending(x => Js.Num(x["ts"]) ?? 0).Take(8)) outp[(string)v["hash"]!] = (target, v);
        }
        return outp.Values.OrderByDescending(x => Js.Num(x.Item2["ts"]) ?? 0).ToList();
    }
    /// <summary>The identity a channel's key is bound to (coins paid to the channel go there).</summary>
    public static string OwnerIdentity(this ClientSession s, string chain) { var d = s.Chain(chain); return d?.Owner == null ? "" : s.Engine.Read(e => e.Store.Ledger.Resolve(d.Owner)); }
}
