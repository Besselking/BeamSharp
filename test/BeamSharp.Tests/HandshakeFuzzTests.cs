using System.Buffers.Binary;
using System.Text;
using BeamSharp.Protocol;
using Xunit;

namespace BeamSharp.Tests;

/// <summary>
/// The handshake is the most exposed code in the library: it parses bytes from anyone who can open
/// a socket, <em>before</em> the cookie has authenticated them. Everything else at least requires
/// the shared secret first.
/// <para>
/// The contract fuzzed here is that hostile input produces a connection failure — a
/// <see cref="HandshakeException"/>, an end of stream, or an I/O error — and never an index,
/// argument or arithmetic exception, which would mean a parser reading outside what it checked.
/// </para>
/// </summary>
public class HandshakeFuzzTests
{
    private const string LocalNode = "victim@host";
    private const string Cookie = "cookie";

    private static void AssertCleanFailure(Exception? ex, byte[] input, string direction)
    {
        switch (ex)
        {
            case null:
            case HandshakeException:
            case EndOfStreamException:
            case IOException:
            case OperationCanceledException:
                return;
            default:
                Assert.Fail($"{ex.GetType().Name} escaped the {direction} handshake for " +
                            $"{Convert.ToHexString(input)}: {ex.Message}");
                return;
        }
    }

    private static async Task<Exception?> AcceptAsync(byte[] input, int prefix)
    {
        try
        {
            await Handshake.AcceptAsync(new FakeDuplexStream(input), LocalNode, 7,
                DistributionFlags.Default, _ => Cookie, prefix, CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task<Exception?> ConnectAsync(byte[] input, int prefix)
    {
        try
        {
            await Handshake.ConnectAsync(new FakeDuplexStream(input), LocalNode, 7,
                DistributionFlags.Default, "peer@host", Cookie, prefix, CancellationToken.None);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Wraps a payload in the length prefix the transport uses.</summary>
    private static byte[] Frame(byte[] payload, int prefix)
    {
        var frame = new byte[prefix + payload.Length];
        if (prefix == 2) BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)payload.Length);
        else BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, prefix);
        return frame;
    }

    [Theory]
    [InlineData(Handshake.TcpLengthPrefix)]
    [InlineData(Handshake.TlsLengthPrefix)]
    public async Task Arbitrary_bytes_from_a_dialling_peer_fail_cleanly(int prefix)
    {
        var rng = new Random(1234 + prefix);

        for (var i = 0; i < 4_000; i++)
        {
            var input = new byte[rng.Next(1, 96)];
            rng.NextBytes(input);
            AssertCleanFailure(await AcceptAsync(input, prefix), input, "accepting");
        }
    }

    [Theory]
    [InlineData(Handshake.TcpLengthPrefix)]
    [InlineData(Handshake.TlsLengthPrefix)]
    public async Task Arbitrary_bytes_from_a_dialled_peer_fail_cleanly(int prefix)
    {
        var rng = new Random(5678 + prefix);

        for (var i = 0; i < 4_000; i++)
        {
            var input = new byte[rng.Next(1, 96)];
            rng.NextBytes(input);
            AssertCleanFailure(await ConnectAsync(input, prefix), input, "connecting");
        }
    }

    [Theory]
    [InlineData(Handshake.TcpLengthPrefix)]
    [InlineData(Handshake.TlsLengthPrefix)]
    public async Task Well_formed_name_messages_with_hostile_fields_fail_cleanly(int prefix)
    {
        // Random bytes rarely get past the first tag byte. Building messages that look right and
        // lying in the length fields is what actually reaches the parsing.
        var rng = new Random(4321);

        for (var i = 0; i < 4_000; i++)
        {
            var nameBytes = new byte[rng.Next(0, 24)];
            rng.NextBytes(nameBytes);

            var payload = new byte[15 + nameBytes.Length];
            payload[0] = (byte)'N';
            BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(1), (ulong)DistributionFlags.Default);
            BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(9), (uint)rng.Next());
            // The claimed name length is deliberately unrelated to what follows.
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(13), (ushort)rng.Next(0, ushort.MaxValue));
            nameBytes.CopyTo(payload, 15);

            var framed = Frame(payload, prefix);
            AssertCleanFailure(await AcceptAsync(framed, prefix), framed, "accepting");
        }
    }

    [Theory]
    [InlineData(Handshake.TcpLengthPrefix)]
    [InlineData(Handshake.TlsLengthPrefix)]
    public async Task Well_formed_challenge_messages_with_hostile_fields_fail_cleanly(int prefix)
    {
        // The dialling side has to survive a hostile server too: EPMD is unauthenticated, so what
        // answers on the port it names is not necessarily what we meant to reach.
        var rng = new Random(8765);

        for (var i = 0; i < 4_000; i++)
        {
            var nameBytes = new byte[rng.Next(0, 24)];
            rng.NextBytes(nameBytes);

            var challenge = new byte[19 + nameBytes.Length];
            challenge[0] = (byte)'N';
            BinaryPrimitives.WriteUInt64BigEndian(challenge.AsSpan(1), (ulong)DistributionFlags.Default);
            BinaryPrimitives.WriteUInt32BigEndian(challenge.AsSpan(9), (uint)rng.Next());
            BinaryPrimitives.WriteUInt32BigEndian(challenge.AsSpan(13), (uint)rng.Next());
            BinaryPrimitives.WriteUInt16BigEndian(challenge.AsSpan(17), (ushort)rng.Next(0, ushort.MaxValue));
            nameBytes.CopyTo(challenge, 19);

            var stream = new List<byte>();
            stream.AddRange(Frame(Encoding.ASCII.GetBytes("sok"), prefix));
            stream.AddRange(Frame(challenge, prefix));

            var input = stream.ToArray();
            AssertCleanFailure(await ConnectAsync(input, prefix), input, "connecting");
        }
    }

    [Theory]
    [InlineData(Handshake.TcpLengthPrefix)]
    [InlineData(Handshake.TlsLengthPrefix)]
    public async Task Truncating_a_valid_exchange_anywhere_fails_cleanly(int prefix)
    {
        // Every prefix of a plausible conversation, so a peer that hangs up mid-message is handled.
        var name = Encoding.UTF8.GetBytes("peer@host");
        var payload = new byte[15 + name.Length];
        payload[0] = (byte)'N';
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(1), (ulong)DistributionFlags.Default);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(9), 7);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(13), (ushort)name.Length);
        name.CopyTo(payload, 15);

        var full = Frame(payload, prefix);

        for (var length = 0; length < full.Length; length++)
        {
            var truncated = full.AsSpan(0, length).ToArray();
            AssertCleanFailure(await AcceptAsync(truncated, prefix), truncated, "accepting");
        }
    }

    [Fact]
    public async Task An_absurd_frame_length_is_refused_without_allocating_it()
    {
        // A four-byte prefix can claim four gigabytes. Believing it would be the same mistake the
        // term decoder used to make with element counts.
        var input = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, (byte)'N' };

        var before = GC.GetAllocatedBytesForCurrentThread();
        var ex = await AcceptAsync(input, Handshake.TlsLengthPrefix);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsType<HandshakeException>(ex);
        Assert.True(allocated < 64 * 1024, $"refusing the frame allocated {allocated:N0} bytes");
    }
}
