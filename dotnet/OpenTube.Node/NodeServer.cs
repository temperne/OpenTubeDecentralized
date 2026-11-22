using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace OpenTube.Node;

/// <summary>
/// The node on the network: serves the website (the same page as the site, so any node is a way in for browsers),
/// the node protocol at /ot, a tracker at /tracker, a live hub at /live, /status and /entries.json. Starts a
/// Cloudflare quick tunnel when cloudflared is installed, so the node has a public https address with no setup.
/// </summary>
public sealed partial class NodeServer : IAsyncDisposable
{
    public OpenTubeNode Node { get; }
    WebApplication? app;
    Process? tunnel;

    public NodeServer(NodeOptions o) => Node = new OpenTubeNode(o);

    public async Task StartAsync()
    {
        await Node.StartAsync();
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        b.WebHost.UseUrls($"http://0.0.0.0:{Node.Options.Port}");
        app = b.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers.AccessControlAllowOrigin = "*";
            var path = ctx.Request.Path.Value;
            if (ctx.WebSockets.IsWebSocketRequest && path is "/ot" or "/tracker" or "/live")
            {
                var ws = await ctx.WebSockets.AcceptWebSocketAsync();
                await Node.AcceptAsync(ws, path == "/ot" ? "node-in" : path[1..], ctx.Connection.RemoteIpAddress?.ToString() ?? "?");
                return;
            }
            await next();
        });
        app.MapGet("/status", () => Results.Text(Node.Status.ToJsonString(), "application/json"));
        app.MapGet("/entries.json", async (HttpContext c) => { c.Response.Headers.CacheControl = "max-age=60"; return Results.Text(await Node.EntriesJsonAsync() ?? "{}", "application/json"); });
        // the website: index.html and its files, straight from the site folder
        var files = new PhysicalFileProvider(Node.SiteDir);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ServeUnknownFileTypes = false, OnPrepareResponse = r => r.Context.Response.Headers.CacheControl = "no-cache" });
        await app.StartAsync();
        Node.Log($"website and node on http://localhost:{Node.Options.Port} (peers: ws://<this computer's address>:{Node.Options.Port}/ot)");
        if (Node.Options.Tunnel) StartTunnel();
    }

    // ---- Cloudflare quick tunnel: a public https address, no account needed (it changes if cloudflared restarts)
    void StartTunnel()
    {
        var exe = FindOnPath(OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared");
        if (exe == null) { Node.Log("cloudflared isn't installed, so this node has no public address (install it for one: https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/). Others can still connect by IP: --peers=<ip>:<port>."); return; }
        try
        {
            tunnel = Process.Start(new ProcessStartInfo(exe, $"tunnel --no-autoupdate --url http://localhost:{Node.Options.Port}") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false });
            tunnel!.ErrorDataReceived += (_, e) =>
            {
                var m = e.Data == null ? null : TunnelUrl().Match(e.Data);
                if (m is { Success: true } && Node.PublicUrl == null) { Node.PublicUrl = m.Value; Node.Log($"public address: {m.Value} (a Cloudflare tunnel; browsers anywhere can open it)"); }
            };
            tunnel.BeginErrorReadLine(); tunnel.BeginOutputReadLine();
            Node.Log("starting a Cloudflare tunnel…");
        }
        catch (Exception e) { Node.Log("couldn't start cloudflared: " + e.Message); }
    }
    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com")] private static partial Regex TunnelUrl();

    static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists);

    public async ValueTask DisposeAsync()
    {
        try { if (tunnel is { HasExited: false }) tunnel.Kill(); } catch { }
        if (app != null) await app.StopAsync();
        await Node.DisposeAsync();
    }
}
