using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BeamSharp.Extensions.Hosting.Tests;

public class StartupDiallingTests
{
    /// <summary>
    /// An IHostedService that has not returned holds up everything started after it, so dialling
    /// peers there puts the EPMD and handshake timeouts in front of the application.
    /// </summary>
    [Fact]
    public async Task Unreachable_startup_peers_do_not_hold_up_the_application()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBeamSharpNode(o =>
        {
            o.NodeName = $"bs_startup@{Environment.MachineName}";
            o.Cookie = "startup-cookie";

            // Hosts that swallow packets rather than refusing, so each lookup runs to its timeout.
            o.ConnectTo.Add("a@10.255.255.1");
            o.ConnectTo.Add("b@10.255.255.2");
            o.ConnectTo.Add("c@10.255.255.3");
        });

        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().Single();

        var sw = Stopwatch.StartNew();
        await hosted.StartAsync(CancellationToken.None);
        sw.Stop();

        // Three peers at a 5s EPMD timeout each is 15s in series, or 5s even dialled in parallel.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"startup took {sw.Elapsed.TotalSeconds:0.0}s with three unreachable peers configured");

        await hosted.StopAsync(CancellationToken.None);
    }
}
