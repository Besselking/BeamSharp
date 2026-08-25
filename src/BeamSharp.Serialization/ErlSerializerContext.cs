namespace BeamSharp.Serialization;

/// <summary>
/// Declares that the source generator should emit a converter for <paramref name="type"/> on the
/// context this is applied to.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class ErlSerializableAttribute(Type type) : Attribute
{
    /// <summary>The type to generate a converter for.</summary>
    public Type Type { get; } = type;
}

/// <summary>
/// Base class for a generated serialization context.
/// <para>
/// Declare a partial class deriving from this, list the types with
/// <see cref="ErlSerializableAttribute"/>, and the generator fills in the rest:
/// </para>
/// <code>
/// [ErlSerializable(typeof(Person))]
/// [ErlSerializable(typeof(Order))]
/// internal partial class AppTerms : ErlSerializerContext;
///
/// var term = ErlSerializer.Serialize(person, AppTerms.Default);
/// </code>
/// <para>
/// A context adds no reflection of its own, so unless the caller went out of its way to add the
/// fallback, a type that was never declared fails at the call site naming itself rather than quietly
/// depending on metadata a trimmer may have removed.
/// </para>
/// </summary>
public abstract class ErlSerializerContext
{
    private readonly Lazy<ErlSerializerOptions> _options;

    /// <summary>Builds a context, optionally starting from an existing configuration.</summary>
    /// <param name="options">
    /// Naming policy, key kind and the rest. Copied, then locked. Reflection is switched off
    /// regardless of what is passed in.
    /// </param>
    protected ErlSerializerContext(ErlSerializerOptions? options = null)
    {
        // Not built here: CreateFactory is the derived half's, and a constructor cannot ask a
        // subclass for anything before that subclass has run. The generated factories carry no
        // state, so nothing has noticed; a factory that reads a field its own constructor sets
        // would see the default instead. Lazy rather than a plain null check because Options is
        // reachable from any thread once the context is.
        _options = new Lazy<ErlSerializerOptions>(() =>
        {
            var built = options is null ? new ErlSerializerOptions() : new ErlSerializerOptions(options);
            // First, so generated converters win over a reflection fallback if the caller added one.
            built.ConverterFactories.Insert(0, CreateFactory());
            built.MakeReadOnly();
            return built;
        });
    }

    /// <summary>The options to serialize with. Frozen.</summary>
    public ErlSerializerOptions Options => _options.Value;

    /// <summary>Supplied by the generated half of the class.</summary>
    protected abstract ErlConverterFactory CreateFactory();
}
