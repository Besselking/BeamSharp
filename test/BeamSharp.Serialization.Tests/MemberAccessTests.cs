using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Serialization.Tests;

/// <summary>
/// The reflection fallback used to read every member twice: once for the null test, then again
/// inside the write. The generator has always bound the value once, so the fallback was diverging
/// from the implementation it is checked against.
/// </summary>
public class MemberAccessTests
{
    [Fact]
    public void Each_member_is_read_exactly_once()
    {
        var counting = new CountingGetters();
        ErlSerializer.Serialize(counting, Reflected.Options);

        // NameReads and ValueReads are themselves members, so the serializer reads them too --
        // once each, which is exactly what is being asserted.
        Assert.Equal(1, counting.NameReads);
        Assert.Equal(1, counting.ValueReads);
    }

    [Fact]
    public void The_value_written_is_the_value_that_passed_the_null_test()
    {
        // A getter that changes between calls is unusual but legal, and reading twice meant the
        // value checked for null was not the value that got written -- the second read's.
        var flipping = new FlipsAfterFirstRead();
        var term = Assert.IsType<ErlMap>(ErlSerializer.Serialize(flipping, Reflected.Options));

        Assert.Equal(new ErlBinary("first"u8.ToArray()), term.Get("value"));
    }

    private sealed class CountingGetters
    {
        private int _nameReads;
        private int _valueReads;

        public int NameReads => _nameReads;
        public int ValueReads => _valueReads;

        public string Name
        {
            get
            {
                _nameReads++;
                return "n";
            }
        }

        public string Value
        {
            get
            {
                _valueReads++;
                return "v";
            }
        }
    }

    private sealed class FlipsAfterFirstRead
    {
        private int _reads;

        public string Value => _reads++ == 0 ? "first" : "second";
    }
}
