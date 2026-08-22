using System.Net;
using System.Net.Sockets;

namespace BeamSharp.Networking;

/// <summary>
/// Resolves Erlang-style host names. Short node names such as <c>foo@mybox</c> carry a bare host that
/// the system resolver often cannot look up, even though Erlang's own resolver can, so this walks a
/// small fallback chain instead of failing outright.
/// </summary>
public static class HostResolver
{
    /// <summary>Resolves a host name to addresses, falling back to loopback and mDNS style names.</summary>
    public static async ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default)
    {
        if (IPAddress.TryParse(host, out var literal)) return [literal];

        var addresses = await TryResolveAsync(host, ct).ConfigureAwait(false);
        if (addresses.Length > 0) return addresses;

        // A short name for this very machine: EPMD and the peer are both local.
        if (string.Equals(host, Node.NodeName.LocalShortHost, StringComparison.OrdinalIgnoreCase))
            return [IPAddress.Loopback];

        // macOS and many Linux desktops publish the short name over mDNS as <host>.local.
        if (!host.Contains('.'))
        {
            addresses = await TryResolveAsync(host + ".local", ct).ConfigureAwait(false);
            if (addresses.Length > 0) return addresses;
        }

        throw new SocketException((int)SocketError.HostNotFound, $"could not resolve host '{host}'");
    }

    /// <summary>Connects a TCP socket to <paramref name="host"/>, using the same fallback chain.</summary>
    public static async Task<TcpClient> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        var addresses = await ResolveAsync(host, ct).ConfigureAwait(false);
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(addresses, port, ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async ValueTask<IPAddress[]> TryResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            return await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }
    }
}
