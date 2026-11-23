namespace OpenTube.Node;

/// <summary>How a node runs. Every value can come from the command line (--port=8080) or the environment (PORT=8080).</summary>
public sealed class NodeOptions
{
    /// <summary>The port for the website, peers (/ot), the tracker (/tracker) and the live hub (/live).</summary>
    public int Port { get; set; } = 8080;
    /// <summary>Where this node keeps its key, blocks and settings.</summary>
    public string DataDir { get; set; } = DefaultDataDir();
    /// <summary>The website it serves (index.html and its files). Default: the folder above, or next to the program.</summary>
    public string? SiteDir { get; set; }
    /// <summary>Other nodes to connect to (ws://host:port/ot), e.g. an entry node or a friend's IP address.</summary>
    public List<string> Peers { get; set; } = [];
    /// <summary>Proof of work for registrations: "own" (this node's own), "all" (everyone's, like a home node), "off".</summary>
    public string Mine { get; set; } = "own";
    /// <summary>The owner's grant (base64url JSON from the site's "My node"), so the node shows up as theirs.</summary>
    public string? Grant { get; set; }
    /// <summary>A name for this computer on the owner's Devices page.</summary>
    public string? Label { get; set; }
    /// <summary>
    /// A Cloudflare quick tunnel (cloudflared, downloaded if needed): "auto" when this computer can't be reached directly
    /// (behind a router, or ports 80/443 taken), "on" always, "off" never (--no-tunnel).
    /// </summary>
    public string TunnelMode { get; set; } = "auto";
    public bool Tunnel => TunnelMode != "off";
    /// <summary>
    /// Serving HTTPS itself: "auto" (when this computer is directly on the internet: &lt;ip&gt;.sslip.io with a Let's Encrypt
    /// certificate), a domain name pointing at this computer, or "off".
    /// </summary>
    public string Public { get; set; } = "auto";
    public string? PublicIp { get; set; }
    /// <summary>A named Cloudflare tunnel (your Cloudflare account) for a fixed address: --tunnel-token=… --public-url=https://your.host.</summary>
    public string? TunnelToken { get; set; }
    public string? FixedPublicUrl { get; set; }
    public string? Email { get; set; }
    public int HttpPort { get; set; } = 80;
    public int HttpsPort { get; set; } = 443;
    public string? AcmeDirectory { get; set; }
    /// <summary>Publish this node's own ranking list ("Trending on …"), like every node. --no-ranking turns it off.</summary>
    public bool Ranks { get; set; } = true;
    /// <summary>Join the network's WebRTC room (the peer mesh) like every browser. --no-mesh (or MESH=0) turns it off.</summary>
    public bool Mesh { get; set; } = true;
    /// <summary>A flash computer: the computer that started it (its node key) and its tag (set by the setup script).</summary>
    public string? FlashParent { get; set; }
    public string? FlashTag { get; set; }
    /// <summary>Announce as an entry node even without a public address (local tests; flash computers on one machine).</summary>
    public bool Entry { get; set; }
    /// <summary>Video hosting (with --grant): keep and share the videos of your joined and own channels. --no-host turns it off.</summary>
    public bool Host { get; set; } = true;
    /// <summary>Where the videos are kept (--videos=DIR, VIDEO_DIR). Default: "OpenTube Videos" in your home folder.</summary>
    public string VideoDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OpenTube Videos");
    /// <summary>Size limit per joined channel, in GB (--cap-gb=5); your own channels are never trimmed.</summary>
    public double CapGB { get; set; } = 5;
    /// <summary>Only these trackers for the peer mesh (comma-separated, for tests).</summary>
    public List<string>? MeshTrackers { get; set; }
    /// <summary>The site this node came from (--site=https://…): its /entries.json lists entry nodes to connect to.</summary>
    public string? Site { get; set; }
    /// <summary>The port OBS streams to (rtmp://this-computer:PORT/live); 0: no OBS receiver. --obs-port=1935, --no-obs.</summary>
    public int ObsPort { get; set; } = 1935;
    public string? RankName { get; set; }

