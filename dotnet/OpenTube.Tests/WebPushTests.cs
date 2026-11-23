using System.Text;
using OpenTube.Core;

/// <summary>Web Push encryption (RFC 8291) and VAPID (RFC 8292): a round trip here; interop is checked against
/// Python's http_ece by WEBPUSH_INTEROP_DIR (see the push test script).</summary>
public class WebPushTests
{
    [Fact]
    public void RoundTrip()
    {
        var (pub, priv) = WebPush.NewKeys(); var auth = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var msg = Encoding.UTF8.GetBytes("{\"type\":\"live\",\"text\":\"🔴 Host Channel is live\"}");
        var body = WebPush.Encrypt(msg, pub, auth);
        Assert.Equal(msg, WebPush.Decrypt(body, pub, priv, auth));
    }
    [Fact]
    public void Interop()
    {
        var dir = Environment.GetEnvironmentVariable("WEBPUSH_INTEROP_DIR"); if (dir == null) return;
        // our encryption, for Python to decrypt; and Python's, for us to decrypt
        var kf = Path.Combine(dir, "keys.txt"); byte[] pub, priv, auth;
        if (File.Exists(kf)) { var k = File.ReadAllLines(kf); (pub, priv, auth) = (B64u.Decode(k[0]), B64u.Decode(k[1]), B64u.Decode(k[2])); }
        else { (pub, priv) = WebPush.NewKeys(); auth = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16); File.WriteAllText(kf, $"{B64u.Encode(pub)}\n{B64u.Encode(priv)}\n{B64u.Encode(auth)}"); }
        File.WriteAllBytes(Path.Combine(dir, "from-cs.bin"), WebPush.Encrypt(Encoding.UTF8.GetBytes("hello from C#"), pub, auth));
        var py = Path.Combine(dir, "from-py.bin");
        if (File.Exists(py)) File.WriteAllText(Path.Combine(dir, "cs-read.txt"), Encoding.UTF8.GetString(WebPush.Decrypt(File.ReadAllBytes(py), pub, priv, auth)));
        var (vp, vk) = WebPush.NewKeys();
        File.WriteAllText(Path.Combine(dir, "vapid.txt"), WebPush.Vapid("https://push.example.com/abc", vp, vk, "https://opentube.example"));
    }
}
