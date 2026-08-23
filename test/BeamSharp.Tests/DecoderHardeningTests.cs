using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Tests;

/// <summary>
/// The decoder reads bytes off a socket, so hostile or corrupt input is an expected case rather
/// than an exceptional one. These tests pin two properties: it never allocates on the strength of a
/// length it has not checked, and it has exactly one failure mode.
/// </summary>
public class DecoderHardeningTests
{
    /// <summary>A term header claiming <paramref name="count"/> elements, with no elements after it.</summary>
    private static byte[] Claiming(byte tag, uint count) =>
        [131, tag, (byte)(count >> 24), (byte)(count >> 16), (byte)(count >> 8), (byte)count];

    [Theory]
    [InlineData(TermTags.LargeTuple)]
    [InlineData(TermTags.List)]
    [InlineData(TermTags.Map)]
    public void A_tiny_frame_claiming_a_huge_element_count_allocates_nothing(byte tag)
    {
        // Before this was bounded, six bytes claiming a hundred million elements allocated 763 MB
        // (1.5 GB for a map) before noticing the elements were not there.
        var hostile = Claiming(tag, 100_000_000);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        Assert.Throws<ErlDecodeException>(() => TermDecoder.Decode(hostile));
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.True(allocated < 64 * 1024, $"decoding allocated {allocated:N0} bytes for a 6 byte input");
    }

    [Theory]
    [InlineData(TermTags.LargeTuple)]
    [InlineData(TermTags.List)]
    [InlineData(TermTags.Map)]
    [InlineData(TermTags.Binary)]
    [InlineData(TermTags.LargeBig)]
    public void A_length_beyond_int_range_is_rejected_as_malformed(byte tag)
    {
        Assert.Throws<ErlDecodeException>(() => TermDecoder.Decode(Claiming(tag, uint.MaxValue)));
    }

    [Fact]
    public void Arbitrary_bytes_only_ever_produce_a_decode_exception()
    {
        // A parser fed by the network wants one failure mode. Anything else leaking out means a
        // caller has to guess which exception types to catch.
        var rng = new Random(20260823);
        var decoded = 0;

        for (var i = 0; i < 50_000; i++)
        {
            var bytes = new byte[rng.Next(1, 64)];
            rng.NextBytes(bytes);
            bytes[0] = 131;

            try
            {
                TermDecoder.Decode(bytes);
                decoded++;
            }
            catch (ErlDecodeException)
            {
                // The one acceptable outcome for junk.
            }
            catch (Exception ex)
            {
                Assert.Fail($"{ex.GetType().Name} escaped for input {Convert.ToHexString(bytes)}: {ex.Message}");
            }
        }

        // Sanity: the corpus is not so hostile that nothing ever parses, which would make this vacuous.
        Assert.True(decoded > 0, "no random input decoded, so this test proves nothing");
    }

    [Fact]
    public void Truncating_a_valid_term_anywhere_only_produces_a_decode_exception()
    {
        foreach (var name in new[] { "nested", "map", "list_mixed", "improper", "export", "bignum_pos" })
        {
            var valid = Fixtures.Get(name);

            for (var length = 1; length < valid.Length; length++)
            {
                var truncated = valid.AsSpan(0, length).ToArray();
                try
                {
                    TermDecoder.Decode(truncated);
                }
                catch (ErlDecodeException)
                {
                    // Expected.
                }
                catch (Exception ex)
                {
                    Assert.Fail($"{ex.GetType().Name} escaped for {name} truncated to {length} bytes: {ex.Message}");
                }
            }
        }
    }

    [Fact]
    public void Flipping_bytes_in_a_valid_term_only_produces_a_decode_exception()
    {
        var rng = new Random(99);

        foreach (var name in new[] { "nested", "map", "tuple3", "string_255" })
        {
            var valid = Fixtures.Get(name);

            for (var i = 0; i < 2_000; i++)
            {
                var mutated = valid.ToArray();
                mutated[rng.Next(1, mutated.Length)] = (byte)rng.Next(256);

                try
                {
                    TermDecoder.Decode(mutated);
                }
                catch (ErlDecodeException)
                {
                    // Expected.
                }
                catch (Exception ex)
                {
                    Assert.Fail($"{ex.GetType().Name} escaped for a mutated {name}: {ex.Message}");
                }
            }
        }
    }

    [Fact]
    public void A_bitstring_with_an_impossible_trailing_bit_count_is_rejected()
    {
        // BIT_BINARY_EXT: length 1, then the bit count, then the byte.
        byte[] Bits(byte count) => [131, TermTags.BitBinary, 0, 0, 0, 1, count, 0xFF];

        Assert.Throws<ErlDecodeException>(() => TermDecoder.Decode(Bits(0)));
        Assert.Throws<ErlDecodeException>(() => TermDecoder.Decode(Bits(9)));
        Assert.Equal(new ErlBitstring([0xFF], 3), TermDecoder.Decode(Bits(3)));
    }
}
