using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OpenTube.Core;

/// <summary>
/// The network: its first block (MAIN_GENESIS in the JavaScript core — its hash is the network ID; changing anything
/// in it makes a different network). OpenTube.Tests checks this copy hashes to the same ID the JavaScript computes.
/// </summary>
public static partial class Network
{
    public const string MainGenesisJson = """
{"v":1,"chain":null,"parents":[],"height":0,"ts":1794960000000,"author":null,"nonce":0,"payload":{"type":"genesis","kind":"main","name":"OpenTube Main Chain","difficulty":18,"commentDifficulty":10,"maxEntries":16,"trackers":["wss://tracker.openwebtorrent.com","wss://tracker.webtorrent.dev","wss://tracker.btorrent.xyz","wss://tracker.files.fm:7073/announce"]}}
""";
    public static JsonObject MainGenesis() => JsonNode.Parse(MainGenesisJson)!.AsObject();
    public static string MainId { get; } = Blocks.Hash(MainGenesis());
    /// <summary>The network's home domain and site; the Cloudflare address serves until the domain's DNS is live.</summary>
    public const string Domain = "opentuber.com";
    public const string SiteUrl = "https://" + Domain;
    public const string FallbackSite = "https://opentubedecentralized.jasonajensen.workers.dev";
    /// <summary>The core's release name (VERSION), read from the page.</summary>
    public static string VersionOf(string html) => VersionRe().Match(html) is { Success: true } m ? m.Groups[1].Value : "?";
    [GeneratedRegex("const VERSION = '([^']+)'")] private static partial Regex VersionRe();
}
