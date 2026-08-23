using System.Collections;
using System.Runtime.CompilerServices;
using BeamSharp.Serialization.Converters;
using BeamSharp.Terms;

namespace BeamSharp.Serialization.Reflection;

/// <summary>Maps sequences onto Erlang lists.</summary>
public sealed class CollectionConverterFactory : ErlConverterFactory
{
    public static readonly CollectionConverterFactory Instance = new();

    public override bool CanConvert(Type type) => ElementType(type) is not null;

        [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = Justifications.ReflectionFallback)]
    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = Justifications.ReflectionFallback)]
    public override ErlConverter CreateConverter(Type type, ErlSerializerOptions options) =>
        ConverterActivator.Create(typeof(ErlCollectionConverter<,>).MakeGenericType(type, ElementType(type)!));

    /// <summary>The element type, or null when this is not a sequence we handle.</summary>
    internal static Type? ElementType(Type type)
    {
        // string is IEnumerable<char>, and byte[] has its own binary conversion.
        if (type == typeof(string) || type == typeof(byte[])) return null;
        if (DictionaryConverterFactory.KeyValueTypes(type) is not null) return null;

        if (type.IsArray) return type.GetElementType();

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(List<>) || definition == typeof(HashSet<>) ||
                definition == typeof(IEnumerable<>) || definition == typeof(ICollection<>) ||
                definition == typeof(IList<>) || definition == typeof(IReadOnlyList<>) ||
                definition == typeof(IReadOnlyCollection<>))
                return type.GetGenericArguments()[0];
        }

        return null;
    }

}

/// <summary>Maps dictionaries onto Erlang maps.</summary>
public sealed class DictionaryConverterFactory : ErlConverterFactory
{
    public static readonly DictionaryConverterFactory Instance = new();

    public override bool CanConvert(Type type) => KeyValueTypes(type) is not null;

        [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = Justifications.ReflectionFallback)]
    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = Justifications.ReflectionFallback)]
    public override ErlConverter CreateConverter(Type type, ErlSerializerOptions options)
    {
        var (key, value) = KeyValueTypes(type)!.Value;
        return ConverterActivator.Create(
            typeof(ErlDictionaryConverter<,,>).MakeGenericType(type, key, value));
    }

    internal static (Type Key, Type Value)? KeyValueTypes(Type type)
    {
        if (!type.IsGenericType) return null;

        var definition = type.GetGenericTypeDefinition();
        if (definition != typeof(Dictionary<,>) && definition != typeof(IDictionary<,>) &&
            definition != typeof(IReadOnlyDictionary<,>) && definition != typeof(SortedDictionary<,>))
            return null;

        var args = type.GetGenericArguments();
        return (args[0], args[1]);
    }

}

/// <summary>Maps C# tuples onto Erlang tuples, which is about as direct as the mapping gets.</summary>
public sealed class TupleConverterFactory : ErlConverterFactory
{
    public static readonly TupleConverterFactory Instance = new();

    public override bool CanConvert(Type type) =>
        typeof(ITuple).IsAssignableFrom(type) && type.IsGenericType;

        [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = Justifications.ReflectionFallback)]
    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = Justifications.ReflectionFallback)]
    public override ErlConverter CreateConverter(Type type, ErlSerializerOptions options) =>
        (ErlConverter)Activator.CreateInstance(typeof(TupleConverter<>).MakeGenericType(type))!;

    private sealed class TupleConverter<TTuple> : ErlConverter<TTuple> where TTuple : ITuple
    {
        private readonly Type[] _elements = typeof(TTuple).GetGenericArguments();

        public override ErlTerm Write(TTuple value, ErlSerializerOptions options)
        {
            var items = new ErlTerm[value.Length];
            for (var i = 0; i < value.Length; i++)
                items[i] = ValueHelper.Write(value[i], _elements[i], options);
            return new ErlTuple(items);
        }

        [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2087",
            Justification = Justifications.ReflectionFallback)]
        public override TTuple Read(ErlTerm term, ErlSerializerOptions options)
        {
            if (term is not ErlTuple tuple || tuple.Arity != _elements.Length)
                throw TermRead.Mismatch(term, $"a tuple of {_elements.Length} elements");

            var args = new object?[_elements.Length];
            for (var i = 0; i < args.Length; i++)
                args[i] = ValueHelper.Read(tuple[i], _elements[i], options);

            return (TTuple)Activator.CreateInstance(typeof(TTuple), args)!;
        }
    }
}

/// <summary>Honours <see cref="ErlConvertAttribute"/> on a type.</summary>
public sealed class AttributeConverterFactory : ErlConverterFactory
{
    public static readonly AttributeConverterFactory Instance = new();

