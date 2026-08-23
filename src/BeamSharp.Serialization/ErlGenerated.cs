using System.ComponentModel;
using BeamSharp.Serialization.Converters;
using BeamSharp.Terms;

namespace BeamSharp.Serialization;

/// <summary>
/// Helpers called by generated converters. Not part of the supported surface — the shape here
/// tracks whatever the generator needs.
/// <para>
/// Value conversion deliberately routes through the same code the reflection converter uses, so the
/// two cannot drift apart in how they treat nulls, declared versus runtime types, or missing keys.
/// </para>
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ErlGenerated
{
    /// <summary>Builds the key for a member, honouring the configured key kind.</summary>
    public static ErlTerm Key(ErlSerializerOptions options, string name, bool forceAtom) =>
        forceAtom || options.MapKeyKind == ErlMapKeyKind.Atom ? new ErlAtom(name) : new ErlBinary(name);

    /// <summary>Applies the naming policy unless the member declared a literal name.</summary>
    public static string Name(ErlSerializerOptions options, string clrName, string? explicitName) =>
        explicitName ?? options.PropertyNamingPolicy.ConvertName(clrName);

    /// <summary>Writes a member value, using its declared type exactly as reflection would.</summary>
    public static ErlTerm Write<T>(T value, ErlSerializerOptions options) =>
        ValueHelper.Write(value, typeof(T), options);

    /// <summary>Reads a member value back.</summary>
    public static T Read<T>(ErlTerm term, ErlSerializerOptions options) =>
        (T)ValueHelper.Read(term, typeof(T), options)!;

    /// <summary>Writes a string member marked <see cref="ErlAsAtomAttribute"/>.</summary>
    public static ErlTerm WriteAtomString(string? value, ErlSerializerOptions options) =>
        value is null ? options.NullAtom : new ErlAtom(value);

    /// <summary>Reads a string member marked <see cref="ErlAsAtomAttribute"/>.</summary>
    public static string? ReadAtomString(ErlTerm term, ErlSerializerOptions options) =>
        ValueHelper.IsNull(term, options) ? null : TermRead.Text(term);

    /// <summary>Writes through a converter named by <see cref="ErlConvertAttribute"/>.</summary>
    public static ErlTerm WriteVia<T>(ErlConverter<T> converter, T value, ErlSerializerOptions options) =>
        value is null ? options.NullAtom : converter.Write(value, options);

    /// <summary>Reads through a converter named by <see cref="ErlConvertAttribute"/>.</summary>
    public static T? ReadVia<T>(ErlConverter<T> converter, ErlTerm term, ErlSerializerOptions options) =>
        ValueHelper.IsNull(term, options) ? default : converter.Read(term, options);

    /// <summary>True when the term stands for null.</summary>
    public static bool IsNull(ErlTerm term, ErlSerializerOptions options) => ValueHelper.IsNull(term, options);

    /// <summary>Checks the term is a map, with the message the reflection converter would give.</summary>
    public static ErlMap ExpectMap(ErlTerm term, string typeName) =>
        term as ErlMap ?? throw new ErlSerializationException(
            $"expected a map to build a {typeName} from but the term was {term}");

    /// <summary>Checks the term is the right tagged tuple, matching the reflection converter's errors.</summary>
    public static ErlTuple ExpectTuple(ErlTerm term, string tag, int arity)
    {
        if (term is not ErlTuple tuple || tuple.Arity != arity)
            throw new ErlSerializationException(
                $"expected a {arity} element {tag} tuple but the term was {term}");

        if (!tuple[0].IsAtom(tag))
            throw new ErlSerializationException($"expected a tuple tagged {tag} but it was tagged {tuple[0]}");

        return tuple;
    }

    /// <summary>
    /// Rejects a type whose members collapse onto one key. The naming policy is a runtime setting, so
    /// this cannot be decided while generating and is checked when the converter is built instead.
    /// </summary>
    public static void EnsureDistinctKeys(string typeName, string[] names, string[] clrNames)
    {
        for (var i = 0; i < names.Length; i++)
            for (var j = i + 1; j < names.Length; j++)
                if (names[i] == names[j])
                    throw new ErlSerializationException(
                        $"{typeName} maps more than one member onto '{names[i]}': {clrNames[i]}, {clrNames[j]}");
    }
}
