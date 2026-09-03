using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Serialization.Tests;

/// <summary>
/// <c>object</c> is asymmetric: writing one can look at the runtime type, reading one has nothing
/// to look at. Left to the reflection fallback it went quiet in both directions — an empty map out,
/// a bare instance back — so these pin down that it now either does the right thing or says why it
/// cannot.
/// </summary>
public class ObjectTypedTests
{
    private static readonly ErlSerializerOptions Reflected = ErlReflection.Default;

    [Fact]
    public void Writing_through_a_static_object_uses_the_runtime_type()
    {
        var person = new Person("Ada", 36);
        object boxed = person;

        // The generic overload used to resolve a converter for object itself and write %{}, while
        // the Type overload got this right. Two overloads of one method, disagreeing silently.
        Assert.Equal(
            ErlSerializer.Serialize(person, Reflected),
            ErlSerializer.Serialize(boxed, Reflected));

        Assert.Equal(
            ErlSerializer.Serialize(boxed, typeof(object), Reflected),
            ErlSerializer.Serialize(boxed, Reflected));
    }

    [Fact]
    public void A_dictionary_of_mixed_values_still_writes()
    {
        var values = new Dictionary<string, object> { ["name"] = "Ada", ["age"] = 36 };

        var map = Assert.IsType<ErlMap>(ErlSerializer.Serialize(values, Reflected));
        Assert.Equal(new ErlBinary("Ada"), map[new ErlBinary("name")]);
        Assert.Equal(new ErlInt(36), map[new ErlBinary("age")]);
    }

    [Fact]
    public void Reading_into_object_says_why_it_cannot()
    {
        var term = ErlSerializer.Serialize(new Person("Ada", 36), Reflected);

        var ex = Assert.Throws<ErlSerializationException>(
            () => ErlSerializer.Deserialize<object>(term, Reflected));
        Assert.Contains("nothing in a term says which type to build", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ErlTerm", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reading_a_member_typed_object_says_why_it_cannot()
    {
        var term = Erl.Map(("payload", ErlSerializer.Serialize(new Person("Ada", 36), Reflected)));

        Assert.Throws<ErlSerializationException>(
            () => ErlSerializer.Deserialize<Parcel>(term, Reflected));
    }

    [Fact]
    public void A_bare_object_instance_is_refused_rather_than_written_as_an_empty_map()
    {
        Assert.Throws<ErlSerializationException>(() => ErlSerializer.Serialize(new object(), Reflected));
    }

    [Fact]
    public void An_ErlTerm_member_is_the_way_to_carry_something_not_known_up_front()
    {
        var value = new Envelope("person", ErlSerializer.Serialize(new Person("Ada", 36), Reflected));

        var back = ErlSerializer.Deserialize<Envelope>(ErlSerializer.Serialize(value, Reflected), Reflected);

        Assert.Equal(value.Kind, back.Kind);
        Assert.Equal(value.Payload, back.Payload);
        Assert.Equal(new Person("Ada", 36), ErlSerializer.Deserialize<Person>(back.Payload, Reflected));
    }

    [Fact]
    public void A_dictionary_of_terms_round_trips_where_one_of_objects_cannot()
    {
        var values = new Dictionary<string, ErlTerm>
        {
            ["name"] = new ErlBinary("Ada"),
            ["age"] = new ErlInt(36)
        };

        var back = ErlSerializer.Deserialize<Dictionary<string, ErlTerm>>(
            ErlSerializer.Serialize(values, Reflected), Reflected);

        Assert.Equal(values, back);
    }
}

/// <summary>An object member: writable through the runtime type, unreadable by design.</summary>
public record Parcel(object Payload);
