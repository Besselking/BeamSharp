using System.Diagnostics.CodeAnalysis;
using BeamSharp.Node;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BeamSharp.Extensions.Hosting;

/// <summary>Registers an Erlang node with the dependency injection container.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds a node that starts and stops with the application.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services
    ///     .AddBeamSharpNode(options =>
    ///     {
    ///         options.NodeName = "orders@myhost";
    ///         options.Cookie = builder.Configuration["Erlang:Cookie"];
    ///     })
    ///     .AddGenServer("orders", sp => new OrderServer(sp.GetRequiredService&lt;IOrderStore&gt;()));
    /// </code>
    /// </example>
    public static IBeamSharpBuilder AddBeamSharpNode(
        this IServiceCollection services, Action<BeamSharpOptions>? configure = null)
    {
        if (configure is not null) services.Configure(configure);
        else services.AddOptions<BeamSharpOptions>();

        services.TryAddSingleton<BeamSharpMetrics>();
        services.TryAddSingleton<BeamSharpNodeService>();
        services.AddHostedService(sp => sp.GetRequiredService<BeamSharpNodeService>());

        // Resolving the node before the host has started is a race, so this deliberately throws
        // rather than handing back a half-built one.
        services.TryAddSingleton(sp => sp.GetRequiredService<BeamSharpNodeService>().Node);

        return new BeamSharpBuilder(services);
    }

    /// <summary>Binds node settings from a configuration section.</summary>
    /// <remarks>
    /// Configuration binding reads properties reflectively. Under trimming or AOT, use the
    /// <see cref="AddBeamSharpNode(IServiceCollection, Action{BeamSharpOptions})"/> overload and set
    /// the properties yourself.
    /// </remarks>
    [RequiresUnreferencedCode("Configuration binding reads BeamSharpOptions reflectively.")]
    [RequiresDynamicCode("Configuration binding reads BeamSharpOptions reflectively.")]
    public static IBeamSharpBuilder AddBeamSharpNode(
        this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration section)
    {
        services.AddOptions<BeamSharpOptions>().Bind(section);

        services.TryAddSingleton<BeamSharpMetrics>();
        services.TryAddSingleton<BeamSharpNodeService>();
        services.AddHostedService(sp => sp.GetRequiredService<BeamSharpNodeService>());
        services.TryAddSingleton(sp => sp.GetRequiredService<BeamSharpNodeService>().Node);

        return new BeamSharpBuilder(services);
    }
}

/// <summary>Fluent surface for populating a node.</summary>
public interface IBeamSharpBuilder
{
    IServiceCollection Services { get; }

    /// <summary>Registers a <c>gen_server</c> under <paramref name="name"/>, built from the container.</summary>
    IBeamSharpBuilder AddGenServer(string name, Func<IServiceProvider, IErlangGenServer> factory);

    /// <summary>Registers a <c>gen_server</c> resolved from the container by type.</summary>
    IBeamSharpBuilder AddGenServer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        THandler>(string name) where THandler : class, IErlangGenServer;

    /// <summary>Registers the handler for incoming <c>:rpc.call/4</c> and <c>:erpc.call/4</c>.</summary>
    IBeamSharpBuilder AddRpcHandler(Func<IServiceProvider, IErlangRpcHandler> factory);

    /// <summary>Runs arbitrary setup against the node before it starts accepting connections.</summary>
    IBeamSharpBuilder Configure(Func<ErlangNode, IServiceProvider, CancellationToken, ValueTask> configure);
}

internal sealed class BeamSharpBuilder(IServiceCollection services) : IBeamSharpBuilder
{
    public IServiceCollection Services { get; } = services;

    public IBeamSharpBuilder AddGenServer(string name, Func<IServiceProvider, IErlangGenServer> factory) =>
        Configure((node, provider, _) =>
        {
            node.RegisterGenServer(name, factory(provider));
            return ValueTask.CompletedTask;
        });

    public IBeamSharpBuilder AddGenServer<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        THandler>(string name) where THandler : class, IErlangGenServer
    {
        Services.TryAddSingleton<THandler>();
        return AddGenServer(name, sp => sp.GetRequiredService<THandler>());
    }

    public IBeamSharpBuilder AddRpcHandler(Func<IServiceProvider, IErlangRpcHandler> factory) =>
        Configure((node, provider, _) =>
        {
            node.RpcHandler = factory(provider);
            return ValueTask.CompletedTask;
        });

    public IBeamSharpBuilder Configure(
        Func<ErlangNode, IServiceProvider, CancellationToken, ValueTask> configure)
    {
        Services.AddSingleton<IErlangNodeConfigurator>(
            provider => new DelegateConfigurator(configure, provider));
        return this;
    }

    private sealed class DelegateConfigurator(
        Func<ErlangNode, IServiceProvider, CancellationToken, ValueTask> configure,
        IServiceProvider provider) : IErlangNodeConfigurator
    {
        public ValueTask ConfigureAsync(ErlangNode node, CancellationToken cancellationToken) =>
            configure(node, provider, cancellationToken);
    }
}
