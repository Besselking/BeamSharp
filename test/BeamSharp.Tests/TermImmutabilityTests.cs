using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Tests;

/// <summary>
/// Terms are values: they are compared and hashed, and ErlRef keys the node's pending calls and
/// incoming monitors. A term whose bytes can change after it is in a dictionary is a term the
/// dictionary can never find again -- not under the new value, and not under the old one either,
/// because the bucket was chosen from a hash that no longer matches anything.
/// </summary>
public class TermImmutabilityTests
{
    [Fact]
    public void A_binary_cannot_be_changed_through_the_array_it_was_built_from()
    {
        var source = "session-id"u8.ToArray();
        var key = new ErlBinary(source);
        var map = new ErlMap([new KeyValuePair<ErlTerm, ErlTerm>(key, Erl.Atom("secret"))]);

        source[0] = (byte)'X';

        Assert.Equal(Erl.Atom("secret"), map[key]);
        Assert.Equal("session-id", key.AsString());
    }

    [Fact]
    public void A_reference_cannot_be_changed_through_the_array_it_was_built_from()
    {
        var words = new uint[] { 1, 2, 3 };
        var reference = new ErlRef("n@h", 1, words);

        words[0] = 99;

        Assert.Equal(1u, reference.Ids.Span[0]);
        Assert.Equal(new ErlRef("n@h", 1, [1, 2, 3]), reference);
    }

    [Fact]
    public void A_bitstring_and_a_fun_cannot_be_changed_through_theirs()
    {
        var bits = new byte[] { 0xFF, 0x80 };
        var bitstring = new ErlBitstring(bits, 1);

        var encoded = new byte[] { 1, 2, 3 };
        var fun = new ErlFun(encoded);

        bits[0] = 0;
        encoded[0] = 0;

        Assert.Equal(0xFF, bitstring.Data.Span[0]);
        Assert.Equal(1, fun.Encoded.Span[0]);
    }
}
