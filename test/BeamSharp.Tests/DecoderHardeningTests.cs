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

        // Warm the path first, so jitting the decoder is not counted as decoding.
        try { TermDecoder.Decode(hostile); } catch (ErlDecodeException) { }

        // Per-thread, not per-process: xunit runs test classes in parallel, and GetTotalAllocatedBytes
        // would bill this test for whatever else happened to be running beside it.
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ErlDecodeException>(() => TermDecoder.Decode(hostile));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 16 * 1024, $"decoding allocated {allocated:N0} bytes for a 6 byte input");
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
    public void Deeply_nested_terms_are_refused_rather_than_overflowing_the_stack()
    {
        // Two bytes a level: {104, 1} is a one-element tuple. Forty kilobytes of them nests twenty
        // thousand deep, and the decoder walks nesting with the call stack, so before this limit a
        // 40 KB frame aborted the process. A stack overflow is not catchable — no try/catch helps.
        var deep = new byte[1 + 20_000 * 2 + 1];
        deep[0] = 131;
        for (var i = 0; i < 20_000; i++)
        {
            deep[1 + i * 2] = TermTags.SmallTuple;
            deep[2 + i * 2] = 1;
        }
        deep[^1] = TermTags.Nil;

        var ex = Assert.Throws<ErlDecodeException>(() => TermDecoder.Decode(deep));
        Assert.Contains("nested deeper", ex.Message);
    }

    [Fact]
    public void Nesting_up_to_the_limit_still_decodes()
    {
        // The limit has to be a limit, not a ceiling that ordinary terms bump into.
        ErlTerm nested = ErlList.Empty;
        for (var i = 0; i < TermDecoder.DefaultMaxDepth - 2; i++) nested = new ErlTuple(nested);

        Assert.Equal(nested, TermDecoder.Decode(TermEncoder.Encode(nested)));
    }

    [Fact]
    public void Randomly_shaped_terms_round_trip_or_are_refused_cleanly()
    {
        // Structure-aware fuzzing, as opposed to the random-bytes kind above. Random bytes almost
        // never nest — they die at the first tag — which is exactly why they missed the stack
        // overflow. Generating shapes explores the parts of the decoder that recurse.
        var rng = new Random(4242);

        for (var i = 0; i < 2_000; i++)
        {
            var term = RandomTerm(rng, depth: 0);
            var bytes = TermEncoder.Encode(term);

            Assert.Equal(term, TermDecoder.Decode(bytes));

            // Now corrupt it and require the one failure mode.
            var mutated = bytes.ToArray();
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
                Assert.Fail($"{ex.GetType().Name} escaped for a mutated random term: {ex.Message}");
            }
        }
    }

    private static ErlTerm RandomTerm(Random rng, int depth)
    {
        // Bias towards leaves as depth grows, so the shapes terminate.
        var leafOnly = depth > 6 || rng.Next(100) < depth * 12;

        return (leafOnly ? rng.Next(5) : rng.Next(9)) switch
        {
            0 => new ErlInt(rng.NextInt64(long.MinValue, long.MaxValue)),
            1 => new ErlAtom($"atom{rng.Next(50)}"),
            2 => new ErlBinary(Enumerable.Range(0, rng.Next(8)).Select(_ => (byte)rng.Next(256)).ToArray()),
            3 => new ErlFloat(rng.NextDouble() * 1e6),
            4 => new ErlPid("n@h", (uint)rng.Next(), (uint)rng.Next(), 7),
            5 => new ErlTuple(Enumerable.Range(0, rng.Next(4)).Select(_ => RandomTerm(rng, depth + 1)).ToArray()),
            6 => new ErlList(Enumerable.Range(0, rng.Next(4)).Select(_ => RandomTerm(rng, depth + 1)).ToArray()),
            7 => new ErlList([RandomTerm(rng, depth + 1)], RandomTerm(rng, depth + 1)),
            _ => new ErlMap(Enumerable.Range(0, rng.Next(3))
                .Select(k => new KeyValuePair<ErlTerm, ErlTerm>(
                    new ErlAtom($"k{k}"), RandomTerm(rng, depth + 1))))
        };
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

    [Fact]
    public void An_export_with_an_out_of_range_arity_is_rejected()
    {
        // EXPORT_EXT: module atom, function atom, then an arity that a peer picks. Erlang integers
        // are unbounded, so nothing stops that arity being a bignum, and the decoder used to cast it
        // to int unchecked — an OverflowException, which is not the failure mode this class pins.
        static byte[] Export(byte[] arity)
        {
            List<byte> frame = [131, TermTags.Export];
            void Atom(string name)
            {
                frame.Add(TermTags.SmallAtomUtf8);
                frame.Add((byte)name.Length);
                frame.AddRange(System.Text.Encoding.UTF8.GetBytes(name));
            }

            Atom("erlang");
            Atom("apply");
            frame.AddRange(arity);
            return [.. frame];
        }

        // SMALL_BIG_EXT: 9 digits, positive, little-endian 2^70.
        byte[] bignum = [TermTags.SmallBig, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x40];
        Assert.Throws<ErlDecodeException>(() => TermDecoder.Decode(Export(bignum)));

        // An arity past what the emulator allows a function is malformed too, bignum or not.
        Assert.Throws<ErlDecodeException>(() =>
            TermDecoder.Decode(Export([TermTags.Integer, 0, 0, 1, 0])));

        // And the ordinary case still decodes.
        Assert.Equal(new ErlExport(new ErlAtom("erlang"), new ErlAtom("apply"), 2),
            TermDecoder.Decode(Export([TermTags.SmallInteger, 2])));
    }

    [Fact]
    public void A_map_that_repeats_a_key_is_rejected()
    {
        // MAP_EXT with arity 2, both entries keyed 'a'. Erlang's own decoder is the reference here:
        //   binary_to_term(<<131,116,0,0,0,2, 119,1,$a, 97,1, 119,1,$a, 97,2>>)  ->  error:badarg
        // so a frame like this is malformed rather than a frame to guess the meaning of.
        byte[] duplicate =
        [
            131, TermTags.Map, 0, 0, 0, 2,
            TermTags.SmallAtomUtf8, 1, (byte)'a', TermTags.SmallInteger, 1,
            TermTags.SmallAtomUtf8, 1, (byte)'a', TermTags.SmallInteger, 2
        ];
        Assert.Throws<ErlDecodeException>(() => TermDecoder.Decode(duplicate));

        // Distinct keys still decode, so this is not rejecting every map with a repeated value.
        byte[] distinct =
        [
            131, TermTags.Map, 0, 0, 0, 2,
            TermTags.SmallAtomUtf8, 1, (byte)'a', TermTags.SmallInteger, 1,
            TermTags.SmallAtomUtf8, 1, (byte)'b', TermTags.SmallInteger, 1
        ];
        Assert.Equal(2, Assert.IsType<ErlMap>(TermDecoder.Decode(distinct)).Count);
    }

    [Fact]
    public void A_map_built_in_csharp_still_takes_the_last_value_for_a_key()
    {
        // The other half of Erlang's behaviour: #{a => 1, a => 2} is #{a => 2}. Only what arrives
        // off a socket is held to binary_to_term's stricter rule.
        var map = new ErlMap(
        [
            new KeyValuePair<ErlTerm, ErlTerm>(new ErlAtom("a"), new ErlInt(1)),
            new KeyValuePair<ErlTerm, ErlTerm>(new ErlAtom("a"), new ErlInt(2))
        ]);

        Assert.Equal(1, map.Count);
        Assert.Equal(new ErlInt(2), map.Get("a"));
    }
}