    public override bool CanConvert(Type type) =>
        type.GetCustomAttributes(typeof(ErlConvertAttribute), false).Length > 0;

    public override ErlConverter CreateConverter(Type type, ErlSerializerOptions options)
    {
        var attribute = (ErlConvertAttribute)type.GetCustomAttributes(typeof(ErlConvertAttribute), false)[0];
        return Create(attribute.ConverterType, type);
    }

    /// <summary>Instantiates a converter type, checking it actually handles what it is attached to.</summary>
        [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = Justifications.ReflectionFallback)]
    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = Justifications.ReflectionFallback)]
    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = Justifications.ReflectionFallback)]
    internal static ErlConverter Create(Type converterType, Type handledType)
    {
        if (Activator.CreateInstance(converterType) is not ErlConverter converter)
            throw new ErlSerializationException(
                $"{converterType} is named in an [ErlConvert] attribute but does not derive from ErlConverter<T>");

        if (!converter.HandledType.IsAssignableFrom(handledType))
            throw new ErlSerializationException(
                $"{converterType} handles {converter.HandledType}, but it is attached to {handledType}");

        return converter;
    }
}

/// <summary>Maps <c>Nullable&lt;T&gt;</c> onto the underlying converter plus a null atom.</summary>
public sealed class NullableConverterFactory : ErlConverterFactory
{
    public static readonly NullableConverterFactory Instance = new();

    public override bool CanConvert(Type type) => Nullable.GetUnderlyingType(type) is not null;

        [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = Justifications.ReflectionFallback)]
    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2071",
        Justification = Justifications.ReflectionFallback)]
    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = Justifications.ReflectionFallback)]
    public override ErlConverter CreateConverter(Type type, ErlSerializerOptions options)
    {
        var inner = Nullable.GetUnderlyingType(type)!;
        return ConverterActivator.Create(typeof(ErlNullableConverter<>).MakeGenericType(inner), options);
    }

}

/// <summary>
/// Writes enum members as atoms — <c>Status.InProgress</c> becomes <c>:in_progress</c>. A value that
/// is not a declared member (a flags combination, say) falls back to its integer.
/// </summary>
public sealed class EnumConverterFactory : ErlConverterFactory
{
    public static readonly EnumConverterFactory Instance = new();

    public override bool CanConvert(Type type) => type.IsEnum;

    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = Justifications.ReflectionFallback)]
    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = Justifications.ReflectionFallback)]
    public override ErlConverter CreateConverter(Type type, ErlSerializerOptions options) =>
        ((IEnumBuilder)Activator.CreateInstance(typeof(EnumBuilder<>).MakeGenericType(type))!)
        .Build(options);

    private interface IEnumBuilder
    {
        ErlConverter Build(ErlSerializerOptions options);
    }

    /// <summary>
    /// Reads the enum's members and hands them to the converter. This is the only place that
    /// discovers them reflectively; generated code passes them in at compile time instead, which is
    /// why the converter itself needs no reflection.
    /// </summary>
    private sealed class EnumBuilder<T> : IEnumBuilder where T : struct, Enum
    {
        [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
            Justification = Justifications.ReflectionFallback)]
        public ErlConverter Build(ErlSerializerOptions options)
        {
            var members = Enum.GetNames<T>().Select(name =>
            {
                var explicitName = typeof(T).GetField(name)!
                    .GetCustomAttributes(typeof(ErlPropertyAttribute), false)
                    .Cast<ErlPropertyAttribute>().FirstOrDefault()?.Name;
                return (Enum.Parse<T>(name), name, explicitName);
            }).ToArray();

            return new ErlEnumConverter<T>(options, members);
        }
    }
}

/// <summary>Hands <see cref="ErlTerm"/> values through untouched, so raw terms can be embedded.</summary>
public sealed class TermPassthroughFactory : ErlConverterFactory
{
    public static readonly TermPassthroughFactory Instance = new();

    public override bool CanConvert(Type type) => typeof(ErlTerm).IsAssignableFrom(type);

        [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = Justifications.ReflectionFallback)]
    [global::System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = Justifications.ReflectionFallback)]
    public override ErlConverter CreateConverter(Type type, ErlSerializerOptions options) =>
        (ErlConverter)Activator.CreateInstance(typeof(PassthroughConverter<>).MakeGenericType(type))!;

    private sealed class PassthroughConverter<T> : ErlConverter<T> where T : ErlTerm
    {
        public override ErlTerm Write(T value, ErlSerializerOptions options) => value;

        public override T Read(ErlTerm term, ErlSerializerOptions options) =>
            term as T ?? throw TermRead.Mismatch(term, typeof(T).Name);
    }
}
