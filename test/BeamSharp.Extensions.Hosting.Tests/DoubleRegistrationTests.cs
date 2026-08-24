using BeamSharp.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BeamSharp.Extensions.Hosting.Tests;

public class DoubleRegistrationTests
{
    /// <summary>
    /// A library registering the node and the app hosting it doing the same is the ordinary way to
    /// reach this, and a node started twice throws "node already started" at boot.
    /// </summary>
    /// <remarks>
    /// This holds today without anything here arranging it: <c>AddHostedService</c> is itself a
    /// <c>TryAddEnumerable</c>, which dedupes on the implementation type. That is worth a test
    /// precisely because nothing in this file says so — the property is inherited, and inherited
    /// properties are the ones that disappear without anyone noticing.
    /// </remarks>
    [Fact]
    public async Task Registering_the_node_twice_starts_it_once()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddBeamSharpNode(o => { o.NodeName = "twice@localhost"; o.Cookie = "twice-cookie"; });
        services.AddBeamSharpNode(o => { o.NodeName = "twice@localhost"; o.Cookie = "twice-cookie"; });

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().OfType<BeamSharpNodeService>().ToList();

        Assert.Single(hosted);
    }
}
