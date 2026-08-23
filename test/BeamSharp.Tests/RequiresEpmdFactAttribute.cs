using System.Net.Sockets;
using BeamSharp.Epmd;
using Xunit;

namespace BeamSharp.Tests;

/// <summary>
/// A fact that needs a running port mapper. Skipping says so, rather than failing for a reason
/// unrelated to the code under test or passing quietly without having run.
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
