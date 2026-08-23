using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace BeamSharp.Serialization.Generator;

/// <summary>
/// Walks the member type graph collecting the generic instantiations the generated factory has to
/// construct by hand. Anything the runtime already has a non-generic converter for is left alone.
/// </summary>
internal sealed class AuxiliaryCollector(HashSet<string> declaredTypeNames)
{
    private static readonly HashSet<string> BuiltIn =
    [
        "string", "char", "bool", "sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong",
        "float", "double", "decimal", "byte[]",
        "System.Numerics.BigInteger", "System.DateTime", "System.DateTimeOffset", "System.DateOnly",
        "System.TimeOnly", "System.TimeSpan", "System.Guid", "System.Uri", "object"
    ];

    private readonly Dictionary<string, AuxiliaryModel> _found = new(StringComparer.Ordinal);

    public ImmutableArray<AuxiliaryModel> Collected => _found.Values.ToImmutableArray();

    public void Collect(ITypeSymbol type)
    {
        var name = Name(type);

        if (_found.ContainsKey(name) || declaredTypeNames.Contains(name)) return;
        if (IsBuiltIn(type)) return;

        if (type.TypeKind == TypeKind.Enum)
        {
            _found[name] = new AuxiliaryModel(AuxiliaryKind.Enum, name, name, null, EnumMembers(type));
            return;
        }

        if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            type is INamedTypeSymbol { TypeArguments.Length: 1 } nullable)
        {
            var underlying = nullable.TypeArguments[0];
            _found[name] = new AuxiliaryModel(
                AuxiliaryKind.Nullable, name, Name(underlying), null, EquatableArray<EnumMemberModel>.Empty);
            Collect(underlying);
            return;
        }

        if (DictionaryTypes(type) is var (key, value) && key is not null && value is not null)
        {
            _found[name] = new AuxiliaryModel(
                AuxiliaryKind.Dictionary, name, Name(key), Name(value), EquatableArray<EnumMemberModel>.Empty);
            Collect(key);
            Collect(value);
            return;
        }

        if (ElementType(type) is { } element)
        {
            _found[name] = new AuxiliaryModel(
                AuxiliaryKind.Collection, name, Name(element), null, EquatableArray<EnumMemberModel>.Empty);
            Collect(element);
        }

        // Anything else is either declared, or will fail at the call site with a message naming it.
    }

    private static EquatableArray<EnumMemberModel> EnumMembers(ITypeSymbol type)
    {
        var members = ImmutableArray.CreateBuilder<EnumMemberModel>();

        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (!field.IsStatic || !field.HasConstantValue) continue;

            var explicitName = field.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() ==
                                     "BeamSharp.Serialization.ErlPropertyAttribute")
                ?.ConstructorArguments[0].Value as string;

            members.Add(new EnumMemberModel(field.Name, explicitName));
        }

        return members.ToImmutable();
    }

    /// <summary>Mirrors CollectionConverterFactory.ElementType so both paths agree on what is a list.</summary>
    internal static ITypeSymbol? ElementType(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String) return null;
        if (type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte }) return null;
        if (type is IArrayTypeSymbol array) return array.ElementType;

        if (type is not INamedTypeSymbol { IsGenericType: true } named) return null;

        return named.ConstructedFrom.ToDisplayString() switch
        {
            "System.Collections.Generic.List<T>" or
            "System.Collections.Generic.HashSet<T>" or
            "System.Collections.Generic.IEnumerable<T>" or
            "System.Collections.Generic.ICollection<T>" or
            "System.Collections.Generic.IList<T>" or
            "System.Collections.Generic.IReadOnlyList<T>" or
            "System.Collections.Generic.IReadOnlyCollection<T>" => named.TypeArguments[0],
            _ => null
        };
    }

    /// <summary>Mirrors DictionaryConverterFactory.KeyValueTypes.</summary>
    private static (ITypeSymbol?, ITypeSymbol?) DictionaryTypes(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol { IsGenericType: true } named) return (null, null);

        return named.ConstructedFrom.ToDisplayString() switch
        {
            "System.Collections.Generic.Dictionary<TKey, TValue>" or
            "System.Collections.Generic.IDictionary<TKey, TValue>" or
            "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>" or
            "System.Collections.Generic.SortedDictionary<TKey, TValue>" =>
                (named.TypeArguments[0], named.TypeArguments[1]),
            _ => (null, null)
        };
    }

    private static bool IsBuiltIn(ITypeSymbol type)
    {
        if (BuiltIn.Contains(type.ToDisplayString())) return true;

        // Terms are handed through untouched by the passthrough factory.
        for (var t = type; t is not null; t = t.BaseType)
            if (t.ToDisplayString() == "BeamSharp.Terms.ErlTerm")
                return true;

        return false;
    }

    private static string Name(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}
