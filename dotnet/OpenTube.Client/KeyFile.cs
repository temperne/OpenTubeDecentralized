using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>
/// Key files locked with a password (the JavaScript page reads and writes the same): the key file's JSON, encrypted
/// with AES-256-GCM under a key derived from the password with PBKDF2-SHA256 (a random salt; the iteration count is in
/// the file). A wrong password fails the GCM tag, so it's caught, never "decrypted" into garbage.
///   { format: "opentube-encrypted", v: 1, kdf: "pbkdf2-sha256", iterations, salt, iv, ct, note }
/// </summary>
public static class KeyFile
{
    public const int Iterations = 210_000, MinPassword = 8;
    public static bool IsEncrypted(string text) { try { return (string?)JsonNode.Parse(text)?["format"] == "opentube-encrypted"; } catch { return false; } }
    /// <summary>The platform's fast PBKDF2-SHA256 (the website sets the browser's WebCrypto: in WebAssembly a managed one takes long).</summary>
    public static Func<byte[], byte[], int, Task<byte[]>>? Pbkdf2;
    static async Task<byte[]> Derive(string password, byte[] salt, int iterations)
    {
        var pw = Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC));
        if (Pbkdf2 != null) return await Pbkdf2(pw, salt, iterations);
        if (!OperatingSystem.IsBrowser()) return Rfc2898DeriveBytes.Pbkdf2(pw, salt, iterations, HashAlgorithmName.SHA256, 32);
        var g = new Pkcs5S2ParametersGenerator(new Sha256Digest()); g.Init(pw, salt, iterations);
        return ((KeyParameter)g.GenerateDerivedMacParameters(256)).GetKey();
    }
    static byte[] Gcm(bool encrypt, byte[] key, byte[] iv, byte[] data)
    {
        var c = new GcmBlockCipher(new AesEngine()); c.Init(encrypt, new AeadParameters(new KeyParameter(key), 128, iv));
        var o = new byte[c.GetOutputSize(data.Length)]; var n = c.ProcessBytes(data, 0, data.Length, o, 0); c.DoFinal(o, n); return o;
    }
    /// <summary>Lock a key file's text with a password.</summary>
    public static async Task<string> Lock(string json, string password)
    {
        if (password.Length < MinPassword) throw new ArgumentException($"Use a password of at least {MinPassword} characters");
        var salt = RandomNumberGenerator.GetBytes(16); var iv = RandomNumberGenerator.GetBytes(12);
        var ct = Gcm(true, await Derive(password, salt, Iterations), iv, Encoding.UTF8.GetBytes(json));
        return new JsonObject
        {
            ["format"] = "opentube-encrypted", ["v"] = 1, ["kdf"] = "pbkdf2-sha256", ["iterations"] = Iterations,
            ["salt"] = B64u.Encode(salt), ["iv"] = B64u.Encode(iv), ["ct"] = B64u.Encode(ct),
            ["note"] = "OpenTube keys, locked with a password. Import this file in OpenTube and enter the password.",
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
    /// <summary>Unlock a locked key file (the inner file's text). Throws on a wrong password.</summary>
    public static async Task<string> Unlock(string text, string password)
    {
        var f = JsonNode.Parse(text) as JsonObject ?? throw new ArgumentException("That isn’t a key file");
        if ((string?)f["kdf"] != "pbkdf2-sha256" || Js.Num(f["iterations"]) is not double it || it < 1000 || it > 10_000_000) throw new ArgumentException("This locked file uses a format this version doesn’t know");
        var key = await Derive(password, B64u.Decode((string)f["salt"]!), (int)it);
        try { return Encoding.UTF8.GetString(Gcm(false, key, B64u.Decode((string)f["iv"]!), B64u.Decode((string)f["ct"]!))); }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException) { throw new ArgumentException("Wrong password"); }
    }
}
