using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BeamSharp.Serialization.Generator;

/// <summary>
/// Emits <c>ErlConverter&lt;T&gt;</c> implementations for the types listed on a serialization
/// context, so serialization needs no reflection over your own types.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ErlSerializerGenerator : IIncrementalGenerator
{
    private const string SerializableAttribute = "BeamSharp.Serialization.ErlSerializableAttribute";
    private const string ContextBase = "BeamSharp.Serialization.ErlSerializerContext";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var contexts = context.SyntaxProvider.ForAttributeWithMetadataName(
            SerializableAttribute,
            predicate: static (node, _) => node is ClassDeclarationSyntax,
            transform: static (ctx, ct) => Analyse(ctx, ct));

        context.RegisterSourceOutput(contexts, static (spc, result) =>
        {
            foreach (var diagnostic in result.Diagnostics) spc.ReportDiagnostic(diagnostic.ToDiagnostic());
            if (result.Context is { } model) spc.AddSource($"{model.ClassName}.g.cs", Emitter.Emit(model));
        });
    }

    private static Result Analyse(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        var symbol = (INamedTypeSymbol)ctx.TargetSymbol;
        var declaration = (ClassDeclarationSyntax)ctx.TargetNode;

        if (!declaration.Modifiers.Any(m => m.ValueText == "partial"))
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.ContextMustBePartial, symbol, symbol.Name));

        if (!DerivesFromContextBase(symbol))
            diagnostics.Add(DiagnosticInfo.Create(Diagnostics.ContextMustDeriveFromBase, symbol, symbol.Name));

        var targets = ctx.Attributes
            .Where(a => a.ConstructorArguments.Length == 1)
            .Select(a => a.ConstructorArguments[0].Value as INamedTypeSymbol)
            .Where(t => t is not null)
            .Select(t => t!)
            .ToList();

        // Declared types are generated in full; everything else reached from a member only needs a
        // rooted converter instantiation, so the collector has to know which is which up front.
        var declaredNames = new HashSet<string>(
            targets.Select(t => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
            StringComparer.Ordinal);

        var auxiliaries = new AuxiliaryCollector(declaredNames);
        var types = ImmutableArray.CreateBuilder<TypeModel>();

        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();

            var model = TypeModelBuilder.Build(target, diagnostics, auxiliaries);
            if (model is { } built) types.Add(built);
        }

        if (diagnostics.Any(d => d.IsError))
            return new Result(null, diagnostics.ToImmutable());

        var contextModel = new ContextModel(
            symbol.ContainingNamespace.IsGlobalNamespace ? null : symbol.ContainingNamespace.ToDisplayString(),
            symbol.Name,
            symbol.DeclaredAccessibility == Accessibility.Public ? "public" : "internal",
            types.ToImmutable(),
            auxiliaries.Collected);

        return new Result(contextModel, diagnostics.ToImmutable());
    }

    private static bool DerivesFromContextBase(INamedTypeSymbol symbol)
    {
        for (var t = symbol.BaseType; t is not null; t = t.BaseType)
            if (t.ToDisplayString() == ContextBase)
                return true;
        return false;
    }

    internal readonly record struct Result(ContextModel? Context, EquatableArray<DiagnosticInfo> Diagnostics);
}

/// <summary>A diagnostic reduced to value-equatable data, so it does not defeat pipeline caching.</summary>
internal readonly record struct DiagnosticInfo(
    string Id, string Title, string MessageFormat, string Category, int Severity,
    string? FilePath, int StartLine, int StartCharacter, int EndLine, int EndCharacter,
    EquatableArray<string> Arguments)
{
    public bool IsError => Severity == (int)DiagnosticSeverity.Error;

    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, ISymbol symbol, params string[] arguments)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var span = location?.GetLineSpan();

        return new DiagnosticInfo(
            descriptor.Id, descriptor.Title.ToString(), descriptor.MessageFormat.ToString(),
            descriptor.Category, (int)descriptor.DefaultSeverity,
            span?.Path, span?.StartLinePosition.Line ?? 0, span?.StartLinePosition.Character ?? 0,
            span?.EndLinePosition.Line ?? 0, span?.EndLinePosition.Character ?? 0,
            arguments.ToImmutableArray());
    }

    public Diagnostic ToDiagnostic()
    {
        var descriptor = new DiagnosticDescriptor(
            Id, Title, MessageFormat, Category, (DiagnosticSeverity)Severity, isEnabledByDefault: true);

        var location = FilePath is null
            ? Location.None
            : Location.Create(FilePath,
                new Microsoft.CodeAnalysis.Text.TextSpan(0, 0),
                new Microsoft.CodeAnalysis.Text.LinePositionSpan(
                    new Microsoft.CodeAnalysis.Text.LinePosition(StartLine, StartCharacter),
                    new Microsoft.CodeAnalysis.Text.LinePosition(EndLine, EndCharacter)));

        return Diagnostic.Create(descriptor, location, Arguments.Cast<object?>().ToArray());
    }
}
