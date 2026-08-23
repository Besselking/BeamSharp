using System.Diagnostics.Metrics;

namespace BeamSharp.Extensions.Hosting;

/// <summary>
/// Counters and gauges for a running node, published under the <c>BeamSharp</c> meter so any
/// OpenTelemetry or <c>dotnet-counters</c> setup picks them up without extra wiring.
/// </summary>
public sealed class BeamSharpMetrics : IDisposable
{
    /// <summary>The meter name to subscribe to.</summary>
    public const string MeterName = "BeamSharp";

    private readonly Meter _meter;
    private readonly Counter<long> _connectionsOpened;
    private readonly Counter<long> _connectionsClosed;
    private int _connected;

    public BeamSharpMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);

        _connectionsOpened = _meter.CreateCounter<long>(
            "beamsharp.connections.opened", "{connection}", "Peer nodes connected since start.");
        _connectionsClosed = _meter.CreateCounter<long>(
            "beamsharp.connections.closed", "{connection}", "Peer connections lost since start.");

        _meter.CreateObservableGauge(
            "beamsharp.connections.active", () => Volatile.Read(ref _connected),
            "{connection}", "Peer nodes connected right now.");
    }

    internal void ConnectionOpened(string peer)
    {
        Interlocked.Increment(ref _connected);
        _connectionsOpened.Add(1, new KeyValuePair<string, object?>("peer", peer));
    }

    internal void ConnectionClosed(string peer, bool clean)
    {
        Interlocked.Decrement(ref _connected);
        _connectionsClosed.Add(1,
            new KeyValuePair<string, object?>("peer", peer),
            new KeyValuePair<string, object?>("clean", clean));
    }

    public void Dispose() => _meter.Dispose();
}
