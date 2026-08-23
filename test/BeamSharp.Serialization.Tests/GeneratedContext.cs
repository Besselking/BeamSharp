namespace BeamSharp.Serialization.Tests;

/// <summary>
/// The generator fills in <c>Default</c> and the converters. Every type here is also exercised
/// through the reflection path, and the two are asserted to agree.
/// </summary>
[ErlSerializable(typeof(Person))]
[ErlSerializable(typeof(ElixirPerson))]
[ErlSerializable(typeof(Point))]
[ErlSerializable(typeof(Mutable))]
[ErlSerializable(typeof(Annotated))]
[ErlSerializable(typeof(Nested))]
[ErlSerializable(typeof(WithFields))]
[ErlSerializable(typeof(Money))]
[ErlSerializable(typeof(Temperature))]
[ErlSerializable(typeof(DuplicateNames))]
[ErlSerializable(typeof(Person[]))]
[ErlSerializable(typeof(Status))]
[ErlSerializable(typeof((int, string)))]
internal partial class TestContext : ErlSerializerContext
{
    public TestContext() { }

    public TestContext(ErlSerializerOptions options) : base(options) { }
}
