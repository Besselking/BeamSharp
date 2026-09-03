using BeamSharp.Terms;

namespace BeamSharp.Serialization.Converters;

/// <summary>Central null handling and value dispatch, shared by every composite converter.</summary>
internal static class ValueHelper
{
    /// <summary>
    /// Writes a value using the converter for its declared type. When the declared type is
    /// <see cref="object"/> the runtime type is used instead, which is what makes a
    /// <c>Dictionary&lt;string, object&gt;</c> of mixed values work.
    /// <para>
    /// Every composite converter writes its parts through here — the reflection converter, the
    /// generated ones by way of <see cref="ErlGenerated.Write{T}"/>, collections and dictionaries —
    /// so this is the one place a cycle can be caught for all of them at once.
    /// </para>
    /// </summary>
    public static ErlTerm Write(object? value, Type declaredType, ErlSerializerOptions options)
    {
        if (value is null) return options.NullAtom;

        var type = declaredType == typeof(object) ? value.GetType() : declaredType;

        using var guard = WriteGuard.Enter(value, type);
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
