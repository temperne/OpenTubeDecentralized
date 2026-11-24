using OpenTube.Client;
using Xunit;

namespace OpenTube.Tests;

public class ClientTests
{
    [Fact]
    public void Bolt11_reads_the_spec_example()   // BOLT #11: "Please send $3 for a cup of coffee to the same peer, within one minute"
    {
        var d = Bolt11.Decode("lnbc2500u1pvjluezsp5zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zyg3zygspp5qqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqqqsyqcyq5rqwzqfqypqdq5xysxxatsyp3k7enxv4jsxqzpu9qrsgquk0rl77nj30yxdy8j9vdx85fkpmdla2087ne0xh8nhedh8w27kyke0lp53ut353s06fv3qfegext0eh0ymjpf39tuven09sam30g4vgpfna3rh");
        Assert.Equal("0001020304050607080900010203040506070809000102030405060708090102", d.Hash);
        Assert.Equal(250000, d.Sats);
        Assert.Equal("1 cup coffee", d.Desc);
        Assert.Equal("bc", d.Network);
    }
    [Fact]
    public void Media_seals_and_opens()
    {
        var key = OpenTube.Core.B64u.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)); var iv = OpenTube.Core.B64u.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        var data = System.Text.Encoding.UTF8.GetBytes("a picture");
        Assert.Equal(data, Media.Open(Media.Seal(data, key, iv), key, iv));
    }
    [Fact]
    public void Key_files_lock_and_unlock()
    {
        var locked = KeyFile.Lock("{\"format\":\"opentube-keys\"}", "correct horse").Result;
        Assert.True(KeyFile.IsEncrypted(locked));
        Assert.Equal("{\"format\":\"opentube-keys\"}", KeyFile.Unlock(locked, "correct horse").Result);
        Assert.Throws<AggregateException>(() => KeyFile.Unlock(locked, "wrong one!").Result);
    }
}
