using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Serialization.Tests;

/// <summary>
/// The generator's contract is that it produces the same terms the reflection converter does. These
/// tests assert exactly that, rather than re-describing the expected shape a second time — a
/// duplicated description could drift, an equivalence check cannot.
/// </summary>
public class GeneratorEquivalenceTests
{
    private static readonly ErlSerializerOptions Reflected = new();

    private static void AssertSame<T>(T value, ErlSerializerOptions? reflected = null,
        ErlSerializerContext? generated = null)
    {
        reflected ??= Reflected;
        generated ??= TestContext.Default;

        var byReflection = ErlSerializer.Serialize(value, reflected);
        var byGenerator = ErlSerializer.Serialize(value, generated);

        Assert.Equal(byReflection, byGenerator);

        // Reading is compared by re-serializing what came back, rather than by comparing the objects:
        // several of these types are plain classes with no structural equality, and terms have it.
        Assert.Equal(
            ErlSerializer.Serialize(ErlSerializer.Deserialize<T>(byReflection, reflected), reflected),
            ErlSerializer.Serialize(ErlSerializer.Deserialize<T>(byGenerator, generated), generated));

        // Each side also has to read what the other wrote.
        Assert.Equal(
            ErlSerializer.Serialize(ErlSerializer.Deserialize<T>(byGenerator, reflected), reflected),
            ErlSerializer.Serialize(ErlSerializer.Deserialize<T>(byReflection, generated), generated));
    }

    [Fact]
    public void A_record_matches()
    {
        AssertSame(new Person("Ada", 36));
    }

    [Fact]
    public void A_struct_shaped_type_matches()
    {
        AssertSame(new ElixirPerson("Ada", 36, "ada@example.com"));
        AssertSame(new ElixirPerson("Grace", 45));   // the optional parameter left at its default
    }

    [Fact]
    public void A_tuple_shaped_type_matches()
    {
        AssertSame(new Point(3, 4));
    }

    [Fact]
    public void A_mutable_class_matches()
    {
        AssertSame(new Mutable { Name = "x", Count = 2, Status = Status.InProgress });
        AssertSame(new Mutable());
    }

    [Fact]
    public void Member_attributes_match()
    {
        AssertSame(new Annotated { Identifier = Guid.NewGuid(), Level = "warning", HTTPMethod = "POST" });
    }

