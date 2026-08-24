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
    private readonly CancellationTokenSource _stopping = new();
    private ErlangNode? _node;
    private Task? _joining;

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

        // An IHostedService that has not returned holds up everything started after it, and dialling
        // a peer can take the EPMD timeout plus the handshake timeout. A peer that is not up yet is
        // an ordinary state for a cluster to be in -- one member has to start first -- so joining
        // happens alongside the application rather than in front of it.
        _joining = Task.Run(() => ConnectToPeersAsync(_node, _stopping.Token), CancellationToken.None);
    }

    private async Task ConnectToPeersAsync(ErlangNode node, CancellationToken ct)
    {
        await Parallel.ForEachAsync(_options.ConnectTo, ct, async (peer, token) =>
        {
            try
            {
                if (await node.ConnectAsync(peer, token).ConfigureAwait(false)) return;
                _logger.LogWarning("could not reach {Peer} at startup", peer);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Shutting down before the cluster finished forming is not a fault.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "could not reach {Peer} at startup", peer);
            }
        }).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_node is null) return;

        // Stop dialling before disposing the node the dials are using.
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_joining is { } joining)
        {
            try
            {
                await joining.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: that is what cancelling it does.
            }

            _joining = null;
        }

        _logger.LogInformation("stopping node {Node}", _node.Name);
        await _node.DisposeAsync().ConfigureAwait(false);
        _node = null;
        _stopping.Dispose();
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
