using System.Reflection;

namespace BeamSharp.Serialization.Converters;

/// <summary>
/// Builds converters reflectively. Activator wraps anything a constructor throws in a
/// TargetInvocationException, which would bury the diagnostic a converter raises about the type it
/// was asked to handle, so that wrapper is peeled off here.
/// </summary>
internal static class ConverterActivator
{
    public static ErlConverter Create(Type converterType, params object?[] arguments)
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
