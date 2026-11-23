using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace OpenTube.Node;

/// <summary>
/// The node on the network: serves the website (the same page as the site, so any node is a way in for browsers),
/// the node protocol at /ot, a tracker at /tracker, a live hub at /live, /status and /entries.json. Starts a
/// public https address with no setup: its own (Let's Encrypt, as &lt;ip&gt;.sslip.io) when it's directly on the internet,
/// otherwise a Cloudflare quick tunnel (see <see cref="Internet"/>).
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
        LoadSaved();
        Node.ReachInfo = ReachJson;
        Node.SetPublic = SetPublic;
        await StartWeb();
    }

    // ---- how this node is reachable, shown in the window, the terminal UI and on the site (Devices) ----
    /// <summary>"direct" (its own HTTPS), "tunnel", "checking", or "none"; Why: the reason when it isn't direct.</summary>
    public string ReachMode { get; private set; } = "checking";
    public string? ReachWhy { get; private set; }
    public string? TunnelError { get; private set; }
    string? tunnelUrl;
    /// <summary>The setting, as the site and UIs offer it: "auto" (own HTTPS when possible, else a tunnel), "tunnel" (a tunnel always, too), "off".</summary>
    public string PublicSetting => Node.Options.Public == "off" && !Node.Options.Tunnel ? "off" : Node.Options.TunnelMode == "on" ? "tunnel" : "auto";
    public string Describe() => ReachMode switch
    {
        "direct" => $"public at {Node.PublicUrl}: its own HTTPS (directly on the internet)" + (tunnelUrl != null ? $", and a tunnel at {tunnelUrl}" : ""),
        "tunnel" when tunnelUrl != null => $"public at {tunnelUrl}: a Cloudflare tunnel" + (ReachWhy != null ? $" ({ReachWhy})" : ""),
        "tunnel" => "starting a Cloudflare tunnel…" + (TunnelError != null ? $" (last try: {TunnelError})" : "") + (ReachWhy != null ? $" ({ReachWhy})" : ""),
        "none" => "not public" + (ReachWhy != null ? $": {ReachWhy}" : "") + $". Others can connect by IP, port {Node.Options.Port}",
        _ => "checking how this computer is on the internet…",
    };
    System.Text.Json.Nodes.JsonObject ReachJson() => new()
    {
        ["mode"] = ReachMode, ["why"] = ReachWhy, ["setting"] = PublicSetting, ["tunnelUrl"] = tunnelUrl, ["tunnelError"] = TunnelError, ["text"] = Describe(),
    };
    string SavedFile => Path.Combine(Node.Options.DataDir, "public.json");
    void LoadSaved()
    {
        try
        {
            if (!File.Exists(SavedFile)) return;
            var j = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SavedFile));
            Apply((string?)j?["setting"] ?? "auto");
            Node.Log($"[internet] public setting from {SavedFile}: {PublicSetting}");
        }
        catch (Exception e) { Node.Log("[internet] couldn't read the saved public setting: " + e.Message); }
    }
    void Apply(string setting)
    {
        var o = Node.Options;
        switch (setting)
        {
            case "off": o.Public = "off"; o.TunnelMode = "off"; break;
            case "tunnel": if (o.Public == "off") o.Public = "auto"; o.TunnelMode = "on"; break;
            default: if (o.Public == "off") o.Public = "auto"; o.TunnelMode = "auto"; break;
        }
    }
    readonly SemaphoreSlim webLock = new(1, 1);
    /// <summary>Change the public setting (from the site, the window or the terminal UI): saved, and applied now (the web side restarts; peers reconnect).</summary>
    public async Task SetPublic(string setting)
    {
        if (setting is not ("auto" or "tunnel" or "off")) throw new ArgumentException("setting: auto, tunnel or off");
        await webLock.WaitAsync();
        try
        {
            Apply(setting); ReachMode = "checking"; ReachWhy = null;
            Directory.CreateDirectory(Node.Options.DataDir);
            File.WriteAllText(SavedFile, new System.Text.Json.Nodes.JsonObject { ["setting"] = setting }.ToJsonString());
            Node.Log($"[internet] public setting: {setting}; applying it");
            await StopWeb();
            await StartWebLocked();
            Node.AnnounceSoon();
        }
        finally { webLock.Release(); }
    }
    async Task StopWeb()
    {
        stopping = true; renew?.Dispose(); renew = null;
        try { if (tunnel is { HasExited: false }) tunnel.Kill(); } catch { }
        if (tunnelTask != null) try { await tunnelTask; } catch { }
        tunnel = null; tunnelTask = null; tunnelUrl = null; TunnelError = null;
        if (app != null) { await app.StopAsync(); await app.DisposeAsync(); app = null; }
        Node.PublicUrl = null; acme = null; cert = null;
        stopping = false;
    }
    Task? tunnelTask;
    async Task StartWeb() { await webLock.WaitAsync(); try { await StartWebLocked(); } finally { webLock.Release(); } }
    async Task StartWebLocked()
    {
        ReachMode = "checking"; ReachWhy = null;
        var direct = await PlanDirect();
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.SetMinimumLevel(LogLevel.Warning);
        b.WebHost.UseShutdownTimeout(TimeSpan.FromSeconds(2));   // switching public access: don't wait on open connections (peers reconnect)
        b.WebHost.ConfigureKestrel(k =>
        {
            k.ListenAnyIP(Node.Options.Port);
            if (direct == null) return;
            k.ListenAnyIP(Node.Options.HttpPort);
            k.ListenAnyIP(Node.Options.HttpsPort, l => l.UseHttps(h => h.ServerCertificateSelector = (_, _) => cert));
        });
        app = b.Build();
        if (acme != null)   // Let's Encrypt's check (port 80)
            app.Use(async (ctx, next) =>
            {
                var p = ctx.Request.Path.Value ?? "";
                if (p.StartsWith("/.well-known/acme-challenge/", StringComparison.Ordinal))
                {
                    if (acme.Tokens.TryGetValue(p[28..], out var ka)) { ctx.Response.ContentType = "text/plain"; await ctx.Response.WriteAsync(ka); }
                    else ctx.Response.StatusCode = 404;
                    return;
                }
                await next();
            });
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
        // the website, exactly what the site serves: the page (also for /@channel and /~list links), the service worker,
        // the seed list, and the node programs (so a new node can be installed from any node, not only the site)
        app.MapGet("/", () => Page());
        app.MapGet("/index.html", () => Page());
        app.MapGet("/{who}", (string who) => ChannelLink().IsMatch(who) ? Page() : Results.NotFound());   // /@channel and /~list links
        foreach (var (file, type) in SiteFiles)
            app.MapGet("/" + file, () => File.Exists(Path.Combine(Node.SiteDir, file))
                ? Results.File(Path.Combine(Node.SiteDir, file), type) : Results.NotFound());
        await app.StartAsync();
        Node.Log($"website and node on http://localhost:{Node.Options.Port} (peers: ws://<this computer's address>:{Node.Options.Port}/ot)");
        if (direct != null && await GoDirect(direct)) { ReachMode = "direct"; if (Node.Options.TunnelMode == "on") tunnelTask = StartTunnel(); }
        else if (Node.Options.Tunnel) { ReachMode = "tunnel"; tunnelTask = StartTunnel(); }
        else
        {
            ReachMode = "none"; ReachWhy ??= "public access is off";
            Node.Log("no public address (tunnel off): others can connect by IP: --peers=<this computer's IP>:" + Node.Options.Port);
        }
    }
    void Why(string why) { ReachWhy ??= why; }

    // ---- reachable directly: our own HTTPS ----
    Acme? acme; System.Security.Cryptography.X509Certificates.X509Certificate2? cert; Timer? renew;

    /// <summary>The name to serve HTTPS as, if this computer is directly on the internet and ports 80/443 are ours; else null (with why).</summary>
    async Task<string?> PlanDirect()
    {
        var o = Node.Options;
        if (o.Public == "off") { Why("its own HTTPS is off"); return null; }
        string host; var domain = o.Public != "auto";
        if (domain) host = o.Public;
        else
        {
            var ip = o.PublicIp != null && System.Net.IPAddress.TryParse(o.PublicIp, out var given) ? given : await Internet.PublicIPv4();
            if (ip == null) { Node.Log("[internet] couldn't find this computer's public IP (no internet?)"); Why("couldn't find this computer's public IP"); return null; }
            host = Internet.SslipName(ip);
            if (Internet.IsLocal(ip)) Node.Log($"[internet] directly on the internet as {ip} (no router in between)");
            else
            {
                var p80 = Internet.Port(o.HttpPort);
                if (p80 != Internet.PortState.Free || !await Internet.Reachable(ip, o.HttpPort))
                {
                    Node.Log($"[internet] behind a router (public IP {ip}, not on this computer, and port {o.HttpPort} isn't forwarded here): using a tunnel");
                    Why($"behind a router: port {o.HttpPort} isn't reachable from the internet"); return null;
                }
                Node.Log($"[internet] reachable as {ip} (ports forwarded to this computer)");
            }
        }
        foreach (var port in new[] { o.HttpPort, o.HttpsPort })
        {
            var st = Internet.Port(port);
            if (st != Internet.PortState.Free) { Why(Internet.Explain(port, st)); Node.Log($"[https] can't serve https://{host} itself: {Internet.Explain(port, st)}. Using a tunnel instead" + (st == Internet.PortState.InUse ? $" (or have that program pass {host} to http://localhost:{o.Port})" : "")); return null; }
        }
        acme = new Acme(o.DataDir, Node.Log, o.AcmeDirectory);
        return host;
    }
    async Task<bool> GoDirect(string host)
    {
        cert = acme!.Load(host);
        if (Acme.NeedsRenewal(cert))
        {
            Node.Log($"[https] getting a certificate for {host} from Let's Encrypt…");
            try { cert = await acme.Issue(host, Node.Options.Email); }
            catch (Exception e)
            {
                Node.Log($"[https] couldn't get a certificate: {e.Message}");
                if (cert == null) { Node.Log("[https] using a tunnel instead"); Why("no certificate: " + e.Message); return false; }
            }
        }
        var port = Node.Options.HttpsPort == 443 ? "" : ":" + Node.Options.HttpsPort;
        Node.PublicUrl = $"https://{host}{port}";
        Node.Log($"public address: {Node.PublicUrl} (this computer, directly; browsers anywhere can open it)");
        renew = new Timer(async _ =>
        {
            if (!Acme.NeedsRenewal(cert)) return;
            try { cert = await acme.Issue(host, Node.Options.Email); } catch (Exception e) { Node.Log($"[https] renewing the certificate failed: {e.Message}"); }
        }, null, TimeSpan.FromHours(12), TimeSpan.FromHours(12));
        return true;
    }

    IResult Page() => Results.Text(Node.Html, "text/html; charset=utf-8");
    /// <summary>The files a node serves besides the page — the same set the Cloudflare site publishes.</summary>
    public static readonly (string File, string Type)[] SiteFiles =
    [
        ("sw.js", "text/javascript"), ("seeds.json", "application/json"),
        ("opentube.mjs", "text/javascript"), ("server.mjs", "text/javascript"), ("host.mjs", "text/javascript"),
        ("rtmp.mjs", "text/javascript"), ("tls.mjs", "text/javascript"), ("headless.mjs", "text/javascript"),
        ("opentube-dotnet.sh", "text/plain; charset=utf-8"), ("opentube-dotnet.ps1", "text/plain; charset=utf-8"),
    ];

    // ---- Cloudflare quick tunnel: a public https address, no account needed (it changes if cloudflared restarts)
    bool stopping;
    async Task StartTunnel()
    {
        var exe = await Internet.Cloudflared(Node.Options.DataDir, Node.Log);
        if (exe == null) { TunnelError = "cloudflared couldn't be downloaded"; if (ReachMode == "tunnel") ReachMode = "none"; Node.Log("no public address: others can still connect by IP: --peers=<ip>:" + Node.Options.Port); return; }
        for (int fails = 0; !stopping; )
        {
            var t0 = DateTime.UtcNow; string? url = null, lastError = null;
            try
            {
                tunnel = Process.Start(new ProcessStartInfo(exe, $"tunnel --no-autoupdate --url http://localhost:{Node.Options.Port}") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false });
                tunnel!.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data) && (LogLevelRe().IsMatch(e.Data) || !e.Data.Contains(" INF "))) lastError = e.Data;
                    var m = e.Data == null ? null : TunnelUrl().Match(e.Data);
                    if (m is { Success: true } && url == null)
                    {
                        url = m.Value; tunnelUrl = url; TunnelError = null; Node.AnnounceSoon();
                        if (Node.PublicUrl == null || Node.PublicUrl.Contains("trycloudflare.com")) Node.PublicUrl = url;
                        Node.Log($"public address: {url} (a Cloudflare tunnel; browsers anywhere can open it)");
                    }
                };
                tunnel.BeginErrorReadLine(); tunnel.BeginOutputReadLine();
                Node.Log("starting a Cloudflare tunnel…");
                await tunnel.WaitForExitAsync();
            }
            catch (Exception e) { Node.Log("couldn't start cloudflared: " + e.Message); }
            if (stopping) return;
            if (url != null && Node.PublicUrl == url) Node.PublicUrl = null;
            tunnelUrl = null;
            fails = DateTime.UtcNow - t0 > TimeSpan.FromMinutes(2) ? 0 : fails + 1;
            if (lastError != null) { TunnelError = ErrPrefix().Replace(lastError, "").Trim(); Node.Log("cloudflared: " + TunnelError); }
            Node.Log($"the Cloudflare tunnel stopped; starting it again{(fails > 1 ? $" in {Math.Min(300, 10 * fails)} s" : "")}");
            for (int w = 0; w < (fails > 1 ? Math.Min(300, 10 * fails) : 2) * 4 && !stopping; w++) await Task.Delay(250);
        }
    }
    [GeneratedRegex(@"https://(?!api\.)[a-z0-9]+(-[a-z0-9]+)+\.trycloudflare\.com")] private static partial Regex TunnelUrl();   // (a quick tunnel's name is words-with-dashes; not api.trycloudflare.com in its errors)
    [GeneratedRegex(@"\s(ERR|FTL)\s")] private static partial Regex LogLevelRe();
    [GeneratedRegex(@"^\S+\s+(ERR|FTL)\s+")] private static partial Regex ErrPrefix();
    [GeneratedRegex(@"^[@~][A-Za-z0-9._-]{3,30}/?$")] private static partial Regex ChannelLink();

    public async ValueTask DisposeAsync()
    {
        await StopWeb(); stopping = true;
        await Node.DisposeAsync();
    }
}