    public static string DefaultDataDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".opentube", "dotnet");

    public static NodeOptions From(string[] args)
    {
        var o = new NodeOptions();
        string? Get(string name, string env)
        {
            var a = args.FirstOrDefault(x => x.StartsWith("--" + name + "=", StringComparison.Ordinal));
            return a != null ? a[(name.Length + 3)..] : Environment.GetEnvironmentVariable(env);
        }
        if (int.TryParse(Get("port", "PORT"), out var port)) o.Port = port;
        o.DataDir = Get("data", "DATA_DIR") ?? o.DataDir;
        o.SiteDir = Get("site-dir", "SITE_DIR");
        o.Peers = (Get("peers", "PEERS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(NormalizePeer).ToList();
        o.Mine = Get("mine", "MINE") ?? o.Mine;
        o.Grant = Get("grant", "NODE_GRANT");
        o.Label = Get("name", "NODE_LABEL");
        if (args.Contains("--no-ranking") || Environment.GetEnvironmentVariable("RANKER") == "0") o.Ranks = false;
        if (args.Contains("--no-host")) o.Host = false;
        o.FlashParent = Get("flash-parent", "FLASH_PARENT"); o.FlashTag = Get("flash-tag", "FLASH_TAG");
        if (args.Contains("--entry") || Environment.GetEnvironmentVariable("ENTRY") == "1") o.Entry = true;
        o.VideoDir = Get("videos", "VIDEO_DIR") ?? o.VideoDir;
        if (double.TryParse(Get("cap-gb", "CAP_GB"), System.Globalization.CultureInfo.InvariantCulture, out var cap) && cap > 0) o.CapGB = cap;
        if (args.Contains("--no-mesh") || Environment.GetEnvironmentVariable("MESH") == "0") o.Mesh = false;
        if (Get("mesh-trackers", "MESH_TRACKERS") is { Length: > 0 } mt) o.MeshTrackers = mt.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        o.RankName = Get("ranking-name", "NODE_NAME");
        o.Site = Get("site", "NODE_SITE")?.TrimEnd('/');
        if (int.TryParse(Get("obs-port", "RTMP_PORT"), out var op)) o.ObsPort = op;
        if (args.Contains("--no-obs")) o.ObsPort = 0;
        o.TunnelMode = Get("tunnel", "TUNNEL") switch { "0" or "off" or "no" => "off", "1" or "on" or "always" => "on", _ => "auto" };
        if (args.Contains("--no-tunnel")) o.TunnelMode = "off";
        if (args.Contains("--tunnel")) o.TunnelMode = "on";
        o.Public = (Get("public", "PUBLIC") ?? "auto") switch { "0" or "off" or "no" => "off", "" or "1" or "auto" => "auto", var d => d.ToLowerInvariant() };
        if (args.Contains("--no-public")) o.Public = "off";
        o.PublicIp = Get("public-ip", "PUBLIC_IP");
        o.TunnelToken = Get("tunnel-token", "TUNNEL_TOKEN"); o.FixedPublicUrl = Get("public-url", "PUBLIC_URL")?.TrimEnd('/');
        if (o.TunnelToken != null) o.TunnelMode = "on";
        o.Email = Get("email", "PUBLIC_EMAIL");
        if (int.TryParse(Get("http-port", "PUBLIC_HTTP_PORT"), out var hp)) o.HttpPort = hp;
        if (int.TryParse(Get("https-port", "PUBLIC_HTTPS_PORT"), out var hsp)) o.HttpsPort = hsp;
        o.AcmeDirectory = Get("acme-directory", "ACME_DIRECTORY");
        return o;
    }
    /// <summary>"1.2.3.4", "1.2.3.4:8080", "https://x.example" or "ws://x/ot" — all become a WebSocket peer address.</summary>
    public static string NormalizePeer(string p)
    {
        p = p.Trim().TrimEnd('/');
        if (!p.Contains("://")) p = "ws://" + (p.Contains(':') ? p : p + ":8080");
        p = p.Replace("https://", "wss://").Replace("http://", "ws://");
        return p.EndsWith("/ot", StringComparison.Ordinal) ? p : p + "/ot";
    }
}
