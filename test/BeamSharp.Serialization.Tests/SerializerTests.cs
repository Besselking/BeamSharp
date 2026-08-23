using System.Numerics;
using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Serialization.Tests;

public class NamingPolicyTests
{
    [Theory]
    [InlineData("FirstName", "first_name")]
    [InlineData("Age", "age")]
    [InlineData("ID", "id")]
    [InlineData("HTTPMethod", "http_method")]
    [InlineData("HTTPServerPort", "http_server_port")]
    [InlineData("Utf8Bytes", "utf8_bytes")]
    [InlineData("already_snake", "already_snake")]
    [InlineData("A", "a")]
    [InlineData("", "")]
    public void Converts_pascal_case_to_snake_case(string input, string expected)
    {
        Assert.Equal(expected, ErlNamingPolicy.SnakeCase.ConvertName(input));
    }

    [Fact]
    public void Other_policies_do_what_they_say()
    {
        Assert.Equal("firstName", ErlNamingPolicy.CamelCase.ConvertName("FirstName"));
        Assert.Equal("FirstName", ErlNamingPolicy.Unchanged.ConvertName("FirstName"));
    }
}

public class ScalarTests
{
    [Fact]
    public void Strings_become_binaries_because_that_is_what_an_elixir_string_is()
    {
        Assert.Equal(Erl.String("hello"), ErlSerializer.Serialize("hello", Reflected.Options));
        Assert.Equal("hello", ErlSerializer.Deserialize<string>(Erl.String("hello"), Reflected.Options));
    }

    [Fact]
    public void Strings_can_also_be_read_from_atoms_and_charlists()
    {
        Assert.Equal("ok", ErlSerializer.Deserialize<string>(Erl.Atom("ok"), Reflected.Options));
        Assert.Equal("hi", ErlSerializer.Deserialize<string>(Erl.CharList("hi"), Reflected.Options));
    }

