using OpenTube.Core;
using OpenTube.Node;
using Xunit;

namespace OpenTube.Tests;

/// <summary>The peer mesh's room ids and SDP encryption, against Trystero 0.26 running in Node.js (values recorded there).</summary>
public class MeshTests
{
    static Mesh Room() { var m = Network.MainId; return new Mesh("opentube-" + m[..16], "opentube/" + m[..32], _ => { }, (_, _, _) => { }, (_, _) => { }, _ => { }); }

    [Fact]
    public void RoomIds()
    {
        var m = Network.MainId;
        Assert.Equal("5q453w6c374m1k6q1o23", Mesh.Sha1Base36($"Trystero@opentube-{m[..16]}@opentube/{m[..32]}")[..20]);
        Assert.Equal("4571a3a6p4n30jp3n4n6", Room().InfoHash);   // what browsers announce (seen on a tracker)
    }

    [Fact]
    public void Encryption()
    {
        var room = Room();
        Assert.Equal("v=0 hello from WebCrypto", room.Decrypt("111,131,94,77,76,246,47,68,46,159,38,223,74,131,187,158$oi75W7CExMU9UfheHOAFklx4oj8v3hGDBWo+m51OjVbZLyqfFSZajQ=="));   // made by WebCrypto (AES-GCM, 16-byte IV), as browsers do
        Assert.Equal("an answer", room.Decrypt(room.Encrypt("an answer")));
    }
}
