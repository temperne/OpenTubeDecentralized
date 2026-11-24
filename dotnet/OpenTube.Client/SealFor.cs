using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using OpenTube.Core;

namespace OpenTube.Client;

/// <summary>Sealing something (a cloud login) for one computer only: ECDH P-256 with its published encryption key,
/// key = SHA-256(shared x || "opentube-seal-v1"), AES-256-GCM. The computer opens it; whatever carries it sees ciphertext.
/// { epk, iv, ct } (base64url), as the JavaScript page's sealFor.</summary>
public static class Sealing
{
    static readonly ECDomainParameters Domain = new(NistNamedCurves.GetByName("P-256"));
    public static JsonObject For(string encPubB64u, JsonObject obj)
    {
        var g = new ECKeyPairGenerator(); g.Init(new ECKeyGenerationParameters(Domain, new SecureRandom()));
        var kp = g.GenerateKeyPair(); var priv = (ECPrivateKeyParameters)kp.Private; var pub = (ECPublicKeyParameters)kp.Public;
        var a = new ECDHBasicAgreement(); a.Init(priv);
        var x = a.CalculateAgreement(new ECPublicKeyParameters(Domain.Curve.DecodePoint(B64u.Decode(encPubB64u)), Domain)).ToByteArrayUnsigned();
        if (x.Length < 32) x = [.. new byte[32 - x.Length], .. x];
        var key = SHA256.HashData([.. x, .. Encoding.UTF8.GetBytes("opentube-seal-v1")]); var iv = RandomNumberGenerator.GetBytes(12);
        var c = new GcmBlockCipher(new AesEngine()); c.Init(true, new AeadParameters(new KeyParameter(key), 128, iv));
        var data = Encoding.UTF8.GetBytes(obj.ToJsonString()); var ct = new byte[c.GetOutputSize(data.Length)]; c.DoFinal(ct, c.ProcessBytes(data, 0, data.Length, ct, 0));
        return new JsonObject { ["epk"] = B64u.Encode(pub.Q.GetEncoded(false)), ["iv"] = B64u.Encode(iv), ["ct"] = B64u.Encode(ct) };
    }
}
