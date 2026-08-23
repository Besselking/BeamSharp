using BeamSharp.Node;
using BeamSharp.Terms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BeamSharp.Extensions.Hosting.Tests;

/// <summary>
/// These run a real host against a real EPMD, because the thing worth checking is the lifetime:
/// that mailboxes are registered before the listener accepts, and that shutdown gives the port back.
/// </summary>
public class HostingTests
{
    private static IHostBuilder Host(string alive, Action<IBeamSharpBuilder>? build = null) =>
        new HostBuilder().ConfigureServices(services =>
        {
            var builder = services.AddBeamSharpNode(options =>
            {
                options.NodeName = $"{alive}@{NodeName.LocalShortHost}";
                options.Cookie = "hosting-test-cookie";
            });
            build?.Invoke(builder);
        });

    [RequiresEpmdFact]
    public async Task A_node_starts_and_stops_with_the_host()
    {
        using var host = Host("bs_host_lifetime").Build();

        await host.StartAsync();
        var node = host.Services.GetRequiredService<ErlangNode>();

        Assert.True(node.Port > 0);
        Assert.Equal("bs_host_lifetime", node.Name.Alive);

        await host.StopAsync();
    }

    [RequiresEpmdFact]
    public async Task Registrations_happen_before_the_node_starts_accepting()
    {
        // The ordering is the point: a peer must never arrive to find a node whose mailboxes are
        // still being wired up.
        using var host = Host("bs_host_registration", b => b.AddGenServer("greeter", _ => new Greeter())).Build();

        await host.StartAsync();
        var node = host.Services.GetRequiredService<ErlangNode>();

        Assert.NotNull(node.Whereis("greeter"));
        Assert.NotNull(node.Whereis("net_kernel"));

        await host.StopAsync();
    }

    [RequiresEpmdFact]
    public async Task A_gen_server_is_built_from_the_container()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGreeting>(new Greeting("hei"));
        services.AddBeamSharpNode(options =>
            {
                options.NodeName = $"bs_host_di@{NodeName.LocalShortHost}";
                options.Cookie = "hosting-test-cookie";
            })
            .AddGenServer("greeter", sp => new Greeter(sp.GetRequiredService<IGreeting>()));

        using var host = new HostBuilder()
            .ConfigureServices(s =>
            {
                foreach (var descriptor in services) s.Add(descriptor);
            })
            .Build();

        await host.StartAsync();

        var node = host.Services.GetRequiredService<ErlangNode>();
        var mailbox = node.Whereis("greeter");
        Assert.NotNull(mailbox);

        await host.StopAsync();
    }

    [RequiresEpmdFact]
    public async Task Resolving_the_node_before_startup_is_refused_rather_than_racing()
    {
        using var host = Host("bs_host_early").Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => host.Services.GetRequiredService<ErlangNode>());
        Assert.Contains("has not started", ex.Message);

        await host.StartAsync();
        Assert.NotNull(host.Services.GetRequiredService<ErlangNode>());
        await host.StopAsync();
    }

    [RequiresEpmdFact]
    public async Task Metrics_track_the_node()
    {
        using var host = Host("bs_host_metrics").Build();
        await host.StartAsync();

        Assert.NotNull(host.Services.GetRequiredService<BeamSharpMetrics>());
        Assert.Equal("BeamSharp", BeamSharpMetrics.MeterName);

        await host.StopAsync();
    }

    private interface IGreeting
    {
        string Word { get; }
    }

    private sealed record Greeting(string Word) : IGreeting;

    private sealed class Greeter(IGreeting? greeting = null) : ErlangGenServer
    {
        public override ValueTask<ErlTerm?> HandleCallAsync(
            ErlTerm request, GenCallFrom from, CancellationToken ct) =>
            ValueTask.FromResult<ErlTerm?>(Erl.String(greeting?.Word ?? "hello"));
    }
}