    [Fact]
    public void Booleans_become_atoms()
    {
        Assert.Equal(Erl.Atom("true"), ErlSerializer.Serialize(true, Reflected.Options));
        Assert.False(ErlSerializer.Deserialize<bool>(Erl.Atom("false"), Reflected.Options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Integers_round_trip(int value)
    {
        Assert.Equal(value, ErlSerializer.Deserialize<int>(ErlSerializer.Serialize(value, Reflected.Options), Reflected.Options));
    }

    [Fact]
    public void Big_integers_round_trip()
    {
        var value = BigInteger.Parse("123456789012345678901234567890");
        Assert.Equal(value, ErlSerializer.Deserialize<BigInteger>(ErlSerializer.Serialize(value, Reflected.Options), Reflected.Options));
    }

    [Fact]
    public void An_integer_too_large_for_the_target_is_reported_clearly()
    {
        var ex = Assert.Throws<ErlSerializationException>(() =>
            ErlSerializer.Deserialize<byte>(Erl.Int(300), Reflected.Options));
        Assert.Contains("Byte", ex.Message);
    }

    [Fact]
    public void Byte_arrays_stay_binaries_rather_than_becoming_lists_of_integers()
    {
        var term = ErlSerializer.Serialize(new byte[] { 1, 2, 3 }, Reflected.Options);
        Assert.IsType<ErlBinary>(term);
        Assert.Equal(new byte[] { 1, 2, 3 }, ErlSerializer.Deserialize<byte[]>(term, Reflected.Options));
    }

    [Fact]
    public void Dates_and_guids_round_trip_as_text()
    {
        var now = new DateTime(2026, 8, 22, 13, 45, 30, DateTimeKind.Utc);
        Assert.Equal(now, ErlSerializer.Deserialize<DateTime>(ErlSerializer.Serialize(now, Reflected.Options), Reflected.Options));

        var id = Guid.NewGuid();
        Assert.Equal(id, ErlSerializer.Deserialize<Guid>(ErlSerializer.Serialize(id, Reflected.Options), Reflected.Options));
    }

    [Fact]
    public void Timespans_round_trip_as_microseconds()
    {
        var span = TimeSpan.FromMilliseconds(1500);
        Assert.Equal(Erl.Int(1_500_000), ErlSerializer.Serialize(span, Reflected.Options));
        Assert.Equal(span, ErlSerializer.Deserialize<TimeSpan>(ErlSerializer.Serialize(span, Reflected.Options), Reflected.Options));
    }

    [Fact]
    public void Timespans_truncate_below_a_microsecond()
    {
        // Documented lossiness: TimeSpan resolves to 100ns, the wire format to 1us.
        var span = TimeSpan.FromTicks(15); // 1.5 microseconds
        Assert.Equal(TimeSpan.FromTicks(10), ErlSerializer.Deserialize<TimeSpan>(ErlSerializer.Serialize(span, Reflected.Options), Reflected.Options));
    }

    [Fact]
    public void Terms_pass_through_untouched()
    {
        var pid = new ErlPid("a@b", 1, 0, 7);
        Assert.Same(pid, ErlSerializer.Serialize<ErlTerm>(pid));
        Assert.Equal(pid, ErlSerializer.Deserialize<ErlPid>(pid, Reflected.Options));
    }
}

public class ObjectShapeTests
{
    [Fact]
    public void A_record_becomes_a_map_with_snake_case_atom_keys()
    {
        var term = Assert.IsType<ErlMap>(ErlSerializer.Serialize(new Person("Ada", 36), Reflected.Options));

        Assert.Equal(2, term.Count);
        Assert.Equal(Erl.String("Ada"), term.Get("first_name"));
        Assert.Equal(Erl.Int(36), term.Get("age"));
    }

    [Fact]
    public void A_record_round_trips_through_its_primary_constructor()
    {
        var person = new Person("Ada", 36);
        Assert.Equal(person, ErlSerializer.Deserialize<Person>(ErlSerializer.Serialize(person, Reflected.Options), Reflected.Options));
    }

    [Fact]
    public void The_struct_attribute_produces_a_real_elixir_struct()
    {
        var term = Assert.IsType<ErlMap>(ErlSerializer.Serialize(new ElixirPerson("Ada", 36, "ada@example.com"), Reflected.Options));

        // Elixir structs are just maps carrying __struct__, so this arrives as %MyApp.Person{}.
        Assert.Equal(Erl.Atom("Elixir.MyApp.Person"), term.Get("__struct__"));
        Assert.Equal(Erl.String("Ada"), term.Get("first_name"));
    }

    [Fact]
    public void The_struct_attribute_accepts_the_elixir_spelling_of_the_module()
    {
        Assert.Equal("Elixir.MyApp.Person", new ErlStructAttribute("MyApp.Person").Module);
        Assert.Equal("Elixir.MyApp.Person", new ErlStructAttribute("Elixir.MyApp.Person").Module);
    }

    [Fact]
    public void A_struct_round_trips_and_ignores_the_struct_key_on_the_way_back()
    {
        var person = new ElixirPerson("Ada", 36, "ada@example.com");
        Assert.Equal(person, ErlSerializer.Deserialize<ElixirPerson>(ErlSerializer.Serialize(person, Reflected.Options), Reflected.Options));
    }

    [Fact]
    public void The_record_attribute_produces_a_tagged_tuple()
    {
        var term = Assert.IsType<ErlTuple>(ErlSerializer.Serialize(new Point(3, 4), Reflected.Options));

        Assert.Equal(3, term.Arity);
        Assert.Equal(Erl.Atom("point"), term[0]);
        Assert.Equal(Erl.Int(3), term[1]);
        Assert.Equal(Erl.Int(4), term[2]);
        Assert.Equal(new Point(3, 4), ErlSerializer.Deserialize<Point>(term, Reflected.Options));
    }

    [Fact]
    public void A_wrongly_tagged_tuple_is_rejected()
    {
        var ex = Assert.Throws<ErlSerializationException>(() =>
            ErlSerializer.Deserialize<Point>(Erl.Tuple(Erl.Atom("line"), Erl.Int(1), Erl.Int(2)), Reflected.Options));
        Assert.Contains("point", ex.Message);
    }

    [Fact]
    public void Settable_properties_are_used_when_there_is_a_parameterless_constructor()
    {
        var value = new Mutable { Name = "x", Count = 2, Status = Status.InProgress };
        var back = ErlSerializer.Deserialize<Mutable>(ErlSerializer.Serialize(value, Reflected.Options), Reflected.Options);

        Assert.Equal("x", back.Name);
        Assert.Equal(2, back.Count);
        Assert.Equal(Status.InProgress, back.Status);
    }

    [Fact]
    public void Missing_keys_fall_back_to_defaults_rather_than_failing()
    {
        var back = ErlSerializer.Deserialize<Mutable>(Erl.Map(("name", Erl.String("only"))), Reflected.Options);

        Assert.Equal("only", back.Name);
        Assert.Equal(0, back.Count);
    }

    [Fact]
    public void Unknown_keys_are_ignored()
    {
        var back = ErlSerializer.Deserialize<Person>(Erl.Map(
            ("first_name", Erl.String("Ada")), ("age", Erl.Int(36)), ("unexpected", Erl.Atom("junk"))), Reflected.Options);

        Assert.Equal(new Person("Ada", 36), back);
    }

    [Fact]
    public void Nested_objects_collections_and_dictionaries_round_trip()
    {
        var value = new Nested(
            new Person("Ada", 36),
            [new Person("Grace", 45), new Person("Alan", 41)],
            new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 });

        var back = ErlSerializer.Deserialize<Nested>(ErlSerializer.Serialize(value, Reflected.Options), Reflected.Options);

        Assert.Equal(value.Owner, back.Owner);
        Assert.Equal(value.Friends, back.Friends);
        Assert.Equal(value.Scores, back.Scores);
    }

    [Fact]
    public void A_type_with_no_usable_constructor_says_so()
    {
        // Its only constructor takes 'required', which no member is named after.
        var ex = Assert.Throws<ErlSerializationException>(() =>
            ErlSerializer.Deserialize<NoUsableConstructor>(Erl.Map(("other", Erl.String("x"))), Reflected.Options));
        Assert.Contains("no parameterless constructor", ex.Message);
    }

    [Fact]
    public void Two_members_mapping_to_one_key_is_caught_rather_than_silently_dropping_data()
    {
        var ex = Assert.Throws<ErlSerializationException>(() =>
            ErlSerializer.Serialize(new DuplicateNames(), Reflected.Options));
        Assert.Contains("more than one member", ex.Message);
    }
}

public class CollectionTests
{
    [Fact]
    public void Lists_and_arrays_become_erlang_lists()
    {
        Assert.Equal(Erl.List(Erl.Int(1), Erl.Int(2)), ErlSerializer.Serialize(new[] { 1, 2 }, Reflected.Options));
        Assert.Equal([1, 2], ErlSerializer.Deserialize<int[]>(Erl.List(Erl.Int(1), Erl.Int(2)), Reflected.Options));
        Assert.Equal([1, 2], ErlSerializer.Deserialize<List<int>>(Erl.List(Erl.Int(1), Erl.Int(2)), Reflected.Options));
    }

    [Fact]
    public void Interface_typed_sequences_materialise_as_lists()
    {
        IReadOnlyList<string> value = ["a", "b"];
        var back = ErlSerializer.Deserialize<IReadOnlyList<string>>(
            ErlSerializer.Serialize(value, Reflected.Options), Reflected.Options);
        Assert.Equal(value, back);
    }

    [Fact]
    public void Sets_round_trip()
    {
        var value = new HashSet<int> { 1, 2, 3 };
        Assert.Equal(value, ErlSerializer.Deserialize<HashSet<int>>(
            ErlSerializer.Serialize(value, Reflected.Options), Reflected.Options));
    }

    [Fact]
    public void Dictionaries_become_maps()
    {
        var term = Assert.IsType<ErlMap>(ErlSerializer.Serialize(new Dictionary<string, int> { ["a"] = 1 }, Reflected.Options));
        Assert.Equal(Erl.Int(1), term[Erl.String("a")]);
    }

    [Fact]
    public void A_dictionary_of_object_uses_each_value_runtime_type()
    {
        var term = Assert.IsType<ErlMap>(ErlSerializer.Serialize(
            new Dictionary<string, object> { ["n"] = 1, ["s"] = "x", ["b"] = true }, Reflected.Options));

        Assert.Equal(Erl.Int(1), term[Erl.String("n")]);
        Assert.Equal(Erl.String("x"), term[Erl.String("s")]);
        Assert.Equal(Erl.Atom("true"), term[Erl.String("b")]);
    }

    [Fact]
    public void Csharp_tuples_map_straight_onto_erlang_tuples()
    {
        var term = Assert.IsType<ErlTuple>(ErlSerializer.Serialize((1, "two", true), Reflected.Options));

        Assert.Equal(3, term.Arity);
        Assert.Equal(Erl.Int(1), term[0]);
        Assert.Equal((1, "two", true), ErlSerializer.Deserialize<(int, string, bool)>(term, Reflected.Options));
    }

    [Fact]
    public void The_ok_tuple_idiom_works_naturally()
    {
        var term = ErlSerializer.Serialize((Erl.Atom("ok"), new Person("Ada", 36)), Reflected.Options);
        var tuple = Assert.IsType<ErlTuple>(term);

        Assert.Equal(Erl.Atom("ok"), tuple[0]);
        Assert.Equal(Erl.String("Ada"), ((ErlMap)tuple[1]).Get("first_name"));
    }
}

public class EnumAndNullTests
{
    [Fact]
    public void Enum_members_become_snake_case_atoms()
    {
        Assert.Equal(Erl.Atom("active"), ErlSerializer.Serialize(Status.Active, Reflected.Options));
        Assert.Equal(Erl.Atom("in_progress"), ErlSerializer.Serialize(Status.InProgress, Reflected.Options));
        Assert.Equal(Erl.Atom("on_hold"), ErlSerializer.Serialize(Status.OnHold, Reflected.Options));
    }

    [Fact]
    public void An_enum_member_can_override_its_atom()
    {
        Assert.Equal(Erl.Atom("done"), ErlSerializer.Serialize(Status.Completed, Reflected.Options));
        Assert.Equal(Status.Completed, ErlSerializer.Deserialize<Status>(Erl.Atom("done"), Reflected.Options));
    }

    [Fact]
    public void An_unknown_enum_atom_lists_the_ones_that_would_work()
    {
        var ex = Assert.Throws<ErlSerializationException>(() =>
            ErlSerializer.Deserialize<Status>(Erl.Atom("nope"), Reflected.Options));

        Assert.Contains("in_progress", ex.Message);
    }

    [Fact]
    public void Null_becomes_nil_and_comes_back()
    {
        Assert.Equal(Erl.Atom("nil"), ErlSerializer.Serialize<string?>(null));
        Assert.Null(ErlSerializer.Deserialize<string?>(Erl.Atom("nil"), Reflected.Options));
        Assert.Null(ErlSerializer.Deserialize<int?>(Erl.Atom("nil"), Reflected.Options));
    }

    [Fact]
    public void Nullable_values_round_trip()
    {
        Assert.Equal(5, ErlSerializer.Deserialize<int?>(
            ErlSerializer.Serialize<int?>(5, Reflected.Options), Reflected.Options));
    }

    [Fact]
    public void Reading_nil_into_a_non_nullable_value_type_is_an_error_not_a_zero()
    {
        var ex = Assert.Throws<ErlSerializationException>(() => ErlSerializer.Deserialize<int>(Erl.Atom("nil"), Reflected.Options));
        Assert.Contains("cannot be null", ex.Message);
    }
}

public class AttributeAndConverterTests
{
    [Fact]
    public void Attributes_rename_hide_and_atomise_members()
    {
        var value = new Annotated { Identifier = Guid.Empty, Level = "warning" };
        var term = Assert.IsType<ErlMap>(ErlSerializer.Serialize(value, Reflected.Options));

        Assert.NotNull(term.Get("id"));
        Assert.Null(term.Get("secret"));
        Assert.Equal(Erl.Atom("warning"), term.Get("level"));
        Assert.Equal(Erl.String("GET"), term.Get("http_method"));
    }

    [Fact]
    public void A_converter_attribute_on_a_type_is_honoured()
    {
        Assert.Equal(Erl.Float(21.5), ErlSerializer.Serialize(new Temperature(21.5), Reflected.Options));
        Assert.Equal(new Temperature(21.5), ErlSerializer.Deserialize<Temperature>(Erl.Float(21.5), Reflected.Options));
    }

    [Fact]
    public void A_registered_converter_takes_priority_over_reflection()
    {
        var options = new ErlSerializerOptions().AddReflectionFallback();
        options.Converters.Add(new MoneyAsTupleConverter());

        var term = Assert.IsType<ErlTuple>(ErlSerializer.Serialize(new Money(12.34m, "EUR"), options));

        Assert.Equal(Erl.Atom("money"), term[0]);
        Assert.Equal(Erl.Int(1234), term[1]);
        Assert.Equal(new Money(12.34m, "EUR"), ErlSerializer.Deserialize<Money>(term, options));
    }

    [Fact]
    public void Without_that_converter_the_same_type_falls_back_to_a_map()
    {
        Assert.IsType<ErlMap>(ErlSerializer.Serialize(new Money(12.34m, "EUR"), Reflected.Options));
    }
}

public class OptionsTests
{
    [Fact]
    public void Binary_keys_can_be_chosen_for_untrusted_field_names()
    {
        var options = new ErlSerializerOptions { MapKeyKind = ErlMapKeyKind.Binary }.AddReflectionFallback();
        var term = Assert.IsType<ErlMap>(ErlSerializer.Serialize(new Person("Ada", 36), options));

        Assert.Equal(Erl.String("Ada"), term[Erl.String("first_name")]);
        Assert.Null(term.Get("first_name"));
    }

    [Fact]
    public void Either_key_flavour_is_accepted_when_reading()
    {
        var atomKeyed = Erl.Map(("first_name", Erl.String("Ada")), ("age", Erl.Int(36)));
        var binaryKeyed = new ErlMap([
            new KeyValuePair<ErlTerm, ErlTerm>(Erl.String("first_name"), Erl.String("Ada")),
            new KeyValuePair<ErlTerm, ErlTerm>(Erl.String("age"), Erl.Int(36))
        ]);

        Assert.Equal(new Person("Ada", 36), ErlSerializer.Deserialize<Person>(atomKeyed, Reflected.Options));
        Assert.Equal(new Person("Ada", 36), ErlSerializer.Deserialize<Person>(binaryKeyed, Reflected.Options));
    }

    [Fact]
    public void Nulls_can_be_omitted_entirely()
    {
        var options = new ErlSerializerOptions { IgnoreNullValues = true }.AddReflectionFallback();
        var term = Assert.IsType<ErlMap>(ErlSerializer.Serialize(new ElixirPerson("Ada", 36), options));

        Assert.Null(term.Get("email"));
        Assert.Equal(2, term.Count - 1); // first_name and age, plus __struct__
    }

    [Fact]
    public void Erlang_style_undefined_can_replace_nil()
    {
        var options = new ErlSerializerOptions { NullValue = ErlNullValue.Undefined }.AddReflectionFallback();
        Assert.Equal(Erl.Atom("undefined"), ErlSerializer.Serialize<string?>(null, options));
    }

    [Fact]
    public void Naming_can_be_switched_off()
    {
        var options = new ErlSerializerOptions { PropertyNamingPolicy = ErlNamingPolicy.Unchanged }.AddReflectionFallback();
        var term = Assert.IsType<ErlMap>(ErlSerializer.Serialize(new Person("Ada", 36), options));

        Assert.Equal(Erl.String("Ada"), term.Get("FirstName"));
    }

    [Fact]
    public void Fields_are_opt_in()
    {
        var withoutFields = Assert.IsType<ErlMap>(ErlSerializer.Serialize(new WithFields { Included = 7 }, Reflected.Options));
        Assert.Null(withoutFields.Get("included"));

        var options = new ErlSerializerOptions { IncludeFields = true }.AddReflectionFallback();
        var withFields = Assert.IsType<ErlMap>(ErlSerializer.Serialize(new WithFields { Included = 7 }, options));
        Assert.Equal(Erl.Int(7), withFields.Get("included"));
    }

    [Fact]
    public void Options_freeze_on_first_use_so_a_stale_cache_cannot_be_observed()
    {
        var options = new ErlSerializerOptions();
        ErlSerializer.Serialize(1, options);

        Assert.True(options.IsReadOnly);
        var ex = Assert.Throws<InvalidOperationException>(() => options.MapKeyKind = ErlMapKeyKind.Binary);
        Assert.Contains("already in use", ex.Message);
    }

    [Fact]
    public void Frozen_options_can_be_copied_and_then_changed()
    {
        var copy = new ErlSerializerOptions(ErlSerializerOptions.Default) { MapKeyKind = ErlMapKeyKind.Binary };
        Assert.Equal(ErlMapKeyKind.Binary, copy.MapKeyKind);
    }

    [Fact]
    public void Without_the_reflection_fallback_a_plain_type_fails_loudly()
    {
        // The core package has no reflection at all now, so this is what an AOT app sees when it
        // forgets to declare a type: a failure at the call site naming the type and the two fixes.
        var options = new ErlSerializerOptions();

        // Built-in conversions still work; only the fallback is absent.
        Assert.Equal(Erl.Int(1), ErlSerializer.Serialize(1, options));

        var ex = Assert.Throws<ErlSerializationException>(() =>
            ErlSerializer.Serialize(new Person("Ada", 36), options));

        Assert.Contains("ErlSerializable(typeof(Person))", ex.Message);
        Assert.Contains("AddReflectionFallback", ex.Message);
    }

    [Fact]
    public void A_registered_converter_satisfies_a_type_without_any_fallback()
    {
        var options = new ErlSerializerOptions();
        options.Converters.Add(new MoneyAsTupleConverter());

        Assert.IsType<ErlTuple>(ErlSerializer.Serialize(new Money(1m, "EUR"), options));
    }
}

public class WireTests
{
    [Fact]
    public void Serialized_objects_survive_the_actual_external_term_format()
    {
        // The point of the exercise: what we build has to encode and decode as a real term.
        var value = new Nested(
            new Person("Ada", 36),
            [new Person("Grace", 45)],
            new Dictionary<string, int> { ["a"] = 1 });

        var encoded = TermEncoder.Encode(ErlSerializer.Serialize(value, Reflected.Options));
        var back = ErlSerializer.Deserialize<Nested>(TermDecoder.Decode(encoded), Reflected.Options);

        Assert.Equal(value.Owner, back.Owner);
        Assert.Equal(value.Friends, back.Friends);
        Assert.Equal(value.Scores, back.Scores);
    }

    [Fact]
    public void A_struct_encodes_to_the_bytes_elixir_expects_for_a_struct()
    {
        var encoded = TermEncoder.Encode(ErlSerializer.Serialize(new ElixirPerson("Ada", 36), Reflected.Options));
        var map = Assert.IsType<ErlMap>(TermDecoder.Decode(encoded));

        Assert.Equal(Erl.Atom("Elixir.MyApp.Person"), map.Get("__struct__"));
    }
}
