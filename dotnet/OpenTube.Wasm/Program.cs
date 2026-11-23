using System.Runtime.InteropServices.JavaScript;
using System.Text.Json.Nodes;
using OpenTube.Core;

/// <summary>What the page can call: the rules ported to C#, taking and returning JSON text.</summary>
public static partial class OpenTubeWasm
{
    public static void Main() { }

    [JSExport] public static bool CheckSigned(string blockJson, double nowMs) => Rules.CheckSigned(JsonNode.Parse(blockJson), (long)nowMs);
    [JSExport] public static bool ValidGenesis(string genesisJson) => Rules.ValidGenesis(JsonNode.Parse(genesisJson));
    [JSExport] public static string Canon(string json) => Blocks.Canon(JsonNode.Parse(json));
    [JSExport] public static string HashBlock(string blockJson) => Blocks.Hash(JsonNode.Parse(blockJson)!.AsObject());
    /// <summary>Many blocks at once (one call, so the boundary between JavaScript and WebAssembly is crossed once).</summary>
    [JSExport] public static string CheckSignedMany(string blocksJson, double nowMs) =>
        new JsonArray(JsonNode.Parse(blocksJson)!.AsArray().Select(b => (JsonNode?)Rules.CheckSigned(b, (long)nowMs)).ToArray()).ToJsonString();
}
