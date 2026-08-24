using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Serialization.Tests;

/// <summary>
/// A context's factory comes from its derived half, so it can only be asked for once that half has
/// run. The generated factories are stateless, which is why this held; a hand-written one that
/// depends on anything its own constructor sets is the case that finds out.
/// </summary>
public class ContextInitOrderTests
{
    [Fact]
    public void The_factory_sees_state_the_derived_constructor_set()
    {
        var context = new ContextWithState("configured");

        // Building the options is what asks for the factory, and by then the derived constructor has
        // run. Asking during construction is what could only ever see the field's default.
        Assert.NotNull(context.Options);
        Assert.Equal("configured", context.SeenByFactory);

        // Built once, not once per access.
        Assert.Same(context.Options, context.Options);
    }

    private sealed class ContextWithState : ErlSerializerContext
    {
        private readonly string _name;

        public ContextWithState(string name) => _name = name;

        public string? SeenByFactory { get; private set; }

        protected override ErlConverterFactory CreateFactory()
        {
            SeenByFactory = _name;
            return new NoopFactory();
        }
    }

    private sealed class NoopFactory : ErlConverterFactory
    {
        public override bool CanConvert(Type type) => false;

        public override ErlConverter CreateConverter(Type type, ErlSerializerOptions options) =>
            throw new NotSupportedException();
    }
}
