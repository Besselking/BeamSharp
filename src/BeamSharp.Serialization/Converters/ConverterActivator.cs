using System.Reflection;

namespace BeamSharp.Serialization.Converters;

/// <summary>
/// Builds converters reflectively. Activator wraps anything a constructor throws in a
/// TargetInvocationException, which would bury the diagnostic a converter raises about the type it
/// was asked to handle, so that wrapper is peeled off here.
/// </summary>
internal static class ConverterActivator
{
    [global::System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode(
        "Instantiates a converter reflectively.")]
    public static ErlConverter Create(
        [global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
            global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)]
        Type converterType,
        params object?[] arguments)
    {
        try
        {
            return (ErlConverter)Activator.CreateInstance(converterType, arguments)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable; keeps the compiler happy
        }
    }
}

/// <summary>
/// Shared justification for the suppressions on the reflection fallback.
/// <para>
/// The reflective paths exist for convenience at development time. A generated
/// <c>ErlSerializerContext</c> writes out every converter instantiation it needs and turns
/// reflection off, so a trimmed or AOT-published app does not reach this code. If it somehow does,
/// the failure is a clear exception naming the type, not silent misbehaviour.
/// </para>
/// </summary>
internal static class Justifications
{
    public const string ReflectionFallback =
        "The reflection fallback is only reached when UseReflection is on; a generated " +
        "ErlSerializerContext roots every instantiation it needs and turns it off.";
}
