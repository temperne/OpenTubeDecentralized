using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenTube.Core;

/// <summary>
/// Web Push (RFC 8030), the standard way to wake a device with a message, with no polling: a node sends one HTTPS
/// request to the device's push address when there's something to say. The message is end-to-end encrypted
/// (RFC 8291, "aes128gcm": only the device can read it) and the node signs who sends it (VAPID, RFC 8292). Browsers
/// give a push address through their push service; Android phones through a UnifiedPush distributor (e.g. ntfy).
/// </summary>
public static class WebPush
{
    /// <summary>A P-256 key pair: the public key as 65 bytes (uncompressed point), the private as 32.</summary>
    public static (byte[] Pub, byte[] Priv) NewKeys()
    {
        using var k = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = k.ExportParameters(true);
        return ([0x04, .. p.Q.X!, .. p.Q.Y!], p.D!);
    }
    static ECParameters Params(byte[] pub, byte[]? priv = null) => new()
    {
        Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = pub[1..33], Y = pub[33..65] }, D = priv,
    };
    public static byte[] Ecdh(byte[] myPriv, byte[] myPub, byte[] theirPub)
    {
        using var me = ECDiffieHellman.Create(Params(myPub, myPriv));
        using var them = ECDiffieHellman.Create(Params(theirPub));
        return me.DeriveRawSecretAgreement(them.PublicKey);
    }

    /// <summary>Encrypt a message for a device (its public key and auth secret from its subscription): the request body.</summary>
    public static byte[] Encrypt(byte[] message, byte[] uaPub, byte[] auth)
    {
        if (message.Length > 3900) throw new ArgumentException("a push message holds at most ~3.9 KB");
        var (asPub, asPriv) = NewKeys();
        var salt = RandomNumberGenerator.GetBytes(16);
        var (cek, nonce) = Keys(Ecdh(asPriv, asPub, uaPub), auth, uaPub, asPub, salt);
        var plain = new byte[message.Length + 1]; message.CopyTo(plain, 0); plain[^1] = 2;   // the last (only) record
        var cipher = new byte[plain.Length]; var tag = new byte[16];
        using (var g = new AesGcm(cek, 16)) g.Encrypt(nonce, plain, cipher, tag);
        var head = new byte[16 + 4 + 1 + 65]; salt.CopyTo(head, 0); BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(16), 4096); head[20] = 65; asPub.CopyTo(head, 21);
        return [.. head, .. cipher, .. tag];
    }
    /// <summary>Read a message sent to this device (its key pair and auth secret).</summary>
    public static byte[] Decrypt(byte[] body, byte[] uaPub, byte[] uaPriv, byte[] auth)
    {
        var salt = body[..16]; int idLen = body[20]; var asPub = body[21..(21 + idLen)];
        var data = body[(21 + idLen)..];
        var (cek, nonce) = Keys(Ecdh(uaPriv, uaPub, asPub), auth, uaPub, asPub, salt);
        var plain = new byte[data.Length - 16];
        using (var g = new AesGcm(cek, 16)) g.Decrypt(nonce, data[..^16], data[^16..], plain);
        int end = plain.Length - 1; while (end >= 0 && plain[end] == 0) end--;   // padding, then the delimiter
        return plain[..end];
    }
    static (byte[] Cek, byte[] Nonce) Keys(byte[] ecdh, byte[] auth, byte[] uaPub, byte[] asPub, byte[] salt)
    {
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdh, 32, auth, [.. "WebPush: info\0"u8, .. uaPub, .. asPub]);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        return (HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray()), HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray()));
    }

    /// <summary>The sender's signature for one push service (VAPID): the Authorization header's value.</summary>
    public static string Vapid(string endpoint, byte[] pub, byte[] priv, string subject)
    {
        var aud = new Uri(endpoint).GetLeftPart(UriPartial.Authority);
        var header = B64u.Encode(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
        var claims = B64u.Encode(Encoding.UTF8.GetBytes(new JsonObject { ["aud"] = aud, ["exp"] = DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds(), ["sub"] = subject }.ToJsonString()));
        using var k = ECDsa.Create(Params(pub, priv));
        var sig = k.SignData(Encoding.ASCII.GetBytes(header + "." + claims), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"vapid t={header}.{claims}.{B64u.Encode(sig)}, k={B64u.Encode(pub)}";
    }

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    /// <summary>Send one message. Returns the push service's status (201: taken; 404/410: that address is gone).</summary>
    public static async Task<int> Send(string endpoint, byte[] uaPub, byte[] auth, byte[] message, byte[] vapidPub, byte[] vapidPriv, string subject, int ttlSeconds = 86400, string urgency = "normal")
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(Encrypt(message, uaPub, auth)) };
        req.Content.Headers.ContentType = new("application/octet-stream");
        req.Content.Headers.ContentEncoding.Add("aes128gcm");
        req.Headers.TryAddWithoutValidation("TTL", ttlSeconds.ToString());
        req.Headers.TryAddWithoutValidation("Urgency", urgency);
        req.Headers.TryAddWithoutValidation("Authorization", Vapid(endpoint, vapidPub, vapidPriv, subject));
        using var r = await http.SendAsync(req);
        return (int)r.StatusCode;
    }
}

/// <summary>
/// Telling a node "this private message is for your person" without telling anyone else. A message's recipient is
/// hidden (only their messaging key recognizes its tag, and that key also reads it, so it never leaves their devices).
/// Each node publishes a NOTIFY key (P-256) in its signed status; a sender adds, for each of the recipient's nodes, a
/// hint: sha256("opentube-mail-hint:" + hex(ECDH(one-time key, notify key)) + ":" + salt + ":" + recipient)[..32 hex].
/// Only that node (holding the notify key's private half) can recompute it: it learns the message is for its person,
/// and can push them a notification; it can't read the message, and nobody else can tell whom it's for. Senders pad
/// the hints to three with random ones, so their number says nothing.
/// </summary>
public static class MailHints
{
    public const int Count = 3;
    public static string Of(byte[] ecdhSecret, string salt, string recipient) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("opentube-mail-hint:" + Convert.ToHexStringLower(ecdhSecret) + ":" + salt + ":" + recipient)))[..32];
    /// <summary>On a node: the hint this message would carry for <paramref name="recipient"/>, from its one-time key.</summary>
    public static string? For(string ephB64u, string salt, string recipient, byte[] notifyPub, byte[] notifyPriv)
    {
        try { return Of(WebPush.Ecdh(notifyPriv, notifyPub, B64u.Decode(ephB64u)), salt, recipient); } catch { return null; }
    }
}
