using System.Collections;
using System.Runtime.CompilerServices;
using BeamSharp.Terms;

namespace BeamSharp.Serialization.Converters;

/// <summary>Maps sequences onto Erlang lists.</summary>
internal sealed class CollectionConverterFactory : ErlConverterFactory
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
internal sealed class DictionaryConverterFactory : ErlConverterFactory
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
internal sealed class TupleConverterFactory : ErlConverterFactory
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
internal sealed class AttributeConverterFactory : ErlConverterFactory
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
