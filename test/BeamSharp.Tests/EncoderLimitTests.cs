using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Tests;

/// <summary>
/// The decoder is careful about input it did not produce. The encoder writes terms a caller built,
/// which is a different threat but not a smaller one: a length that wraps its field corrupts the
/// frame silently, and a term that nests too deeply takes the process with it.
/// </summary>
public class EncoderLimitTests
{
    [Fact]
    public void An_atom_too_long_for_its_length_field_is_refused_rather_than_truncated()
    {
        // 70,000 & 0xFFFF is 4,464: the atom used to encode with that declared on the wire, and the
        // 65,536 bytes past it were then read as further terms, desynchronising the whole frame.
        var oversized = new string('a', 70_000);
        var ex = Assert.Throws<ArgumentException>(() => TermEncoder.Encode(new ErlAtom(oversized)));
        // Formatted invariantly: these messages get logged and compared, and a thousands
        // separator that changes with the machine's locale makes that worse, not friendlier.
        Assert.Contains("an atom of 70000 bytes", ex.Message, StringComparison.Ordinal);

        // 255 characters is the emulator's limit and 1,020 bytes is what they take in UTF-8, which
        // is the reason ATOM_UTF8_EXT carries a two-byte length at all.
        var longestAllowed = new string('a', TermEncoder.MaxAtomBytes);
        Assert.Equal(new ErlAtom(longestAllowed), TermDecoder.Decode(TermEncoder.Encode(new ErlAtom(longestAllowed))));

        // And a multi-byte atom is measured in bytes, not characters, so it still round-trips.
        var multiByte = new string('é', 255);
        Assert.Equal(new ErlAtom(multiByte), TermDecoder.Decode(TermEncoder.Encode(new ErlAtom(multiByte))));
    }

    [Fact]
    public void A_term_nested_past_the_limit_is_refused_rather_than_overflowing_the_stack()
    {
        ErlTerm deep = new ErlAtom("bottom");
        for (var i = 0; i < TermEncoder.MaxDepth + 10; i++) deep = new ErlTuple(deep);

        // Not a StackOverflowException, which the runtime does not let anyone catch.
        Assert.Throws<ArgumentException>(() => TermEncoder.Encode(deep));

        ErlTerm allowed = new ErlAtom("bottom");
        for (var i = 0; i < TermEncoder.MaxDepth - 2; i++) allowed = new ErlTuple(allowed);
        Assert.NotEmpty(TermEncoder.Encode(allowed));
    }
}
