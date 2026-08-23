using System.Diagnostics.CodeAnalysis;
using BeamSharp.Serialization.Reflection;

namespace BeamSharp.Serialization;

/// <summary>
/// Adds the reflection fallback to a set of options, so any type can be serialized without being
/// declared first.
/// </summary>
public static class ReflectionFallbackExtensions
{
    internal const string Warning =
        "The reflection fallback inspects types at runtime and is not trim- or AOT-safe. " +
        "Declare your types on a generated ErlSerializerContext instead when publishing trimmed or AOT.";

    /// <summary>
    /// Registers the reflective converters: collections, dictionaries, enums, nullables, tuples,
    /// <c>[ErlConvert]</c> types, and finally plain objects.
    /// </summary>
    /// <remarks>
    /// The object fallback claims every type, so it is registered last, after the built-in scalars
    /// have had their chance.
    /// </remarks>
    [RequiresUnreferencedCode(Warning)]
    [RequiresDynamicCode(Warning)]
    public static ErlSerializerOptions AddReflectionFallback(this ErlSerializerOptions options)
    {
        options.ConverterFactories.Add(AttributeConverterFactory.Instance);
        options.ConverterFactories.Add(TermPassthroughFactory.Instance);
        options.ConverterFactories.Add(NullableConverterFactory.Instance);
        options.ConverterFactories.Add(EnumConverterFactory.Instance);
        options.ConverterFactories.Add(TupleConverterFactory.Instance);
        options.ConverterFactories.Add(DictionaryConverterFactory.Instance);
        options.ConverterFactories.Add(CollectionConverterFactory.Instance);
        options.ConverterFactories.Add(ObjectConverterFactory.Instance);
        return options;
    }
}

/// <summary>Convenience entry points for reflection-based configuration.</summary>
public static class ErlReflection
{
    private const string Note =
        "Referencing BeamSharp.Serialization.Reflection is itself the opt-in to reflection; " +
        "an app that does not want it does not reference the package.";

    /// <summary>Fresh options with the reflection fallback already registered.</summary>
    [RequiresUnreferencedCode(ReflectionFallbackExtensions.Warning)]
    [RequiresDynamicCode(ReflectionFallbackExtensions.Warning)]
    public static ErlSerializerOptions CreateOptions() =>
        new ErlSerializerOptions().AddReflectionFallback();

    /// <summary>
    /// Shared options with the reflection fallback, for when trimming is not a concern. Frozen on
    /// first use, like any other options instance.
    /// </summary>
    public static ErlSerializerOptions Default { get; } = BuildDefault();

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = Note)]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = Note)]
    private static ErlSerializerOptions BuildDefault() =>
        new ErlSerializerOptions().AddReflectionFallback();
}
