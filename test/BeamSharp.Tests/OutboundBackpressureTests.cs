using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
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
    private static byte[] UnlinkFrame()
    {
        var enc = new TermEncoder(256);
        enc.WriteByte(0);
        enc.WriteByte(0);
        enc.WriteByte(0);
        enc.WriteByte(0);
        enc.WriteByte(112); // PASS_THROUGH
        enc.WriteVersionedTerm(new ErlTuple(
            new ErlInt((int)DistOp.UnlinkId),
            new ErlInt(1),
            new ErlPid("flood@nowhere", 1, 0, 1),
            new ErlPid("victim@nowhere", 1, 0, 1)));
        enc.PatchUInt32(0, (uint)(enc.Length - 4));
        return enc.Segment.ToArray();
    }

    [Fact]
    public async Task A_peer_that_never_reads_our_acks_does_not_wedge_the_read_loop()
    {
        // Deliberately not a real socket on both ends: whether the outbound queue fills would then
        // depend on how much the kernel is willing to buffer, which differs by platform and by
        // tuning. A stream that never finishes a write reaches the state under test on any machine.
        using var choked = new ChokedStream();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var peer = new TcpClient();
        await peer.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var accepted = await listener.AcceptTcpClientAsync();

        var connection = new DistConnection(accepted, choked,
            new HandshakeResult("flood@nowhere", DistributionFlags.Default, 1), TimeSpan.FromSeconds(60));

        var handled = 0;
        connection.OnMessage = (_, _) =>
        {
            // Stand in for the dispatcher: answer every inbound signal, as the real handlers do.
            Interlocked.Increment(ref handled);
            connection.TrySendSignal(new ErlTuple(
                new ErlInt((int)DistOp.UnlinkIdAck),
                new ErlInt(1),
                new ErlPid("flood@nowhere", 1, 0, 1),
                new ErlPid("victim@nowhere", 1, 0, 1)));
            return Task.CompletedTask;
        };
        connection.Start();

        // Comfortably past what the queue holds, since nothing can drain it.
        var frame = UnlinkFrame();
        for (var i = 0; i < 6000; i++) choked.Deliver(frame);

        // A read loop that waits for room here stops at the queue's capacity and never resumes: no
        // close, no log, no tick, since the watchdog waits in the same place.
        var closed = SpinWait.SpinUntil(() => connection.IsClosed, TimeSpan.FromSeconds(15));
        Assert.True(closed,
            $"the read loop stopped after {Volatile.Read(ref handled):N0} signals and the connection stayed open");

        // And it got far enough to fill the queue rather than dying early for some other reason.
        Assert.True(handled > 4000, $"only {handled:N0} signals were handled before the connection closed");

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

    /// <summary>
    /// Serves queued frames to the reader and never finishes a write, which is what a peer whose
    /// receive window has closed looks like from this side -- without depending on a real one.
    /// </summary>
    private sealed class ChokedStream : Stream
    {
        private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>();
        private readonly CancellationTokenSource _disposed = new();
        private ReadOnlyMemory<byte> _current;

        public void Deliver(byte[] frame) => _inbound.Writer.TryWrite(frame);

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
            while (_current.IsEmpty)
                _current = await _inbound.Reader.ReadAsync(linked.Token).ConfigureAwait(false);

            var take = Math.Min(buffer.Length, _current.Length);
            _current[..take].CopyTo(buffer);
            _current = _current[take..];
            return take;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            // The whole point: this never completes, so nothing drains the outbound queue.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
            await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
        }

        private int _disposeCount;

        protected override void Dispose(bool disposing)
        {
            // DistConnection disposes the stream it was given and the test holds one too, so this
            // is called twice.
            if (disposing && Interlocked.Exchange(ref _disposeCount, 1) == 0)
            {
                _disposed.Cancel();
                _disposed.Dispose();
            }

            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
