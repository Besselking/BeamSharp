using System.Buffers.Binary;
using System.Net.Sockets;
using BeamSharp.Terms;

namespace BeamSharp.Protocol;

/// <summary>One distribution frame: a control tuple plus an optional payload term.</summary>
public readonly record struct DistMessage(ErlTerm Control, ErlTerm? Payload)
{
    public DistOp Op => (DistOp)((ErlInt)((ErlTuple)Control)[0]).AsInt;
}

/// <summary>
/// A live connection to one peer node, after the handshake. Frames are length-prefixed with four
/// bytes; a zero-length frame is a keepalive tick.
/// </summary>
public sealed class DistConnection : IAsyncDisposable
{
    private const byte PassThrough = 112;
    private const int MaxFrameLength = 64 * 1024 * 1024;

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private long _lastReceivedTicks;
    private long _lastSentTicks;
    private int _closed;

    internal DistConnection(TcpClient client, NetworkStream stream, HandshakeResult handshake, TimeSpan tickTime)
    {
        _client = client;
        _stream = stream;
        PeerNode = handshake.PeerNode;
        Flags = handshake.Flags;
        PeerCreation = handshake.PeerCreation;
        TickTime = tickTime;
        _lastReceivedTicks = Environment.TickCount64;
        _lastSentTicks = Environment.TickCount64;
    }

    public string PeerNode { get; }
    public DistributionFlags Flags { get; }
    public uint PeerCreation { get; }
    public TimeSpan TickTime { get; }
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Invoked for every control message received.</summary>
    public Func<DistConnection, DistMessage, Task>? OnMessage { get; set; }

    /// <summary>Invoked once when the connection goes away, with the reason if it was an error.</summary>
    public Action<DistConnection, Exception?>? Closed { get; set; }

    /// <summary>Starts the read loop and the keepalive ticker.</summary>
    public void Start()
    {
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(TickLoopAsync);
    }

    /// <summary>Sends a control message with an optional payload term.</summary>
    public async Task SendAsync(ErlTerm control, ErlTerm? payload = null, CancellationToken ct = default)
    {
        var enc = new TermEncoder(512);
        enc.WriteByte(0);
        enc.WriteByte(0);
        enc.WriteByte(0);
        enc.WriteByte(0);
        enc.WriteByte(PassThrough);
        enc.WriteVersionedTerm(control);
        if (payload is not null) enc.WriteVersionedTerm(payload);
        enc.PatchUInt32(0, (uint)(enc.Length - 4));

        await WriteRawAsync(enc.Segment, ct).ConfigureAwait(false);
    }

    private async Task WriteRawAsync(ArraySegment<byte> bytes, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsClosed) throw new IOException($"connection to {PeerNode} is closed");
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            _lastSentTicks = Environment.TickCount64;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? error = null;
        try
        {
            var header = new byte[4];
            while (!_cts.IsCancellationRequested)
            {
                await Handshake.ReadExactAsync(_stream, header, _cts.Token).ConfigureAwait(false);
                _lastReceivedTicks = Environment.TickCount64;

                var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length == 0) continue; // tick
                if (length > MaxFrameLength)
                    throw new IOException($"distribution frame of {length} bytes exceeds the {MaxFrameLength} byte limit");

                var body = new byte[length];
                await Handshake.ReadExactAsync(_stream, body, _cts.Token).ConfigureAwait(false);
                _lastReceivedTicks = Environment.TickCount64;

                var message = ParseFrame(body);
                if (message is { } m && OnMessage is { } handler)
                    await handler(this, m).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            CloseInternal(error);
        }
    }

    private static DistMessage? ParseFrame(byte[] body)
    {
        switch (body[0])
        {
            case PassThrough:
            {
                var control = TermDecoder.Decode(body.AsSpan(1), out var used);
                ErlTerm? payload = null;
                var rest = 1 + used;
                if (rest < body.Length) payload = TermDecoder.Decode(body.AsSpan(rest));
                return new DistMessage(control, payload);
            }

            case 131 when body.Length > 1 && body[1] == 68:
                throw new IOException(
                    "peer sent an atom-cache distribution header even though DFLAG_DIST_HDR_ATOM_CACHE was not negotiated");

            case 131 when body.Length > 1 && body[1] is 69 or 70:
                throw new IOException(
                    "peer sent a fragmented message even though DFLAG_FRAGMENTS was not negotiated");

            default:
                throw new IOException($"unrecognised distribution frame starting with byte {body[0]}");
        }
    }

    private async Task TickLoopAsync()
    {
        // OTP sends a tick when a quarter of net_ticktime has passed with nothing sent, and gives up
        // on a peer that has been silent for a full net_ticktime.
        var interval = TickTime / 4;
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                var now = Environment.TickCount64;
                if (now - Volatile.Read(ref _lastReceivedTicks) > TickTime.TotalMilliseconds * 1.25)
                {
                    CloseInternal(new TimeoutException($"no tick from {PeerNode} within {TickTime.TotalSeconds:0}s"));
                    return;
                }

                if (now - Volatile.Read(ref _lastSentTicks) >= interval.TotalMilliseconds)
                    await WriteRawAsync(new ArraySegment<byte>(new byte[4]), _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CloseInternal(ex);
        }
    }

    private void CloseInternal(Exception? error)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _cts.Cancel(); } catch { /* already disposed */ }
        try { _client.Close(); } catch { /* best effort */ }
        Closed?.Invoke(this, error);
    }

    public ValueTask DisposeAsync()
    {
        CloseInternal(null);
        _cts.Dispose();
        _client.Dispose();
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
