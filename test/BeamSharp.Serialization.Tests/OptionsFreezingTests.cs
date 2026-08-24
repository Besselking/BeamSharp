using BeamSharp.Serialization;
using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Serialization.Tests;

/// <summary>
/// Options freeze the first time they are used, because a converter resolved under one
/// configuration and then cached must not be asked to honour another.
/// </summary>
public class OptionsFreezingTests
{
    [Fact]
    public void A_frozen_option_set_refuses_its_collections_too()
    {
        var options = new ErlSerializerOptions();
        options.MakeReadOnly();

        Assert.True(options.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => options.IgnoreNullValues = true);

        // A read-only property is not enough on its own: it stops the reference being replaced, not
        // the list being changed.
        Assert.Throws<InvalidOperationException>(() => options.Converters.Add(new ShoutingConverter()));
        Assert.Throws<InvalidOperationException>(() => options.Converters.Clear());
        Assert.Throws<InvalidOperationException>(() => options.ConverterFactories.Clear());
    }

    [Fact]
    public void The_shared_default_cannot_be_hijacked()
    {
        var before = ErlSerializer.Serialize(7, ErlSerializerOptions.Default);

        // Converters is consulted ahead of the built-in scalars, so this reached even int: every
        // later Serialize<int> anywhere in the process would have returned the hijacked value.
        Assert.Throws<InvalidOperationException>(
            () => ErlSerializerOptions.Default.Converters.Add(new ShoutingConverter()));

        Assert.Equal(before, ErlSerializer.Serialize(7, ErlSerializerOptions.Default));
    }

    [Fact]
    public void Copied_options_are_mutable_again_and_freeze_independently()
    {
        var frozen = new ErlSerializerOptions();
        frozen.MakeReadOnly();

        var copy = new ErlSerializerOptions(frozen);
        copy.Converters.Add(new ShoutingConverter());          // the documented way to override
        Assert.Single(copy.Converters);
        Assert.Empty(frozen.Converters);

        copy.MakeReadOnly();
        Assert.Throws<InvalidOperationException>(() => copy.Converters.Add(new ShoutingConverter()));
    }

    private sealed class ShoutingConverter : ErlConverter<int>
    {
        public override ErlTerm Write(int value, ErlSerializerOptions options) => new ErlAtom("hijacked");
        public override int Read(ErlTerm term, ErlSerializerOptions options) => 0;
    }
}
