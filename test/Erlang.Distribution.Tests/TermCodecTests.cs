using Erlang.Distribution.Terms;
using Xunit;

namespace Erlang.Distribution.Tests;

public class TermCodecTests
{
    /// <summary>Fixtures whose encoding is deterministic, so our bytes must match the BEAM's exactly.</summary>
    public static TheoryData<string, ErlTerm> ByteExact => new()
    {
        { "atom_ok", Erl.Atom("ok") },
        { "atom_unicode", Erl.Atom("héllo") },
        { "int_0", Erl.Int(0) },
        { "int_255", Erl.Int(255) },
        { "int_256", Erl.Int(256) },
        { "int_neg1", Erl.Int(-1) },
        { "int_max32", Erl.Int(int.MaxValue) },
        { "int_min32", Erl.Int(int.MinValue) },
        { "bignum_pos", Erl.Int(System.Numerics.BigInteger.Parse("123456789012345678901234567890")) },
        { "bignum_neg", Erl.Int(System.Numerics.BigInteger.Parse("-98765432109876543210")) },
        { "float", Erl.Float(3.14159) },
        { "float_neg_zero", Erl.Float(-0.0) },
        { "binary", Erl.String("hello world") },
        { "binary_empty", Erl.Binary([]) },
        { "bitstring", new ErlBitstring([0xA0], 3) },
        { "nil", Erl.Nil },
        { "charlist", Erl.CharList("abc") },
        { "list_mixed", Erl.List(Erl.Int(1), Erl.Atom("a"), Erl.String("b"), Erl.Float(2.0)) },
        { "improper", Erl.ImproperList([Erl.Atom("a")], Erl.Atom("b")) },
        { "tuple0", Erl.Tuple() },
        { "tuple3", Erl.Tuple(Erl.Int(1), Erl.Atom("two"), Erl.String("three")) },
        { "export", new ErlExport(new ErlAtom("lists"), new ErlAtom("reverse"), 1) },
        { "string_255", new ErlList(Enumerable.Range(1, 255).Select(i => (ErlTerm)Erl.Int(i))) }
    };

    [Theory]
    [MemberData(nameof(ByteExact))]
    public void Decodes_bytes_produced_by_the_beam(string fixture, ErlTerm expected)
    {
        Assert.Equal(expected, TermDecoder.Decode(Fixtures.Get(fixture)));
    }

    [Theory]
    [MemberData(nameof(ByteExact))]
    public void Encodes_the_same_bytes_the_beam_does(string fixture, ErlTerm term)
    {
        Assert.Equal(Fixtures.Get(fixture), TermEncoder.Encode(term));
    }

    [Theory]
    // Maps have no defined key order, so only the decoded value is comparable.
    [InlineData("map")]
    [InlineData("map_empty")]
    [InlineData("nested")]
    public void Round_trips_terms_whose_encoding_is_not_order_stable(string fixture)
    {
        var decoded = TermDecoder.Decode(Fixtures.Get(fixture));
        Assert.Equal(decoded, TermDecoder.Decode(TermEncoder.Encode(decoded)));
    }

    [Fact]
    public void Decodes_a_map_into_something_lookups_work_on()
    {
        var map = Assert.IsType<ErlMap>(TermDecoder.Decode(Fixtures.Get("map")));

        Assert.Equal(3, map.Count);
        Assert.Equal(Erl.Int(1), map.Get("a"));
        Assert.Equal(Erl.List(Erl.Int(2)), map[Erl.String("b")]);
        Assert.Equal(new ErlMap(), map[Erl.Tuple(Erl.Atom("c"))]);
    }

    [Fact]
    public void Keeps_the_tail_of_an_improper_list()
    {
        // OTP sends gen_server reply tags as [alias | Ref], so the tail has to survive.
        var list = Assert.IsType<ErlList>(TermDecoder.Decode(Fixtures.Get("improper")));

        Assert.False(list.IsProper);
        Assert.Equal(Erl.Atom("a"), list[0]);
        Assert.Equal(Erl.Atom("b"), list.Tail);
    }

    [Fact]
    public void Treats_an_empty_list_tail_as_a_proper_list()
    {
        var list = Assert.IsType<ErlList>(TermDecoder.Decode(Fixtures.Get("charlist")));
        Assert.True(list.IsProper);
        Assert.Null(list.Tail);
    }

    [Fact]
    public void Picks_the_narrowest_integer_tag()
    {
        Assert.Equal(97, TermEncoder.Encode(Erl.Int(200))[1]);   // SMALL_INTEGER_EXT
        Assert.Equal(98, TermEncoder.Encode(Erl.Int(-200))[1]);  // INTEGER_EXT
        Assert.Equal(110, TermEncoder.Encode(                      // SMALL_BIG_EXT
            Erl.Int(System.Numerics.BigInteger.Pow(2, 64)))[1]);
    }

    [Fact]
    public void Terms_compare_by_value_so_they_work_as_map_keys()
    {
        var a = Erl.Tuple(Erl.String("k"), Erl.List(Erl.Int(1)));
        var b = Erl.Tuple(Erl.String("k"), Erl.List(Erl.Int(1)));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        var map = new ErlMap([new KeyValuePair<ErlTerm, ErlTerm>(a, Erl.Ok)]);
        Assert.Equal(Erl.Ok, map[b]);
    }

    [Fact]
    public void Round_trips_pids_refs_and_ports()
    {
        ErlTerm[] terms =
        [
            new ErlPid("a@b", 10, 0, 0x6A89E7ED),
            new ErlRef("a@b", 0x6A89E7ED, [1, 2, 3]),
            new ErlPort("a@b", 0x1122334455667788, 7)
        ];

        foreach (var term in terms)
            Assert.Equal(term, TermDecoder.Decode(TermEncoder.Encode(term)));
    }

    [Fact]
    public void Reports_how_many_bytes_a_term_used()
    {
        // The pass-through frame format packs two terms back to back, so this has to be exact.
        var first = TermEncoder.Encode(Erl.Tuple(Erl.Int(6), Erl.Atom("x")));
        var second = TermEncoder.Encode(Erl.String("payload"));
        var frame = first.Concat(second).ToArray();

        var control = TermDecoder.Decode(frame, out var used);

        Assert.Equal(first.Length, used);
        Assert.Equal(Erl.Tuple(Erl.Int(6), Erl.Atom("x")), control);
        Assert.Equal(Erl.String("payload"), TermDecoder.Decode(frame.AsSpan(used)));
    }

    [Fact]
    public void Rejects_atom_cache_references()
    {
        // We never negotiate the atom cache, so seeing one means the peer got the flags wrong.
        var ex = Assert.Throws<ErlDecodeException>(() => TermDecoder.Decode(new byte[] { 131, 82, 0 }));
        Assert.Contains("atom cache", ex.Message);
    }

    [Fact]
    public void Rejects_truncated_input()
    {
        Assert.Throws<ErlDecodeException>(() => TermDecoder.Decode(new byte[] { 131, 109, 0, 0, 0, 10, 1, 2 }));
    }

    [Fact]
    public void Reads_text_out_of_binaries_atoms_and_charlists()
    {
        Assert.Equal("hi", Erl.String("hi").AsText());
        Assert.Equal("hi", Erl.Atom("hi").AsText());
        Assert.Equal("hi", Erl.CharList("hi").AsText());
    }
}
