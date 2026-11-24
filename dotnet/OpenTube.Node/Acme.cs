using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenTube.Core;

namespace OpenTube.Node;

/// <summary>
/// Gets a certificate from Let's Encrypt (ACME, RFC 8555) with the http-01 check: Let's Encrypt fetches a token from
/// http://NAME/.well-known/acme-challenge/… on port 80, which <see cref="Tokens"/> answers. Account key and certificates
/// are kept in DATA/tls (the account key is ECDSA P-256).
/// </summary>
public sealed class Acme(string dataDir, Action<string> log, string? directory = null)
{
    public const string LetsEncrypt = "https://acme-v02.api.letsencrypt.org/directory";
    readonly string dir = Path.Combine(dataDir, "tls");
    readonly string directoryUrl = directory ?? LetsEncrypt;
    /// <summary>token → key authorization, served on port 80 while a check is under way.</summary>
    public readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Tokens = [];
    readonly HttpClient http = new(Handler()) { Timeout = TimeSpan.FromSeconds(30) };
    /// <summary>ACME_CA_FILE: also trust this CA for the ACME server (a local test server such as Pebble); still fully checked.</summary>
    static HttpClientHandler Handler()
    {
        var caFile = Environment.GetEnvironmentVariable("ACME_CA_FILE");
        if (string.IsNullOrEmpty(caFile)) return new HttpClientHandler();
        var ca = X509Certificate2.CreateFromPem(File.ReadAllText(caFile));
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, c, _, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None) return true;
                if (c == null || (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; chain.ChainPolicy.CustomTrustStore.Add(ca);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(c);
            },
        };
    }
    ECDsa? key; string? kid, nonce; JsonObject? dirs;

    string CertFile(string host) => Path.Combine(dir, host + ".crt");
    string KeyFile(string host) => Path.Combine(dir, host + ".key");

    /// <summary>The certificate we have for this name (null if none or unreadable).</summary>
    public X509Certificate2? Load(string host)
    {
        try
        {
            if (!File.Exists(CertFile(host))) return null;
            using var c = X509Certificate2.CreateFromPem(File.ReadAllText(CertFile(host)), File.ReadAllText(KeyFile(host)));
            return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx), null);   // (Windows' TLS needs a stored key)
        }
        catch { return null; }
    }
    public static bool NeedsRenewal(X509Certificate2? c) => c == null || c.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddDays(30);

    /// <summary>Get a new certificate for <paramref name="host"/>; throws with Let's Encrypt's reason if it can't.</summary>
    public async Task<X509Certificate2> Issue(string host, string? email = null, string[]? names = null)
    {
        // names: one certificate for several names (wildcards too), checked through this node's own DNS (dns-01)
        var all = names is { Length: > 0 } ? names : [host]; var viaDns = names is { Length: > 0 };
        Directory.CreateDirectory(dir);
        var accountFile = Path.Combine(dir, "account-ec.pem");
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(accountFile)) key.ImportFromPem(File.ReadAllText(accountFile));
        else { File.WriteAllText(accountFile, key.ExportPkcs8PrivateKeyPem()); TryPrivate(accountFile); }
        dirs = JsonNode.Parse(await http.GetStringAsync(directoryUrl))!.AsObject();

        var acct = new JsonObject { ["termsOfServiceAgreed"] = true };
        if (!string.IsNullOrEmpty(email)) acct["contact"] = new JsonArray("mailto:" + email);
        var (_, accRes) = await Post((string)dirs["newAccount"]!, acct, useJwk: true);
        kid = accRes.Headers.Location?.ToString() ?? throw new Exception("no account");

        var (order, orderRes) = await Post((string)dirs["newOrder"]!, new JsonObject { ["identifiers"] = new JsonArray(all.Select(n => (JsonNode?)new JsonObject { ["type"] = "dns", ["value"] = n }).ToArray()) });
        var orderUrl = orderRes.Headers.Location!.ToString();
        foreach (var az in order!["authorizations"]!.AsArray())
        {
            var (authz, _) = await Post((string)az!, null);
            if ((string?)authz!["status"] == "valid") continue;
            var kind = viaDns ? "dns-01" : "http-01";
            var ch = authz["challenges"]!.AsArray().FirstOrDefault(c => (string?)c!["type"] == kind) ?? throw new Exception($"no {kind} check offered");
            var token = (string)ch!["token"]!;
            var txtName = "_acme-challenge." + (string?)authz["identifier"]?["value"];
            if (viaDns) DnsServer.Txt.GetOrAdd(txtName, _ => []).Add(B64u.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(token + "." + Thumbprint()))));
            else Tokens[token] = token + "." + Thumbprint();
            try
            {
                await Post((string)ch["url"]!, new JsonObject());
                for (int i = 0; ; i++)
                {
                    await Task.Delay(1500);
                    var (a, _) = await Post((string)az!, null);
                    var st = (string?)a!["status"];
                    if (st == "valid") break;
                    if (st == "invalid" || i > 40) throw new Exception((viaDns ? "Let's Encrypt couldn't check this domain's DNS (are its name servers this computer?): " : "Let's Encrypt couldn't reach this computer: ") + (a["challenges"]?.AsArray().Select(c => (string?)c?["error"]?["detail"]).FirstOrDefault(d => d != null) ?? st));
                }
            }
            finally { Tokens.TryRemove(token, out _); if (viaDns) DnsServer.Txt.TryRemove(txtName, out _); }
        }
        using var certKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=" + host, certKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder(); foreach (var n in all) san.AddDnsName(n); req.CertificateExtensions.Add(san.Build());
        var (fin, _) = await Post((string)order["finalize"]!, new JsonObject { ["csr"] = B64u.Encode(req.CreateSigningRequest()) });
        for (int i = 0; (string?)fin!["status"] != "valid"; i++)
        {
            if ((string?)fin["status"] == "invalid" || i > 40) throw new Exception("Let's Encrypt refused the certificate request");
            await Task.Delay(1500); (fin, _) = await Post(orderUrl, null);
        }
        var (_, certRes) = await Post((string)fin["certificate"]!, null, raw: true);
        var pem = await certRes.Content.ReadAsStringAsync();
        File.WriteAllText(KeyFile(host), certKey.ExportPkcs8PrivateKeyPem()); TryPrivate(KeyFile(host));
        File.WriteAllText(CertFile(host), pem);
        var cert = Load(host)!;
        log($"[https] certificate for {host} from Let's Encrypt, valid until {cert.NotAfter:yyyy-MM-dd}");
        return cert;
    }

    static void TryPrivate(string f) { if (!OperatingSystem.IsWindows()) try { File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { } }

    JsonObject Jwk()
    {
        var p = key!.ExportParameters(false);
        return new JsonObject { ["crv"] = "P-256", ["kty"] = "EC", ["x"] = B64u.Encode(p.Q.X!), ["y"] = B64u.Encode(p.Q.Y!) };
    }
    string Thumbprint() => B64u.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(Jwk().ToJsonString())));   // members already in order

    async Task<string> Nonce()
    {
        if (nonce != null) { var n = nonce; nonce = null; return n; }
        using var r = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, (string)dirs!["newNonce"]!));
        return r.Headers.GetValues("Replay-Nonce").First();
    }
    /// <summary>A signed POST (payload null: POST-as-GET). Retries once on a stale nonce.</summary>
    async Task<(JsonNode? body, HttpResponseMessage res)> Post(string url, JsonObject? payload, bool useJwk = false, bool raw = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            var prot = new JsonObject { ["alg"] = "ES256", ["nonce"] = await Nonce(), ["url"] = url };
            if (useJwk) prot["jwk"] = Jwk(); else prot["kid"] = kid;
            var p64 = B64u.Encode(Encoding.UTF8.GetBytes(prot.ToJsonString()));
            var b64 = payload == null ? "" : B64u.Encode(Encoding.UTF8.GetBytes(payload.ToJsonString()));
            var sig = key!.SignData(Encoding.ASCII.GetBytes(p64 + "." + b64), HashAlgorithmName.SHA256);   // r||s, as JWS wants
            var body = new JsonObject { ["protected"] = p64, ["payload"] = b64, ["signature"] = B64u.Encode(sig) }.ToJsonString();
            var content = new StringContent(body); content.Headers.ContentType = new MediaTypeHeaderValue("application/jose+json");
            var res = await http.PostAsync(url, content);
            if (res.Headers.TryGetValues("Replay-Nonce", out var ns)) nonce = ns.First();
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync();
                if (attempt == 0 && err.Contains("badNonce")) continue;
                string detail; try { detail = (string?)JsonNode.Parse(err)?["detail"] ?? err; } catch (JsonException) { detail = err; }
                throw new Exception($"Let's Encrypt: {detail}");
            }
            if (raw) return (null, res);
            var text = await res.Content.ReadAsStringAsync();
            return (text.Length > 0 ? JsonNode.Parse(text) : null, res);
        }
    }
}
