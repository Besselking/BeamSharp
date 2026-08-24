using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Serialization.Tests;

/// <summary>
/// The byte[] converter sits at both boundaries, so it is the one place where a caller's array and
/// a term's bytes could end up being the same array.
/// </summary>
public class ByteArrayAliasingTests
{
    [Fact]
    public void Round_tripping_bytes_does_not_alias_either_end()
    {
        var original = new byte[] { 1, 2, 3, 4 };
        var term = ErlSerializer.Serialize(original, Reflected.Options);

        // Serializing must not keep the caller's array...
        original[0] = 99;
        Assert.Equal(1, Assert.IsType<ErlBinary>(term).Data.Span[0]);

        // ...and deserializing must not hand back the term's, or two callers share one array.
        var first = ErlSerializer.Deserialize<byte[]>(term, Reflected.Options);
        var second = ErlSerializer.Deserialize<byte[]>(term, Reflected.Options);
        first![0] = 42;

        Assert.Equal(1, second![0]);
        Assert.Equal(1, Assert.IsType<ErlBinary>(term).Data.Span[0]);
    }
}
