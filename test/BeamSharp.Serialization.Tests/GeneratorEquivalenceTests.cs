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
    private static readonly ErlSerializerOptions Reflected = ErlReflection.Default;

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
        var reflected = new ErlSerializerOptions { MapKeyKind = kind }.AddReflectionFallback();
        AssertSame(new Person("Ada", 36), reflected, new TestContext(reflected));
    }

    [Fact]
    public void Naming_policy_is_honoured_identically()
    {
        // Names are resolved when the converter is built, not when it is generated, which is what
        // keeps a compile-time converter responsive to a runtime setting.
        var reflected = new ErlSerializerOptions { PropertyNamingPolicy = ErlNamingPolicy.Unchanged }.AddReflectionFallback();
        AssertSame(new Annotated { Level = "info" }, reflected, new TestContext(reflected));

        var camel = new ErlSerializerOptions { PropertyNamingPolicy = ErlNamingPolicy.CamelCase }.AddReflectionFallback();
        AssertSame(new Person("Ada", 36), camel, new TestContext(camel));
    }

    [Fact]
    public void Ignoring_nulls_is_honoured_identically()
    {
        var reflected = new ErlSerializerOptions { IgnoreNullValues = true }.AddReflectionFallback();
        AssertSame(new ElixirPerson("Ada", 36), reflected, new TestContext(reflected));
    }

    [Fact]
    public void Undefined_instead_of_nil_is_honoured_identically()
    {
        var reflected = new ErlSerializerOptions { NullValue = ErlNullValue.Undefined }.AddReflectionFallback();
        AssertSame(new ElixirPerson("Ada", 36), reflected, new TestContext(reflected));
    }

    [Fact]
    public void Including_fields_is_honoured_identically()
    {
        var reflected = new ErlSerializerOptions { IncludeFields = true }.AddReflectionFallback();
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
    public void A_context_adds_no_reflection_of_its_own()
    {
        // Nothing to switch off any more: the reflection fallback is a separate package, and a
        // context simply does not reference it.
        Assert.True(TestContext.Default.Options.IsReadOnly);
        Assert.DoesNotContain(TestContext.Default.Options.ConverterFactories,
            factory => factory.GetType().Assembly != typeof(ErlSerializer).Assembly
                       && factory.GetType().Namespace?.EndsWith("Reflection", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void An_undeclared_type_fails_at_the_call_site_rather_than_falling_back()
    {
        // The whole point: under AOT this is the failure you want, at the place you can fix it.
        var ex = Assert.Throws<ErlSerializationException>(() =>
            ErlSerializer.Serialize(new Undeclared("x"), TestContext.Default));

        Assert.Contains(nameof(Undeclared), ex.Message);
        Assert.Contains("ErlSerializable", ex.Message);
        Assert.Contains("AddReflectionFallback", ex.Message);
    }

    [Fact]
    public void Built_in_scalar_conversions_need_no_declaration()
    {
        Assert.Equal(Erl.Int(1), ErlSerializer.Serialize(1, TestContext.Default));
        Assert.Equal(Erl.String("x"), ErlSerializer.Serialize("x", TestContext.Default));
        Assert.Equal(Erl.Atom("true"), ErlSerializer.Serialize(true, TestContext.Default));
    }

    [Fact]
    public void A_collection_reached_from_a_declared_type_is_rooted_by_the_generator()
    {
        // Nested declares List<Person>, so its converter instantiation is written out and no
        // reflective factory is needed for it.
        var value = new Nested(new Person("Ada", 36), [new Person("Grace", 45)], new Dictionary<string, int>());
        var back = ErlSerializer.Deserialize<Nested>(
            ErlSerializer.Serialize(value, TestContext.Default), TestContext.Default);

        Assert.Equal("Grace", back.Friends[0].FirstName);
    }

    [Fact]
    public void A_declared_array_gets_a_converter()
    {
        // An array is an IArrayTypeSymbol rather than an INamedTypeSymbol, so it was being dropped
        // from the attribute list without a word. Silence is the part worth a regression test.
        Person[] people = [new Person("Ada", 36), new Person("Alan", 41)];

        var back = ErlSerializer.Deserialize<Person[]>(
            ErlSerializer.Serialize(people, TestContext.Default), TestContext.Default);

        Assert.Equal(people, back);
    }

    [Fact]
    public void A_declared_enum_and_tuple_get_converters()
    {
        Assert.Equal(Erl.Atom("in_progress"), ErlSerializer.Serialize(Status.InProgress, TestContext.Default));
        Assert.Equal((1, "two"), ErlSerializer.Deserialize<(int, string)>(
            ErlSerializer.Serialize((1, "two"), TestContext.Default), TestContext.Default));
    }

    [Fact]
    public void The_reflection_fallback_can_back_a_context_when_trimming_is_not_a_concern()
    {
        // Both together: generated converters are consulted first, reflection catches the rest.
        var context = new TestContext(new ErlSerializerOptions().AddReflectionFallback());

        Assert.Equal(
            ErlSerializer.Serialize(new Person("Ada", 36), TestContext.Default),
            ErlSerializer.Serialize(new Person("Ada", 36), context));

        // Undeclared, so this can only have come from the fallback.
        Assert.IsType<ErlMap>(ErlSerializer.Serialize(new Undeclared("x"), context));
    }

    [Fact]
    public void Generated_output_survives_the_external_term_format()
    {
        var value = new ElixirPerson("Ada", 36, "ada@example.com");
        var encoded = TermEncoder.Encode(ErlSerializer.Serialize(value, TestContext.Default));

        Assert.Equal(value, ErlSerializer.Deserialize<ElixirPerson>(TermDecoder.Decode(encoded), TestContext.Default));
    }
}
