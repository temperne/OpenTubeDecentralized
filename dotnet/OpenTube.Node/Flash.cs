using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Node;

/// <summary>
/// Flash computers (as the JavaScript node): a cloud server started for a stream (it does the heavy work: combining
/// cameras, serving viewers). The owner and the people the computer is shared with attach their own cloud login and
/// start servers on their own account; each runs as that person's computer (their grant) with the .NET node. This
/// computer keeps a copy of every recording a flash computer makes, and deletes the server once it's idle with every
/// recording copied here (or at its time limit, or when it never came online / stopped answering). With scaling on, it
/// starts one per N viewers. Every 10 minutes it deletes leftovers (servers it lost track of, deletes that didn't take).
/// Cloud logins come sealed from the site to this computer's encryption key (P-256 ECDH + AES-256-GCM): peers and
/// tunnels passing the message see only ciphertext. Kept in DATA/flash.json (the JavaScript node's format).
/// </summary>
public sealed class Flash
{
    readonly OpenTubeNode node; readonly string file; readonly JsonObject store; readonly ECDiffieHellman enc;
    readonly Dictionary<string, Process> local = [];
    readonly object gate = new();
    public string EncPub { get; }
    static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    static readonly string[] OwnerOnly = ["custom", "local"];
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    JsonObject Users => (JsonObject)store["users"]!;
    JsonArray Children => (JsonArray)store["children"]!;
    public int People => Users.Count;
    void Log(string m) => node.Log("[flash] " + m);

