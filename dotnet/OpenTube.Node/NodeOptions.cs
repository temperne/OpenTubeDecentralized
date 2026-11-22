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
    /// <summary>Start a Cloudflare quick tunnel (cloudflared) so the node is reachable from the internet without setup.</summary>
    public bool Tunnel { get; set; } = true;
    /// <summary>Publish this node's own ranking list ("Trending on …"), like every node. --no-ranking turns it off.</summary>
    public bool Ranks { get; set; } = true;
    /// <summary>The site this node came from (--site=https://…): its /entries.json lists entry nodes to connect to.</summary>
    public string? Site { get; set; }
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
        o.RankName = Get("ranking-name", "NODE_NAME");
        o.Site = Get("site", "NODE_SITE")?.TrimEnd('/');
        if (args.Contains("--no-tunnel") || Environment.GetEnvironmentVariable("TUNNEL") == "0") o.Tunnel = false;
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
