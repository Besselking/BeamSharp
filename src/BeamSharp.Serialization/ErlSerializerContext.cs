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
/// The options a context exposes have <see cref="ErlSerializerOptions.UseReflection"/> switched off,
/// so a type that was never declared fails at the call site naming itself rather than quietly
/// falling back to reflection over metadata a trimmer may have removed.
/// </para>
/// </summary>
public abstract class ErlSerializerContext
{
    /// <summary>Builds a context, optionally starting from an existing configuration.</summary>
    /// <param name="options">
    /// Naming policy, key kind and the rest. Copied, then locked. Reflection is switched off
    /// regardless of what is passed in.
    /// </param>
    protected ErlSerializerContext(ErlSerializerOptions? options = null)
    {
        Options = options is null ? new ErlSerializerOptions() : new ErlSerializerOptions(options);
        Options.UseReflection = false;
        Options.ConverterFactories.Add(CreateFactory());
        Options.MakeReadOnly();
    }

    /// <summary>The options to serialize with. Frozen.</summary>
    public ErlSerializerOptions Options { get; }

    /// <summary>Supplied by the generated half of the class.</summary>
    protected abstract ErlConverterFactory CreateFactory();
}