    public Flash(OpenTubeNode node)
    {
        this.node = node; file = Path.Combine(node.Options.DataDir, "flash.json");
        store = (File.Exists(file) ? Try(() => JsonNode.Parse(File.ReadAllText(file)) as JsonObject) : null) ?? [];
        store["users"] ??= new JsonObject(); store["children"] ??= new JsonArray();
        // the encryption key for sealed cloud logins (enc.json, as the JavaScript node: the private scalar, base64url)
        var ef = Path.Combine(node.Options.DataDir, "enc.json");
        enc = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try { var d = B64u.Decode((string)JsonNode.Parse(File.ReadAllText(ef))!["d"]!); enc.ImportParameters(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d, Q = PubFromD(d) }); }
        catch { var p = enc.ExportParameters(true); File.WriteAllText(ef, new JsonObject { ["d"] = B64u.Encode(p.D!) }.ToJsonString()); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ef, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        var q = enc.ExportParameters(false).Q; EncPub = B64u.Encode([4, .. q.X!, .. q.Y!]);
        // the owner's login from the command line (--flash-provider=… --flash-token=…)
        if (node.OwnerUser != null && Environment.GetEnvironmentVariable("FLASH_PROVIDER") is { Length: > 0 } fp)
        {
            var u = Users[node.OwnerUser] as JsonObject ?? []; Users[node.OwnerUser] = u; u["provider"] = fp;
            foreach (var (k, e) in new[] { ("token", "FLASH_TOKEN"), ("region", "FLASH_REGION"), ("plan", "FLASH_PLAN"), ("image", "FLASH_IMAGE"), ("createCmd", "FLASH_CREATE_CMD"), ("deleteCmd", "FLASH_DELETE_CMD") })
                if (Environment.GetEnvironmentVariable(e) is { Length: > 0 } v) u[k] = v;
            if (double.TryParse(Environment.GetEnvironmentVariable("FLASH_MAX_HOURS"), out var mh)) u["maxHours"] = mh;
            if (double.TryParse(Environment.GetEnvironmentVariable("FLASH_IDLE_MIN"), out var im)) u["idleMin"] = im;
        }
        Save();
    }
    static ECPoint PubFromD(byte[] d) { using var e = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d }); return e.ExportParameters(false).Q; }
    static T? Try<T>(Func<T> f) { try { return f(); } catch { return default; } }
    void Save() { lock (gate) try { File.WriteAllText(file, store.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { } }

    /// <summary>{ epk, iv, ct } (base64url; ct ends with the 16-byte GCM tag), sealed in a browser to this computer's key → the JSON inside.</summary>
    public JsonObject Unseal(JsonObject s)
    {
        var epk = B64u.Decode((string)s["epk"]!);
        using var their = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = epk[1..33], Y = epk[33..65] } });
        var shared = enc.DeriveRawSecretAgreement(their.PublicKey);
        var key = SHA256.HashData([.. shared, .. Encoding.UTF8.GetBytes("opentube-seal-v1")]);
        var iv = B64u.Decode((string)s["iv"]!); var ct = B64u.Decode((string)s["ct"]!);
        using var gcm = new AesGcm(key, 16); var plain = new byte[ct.Length - 16];
        gcm.Decrypt(iv, ct.AsSpan(0, ct.Length - 16), ct.AsSpan(ct.Length - 16), plain);
        return (JsonObject)JsonNode.Parse(plain)!;
    }

    // ---- providers: create(cfg, name, setup script, tag, grant) -> (id, ip), remove(cfg, id), list (some)
    static readonly Dictionary<string, (string Billing, Dictionary<string, string> Defaults)> Providers = new()
    {
        ["hetzner"] = ("hour", new() { ["region"] = "ash", ["plan"] = "cpx21", ["image"] = "ubuntu-24.04" }),
        ["digitalocean"] = ("second", new() { ["region"] = "nyc3", ["plan"] = "s-2vcpu-4gb", ["image"] = "ubuntu-24-04-x64" }),
        ["vultr"] = ("hour", new() { ["region"] = "ewr", ["plan"] = "vhf-2c-4gb", ["image"] = "2284" }),
        ["aws"] = ("second", new() { ["region"] = "us-east-1", ["plan"] = "c7a.large", ["image"] = "" }),
        ["gcp"] = ("second", new() { ["region"] = "us-central1-a", ["plan"] = "e2-standard-2", ["image"] = "projects/ubuntu-os-cloud/global/images/family/ubuntu-2404-lts-amd64" }),
        ["linode"] = ("hour", new() { ["region"] = "us-ord", ["plan"] = "g6-standard-2", ["image"] = "linode/ubuntu24.04" }),
        ["scaleway"] = ("hour", new() { ["region"] = "fr-par-1", ["plan"] = "PLAY2-MICRO", ["image"] = "ubuntu_noble" }),
        ["custom"] = ("hour", new()),
        ["local"] = ("second", new()),
    };
    static string? Opt(JsonObject c, string k) => (string?)c[k] is { Length: > 0 } v ? v : Providers.TryGetValue((string?)c["provider"] ?? "", out var p) ? p.Defaults.GetValueOrDefault(k) : null;
    static Dictionary<string, string> Bearer(JsonObject c) => new() { ["Authorization"] = "Bearer " + (string?)c["token"] };

    static async Task<JsonNode?> Fetch(string method, string url, object? body, Dictionary<string, string>? headers = null)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        foreach (var (k, v) in headers ?? []) if (!k.Equals("content-type", StringComparison.OrdinalIgnoreCase)) req.Headers.TryAddWithoutValidation(k, v);
        if (body is string sb) req.Content = new StringContent(sb, Encoding.UTF8, headers?.GetValueOrDefault("content-type") ?? "text/plain");
        else if (body is JsonNode jb) req.Content = new StringContent(jb.ToJsonString(), Encoding.UTF8, "application/json");
        using var r = await http.SendAsync(req); var text = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Exception($"{new Uri(url).Host} answered {(int)r.StatusCode}: {text[..Math.Min(200, text.Length)]}");
        try { return text.Length > 0 ? JsonNode.Parse(text) : new JsonObject(); } catch { return JsonValue.Create(text); }
    }
    /// <summary>AWS Signature Version 4 (token: ACCESS_KEY_ID:SECRET_ACCESS_KEY).</summary>
    static async Task<string> AwsCall(JsonObject c, string service, string region, string body, Dictionary<string, string> headers)
    {
        var parts = ((string?)c["token"] ?? "").Split(':'); if (parts.Length < 2) throw new Exception("the AWS key is ACCESS_KEY_ID:SECRET_ACCESS_KEY");
        var (ak, sk) = (parts[0], parts[1]); var host = $"{service}.{region}.amazonaws.com";
        var amzDate = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'"); var day = amzDate[..8];
        string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
        static byte[] Hmac(byte[] k, string s) => HMACSHA256.HashData(k, Encoding.UTF8.GetBytes(s));
        var hd = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["host"] = host, ["x-amz-date"] = amzDate };
        foreach (var (k, v) in headers) hd[k.ToLowerInvariant()] = v;
        var signed = string.Join(';', hd.Keys);
        var canonical = string.Join('\n', "POST", "/", "", string.Concat(hd.Select(kv => $"{kv.Key}:{kv.Value.Trim()}\n")), signed, Sha(body));
        var scope = $"{day}/{region}/{service}/aws4_request"; var toSign = string.Join('\n', "AWS4-HMAC-SHA256", amzDate, scope, Sha(canonical));
        var sig = Convert.ToHexStringLower(Hmac(Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + sk), day), region), service), "aws4_request"), toSign));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/") { Content = new StringContent(body, Encoding.UTF8) };
        req.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(hd["content-type"]);
        foreach (var (k, v) in hd) if (k is not ("host" or "content-type")) req.Headers.TryAddWithoutValidation(k, v);
        req.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential={ak}/{scope}, SignedHeaders={signed}, Signature={sig}");
        using var r = await http.SendAsync(req); var text = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Exception($"AWS {service} answered {(int)r.StatusCode}: {text[..Math.Min(300, text.Length)]}");
        return text;
    }
    readonly Dictionary<string, (string Project, string Auth, double Exp)> gcpTok = [];
    /// <summary>Google: a service account's JSON key → an OAuth access token (an hour), per login.</summary>
    async Task<(string Project, string Auth)> GcpAuth(JsonObject c)
    {
        var raw = (string?)c["token"] ?? ""; var k = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        lock (gate) if (gcpTok.TryGetValue(k, out var have) && have.Exp > Now() + 60000) return (have.Project, have.Auth);
        if (!raw.Trim().StartsWith('{')) raw = await File.ReadAllTextAsync(raw);
        var sa = JsonNode.Parse(raw)!; var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string B64(JsonObject o) => B64u.Encode(Encoding.UTF8.GetBytes(o.ToJsonString()));
        var unsigned = B64(new JsonObject { ["alg"] = "RS256", ["typ"] = "JWT" }) + "." + B64(new JsonObject { ["iss"] = sa["client_email"]?.DeepClone(), ["scope"] = "https://www.googleapis.com/auth/compute", ["aud"] = "https://oauth2.googleapis.com/token", ["iat"] = now, ["exp"] = now + 3600 });
        using var rsa = RSA.Create(); rsa.ImportFromPem((string)sa["private_key"]!);
        var jwt = unsigned + "." + B64u.Encode(rsa.SignData(Encoding.UTF8.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var r = await Fetch("POST", "https://oauth2.googleapis.com/token", $"grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer&assertion={jwt}", new() { ["content-type"] = "application/x-www-form-urlencoded" });
        var t = ((string)sa["project_id"]!, "Bearer " + (string)r!["access_token"]!, Now() + (Js.Num(r["expires_in"]) ?? 3600) * 1000);
        lock (gate) gcpTok[k] = t; return (t.Item1, t.Item2);
    }
    static async Task<string> RunShell(string? cmd, Dictionary<string, string> env)
    {
        if (string.IsNullOrEmpty(cmd)) throw new Exception("no command set");
        var psi = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd", "/c " + cmd) : new ProcessStartInfo("sh") { ArgumentList = { "-c", cmd } };
        psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.UseShellExecute = false;
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new Exception($"command failed ({p.ExitCode}): {(await e)[..Math.Min(200, (await e).Length)]}");
        return await o;
    }
    async Task<(string Id, string? Ip)> Create(JsonObject c, string name, string ud, string tag, string grant)
    {
        string? S(JsonNode? n) => (string?)n;
        switch ((string?)c["provider"])
        {
            case "hetzner": { var r = await Fetch("POST", "https://api.hetzner.cloud/v1/servers", new JsonObject { ["name"] = name, ["server_type"] = Opt(c, "plan"), ["image"] = Opt(c, "image"), ["location"] = Opt(c, "region"), ["user_data"] = ud }, Bearer(c)); return (r!["server"]!["id"]!.ToString(), S(r["server"]?["public_net"]?["ipv4"]?["ip"])); }
            case "digitalocean": { var r = await Fetch("POST", "https://api.digitalocean.com/v2/droplets", new JsonObject { ["name"] = name, ["region"] = Opt(c, "region"), ["size"] = Opt(c, "plan"), ["image"] = Opt(c, "image"), ["user_data"] = ud }, Bearer(c)); return (r!["droplet"]!["id"]!.ToString(), null); }
            case "vultr":
            {
                var r = await Fetch("POST", "https://api.vultr.com/v2/instances", new JsonObject { ["region"] = Opt(c, "region"), ["plan"] = Opt(c, "plan"), ["os_id"] = int.Parse(Opt(c, "image") ?? "2284"), ["label"] = name, ["hostname"] = name, ["tags"] = new JsonArray("opentube-flash"), ["user_data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(ud)) }, Bearer(c));
                var ip = S(r!["instance"]?["main_ip"]); return (r["instance"]!["id"]!.ToString(), ip is { } i && i != "0.0.0.0" ? i : null);
            }
            case "aws":
            {
                var region = Opt(c, "region")!; var ami = Opt(c, "image");
                if (string.IsNullOrEmpty(ami)) ami = (string)JsonNode.Parse(await AwsCall(c, "ssm", region, new JsonObject { ["Name"] = "/aws/service/canonical/ubuntu/server/24.04/stable/current/amd64/hvm/ebs-gp3/ami-id" }.ToJsonString(), new() { ["content-type"] = "application/x-amz-json-1.1", ["x-amz-target"] = "AmazonSSM.GetParameter" }))!["Parameter"]!["Value"]!;
                var q = string.Join('&', new Dictionary<string, string> { ["Action"] = "RunInstances", ["Version"] = "2016-11-15", ["ImageId"] = ami, ["InstanceType"] = Opt(c, "plan")!, ["MinCount"] = "1", ["MaxCount"] = "1", ["UserData"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(ud)), ["InstanceInitiatedShutdownBehavior"] = "terminate", ["TagSpecification.1.ResourceType"] = "instance", ["TagSpecification.1.Tag.1.Key"] = "Name", ["TagSpecification.1.Tag.1.Value"] = name }.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
                var xml = await AwsCall(c, "ec2", region, q, new() { ["content-type"] = "application/x-www-form-urlencoded; charset=utf-8" });
                var m = System.Text.RegularExpressions.Regex.Match(xml, "<instanceId>([^<]+)</instanceId>"); if (!m.Success) throw new Exception("EC2 didn’t return an instance: " + xml[..Math.Min(200, xml.Length)]);
                return (m.Groups[1].Value, null);
            }
            case "gcp":
            {
                var (project, auth) = await GcpAuth(c); var zone = Opt(c, "region");
                await Fetch("POST", $"https://compute.googleapis.com/compute/v1/projects/{project}/zones/{zone}/instances", new JsonObject
                {
                    ["name"] = name, ["machineType"] = $"zones/{zone}/machineTypes/{Opt(c, "plan")}",
                    ["disks"] = new JsonArray(new JsonObject { ["boot"] = true, ["autoDelete"] = true, ["initializeParams"] = new JsonObject { ["sourceImage"] = Opt(c, "image"), ["diskSizeGb"] = "30" } }),
                    ["networkInterfaces"] = new JsonArray(new JsonObject { ["network"] = "global/networks/default", ["accessConfigs"] = new JsonArray(new JsonObject { ["type"] = "ONE_TO_ONE_NAT", ["name"] = "External NAT" }) }),
                    ["metadata"] = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["key"] = "startup-script", ["value"] = ud }) }, ["labels"] = new JsonObject { ["opentube"] = "flash" },
                }, new() { ["Authorization"] = auth });
                return ($"{zone}/{name}", null);
            }
            case "linode":
            {
                var r = await Fetch("POST", "https://api.linode.com/v4/linode/instances", new JsonObject { ["label"] = name, ["region"] = Opt(c, "region"), ["type"] = Opt(c, "plan"), ["image"] = Opt(c, "image"), ["root_pass"] = B64u.Encode(RandomNumberGenerator.GetBytes(24)) + "A1!", ["metadata"] = new JsonObject { ["user_data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(ud)) }, ["tags"] = new JsonArray("opentube-flash") }, Bearer(c));
                return (r!["id"]!.ToString(), S(r["ipv4"]?[0]));
            }
            case "scaleway":
            {
                var t = ((string?)c["token"] ?? "").Split(':'); var bas = $"https://api.scaleway.com/instance/v1/zones/{Opt(c, "region")}"; var hd = new Dictionary<string, string> { ["X-Auth-Token"] = t.ElementAtOrDefault(1) ?? "" };
                var r = await Fetch("POST", $"{bas}/servers", new JsonObject { ["name"] = name, ["project"] = t[0], ["commercial_type"] = Opt(c, "plan"), ["image"] = Opt(c, "image"), ["dynamic_ip_required"] = true, ["tags"] = new JsonArray("opentube-flash") }, hd);
                var id = (string)r!["server"]!["id"]!;
                await Fetch("PATCH", $"{bas}/servers/{id}/user_data/cloud-init", ud, new(hd) { ["content-type"] = "text/plain" });
                await Fetch("POST", $"{bas}/servers/{id}/action", new JsonObject { ["action"] = "poweron" }, hd);
                return (id, null);
            }
            case "custom":
            {   // any other provider, owner only: create gets OT_NAME and OT_USERDATA_FILE and prints {"id": "...", "ip": "..."}
                var f = Path.Combine(node.Options.DataDir, $"flash-{name}.sh"); await File.WriteAllTextAsync(f, ud);
                var o = await RunShell((string?)c["createCmd"], new() { ["OT_NAME"] = name, ["OT_USERDATA_FILE"] = f });
                var j = JsonNode.Parse(o.Trim().Split('\n')[^1])!; return (j["id"]!.ToString(), S(j["ip"]));
            }
            case "local":
            {   // testing on one machine (owner only): the flash computer is another OpenTube here
                var port = 20000 + Random.Shared.Next(20000); var dir = Path.Combine(node.Options.DataDir, "flash-local", tag);
                var psi = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!) == "dotnet") psi.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
                foreach (var a in new[] { "--service", $"--port={port}", "--data=" + Path.Combine(dir, "data"), "--videos=" + Path.Combine(dir, "videos"), $"--peers=127.0.0.1:{node.Options.Port}", "--mine=own", "--no-ranking", "--no-mesh", "--grant=" + grant, "--site-dir=" + node.SiteDir, "--name=" + name, $"--obs-port={port + 1}", "--flash-parent=" + node.NodeKey, "--flash-tag=" + tag, "--no-tunnel", "--public=off", "--entry" })
                    psi.ArgumentList.Add(a);
                if (node.Options.Site != null) psi.ArgumentList.Add("--site=" + node.Options.Site);
                psi.Environment["FLASH_PROVIDER"] = ""; psi.Environment["AUTO_UPDATE"] = "0"; psi.Environment.Remove("OPENTUBE_APP_DIR");
                var p = Process.Start(psi)!;
                p.OutputDataReceived += (_, e) => { if (e.Data != null && Environment.GetEnvironmentVariable("FLASH_DEBUG") == "1") Log($"{tag}: {e.Data.Trim()[..Math.Min(200, e.Data.Trim().Length)]}"); };
                p.ErrorDataReceived += (_, _) => { }; p.BeginOutputReadLine(); p.BeginErrorReadLine();
                lock (gate) local[tag] = p; return (tag, "127.0.0.1");
            }
            default: throw new Exception("unknown provider");
        }
    }
    async Task Remove(JsonObject c, string id)
    {
        switch ((string?)c["provider"])
        {
            case "hetzner": await Fetch("DELETE", $"https://api.hetzner.cloud/v1/servers/{id}", null, Bearer(c)); break;
            case "digitalocean": await Fetch("DELETE", $"https://api.digitalocean.com/v2/droplets/{id}", null, Bearer(c)); break;
            case "vultr": await Fetch("DELETE", $"https://api.vultr.com/v2/instances/{id}", null, Bearer(c)); break;
            case "aws": await AwsCall(c, "ec2", Opt(c, "region")!, $"Action=TerminateInstances&Version=2016-11-15&InstanceId.1={Uri.EscapeDataString(id)}", new() { ["content-type"] = "application/x-www-form-urlencoded; charset=utf-8" }); break;
            case "gcp": { var (project, auth) = await GcpAuth(c); var p = id.Split('/'); await Fetch("DELETE", $"https://compute.googleapis.com/compute/v1/projects/{project}/zones/{p[0]}/instances/{p[1]}", null, new() { ["Authorization"] = auth }); break; }
            case "linode": await Fetch("DELETE", $"https://api.linode.com/v4/linode/instances/{id}", null, Bearer(c)); break;
            case "scaleway": { var t = ((string?)c["token"] ?? "").Split(':'); await Fetch("POST", $"https://api.scaleway.com/instance/v1/zones/{Opt(c, "region")}/servers/{id}/action", new JsonObject { ["action"] = "terminate" }, new() { ["X-Auth-Token"] = t.ElementAtOrDefault(1) ?? "" }); break; }
            case "custom": await RunShell((string?)c["deleteCmd"], new() { ["OT_ID"] = id }); break;
            case "local": lock (gate) if (local.Remove(id, out var p)) try { p.Kill(true); } catch { } break;
        }
    }
    async Task<List<(string Id, string Label, double Created)>?> List(JsonObject c)
    {
        if ((string?)c["provider"] != "vultr") return null;
        var r = await Fetch("GET", "https://api.vultr.com/v2/instances?tag=opentube-flash&per_page=500", null, Bearer(c));
        return (r?["instances"] as JsonArray ?? []).Select(i => (i!["id"]!.ToString(), (string?)i["label"] ?? "", DateTimeOffset.TryParse((string?)i["date_created"], out var d) ? (double)d.ToUnixTimeMilliseconds() : 0)).ToList();
    }

    /// <summary>The setup script the new server runs once: ffmpeg, then the .NET node as a service (with self-update), as a flash computer of this one, belonging to the person who started it.</summary>
    string UserData(string tag, string grant)
    {
        var site = node.Options.Site ?? "https://opentubedecentralized.jasonajensen.workers.dev";
        var args = string.Join(' ', "--service", $"--site={site}", $"--grant={grant}", "--tunnel", $"--name={tag}", $"--flash-parent={node.NodeKey}", $"--flash-tag={tag}");
        return $"""
            #!/bin/bash
            export DEBIAN_FRONTEND=noninteractive HOME=/root
            apt-get update -y && apt-get install -y curl ca-certificates ffmpeg
            mkdir -p /root/.opentube && curl -fsSL {site}/opentube-dotnet.sh -o /root/.opentube/opentube-dotnet.sh
            cat > /etc/systemd/system/opentube.service <<'UNIT'
            [Unit]
            Description=OpenTube (flash computer)
            After=network-online.target
            [Service]
            Environment=HOME=/root
            ExecStart=/bin/bash /root/.opentube/opentube-dotnet.sh {args}
            Restart=always
            RestartSec=5
            [Install]
            WantedBy=multi-user.target
            UNIT
            systemctl daemon-reload && systemctl enable --now opentube

            """;
    }

    bool MayFlash(string user) => user == node.OwnerUser || (node.Obs?.Store["users"]?[user] is JsonObject u && (u["can"] as JsonArray ?? []).Count > 0 && u["guestOf"] == null);
    JsonObject? CfgOf(string user) => Users[user] as JsonObject;

    public async Task<JsonObject> Start(string user, string? grantB64)
    {
        var cfg = CfgOf(user); var prov = (string?)cfg?["provider"];
        if (cfg == null || prov == null || !Providers.ContainsKey(prov)) throw new Exception("set up your cloud login first");
        if (OwnerOnly.Contains(prov) && user != node.OwnerUser) throw new Exception("only the owner can use this provider");
        if (!OwnerOnly.Contains(prov) && string.IsNullOrEmpty((string?)cfg["token"])) throw new Exception("your cloud login has no API key");
        JsonObject? g = null; try { g = JsonNode.Parse(B64u.Decode(grantB64 ?? ""))as JsonObject; } catch { }
        if (g == null || (string?)g["user"] != user || !Js.NumIs(g["v"], 1) || (string?)g["kind"] != "nodegrant") throw new Exception("a valid grant of yours is needed (the site sends it)");
        var ug = (JsonObject)g.DeepClone(); ug.Remove("sig");
        if (!Rules.VerifySig(user, "opentube-nodegrant:" + Blocks.Canon(ug), g["sig"])) throw new Exception("a valid grant of yours is needed (the site sends it)");
        var cap = Math.Max(3, (int)(Js.Num(cfg["scale"]?["max"]) ?? 0));
        lock (gate)
        {
            if (Children.Count(x => (string?)x!["state"] != "deleted" && (string?)x["user"] == user) >= cap) throw new Exception($"you have {cap} flash computers running already");
            if (Children.Count(x => (string?)x!["state"] != "deleted") >= 20) throw new Exception("20 flash computers are running from this computer already");
        }
        var tag = "flash-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));
        var (id, ip) = await Create(cfg, tag, UserData(tag, grantB64!), tag, grantB64!);
        var ch = new JsonObject { ["tag"] = tag, ["user"] = user, ["id"] = id, ["ip"] = ip, ["provider"] = prov, ["plan"] = Opt(cfg, "plan"), ["region"] = Opt(cfg, "region"), ["createdAt"] = Now(), ["state"] = "starting", ["node"] = null, ["idleSince"] = 0 };
        cfg["grant"] = grantB64;   // kept: this computer may start more for you (scaling with viewers)
        lock (gate) Children.Add(ch);
        Save(); Log($"{node.Obs?.NameOf(user) ?? user[..8]} started {tag} on {prov} ({(string?)ch["plan"] ?? "default plan"})");
        return ch;
    }
    readonly HashSet<string> deleting = [];
    async Task Delete(JsonObject ch, string why)
    {
        var tag = (string)ch["tag"]!;
        lock (gate) { if ((string?)ch["state"] == "deleted" || !deleting.Add(tag)) return; }
        var cfg = (JsonObject)(CfgOf((string)ch["user"]!)?.DeepClone() ?? new JsonObject()); cfg["provider"] = ch["provider"]?.DeepClone();
        try { await Remove(cfg, (string)ch["id"]!); ch["state"] = "deleted"; ch["deletedAt"] = Now(); ch["why"] = why; Log($"deleted {tag}: {why}"); }
        catch (Exception e) { ch["error"] = e.Message; Log($"couldn’t delete {tag} ({e.Message}); trying again in a minute"); }
        finally { lock (gate) deleting.Remove(tag); Save(); }
    }
    /// <summary>What a flash computer recorded (the recordings its liveend blocks offer): (chain, rec).</summary>
    List<(string Chain, JsonObject Rec)> Recordings(string nodeKey) => node.Engine.Read(e =>
        e.Store.Chains.Values.Where(c => c != e.Store.Main).SelectMany(c => Derived.Of(c).NodeRecs.Where(kv => (string?)kv.Value?["by"] == nodeKey).Select(kv => (c.Id, (JsonObject)kv.Value!.DeepClone()))).ToList());
    /// <summary>Viewers of a person's streams right now (per stream, the largest count any of their computers reports).</summary>
    JsonObject ViewersOf(string user)
    {
        var per = new Dictionary<string, double[]>();
        foreach (var n in node.Engine.Read(e => e.Nodes.Values.Select(x => (JsonObject)x.DeepClone()).ToList()))
        {
            if (!((string?)n["grant"]?["user"] == user || (string?)n["node"] == node.NodeKey) || Now() - (Js.Num(n["at"]) ?? 0) > 120000) continue;
            foreach (var (st, v) in n["info"]?["watching"] as JsonObject ?? [])
            {
                var r = v is JsonArray a ? a.Select(x => Js.Num(x) ?? double.NaN).ToArray() : [Js.Num(v) ?? 0, 0, 0, double.NaN];
                if (!per.TryGetValue(st, out var have) || r[0] > have[0]) per[st] = r;
            }
        }
        double Sum(int i) => per.Values.Sum(r => double.IsNaN(r.ElementAtOrDefault(i)) ? 0 : r.ElementAtOrDefault(i));
        var ks = per.Values.Select(r => r.ElementAtOrDefault(3)).Where(x => !double.IsNaN(x)).ToList();
        return new JsonObject { ["total"] = Sum(0), ["poor"] = Sum(1), ["reporting"] = Sum(2), ["kbps"] = ks.Count > 0 ? Math.Round(ks.Average()) : null, ["streams"] = per.Count };
    }
    void Scale()
    {
        foreach (var (user, cn) in Users.ToList())
        {
            if (cn is not JsonObject cfg || !Js.IsTrue(cfg["scale"]?["on"]) || (string?)cfg["grant"] == null) continue;
            List<JsonObject> mine; lock (gate) mine = Children.OfType<JsonObject>().Where(c => (string?)c["user"] == user && (string?)c["state"] != "deleted").ToList();
            if (mine.Any(c => (string?)c["state"] == "starting")) continue;   // one at a time
            var v = ViewersOf(user); var max = (int)(Js.Num(cfg["scale"]!["max"]) ?? 3); double total = Js.Num(v["total"]) ?? 0, poor = Js.Num(v["poor"]) ?? 0, reporting = Js.Num(v["reporting"]) ?? 0;
            var need = Math.Min(max, (int)Math.Ceiling(total / (Js.Num(cfg["scale"]!["per"]) ?? 25)));
            var struggling = reporting >= 3 && poor / reporting > 0.2;   // over a fifth stalling: one more, at most every 5 minutes
            if (struggling && Now() - (Js.Num(cfg["lastScale"]) ?? 0) > 5 * 60000) need = Math.Min(max, Math.Max(need, mine.Count + 1));
            if (total > 0 && need > mine.Count)
            {
                cfg["lastScale"] = Now();
                _ = Start(user, (string)cfg["grant"]!).ContinueWith(t => { if (t.IsFaulted) Log("scaling: " + t.Exception!.InnerException!.Message); else { t.Result["why"] = struggling ? $"started: {poor} of {reporting} viewers struggling" : $"started for {total} viewers"; Log($"scaling up ({t.Result["why"]}): {t.Result["tag"]}"); } });
            }
        }
    }
    void Loop()
    {
        Scale();
        List<JsonObject> kids; lock (gate) kids = Children.OfType<JsonObject>().Where(c => (string?)c["state"] != "deleted").ToList();
        foreach (var ch in kids)
        {
            var user = (string)ch["user"]!; var cfg = CfgOf(user) ?? []; var maxHours = Js.Num(cfg["maxHours"]) ?? 6; var idleMin = Js.Num(cfg["idleMin"]) ?? 5;
            var tag = (string)ch["tag"]!;
            var n = node.Engine.Read(e => e.Nodes.Values.FirstOrDefault(x => (string?)x["info"]?["flash"]?["tag"] == tag && (string?)x["info"]?["flash"]?["parent"] == node.NodeKey && (string?)x["grant"]?["user"] == user)?.DeepClone() as JsonObject);
            if (n != null) { ch["node"] = n["node"]?.DeepClone(); ch["seenAt"] = n["at"]?.DeepClone(); if ((string?)ch["state"] == "starting") { ch["state"] = "running"; Log($"{tag} is online"); } }
            var nk = (string?)ch["node"]; var recs = nk != null ? Recordings(nk) : [];
            foreach (var (chain, r) in recs) node.Hosting?.KeepRecording(chain, r);
            ch["recordings"] = recs.Count; ch["copied"] = recs.Count(x => node.Hosting?.HasComplete((string?)x.Rec["infohash"] ?? "") == true);
            var liveOnIt = nk != null && node.Engine.Read(e => e.Store.Chains.Values.Any(c => c != e.Store.Main && Derived.Of(c).LiveNow.Any(b => (string?)b["author"] == nk)));
            var busyInfo = (string?)n?["info"]?["busy"]; var busy = busyInfo != null || liveOnIt; var now = Now(); var age = now - (Js.Num(ch["createdAt"]) ?? now);
            var billing = (string?)cfg["billing"] is { } b && b != "auto" ? b : Providers.TryGetValue((string?)ch["provider"] ?? "", out var pv) ? pv.Billing : "hour";
            ch["billing"] = billing; ch["paidUntil"] = billing == "hour" ? (Js.Num(ch["createdAt"]) ?? now) + Math.Max(1, Math.Ceiling(age / 3600000)) * 3600000 : null;
            ch["busy"] = busy ? busyInfo ?? "live" : null;
            if (busy || n == null) ch["idleSince"] = 0; else if (!(Js.Num(ch["idleSince"]) > 0)) ch["idleSince"] = now;
            var idleSince = Js.Num(ch["idleSince"]) ?? 0; string? why = null;
            var copiedAll = Js.Num(ch["copied"]) == Js.Num(ch["recordings"]);
            if (age > maxHours * 3600000) why = $"its time limit ({maxHours} h)" + (!copiedAll ? $" — {Js.Num(ch["recordings"]) - Js.Num(ch["copied"])} recording(s) not copied yet!" : "");
            else if (nk == null && age > 20 * 60000) why = "it never came online";
            else if (nk != null && n != null && now - (Js.Num(n["at"]) ?? 0) > 10 * 60000 && age > 30 * 60000) why = "it stopped answering";
            else if (idleSince > 0 && copiedAll && !Js.IsTrue(ch["keep"]))
            {
                if (billing == "hour") { if ((Js.Num(ch["paidUntil"]) ?? 0) - now < 6 * 60000 && now - idleSince > 2 * 60000) why = "idle at the end of its paid hour, every recording copied here"; }
                else if (now - idleSince > idleMin * 60000) why = $"idle {idleMin} min, every recording copied here";
            }
            if (why != null) _ = Delete(ch, why);
        }
        Save();
    }
    async Task Sweep()
    {
        foreach (var (user, cn) in Users.ToList())
        {
            if (cn is not JsonObject cfg || string.IsNullOrEmpty((string?)cfg["token"])) continue;
            List<(string Id, string Label, double Created)>? have;
            try { have = await List(cfg); } catch (Exception e) { Log($"couldn’t list {cfg["provider"]} servers: {e.Message}"); continue; }
            foreach (var i in have ?? [])
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(i.Label, "^flash-[0-9a-f]{6}$")) continue;
                JsonObject? ch; lock (gate) ch = Children.OfType<JsonObject>().FirstOrDefault(c => (string?)c["provider"] == (string?)cfg["provider"] && (string?)c["id"] == i.Id);
                if (ch != null && (string?)ch["state"] != "deleted") continue;
                if (ch == null && Now() - i.Created < 20 * 60000) continue;
                try { await Remove(cfg, i.Id); Log($"deleted {i.Label} on {cfg["provider"]}: {(ch != null ? "it was still there after being deleted" : "a leftover this computer had lost track of")}"); }
                catch (Exception e) { Log($"couldn’t delete {i.Label}: {e.Message}"); }
            }
        }
    }
    public async Task Run(CancellationToken stop)
    {
        long n = 0;
        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(30000, stop).ContinueWith(_ => { });
            try { Loop(); } catch (Exception e) { Log(e.Message); }
            if (n++ % 20 == 1) try { await Sweep(); } catch { }
        }
        lock (gate) foreach (var p in local.Values) try { p.Kill(true); } catch { }
    }

    // ---- control from the site
    public async Task<JsonObject> Control(string cmd, JsonObject a, string user, bool owner)
    {
        if (!MayFlash(user)) throw new Exception("this computer isn’t shared with you for that");
        JsonObject? ch; lock (gate) ch = Js.Str(a["tag"]) is { } t ? Children.OfType<JsonObject>().FirstOrDefault(x => (string?)x["tag"] == t) : null;
        bool Mine(JsonObject x) => (string?)x["user"] == user || owner;
        switch (cmd)
        {
            case "flash": break;
            case "flash.setup":
            {   // your provider and API key, sealed to this computer in your browser
                JsonObject s; try { s = Unseal(a["sealed"] as JsonObject ?? []); } catch { throw new Exception("couldn’t open the settings (sealed for another computer?)"); }
                if (Js.IsTrue(s["off"])) Users.Remove(user);
                else
                {
                    var u = Users[user] as JsonObject ?? []; Users[user] = u;
                    if (s.ContainsKey("provider"))
                    {
                        var p = Js.Str(s["provider"]);
                        if (!string.IsNullOrEmpty(p) && !Providers.ContainsKey(p)) throw new Exception("unknown provider");
                        if (p != null && OwnerOnly.Contains(p) && !owner) throw new Exception("only the owner can use that provider (it runs commands on this computer)");
                        u["provider"] = string.IsNullOrEmpty(p) ? null : p;
                    }
                    if (Js.Str(s["token"]) is { Length: > 0 } tok) u["token"] = tok[..Math.Min(20000, tok.Length)];
                    foreach (var k in new[] { "region", "plan", "image" }) if (Js.Str(s[k]) is { } v) u[k] = v.Trim() is { Length: > 0 } vt ? vt[..Math.Min(120, vt.Length)] : null;
                    if (owner) foreach (var k in new[] { "createCmd", "deleteCmd" }) if (Js.Str(s[k]) is { } v) u[k] = v.Trim() is { Length: > 0 } vt ? vt[..Math.Min(2000, vt.Length)] : null;
                    if (Js.Num(s["maxHours"]) is double mh && mh > 0) u["maxHours"] = Math.Min(48, Math.Max(1, mh));
                    if (Js.Num(s["idleMin"]) is double im && im > 0) u["idleMin"] = Math.Min(240, Math.Max(2, im));
                    if (Js.Str(s["billing"]) is "auto" or "hour" or "second") u["billing"] = Js.Str(s["billing"]);
                }
                Save(); node.AnnounceSoon(); Log($"{(owner ? "the owner" : node.Obs?.NameOf(user))} {(Js.IsTrue(s["off"]) ? "removed their cloud login" : "set up " + ((string?)CfgOf(user)?["provider"] ?? "a cloud login"))}");
                break;
            }
            case "flash.start": await Start(user, Js.Str(a["grant"])); break;
            case "flash.stop":
                if (ch == null || !Mine(ch)) throw new Exception("no such flash computer of yours");
                await Delete(ch, owner && (string?)ch["user"] != user ? "deleted by the computer’s owner" : "deleted by its owner");
                if (ch["error"] != null && (string?)ch["state"] != "deleted") throw new Exception((string)ch["error"]!);
                break;
            case "flash.scale":
            {
                var u = CfgOf(user) ?? throw new Exception("set up your cloud login first");
                u["scale"] = new JsonObject { ["on"] = Js.IsTrue(a["on"]), ["per"] = Math.Min(10000, Math.Max(5, Js.Num(a["per"]) ?? 25)), ["max"] = Math.Min(10, Math.Max(1, Js.Num(a["max"]) ?? 3)) }; Save(); break;
            }
            case "flash.keep": if (ch == null || !Mine(ch)) throw new Exception("no such flash computer of yours"); ch["keep"] = Js.IsTrue(a["keep"]); Save(); break;
            default: throw new Exception("this computer doesn’t know that command");
        }
        var cfgU = CfgOf(user);
        JsonObject Show(JsonObject x) { var o = new JsonObject(); foreach (var k in new[] { "tag", "user", "provider", "plan", "region", "createdAt", "state", "node", "busy", "recordings", "copied", "idleSince", "keep", "why", "error", "deletedAt", "billing", "paidUntil" }) o[k] = x[k]?.DeepClone(); o["mine"] = (string?)x["user"] == user; return o; }
        List<JsonObject> list; lock (gate) list = Children.OfType<JsonObject>().Where(x => Mine(x) && ((string?)x["state"] != "deleted" || Now() - (Js.Num(x["deletedAt"]) ?? 0) < 24 * 3600000)).TakeLast(12).ToList();
        var mineCfg = cfgU == null ? null : new JsonObject
        {
            ["provider"] = cfgU["provider"]?.DeepClone(), ["tokenSet"] = !string.IsNullOrEmpty((string?)cfgU["token"]), ["region"] = cfgU["region"]?.DeepClone(), ["plan"] = cfgU["plan"]?.DeepClone(), ["image"] = cfgU["image"]?.DeepClone(),
            ["maxHours"] = Js.Num(cfgU["maxHours"]) ?? 6, ["idleMin"] = Js.Num(cfgU["idleMin"]) ?? 5, ["billing"] = (string?)cfgU["billing"] ?? "auto",
            ["ready"] = (string?)cfgU["provider"] is { } pr && (OwnerOnly.Contains(pr) || !string.IsNullOrEmpty((string?)cfgU["token"])),
        };
        if (mineCfg != null && owner) { mineCfg["createCmd"] = cfgU!["createCmd"]?.DeepClone(); mineCfg["deleteCmd"] = cfgU["deleteCmd"]?.DeepClone(); }
        var data = new JsonObject
        {
            ["enc"] = EncPub, ["owner"] = owner,
            ["providers"] = new JsonArray(Providers.Keys.Where(k => owner || !OwnerOnly.Contains(k)).Where(k => k != "local" || (string?)cfgU?["provider"] == "local").Select(k => (JsonNode?)k).ToArray()),
            ["defaults"] = new JsonObject(Providers.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, new JsonObject(kv.Value.Defaults.Select(d => new KeyValuePair<string, JsonNode?>(d.Key, d.Value)).Append(new("billing", kv.Value.Billing)))))),
            ["mine"] = mineCfg, ["scale"] = cfgU?["scale"]?.DeepClone() ?? new JsonObject { ["on"] = false, ["per"] = 25, ["max"] = 3 }, ["viewers"] = ViewersOf(user),
            ["children"] = new JsonArray(list.Select(x => (JsonNode?)Show(x)).ToArray()),
        };
        if (owner) data["people"] = Users.Count;
        node.Log($"[control] {cmd}");
        return data;
    }
}
