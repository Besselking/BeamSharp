using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BeamSharp.Epmd;
using Xunit;

namespace BeamSharp.Tests;

/// <summary>
/// EPMD is unauthenticated and speaks in the clear, and a node talks to it before any cookie is
/// involved. These stand a hostile port mapper up on a loopback port and check the client is not at
/// its mercy.
/// </summary>
public sealed class EpmdClientTests : IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<TcpListener> _listeners = [];
    private readonly List<Task> _serving = [];

    /// <summary>
    /// Stands up a port mapper that behaves however the test says.
    /// <para>
    /// The listener and its serving task are held by the fixture rather than left to a discarded
    /// task. An earlier version dropped them, and the connection was sometimes torn down early
    /// under load, which made the test fail for a reason that had nothing to do with the client.
    /// </para>
    /// </summary>
    private int StartFakeEpmd(Func<NetworkStream, CancellationToken, Task> behaviour)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _serving.Add(Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(_shutdown.Token);
                await behaviour(client.GetStream(), _shutdown.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException
                                       or ObjectDisposedException)
            {
                // The client hanging up, or the fixture shutting down, is the expected end.
            }
        }));

        return port;
    }

    /// <summary>
    /// Reads one request the way EPMD frames it: a two byte length, then exactly that many bytes.
    /// <para>
    /// Draining with a single inexact read would assume the whole request arrived in one go, which
    /// is the same assumption that made an earlier version of these tests fail under load.
    /// </para>
    /// </summary>
    private static async Task<byte[]> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, ct);

        var body = new byte[BinaryPrimitives.ReadUInt16BigEndian(header)];
        await stream.ReadExactlyAsync(body, ct);
        return body;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        foreach (var listener in _listeners) listener.Stop();
        _shutdown.Dispose();
    }

    [Fact]
    public async Task A_port_mapper_that_says_nothing_does_not_stall_the_caller()
    {
        // Accept the connection, then go quiet. Without a deadline this waits for ever, and it
        // happens before any handshake timeout can help: the lookup comes first.
        var port = StartFakeEpmd(async (_, ct) => await Task.Delay(System.Threading.Timeout.Infinite, ct));

        var client = new EpmdClient("127.0.0.1", port) { Timeout = TimeSpan.FromMilliseconds(300) };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        // One failure mode: whether cancellation or the socket teardown wins the race is an
        // implementation detail no caller should have to catch both sides of.
        var ex = await Assert.ThrowsAsync<EpmdException>(() => client.LookupAsync("anything"));
        stopwatch.Stop();

        Assert.Contains("did not respond", ex.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"the lookup took {stopwatch.Elapsed.TotalSeconds:0.0}s despite a 0.3s timeout");
    }

    [Fact]
    public async Task An_endless_node_list_is_refused_rather_than_buffered()
    {
        // The names response has no length prefix and is read until the socket closes, so something
        // impersonating EPMD could otherwise stream until the process runs out of memory.
        var port = StartFakeEpmd(async (stream, ct) =>
        {
            var chunk = new byte[8192];
            while (!ct.IsCancellationRequested) await stream.WriteAsync(chunk, ct);
        });

        var client = new EpmdClient("127.0.0.1", port) { Timeout = TimeSpan.FromSeconds(10) };

        var ex = await Assert.ThrowsAsync<EpmdException>(() => client.NamesAsync());
        Assert.Contains("exceeded", ex.Message);
    }

    [Fact]
    public async Task A_truncated_lookup_response_is_reported_rather_than_read_past()
    {
        // Claims a 65535 byte node name and then hangs up.
        var port = StartFakeEpmd(async (stream, ct) =>
        {
            await ReadRequestAsync(stream, ct);
            await stream.WriteAsync(new byte[]
            {
                119, 0,               // PORT2_RESP, result ok
                0, 80,                // port
                77, 0,                // node type, protocol
                0, 6, 0, 6,           // version range
                0xFF, 0xFF            // a name length nothing follows
            }, ct);
        });

        var client = new EpmdClient("127.0.0.1", port) { Timeout = TimeSpan.FromSeconds(5) };

        await Assert.ThrowsAsync<EpmdException>(() => client.LookupAsync("anything"));
    }

    [Fact]
    public async Task A_nonsense_response_tag_is_reported()
    {
        var port = StartFakeEpmd(async (stream, ct) =>
        {
            await ReadRequestAsync(stream, ct);
            await stream.WriteAsync(new byte[] { 0xAB, 0xCD }, ct);
        });

        var client = new EpmdClient("127.0.0.1", port) { Timeout = TimeSpan.FromSeconds(5) };

        var ex = await Assert.ThrowsAsync<EpmdException>(() => client.LookupAsync("anything"));
        Assert.Contains("unexpected", ex.Message);
    }
}
