using BeamSharp.Terms;

namespace BeamSharp.Serialization.Converters;

/// <summary>Central null handling and value dispatch, shared by every composite converter.</summary>
internal static class ValueHelper
{
    /// <summary>
    /// Writes a value using the converter for its declared type. When the declared type is
    /// <see cref="object"/> the runtime type is used instead, which is what makes a
    /// <c>Dictionary&lt;string, object&gt;</c> of mixed values work.
    /// </summary>
    public static ErlTerm Write(object? value, Type declaredType, ErlSerializerOptions options)
    {
        if (value is null) return options.NullAtom;

        var type = declaredType == typeof(object) ? value.GetType() : declaredType;
        return options.GetConverter(type).WriteUntyped(value, options);
    }

    /// <summary>Reads a value, mapping the null atom onto null for any type that can hold it.</summary>
    public static object? Read(ErlTerm term, Type declaredType, ErlSerializerOptions options)
    {
        if (IsNull(term, options))
        {
            if (!declaredType.IsValueType || Nullable.GetUnderlyingType(declaredType) is not null) return null;
            throw new ErlSerializationException(
                $"cannot read {term} into {declaredType}, which is a value type that cannot be null");
        }

        return options.GetConverter(declaredType).ReadUntyped(term, options);
    }

    /// <summary>True when the term is the configured null atom. Both nil and undefined are accepted.</summary>
    public static bool IsNull(ErlTerm term, ErlSerializerOptions options) =>
        term is ErlAtom a && (a.Name == options.NullAtom.Name || a.Name is "nil" or "undefined");
}

/// <summary>Maps <c>Nullable&lt;T&gt;</c> onto the underlying converter plus a null atom.</summary>
internal sealed class NullableConverterFactory : ErlConverterFactory
{
    public static readonly NullableConverterFactory Instance = new();

    public override bool CanConvert(Type type) => Nullable.GetUnderlyingType(type) is not null;

    public override ErlConverter CreateConverter(Type type, ErlSerializerOptions options)
    {
        var inner = Nullable.GetUnderlyingType(type)!;
        return ConverterActivator.Create(
            typeof(NullableConverter<>).MakeGenericType(inner), options.GetConverter(inner));
    }

    private sealed class NullableConverter<T>(ErlConverter<T> inner) : ErlConverter<T?> where T : struct
    {
        public override ErlTerm Write(T? value, ErlSerializerOptions options) =>
            value.HasValue ? inner.Write(value.Value, options) : options.NullAtom;

        public override T? Read(ErlTerm term, ErlSerializerOptions options) =>
            ValueHelper.IsNull(term, options) ? null : inner.Read(term, options);
    }
}

/// <summary>
/// Writes enum members as atoms — <c>Status.InProgress</c> becomes <c>:in_progress</c>. A value that
/// is not a declared member (a flags combination, say) falls back to its integer.
/// </summary>
internal sealed class EnumConverterFactory : ErlConverterFactory
{
    public static readonly EnumConverterFactory Instance = new();

    public override bool CanConvert(Type type) => type.IsEnum;

    public override ErlConverter CreateConverter(Type type, ErlSerializerOptions options) =>
        ConverterActivator.Create(typeof(EnumConverter<>).MakeGenericType(type), options);

    private sealed class EnumConverter<T> : ErlConverter<T> where T : struct, Enum
    {
        private readonly Dictionary<T, string> _toAtom = new();
        private readonly Dictionary<string, T> _fromAtom = new(StringComparer.Ordinal);

        public EnumConverter(ErlSerializerOptions options)
        {
            foreach (var name in Enum.GetNames<T>())
            {
                var value = Enum.Parse<T>(name);
                var field = typeof(T).GetField(name)!;
                var atom = field.GetCustomAttributes(typeof(ErlPropertyAttribute), false)
                               .Cast<ErlPropertyAttribute>().FirstOrDefault()?.Name
                           ?? options.EnumNamingPolicy.ConvertName(name);

                _toAtom.TryAdd(value, atom);
                _fromAtom[atom] = value;
            }
        }

        public override ErlTerm Write(T value, ErlSerializerOptions options) =>
            _toAtom.TryGetValue(value, out var atom)
                ? new ErlAtom(atom)
                : new ErlInt(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));

        public override T Read(ErlTerm term, ErlSerializerOptions options)
        {
            switch (term)
            {
                case ErlAtom a when _fromAtom.TryGetValue(a.Name, out var value):
                    return value;
                case ErlAtom a:
                    throw new ErlSerializationException(
                        $"'{a.Name}' is not a member of {typeof(T).Name}; expected one of " +
                        string.Join(", ", _fromAtom.Keys));
                case ErlInt i:
                    return (T)Enum.ToObject(typeof(T), (long)i.Value);
                default:
                    throw TermRead.Mismatch(term, $"an atom naming a {typeof(T).Name}");
            }
        }
    }
}
