using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using OpenTube.Client;
using OpenTube.Core;
using OpenTube.UI;
using OpenTube.Web;

// OpenTube's website in C# (WebAssembly): the network's rules, chains, the coin and gossip run here in C#
// (OpenTube.Core and OpenTube.Client, the same code as the computers' node); the pages are OpenTube.UI, shared with
// the phone app. JavaScript is only the thin bindings to the browser (wwwroot/opentube.js).
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddSingleton<IClientStore, BrowserStore>();
builder.Services.AddSingleton(sp =>
{
    var site = new Uri(builder.HostEnvironment.BaseAddress).GetLeftPart(UriPartial.Authority);   // the site (and its node, when it has one)
    return new ClientSession(sp.GetRequiredService<IClientStore>(), site) { Log = m => Console.WriteLine("[opentube] " + m) };
});
builder.Services.AddSingleton<IVideoPlayer, BrowserVideoPlayer>();
var host = builder.Build();
await host.Services.GetRequiredService<ClientSession>().Start();
await host.RunAsync();

/// <summary>The browser's IndexedDB, the same database the JavaScript page used ("opentube-chain").</summary>
sealed class BrowserStore(IJSRuntime js) : IClientStore
{
    public async Task<string?> Get(string key) => await js.InvokeAsync<string?>("otStore.get", key);
    public async Task Set(string key, string value) => await js.InvokeVoidAsync("otStore.set", key, value);
    public async Task<List<(string Chain, JsonObject Block)>> LoadBlocks()
    {
        var json = await js.InvokeAsync<string>("otStore.blocks");
        return (JsonNode.Parse(json)?.AsArray() ?? []).Where(r => r?["chainId"] != null && r["block"] is JsonObject).Select(r => ((string)r!["chainId"]!, (JsonObject)r["block"]!.DeepClone())).ToList();
    }
    public void SaveBlock(string chain, JsonObject block) => _ = js.InvokeVoidAsync("otStore.put", chain, block.ToJsonString()).AsTask();
}

/// <summary>Plays a video: BitTorrent over WebRTC into the page's video element (binding: otPlayer in opentube.js).</summary>
sealed class BrowserVideoPlayer(IJSRuntime js, ClientSession s) : IVideoPlayer
{
    public async Task<Playback> Play(string elementId, string infoHash, string file, double size, Action<string> status)
    {
        var trackers = JsonNode.Parse(Network.MainGenesisJson)?["payload"]?["trackers"]?.ToJsonString() ?? "[]";
        var site = s.Site.Replace("https://", "wss://").Replace("http://", "ws://") + "/tracker";
        return new Playback(null, await js.InvokeAsync<string?>("otPlayer.play", elementId, infoHash, file, $"[{System.Text.Json.JsonSerializer.Serialize(site)},{trackers.Trim('[', ']')}]"));
    }
}
