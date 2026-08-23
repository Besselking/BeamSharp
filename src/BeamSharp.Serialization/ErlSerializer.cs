using BeamSharp.Serialization.Converters;
using BeamSharp.Terms;

namespace BeamSharp.Serialization;

/// <summary>
/// Converts C# objects to and from Erlang terms.
/// <para>
/// By default a type becomes a map with snake_case atom keys, so <c>record Person(string FirstName)</c>
/// arrives in Elixir as <c>%{first_name: "..."}</c>. Add <see cref="ErlStructAttribute"/> and it
/// arrives as a real <c>%MyApp.Person{}</c> instead.
/// </para>
/// </summary>
public static class ErlSerializer
{
    /// <summary>Converts a value to a term.</summary>
    public static ErlTerm Serialize<T>(T value, ErlSerializerOptions? options = null)
    {
        options ??= ErlSerializerOptions.Default;
        return value is null ? options.NullAtom : options.GetConverter<T>().Write(value, options);
    }

    /// <summary>Converts a value of a type known only at runtime.</summary>
    public static ErlTerm Serialize(object? value, Type type, ErlSerializerOptions? options = null)
    {
        options ??= ErlSerializerOptions.Default;
        return ValueHelper.Write(value, type, options);
    }

    /// <summary>Reads a term back into <typeparamref name="T"/>.</summary>
    public static T Deserialize<T>(ErlTerm term, ErlSerializerOptions? options = null)
    {
        options ??= ErlSerializerOptions.Default;
        return (T)ValueHelper.Read(term, typeof(T), options)!;
    }

    /// <summary>Reads a term back into a type known only at runtime.</summary>
    public static object? Deserialize(ErlTerm term, Type type, ErlSerializerOptions? options = null)
    {
        options ??= ErlSerializerOptions.Default;
        return ValueHelper.Read(term, type, options);
    }

    /// <summary>Converts a value using a generated context, with no reflection involved.</summary>
    public static ErlTerm Serialize<T>(T value, ErlSerializerContext context) =>
        Serialize(value, context.Options);

    /// <summary>Reads a term back into <typeparamref name="T"/> using a generated context.</summary>
    public static T Deserialize<T>(ErlTerm term, ErlSerializerContext context) =>
        Deserialize<T>(term, context.Options);

    /// <summary>
    /// Reads a term, returning false instead of throwing when it does not fit. Useful for dispatching
    /// on message shape, where a mismatch is an expected outcome rather than an error.
    /// </summary>
    public static bool TryDeserialize<T>(ErlTerm term, out T value, ErlSerializerOptions? options = null)
    {
        try
        {
            value = Deserialize<T>(term, options);
            return true;
        }
        catch (Exception ex) when (ex is ErlSerializationException or InvalidCastException or FormatException)
        {
            value = default!;
            return false;
        }
    }
}