    [Fact]
    public void Nested_objects_and_collections_match()
    {
        AssertSame(new Nested(
            new Person("Ada", 36),
            [new Person("Grace", 45), new Person("Alan", 41)],
            new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }));
    }

    [Fact]
    public void An_init_only_member_keeps_its_initialiser_default_when_the_key_is_absent()
    {
        // The generated code cannot assign an init-only member after construction, so it reads the
        // fallback off a template instance. This is where that has to show up.
        var term = Erl.Map(("included", Erl.Int(7)));

        Assert.Equal(
            ErlSerializer.Deserialize<WithFields>(term, Reflected),
            ErlSerializer.Deserialize<WithFields>(term, TestContext.Default));
    }

    [Theory]
    [InlineData(ErlMapKeyKind.Binary)]
    [InlineData(ErlMapKeyKind.Atom)]
    public void Key_kind_is_honoured_identically(ErlMapKeyKind kind)
    {
        var reflected = new ErlSerializerOptions { MapKeyKind = kind };
        AssertSame(new Person("Ada", 36), reflected, new TestContext(reflected));
    }

    [Fact]
    public void Naming_policy_is_honoured_identically()
    {
        // Names are resolved when the converter is built, not when it is generated, which is what
        // keeps a compile-time converter responsive to a runtime setting.
        var reflected = new ErlSerializerOptions { PropertyNamingPolicy = ErlNamingPolicy.Unchanged };
        AssertSame(new Annotated { Level = "info" }, reflected, new TestContext(reflected));

        var camel = new ErlSerializerOptions { PropertyNamingPolicy = ErlNamingPolicy.CamelCase };
        AssertSame(new Person("Ada", 36), camel, new TestContext(camel));
    }

    [Fact]
    public void Ignoring_nulls_is_honoured_identically()
    {
        var reflected = new ErlSerializerOptions { IgnoreNullValues = true };
        AssertSame(new ElixirPerson("Ada", 36), reflected, new TestContext(reflected));
    }

    [Fact]
    public void Undefined_instead_of_nil_is_honoured_identically()
    {
        var reflected = new ErlSerializerOptions { NullValue = ErlNullValue.Undefined };
        AssertSame(new ElixirPerson("Ada", 36), reflected, new TestContext(reflected));
    }

    [Fact]
    public void Including_fields_is_honoured_identically()
    {
        var reflected = new ErlSerializerOptions { IncludeFields = true };
        AssertSame(new WithFields { Included = 7, Name = "x" }, reflected, new TestContext(reflected));
    }

    [Fact]
    public void A_type_with_its_own_converter_attribute_is_left_to_that_converter()
    {
        // The generator skips it, so the attribute factory picks it up even with reflection off.
        Assert.Equal(Erl.Float(21.5), ErlSerializer.Serialize(new Temperature(21.5), TestContext.Default));
    }

    [Fact]
    public void Duplicate_keys_fail_the_same_way()
    {
        var reflected = Assert.Throws<ErlSerializationException>(() =>
            ErlSerializer.Serialize(new DuplicateNames(), Reflected));
        var generated = Assert.Throws<ErlSerializationException>(() =>
            ErlSerializer.Serialize(new DuplicateNames(), TestContext.Default));

        Assert.Equal(reflected.Message, generated.Message);
    }

    [Fact]
    public void A_bad_tuple_tag_fails_the_same_way()
    {
        var bad = Erl.Tuple(Erl.Atom("line"), Erl.Int(1), Erl.Int(2));

        var reflected = Assert.Throws<ErlSerializationException>(() =>
            ErlSerializer.Deserialize<Point>(bad, Reflected));
        var generated = Assert.Throws<ErlSerializationException>(() =>
            ErlSerializer.Deserialize<Point>(bad, TestContext.Default));

        Assert.Equal(reflected.Message, generated.Message);
    }

    [Fact]
    public void A_non_map_fails_the_same_way()
    {
        var reflected = Assert.Throws<ErlSerializationException>(() =>
            ErlSerializer.Deserialize<Person>(Erl.Int(1), Reflected));
        var generated = Assert.Throws<ErlSerializationException>(() =>
            ErlSerializer.Deserialize<Person>(Erl.Int(1), TestContext.Default));

        Assert.Equal(reflected.Message, generated.Message);
    }
}

public class GeneratedContextTests
{
    [Fact]
    public void A_context_switches_reflection_off()
    {
        Assert.False(TestContext.Default.Options.UseReflection);
        Assert.True(TestContext.Default.Options.IsReadOnly);
    }

    [Fact]
    public void An_undeclared_type_fails_at_the_call_site_rather_than_falling_back()
    {
        // The whole point: under AOT this is the failure you want, at the place you can fix it.
        var ex = Assert.Throws<ErlSerializationException>(() =>
            ErlSerializer.Serialize(new Undeclared("x"), TestContext.Default));

        Assert.Contains("UseReflection is off", ex.Message);
        Assert.Contains(nameof(Undeclared), ex.Message);
    }

    [Fact]
    public void Built_in_conversions_still_work_without_being_declared()
    {
        Assert.Equal(Erl.Int(1), ErlSerializer.Serialize(1, TestContext.Default));
        Assert.Equal(Erl.String("x"), ErlSerializer.Serialize("x", TestContext.Default));
        Assert.Equal(Erl.List(Erl.Int(1)), ErlSerializer.Serialize(new[] { 1 }, TestContext.Default));
    }

    [Fact]
    public void Generated_output_survives_the_external_term_format()
    {
        var value = new ElixirPerson("Ada", 36, "ada@example.com");
        var encoded = TermEncoder.Encode(ErlSerializer.Serialize(value, TestContext.Default));

        Assert.Equal(value, ErlSerializer.Deserialize<ElixirPerson>(TermDecoder.Decode(encoded), TestContext.Default));
    }
}
