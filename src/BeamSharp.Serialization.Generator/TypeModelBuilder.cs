using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace BeamSharp.Serialization.Generator;

/// <summary>
/// Turns a type symbol into the model the emitter works from. The rules here mirror the reflection
/// converter exactly; where they cannot (member order under inheritance for the tuple shape) a
/// diagnostic says so rather than letting the two quietly disagree.
/// </summary>
internal static class TypeModelBuilder
{
    private const string Ns = "BeamSharp.Serialization.";

    public static TypeModel? Build(ITypeSymbol type, ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        AuxiliaryCollector auxiliaries)
    {
        // An array is an IArrayTypeSymbol rather than an INamedTypeSymbol, so this has to come
        // before anything that assumes a named type — declaring Person[] used to be dropped here.
        if (auxiliaries.CollectDeclared(type)) return null;

        if (type is not INamedTypeSymbol named)
        {
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnsupportedType, type,
                type.ToDisplayString(), "it is not a type a converter can be generated for"));
            return null;
        }

        return BuildNamed(named, diagnostics, auxiliaries);
    }

    private static TypeModel? BuildNamed(INamedTypeSymbol type, ImmutableArray<DiagnosticInfo>.Builder diagnostics,
        AuxiliaryCollector auxiliaries)
    {
        // A type with its own converter is registered as that converter directly.
        if (FindAttribute(type, "ErlConvertAttribute") is { } own)
        {
            if (own.ConstructorArguments[0].Value is INamedTypeSymbol converter)
                auxiliaries.CollectCustom(type, converter.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            return null;
        }

        if (type.IsAbstract || type.TypeKind == TypeKind.Interface)
        {
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.UnsupportedType, type,
                type.Name, "it is abstract or an interface, so it cannot be constructed"));
            return null;
        }

        var shape = TermShape.Map;
        string? structModule = null;
        string? recordTag = null;

        if (FindAttribute(type, "ErlStructAttribute") is { } structAttribute)
        {
            shape = TermShape.Struct;
            var module = structAttribute.ConstructorArguments[0].Value as string ?? type.Name;
            structModule = module.StartsWith("Elixir.", StringComparison.Ordinal) ? module : "Elixir." + module;
        }
        else if (FindAttribute(type, "ErlRecordAttribute") is { } recordAttribute)
        {
            shape = TermShape.Record;
            recordTag = recordAttribute.ConstructorArguments[0].Value as string ?? type.Name;
        }

        var members = CollectMembers(type, auxiliaries, out var inherited);

        if (shape == TermShape.Record && inherited)
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.RecordShapeWithInheritance, type, type.Name));

        if (!PlanConstruction(type, members, out var binding, out var defaults, out var parameterless))
        {
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.NoUsableConstructor, type, type.Name));
            return null;
        }

        return new TypeModel(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            SafeName(type),
            shape,
            structModule,
            recordTag,
            members.ToImmutableArray(),
            binding.ToImmutableArray(),
            defaults.ToImmutableArray(),
            parameterless);
    }

    private static List<MemberModel> CollectMembers(INamedTypeSymbol type, AuxiliaryCollector auxiliaries,
        out bool inherited)
    {
        var members = new List<MemberModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        inherited = false;

        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            if (!SymbolEqualityComparer.Default.Equals(current, type) && current.GetMembers().Length > 0)
                inherited = true;

            foreach (var member in current.GetMembers())
            {
                if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public) continue;
                if (FindAttribute(member, "ErlIgnoreAttribute") is not null) continue;

                switch (member)
                {
                    case IPropertySymbol property:
                    {
                        if (property.Parameters.Length > 0) continue;              // indexer
                        if (property.GetMethod is not { DeclaredAccessibility: Accessibility.Public }) continue;
                        // Records synthesise this; it is an implementation detail, not data.
                        if (property.Name == "EqualityContract") continue;
                        if (!seen.Add(property.Name)) continue;

                        var settable = property.SetMethod is { DeclaredAccessibility: Accessibility.Public };

                        auxiliaries.Collect(property.Type);
                        members.Add(Describe(property, property.Type, settable,
                            settable && property.SetMethod!.IsInitOnly, isField: false));
                        break;
                    }

                    case IFieldSymbol field:
                    {
                        if (field.IsImplicitlyDeclared || field.IsConst) continue;  // backing fields
                        if (!seen.Add(field.Name)) continue;

                        auxiliaries.Collect(field.Type);
                        members.Add(Describe(field, field.Type, settable: !field.IsReadOnly,
                            isInitOnly: false, isField: true));
                        break;
                    }
                }
            }
        }

        return members;
    }

    private static MemberModel Describe(ISymbol symbol, ITypeSymbol memberType, bool settable, bool isInitOnly,
        bool isField)
    {
        var explicitName = FindAttribute(symbol, "ErlPropertyAttribute")?.ConstructorArguments[0].Value as string;
        var asAtom = FindAttribute(symbol, "ErlAsAtomAttribute") is not null;
        var converter = FindAttribute(symbol, "ErlConvertAttribute")?.ConstructorArguments[0].Value
            as INamedTypeSymbol;

        var canBeNull = memberType.IsReferenceType ||
                        memberType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

        return new MemberModel(
            symbol.Name,
            memberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            explicitName,
            isField,
            settable,
            isInitOnly,
            canBeNull,
            asAtom,
            converter?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }

    private static bool PlanConstruction(INamedTypeSymbol type, List<MemberModel> members,
        out List<int> binding, out List<string> defaults, out bool parameterless)
    {
        binding = [];
        defaults = [];
        parameterless = false;

        var constructors = type.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .ToList();

        if (constructors.Any(c => c.Parameters.Length == 0) || type.IsValueType)
        {
            parameterless = true;
            return true;
        }

        var best = constructors
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault(c => c.Parameters.All(p =>
                members.Any(m => string.Equals(m.ClrName, p.Name, StringComparison.OrdinalIgnoreCase))));

        if (best is null) return false;

        foreach (var parameter in best.Parameters)
        {
            binding.Add(members.FindIndex(m =>
                string.Equals(m.ClrName, parameter.Name, StringComparison.OrdinalIgnoreCase)));

            var typeName = parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            defaults.Add(parameter.HasExplicitDefaultValue
                ? Literal(parameter.ExplicitDefaultValue, typeName)
                : $"default({typeName})");
        }

        return true;
    }

    /// <summary>Renders a constructor default as C# source, falling back to default(T) when unsure.</summary>
    private static string Literal(object? value, string typeName) => value switch
    {
        null => $"default({typeName})",
        bool b => b ? "true" : "false",
        string s => SymbolDisplayFormat.CSharpErrorMessageFormat is not null
            ? "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
            : $"default({typeName})",
        char c => "'" + (c == '\'' ? "\\'" : c.ToString()) + "'",
        float f => $"({typeName})({f.ToString("R", CultureInfo.InvariantCulture)}f)",
        double d => $"({typeName})({d.ToString("R", CultureInfo.InvariantCulture)}d)",
        decimal m => $"({typeName})({m.ToString(CultureInfo.InvariantCulture)}m)",
        sbyte or byte or short or ushort or int or uint or long or ulong =>
            $"({typeName})({Convert.ToString(value, CultureInfo.InvariantCulture)})",
        _ => $"default({typeName})"
    };

    private static AttributeData? FindAttribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == Ns + name);

    /// <summary>A identifier-safe name for the generated nested class.</summary>
    private static string SafeName(INamedTypeSymbol type)
    {
        var name = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty);
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var c in name) builder.Append(char.IsLetterOrDigit(c) ? c : '_');
        return builder.ToString();
    }
}
