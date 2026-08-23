using Microsoft.CodeAnalysis;

namespace BeamSharp.Serialization.Generator;

internal static class Diagnostics
{
    private const string Category = "BeamSharp.Serialization";

    public static readonly DiagnosticDescriptor ContextMustBePartial = new(
        "BS1001", "Serialization context must be partial",
        "'{0}' carries [ErlSerializable] but is not partial, so the generated half cannot be added to it",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ContextMustDeriveFromBase = new(
        "BS1002", "Serialization context must derive from ErlSerializerContext",
        "'{0}' carries [ErlSerializable] but does not derive from ErlSerializerContext",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoUsableConstructor = new(
        "BS1003", "Type cannot be constructed during deserialization",
        "'{0}' has no parameterless constructor and no constructor whose parameters all match its " +
        "members, so it cannot be deserialized",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedType = new(
        "BS1004", "Type cannot have a converter generated for it",
        "'{0}' cannot have a converter generated: {1}",
        Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RecordShapeWithInheritance = new(
        "BS1005", "Tagged tuple shape depends on member order",
        "'{0}' uses [ErlRecord] and inherits members, whose order across the inheritance chain is not " +
        "guaranteed to match the reflection-based serializer; declare the members on one type",
        Category, DiagnosticSeverity.Warning, isEnabledByDefault: true);
}
