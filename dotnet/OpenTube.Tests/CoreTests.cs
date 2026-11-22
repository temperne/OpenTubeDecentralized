using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Tests;

// Vectors come from the JavaScript core (index.html), so these check that C# agrees with browsers byte for byte.
public class CoreTests
{
    static readonly string Repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
    static readonly JsonObject V = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../data/vectors.json")))!.AsObject();
    static string Page => File.ReadAllText(Path.Combine(Repo, "index.html"));

    [Fact]
    public void Canonical_json_matches_javascript() =>
        Assert.Equal((string)V["canon"]!, Blocks.Canon(JsonNode.Parse("""{"b":[1,"x\u2028\"",{"z":null,"a":true}],"a":-5.5}""")));

    [Fact]
    public void Signature_from_javascript_verifies_in_csharp()
    {
        var id = V["id"]!; var pub = B64u.Decode((string)id["pub"]!);
        Assert.True(Ed25519.Verify(pub, B64u.Decode((string)V["sig"]!), "hello"u8.ToArray()));
        Assert.False(Ed25519.Verify(pub, B64u.Decode((string)V["sig"]!), "hellO"u8.ToArray()));
        // and the same private key signs identically (Ed25519 is deterministic)
        Assert.Equal((string)V["sig"]!, new Identity((string)id["pub"]!, (string)id["priv"]!).Sign("hello"));
        Assert.Equal((string)id["pub"]!, B64u.Encode(Ed25519.PublicOf(B64u.Decode((string)id["priv"]!))));
    }

    [Fact]
    public async Task Javascript_core_runs_in_dotnet_and_agrees()
    {
        using var core = new JsCore(); core.LoadFromPage(Page);
        // the network ID: the genesis hash, computed by the JS core on .NET's crypto, and natively
        var main = JsonSerializer.Deserialize<string>((await core.RunAsync("OpenTubeCore.hashBlock(OpenTubeCore.MAIN_GENESIS)"))!);
        Assert.Equal((string)V["main"]!, main);
        var genesis = JsonNode.Parse((await core.RunAsync("Promise.resolve(OpenTubeCore.MAIN_GENESIS)"))!)!.AsObject();
        Assert.Equal((string)V["main"]!, Blocks.Hash(genesis));
        // a signature made in .NET by the JS core verifies natively, and the other way round
        var id = Identity.New();
        var sig = JsonSerializer.Deserialize<string>((await core.RunAsync($"(async () => (await OpenTubeCore.signer({JsonSerializer.Serialize(id)})).sign('abc'))()"))!)!;
        Assert.True(Ed25519.Verify(B64u.Decode(id.pub), B64u.Decode(sig), "abc"u8.ToArray()));
        Assert.Equal("true", await core.RunAsync($"OpenTubeCore.verify('{id.pub}', 'xyz', '{id.Sign("xyz")}')"));
    }

    [Fact]
    public async Task Native_mining_meets_the_difficulty()
    {
        using var core = new JsCore(); core.LoadFromPage(Page);
        var json = await core.RunAsync("(async () => { const b = { v: 1, chain: null, parents: [], height: 1, ts: 1, author: null, payload: { type: 'x' } }; b.nonce = await __mine(b, 16).promise; b.hash = await OpenTubeCore.hashBlock(b); return { b, z: OpenTubeCore.zeroBits(b.hash) }; })()");
        var r = JsonNode.Parse(json!)!;
        Assert.True((int)r["z"]! >= 16);
        Assert.Equal((string)r["b"]!["hash"]!, Blocks.Hash(r["b"]!.AsObject()));
    }
}
