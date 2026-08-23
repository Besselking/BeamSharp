using System.Net.Sockets;
using BeamSharp.Epmd;
using Xunit;

namespace BeamSharp.Extensions.Hosting.Tests;

/// <summary>
/// A fact that needs a running EPMD. Starting a node registers with it, so without one these would
/// fail for a reason that has nothing to do with the code under test. Skipping says so out loud
/// instead of going quietly green.
/// </summary>
public sealed class RequiresEpmdFactAttribute : FactAttribute
{
    private static readonly Lazy<bool> Available = new(Probe);

    public RequiresEpmdFactAttribute()
    {
        if (!Available.Value)
            Skip = $"no EPMD listening on port {EpmdClient.DefaultPort}; start one with 'epmd -daemon'";
    }

    private static bool Probe()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", EpmdClient.DefaultPort)
                .Wait(TimeSpan.FromSeconds(2)) && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException or ObjectDisposedException)
        {
            return false;
        }
    }
}
