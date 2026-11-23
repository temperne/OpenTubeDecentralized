using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace OpenTube.Core;

/// <summary>Base64url without padding, as the network writes keys and signatures.</summary>
public static class B64u
{
    public static string Encode(ReadOnlySpan<byte> b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Decode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }
}

/// <summary>Ed25519 keys and signatures, byte-compatible with WebCrypto's Ed25519 (raw public keys, PKCS#8 private keys).</summary>
public static class Ed25519
{
    // RFC 8410: SEQUENCE { INTEGER 0, SEQUENCE { OID 1.3.101.112 }, OCTET STRING { OCTET STRING seed } }
    static readonly byte[] Pkcs8Prefix = Convert.FromHexString("302e020100300506032b657004220420");

    public static (byte[] Pub, byte[] Pkcs8) Generate()
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        return (PublicOf(seed), [.. Pkcs8Prefix, .. seed]);
    }
    /// <summary>The 32-byte seed inside a PKCS#8 Ed25519 key (WebCrypto's export), or a bare seed.</summary>
    public static byte[] SeedOf(byte[] pkcs8OrSeed) => pkcs8OrSeed.Length == 32 ? pkcs8OrSeed : pkcs8OrSeed[^32..];
    public static byte[] PublicOf(byte[] seed) => new Ed25519PrivateKeyParameters(SeedOf(seed), 0).GeneratePublicKey().GetEncoded();
    public static byte[] Sign(byte[] pkcs8OrSeed, byte[] msg)
    {
        var s = new Ed25519Signer(); s.Init(true, new Ed25519PrivateKeyParameters(SeedOf(pkcs8OrSeed), 0));
        s.BlockUpdate(msg, 0, msg.Length); return s.GenerateSignature();
    }
    public static bool Verify(byte[] pub, byte[] sig, byte[] msg)
    {
        if (pub.Length != 32 || sig.Length != 64) return false;
        try { var v = new Ed25519Signer(); v.Init(false, new Ed25519PublicKeyParameters(pub, 0)); v.BlockUpdate(msg, 0, msg.Length); return v.VerifySignature(sig); }
        catch { return false; }
    }
}

/// <summary>An identity as the network stores it (key.json): { pub, priv } — base64url raw public key and PKCS#8 private key.</summary>
public sealed record Identity(string pub, string priv)
{
    public static Identity New() { var (p, k) = Ed25519.Generate(); return new(B64u.Encode(p), B64u.Encode(k)); }
    public string Sign(string msg) => B64u.Encode(Ed25519.Sign(B64u.Decode(priv), Encoding.UTF8.GetBytes(msg)));
    public static Identity LoadOrCreate(string file)
    {
        if (File.Exists(file)) return JsonSerializer.Deserialize<Identity>(File.ReadAllText(file))!;
        var id = New(); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file, JsonSerializer.Serialize(id));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return id;
    }
}

/// <summary>Blocks: hash = sha256(canon(block minus hash/sig/nonce) + ':' + nonce); proof of work = leading zero bits of that hash.</summary>
public static class Blocks
{
    /// <summary>Canonical JSON (sorted keys, no spaces, undefined/null-valued keys kept as JSON does) — what every node hashes.</summary>
    public static string Canon(JsonNode? v)
    {
        var sb = new StringBuilder(); Write(sb, v); return sb.ToString();
        static void Write(StringBuilder sb, JsonNode? v)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case JsonArray a:
                    sb.Append('['); for (int i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); Write(sb, a[i]); } sb.Append(']'); break;
                case JsonObject o:
                    sb.Append('{'); var first = true;
                    foreach (var k in o.Select(p => p.Key).Order(StringComparer.Ordinal))
                    { if (!first) sb.Append(','); first = false; sb.Append(JsString(k)).Append(':'); Write(sb, o[k]); }
                    sb.Append('}'); break;
                case JsonValue x:
                    if (x.TryGetValue<string>(out var s)) sb.Append(JsString(s));
                    else if (x.TryGetValue<bool>(out var b)) sb.Append(b ? "true" : "false");
                    else sb.Append(Js.NumberToString(x.GetValue<double>()));
                    break;
            }
        }
    }
    /// <summary>JSON.stringify for a string: escapes only what JavaScript escapes (quotes, backslash, control characters, lone surrogates).</summary>
    public static string JsString(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[++i]); }
                    else if (char.IsSurrogate(c)) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    public static string Preimage(JsonObject block)
    {
        var rest = new JsonObject();
        foreach (var (k, v) in block) if (k is not ("hash" or "sig" or "nonce")) rest[k] = v?.DeepClone();
        return Canon(rest) + ":";
    }
    public static string Hash(JsonObject block) => Sha256Hex(Preimage(block) + Js.NumberToString(block["nonce"]!.GetValue<double>()));
    public static string Sha256Hex(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    public static int LeadingZeroBits(ReadOnlySpan<byte> d)
    {
        int z = 0;
        foreach (var b in d) { if (b == 0) { z += 8; continue; } return z + System.Numerics.BitOperations.LeadingZeroCount((uint)b) - 24; }
        return z;
    }
}

/// <summary>Proof of work on every core: finds a nonce n so that sha256(prefix + n) has at least `bits` leading zero bits.</summary>
public static class Miner
{
    public static long? Find(string prefix, int bits, CancellationToken ct, Action<long>? progress = null)
    {
        var start = Random.Shared.NextInt64(0, 1_000_000_000);
        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        long found = -1, tried = 0; int threads = Math.Max(1, Environment.ProcessorCount);
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, (t, state) =>
        {
            Span<byte> buf = stackalloc byte[prefixBytes.Length + 20]; prefixBytes.CopyTo(buf);
            Span<byte> hash = stackalloc byte[32];
            int i = 0;
            for (long n = start + t; ; n += threads)
            {
                if ((++i & 0x3fff) == 0) { if (ct.IsCancellationRequested || Interlocked.Read(ref found) >= 0) return; progress?.Invoke(Interlocked.Add(ref tried, 0x4000)); }
                n.TryFormat(buf[prefixBytes.Length..], out int w, default, System.Globalization.CultureInfo.InvariantCulture);
                SHA256.HashData(buf[..(prefixBytes.Length + w)], hash);
                if (Blocks.LeadingZeroBits(hash) >= bits) { Interlocked.CompareExchange(ref found, n, -1); return; }
            }
        });
        return found >= 0 ? found : null;
    }
}
