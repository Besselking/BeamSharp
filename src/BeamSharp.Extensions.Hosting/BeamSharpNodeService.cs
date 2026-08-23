using BeamSharp.Node;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeamSharp.Extensions.Hosting;

/// <summary>
/// Owns the node for the lifetime of the application: starts the listener, registers with EPMD,
/// dials any configured peers, and shuts down cleanly.
/// </summary>
public sealed class BeamSharpNodeService : IHostedService, IAsyncDisposable
{
    private readonly BeamSharpOptions _options;
    private readonly ILogger<BeamSharpNodeService> _logger;
    private readonly BeamSharpMetrics _metrics;
    private readonly IEnumerable<IErlangNodeConfigurator> _configurators;
    private ErlangNode? _node;

    public BeamSharpNodeService(
        IOptions<BeamSharpOptions> options,
        ILogger<BeamSharpNodeService> logger,
        BeamSharpMetrics metrics,
        IEnumerable<IErlangNodeConfigurator> configurators)
    {
        _options = options.Value;
        _logger = logger;
        _metrics = metrics;
        _configurators = configurators;
    }

    /// <summary>The running node. Throws until <see cref="StartAsync"/> has completed.</summary>
    public ErlangNode Node =>
        _node ?? throw new InvalidOperationException("the node has not started yet");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var name = _options.ResolveNodeName();

        // The node's own diagnostics are a string callback; route them into the logger rather than
        // asking the core library to take a dependency on the logging abstractions.
        _node = new ErlangNode(name, _options.ToNodeOptions(line => _logger.LogDebug("{Message}", line)));

        _node.NodeUp += peer =>
        {
            _metrics.ConnectionOpened(peer);
            _logger.LogInformation("connected to {Peer}", peer);
        };

        _node.NodeDown += (peer, error) =>
        {
            _metrics.ConnectionClosed(peer, error is null);
            if (error is null) _logger.LogInformation("disconnected from {Peer}", peer);
            else _logger.LogWarning(error, "lost the connection to {Peer}", peer);
        };

        // Registrations run before the listener accepts anything, so a peer cannot arrive to find
        // a node whose mailboxes are still being set up.
        foreach (var configurator in _configurators)
            await configurator.ConfigureAsync(_node, cancellationToken).ConfigureAwait(false);

        await _node.StartAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("node {Node} listening on port {Port}", _node.Name, _node.Port);

        foreach (var peer in _options.ConnectTo)
        {
            if (await _node.ConnectAsync(peer, cancellationToken).ConfigureAwait(false)) continue;
            _logger.LogWarning("could not reach {Peer} at startup", peer);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_node is null) return;

        _logger.LogInformation("stopping node {Node}", _node.Name);
        await _node.DisposeAsync().ConfigureAwait(false);
        _node = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_node is not null) await _node.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Registers mailboxes, gen_servers and rpc handlers on the node before it starts accepting.
/// Implement this rather than reaching for the node from a constructor, which would race startup.
/// </summary>
public interface IErlangNodeConfigurator
{
    ValueTask ConfigureAsync(ErlangNode node, CancellationToken cancellationToken);
}
