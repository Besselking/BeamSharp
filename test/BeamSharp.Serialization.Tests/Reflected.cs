namespace BeamSharp.Serialization.Tests;

/// <summary>
/// The reflection fallback now lives in its own package, so tests that exercise it have to ask for
/// it explicitly. That opt-in is the point of the split.
/// </summary>
internal static class Reflected
{
    public static readonly ErlSerializerOptions Options = ErlReflection.Default;
}
