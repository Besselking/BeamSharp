using BeamSharp.Terms;

namespace BeamSharp.Serialization;

/// <summary>
/// Converts one CLR type to and from an Erlang term.
/// <para>
/// This is the only extension point in the serializer, deliberately. Built-in conversions, the
/// reflection fallback for plain objects and any converter a source generator emits are all the
/// same kind of thing, so replacing reflection with generated code later is a matter of registering
/// different converters rather than a different API.
/// </para>
/// </summary>
public abstract class ErlConverter
{
    /// <summary>The CLR type this converter handles.</summary>
    public abstract Type HandledType { get; }

    internal abstract ErlTerm WriteUntyped(object? value, ErlSerializerOptions options);
    internal abstract object? ReadUntyped(ErlTerm term, ErlSerializerOptions options);
}

/// <summary>Converts <typeparamref name="T"/> to and from an Erlang term.</summary>
public abstract class ErlConverter<T> : ErlConverter
{
    public override Type HandledType => typeof(T);

    /// <summary>Writes <paramref name="value"/> as a term.</summary>
    public abstract ErlTerm Write(T value, ErlSerializerOptions options);

    /// <summary>Reads a <typeparamref name="T"/> back out of <paramref name="term"/>.</summary>
    public abstract T Read(ErlTerm term, ErlSerializerOptions options);

    internal override ErlTerm WriteUntyped(object? value, ErlSerializerOptions options) =>
        Write((T)value!, options);

    internal override object? ReadUntyped(ErlTerm term, ErlSerializerOptions options) =>
        Read(term, options);
}

/// <summary>
/// Produces converters for a family of types — generic collections, enums, tuples and so on —
/// where one converter per closed type cannot be registered up front.
/// </summary>
public abstract class ErlConverterFactory
{
    /// <summary>True when <see cref="CreateConverter"/> can handle <paramref name="type"/>.</summary>
    public abstract bool CanConvert(Type type);

    /// <summary>Builds a converter for the given closed type.</summary>
    public abstract ErlConverter CreateConverter(Type type, ErlSerializerOptions options);
}

/// <summary>Thrown when a value cannot be converted in either direction.</summary>
public sealed class ErlSerializationException : Exception
{
    /// <summary>Creates the exception with an explanatory message.</summary>
    public ErlSerializationException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and an underlying cause.</summary>
    public ErlSerializationException(string message, Exception inner) : base(message, inner) { }
}
