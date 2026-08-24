using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using BeamSharp.Protocol;
using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Tests;

/// <summary>
/// The outbound queue exists so that answering an inbound signal never stops us reading. It is
/// bounded, though, and a bound reached by a peer that has stopped reading is the same deadlock
/// arriving later: these pin that the read loop and the tick watchdog stay off it.
/// </summary>
public sealed class OutboundBackpressureTests
{
    /// <summary>A UNLINK_ID control frame, which obliges us to send an ack back.</summary>
    private static byte[] UnlinkFrame(uint id)
    {
        var enc = new TermEncoder(256);
        enc.WriteByte(0);
        enc.WriteByte(0);
        enc.WriteByte(0);
        enc.WriteByte(0);
        enc.WriteByte(112); // PASS_THROUGH
        enc.WriteVersionedTerm(new ErlTuple(
            new ErlInt((int)DistOp.UnlinkId),
            new ErlInt(id),
            new ErlPid("flood@nowhere", id, 0, 1),
            new ErlPid("victim@nowhere", 1, 0, 1)));
        enc.PatchUInt32(0, (uint)(enc.Length - 4));
        return enc.Segment.ToArray();
    }

    [Fact]
    public async Task A_peer_that_never_reads_our_acks_does_not_wedge_the_read_loop()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        // The flooding peer: it writes and never once reads, so our acks pile up in its receive
        // window, then in our socket buffer, then in the outbound queue.
        using var peer = new TcpClient();
        await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var accepted = await listener.AcceptTcpClientAsync();

        var connection = new DistConnection(accepted, accepted.GetStream(),
            new HandshakeResult("flood@nowhere", DistributionFlags.Default, 1), TimeSpan.FromSeconds(60));

        connection.OnMessage = (_, _) =>
        {
            // Stand in for the dispatcher: answer every inbound signal, as the real handlers do.
            connection.TrySendSignal(new ErlTuple(
                new ErlInt((int)DistOp.UnlinkIdAck),
                new ErlInt(1),
                new ErlPid("flood@nowhere", 1, 0, 1),
                new ErlPid("victim@nowhere", 1, 0, 1)));
            return Task.CompletedTask;
        };
        connection.Start();

        var frame = UnlinkFrame(1);
        var peerStream = peer.GetStream();

        // Comfortably more than the outbound queue holds. Before the fix this loop ran until the
        // socket buffers filled and then blocked here for good, because the far side had stopped
        // reading its own inbound frames: our read loop was parked inside EnqueueAsync.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var flooded = 0;
        try
        {
            for (; flooded < 20_000; flooded++)
                await peerStream.WriteAsync(frame, deadline.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"writing stopped making progress after {flooded:N0} signals: the read loop is wedged");
        }
        catch (IOException)
        {
            // The connection gave up on us, which is the intended outcome: see below.
        }

        // The peer ignored everything we sent it, so the connection is the thing that goes -- not
        // the read loop, and not silently.
        var closed = SpinWait.SpinUntil(() => connection.IsClosed, TimeSpan.FromSeconds(10));
        Assert.True(closed, "the connection stayed open with a peer that had stopped reading");

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task A_full_outbound_queue_closes_the_connection_rather_than_waiting()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new TcpClient();
        await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var accepted = await listener.AcceptTcpClientAsync();

        var connection = new DistConnection(accepted, accepted.GetStream(),
            new HandshakeResult("silent@nowhere", DistributionFlags.Default, 1), TimeSpan.FromSeconds(60));

        // Deliberately not Start()ed: with no write loop draining it, the queue fills and stays
        // full, which is the state a peer that has stopped reading eventually produces.
        var signal = new ErlTuple(new ErlInt((int)DistOp.UnlinkIdAck), new ErlInt(1));

        var sent = 0;
        while (connection.TrySendSignal(signal) && sent < 100_000) sent++;

        Assert.True(connection.IsClosed, "a full queue has to end the connection, not block on it");
        Assert.InRange(sent, 1, 8192);

        await connection.DisposeAsync();
    }
}
