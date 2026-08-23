using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BeamSharp.Protocol;

/// <summary>Everything the handshake settled on.</summary>
public sealed record HandshakeResult(string PeerNode, DistributionFlags Flags, uint PeerCreation);

/// <summary>
/// The OTP 23+ distribution handshake ('N' messages, 64-bit flags, MD5 cookie challenge).
/// <para>
/// The length prefix on handshake frames depends on the transport, which is easy to miss:
/// <c>inet_tcp_dist</c> uses two bytes during the handshake and switches to four afterwards, while
/// <c>inet_tls_dist</c> uses four throughout. Getting it wrong produces a connection that hangs
/// rather than one that reports anything useful.
/// </para>
/// </summary>
public static class Handshake
{
    /// <summary>Handshake framing for a plain TCP connection.</summary>
    public const int TcpLengthPrefix = 2;

    /// <summary>Handshake framing for a TLS connection, which never uses the 2-byte form.</summary>
    public const int TlsLengthPrefix = 4;

    /// <summary>Runs the handshake as the side that accepted the TCP connection.</summary>
    public static async Task<HandshakeResult> AcceptAsync(
        Stream stream,
        string localNode,
        uint localCreation,
        DistributionFlags localFlags,
        Func<string, string> cookieForNode,
        int lengthPrefix = TcpLengthPrefix,
        CancellationToken ct = default)
    {
        // 1. Peer sends its name.
        var nameMsg = await ReadFrameAsync(stream, lengthPrefix, ct).ConfigureAwait(false);
        var (peerFlags, peerNode, peerCreation, isOldFormat) = ParseName(nameMsg);

        var missing = DistributionFlags.Mandatory & ~ExpandMandatoryDigest(peerFlags);
        if (!isOldFormat && missing != DistributionFlags.None)
        {
            await SendStatusAsync(stream, "not_allowed", lengthPrefix, ct).ConfigureAwait(false);
            throw new HandshakeException($"peer {peerNode} is missing mandatory distribution flags: {missing}");
        }

        // 2. Accept it.
        await SendStatusAsync(stream, "ok", lengthPrefix, ct).ConfigureAwait(false);

        // 3. Our challenge.
        var ourChallenge = NextChallenge();
        await WriteFrameAsync(stream, BuildNewChallenge(localFlags, ourChallenge, localCreation, localNode), lengthPrefix, ct)
            .ConfigureAwait(false);

        // 3b. A pre-OTP-23 peer follows up with the high flag bits and its creation.
        if (isOldFormat)
        {
            var complement = await ReadFrameAsync(stream, lengthPrefix, ct).ConfigureAwait(false);
            if (complement.Length != 9 || complement[0] != (byte)'c')
                throw new HandshakeException("expected a 'c' complement message from a pre-OTP-23 peer");
            var highFlags = BinaryPrimitives.ReadUInt32BigEndian(complement.AsSpan(1));
            peerFlags |= (DistributionFlags)((ulong)highFlags << 32);
            peerCreation = BinaryPrimitives.ReadUInt32BigEndian(complement.AsSpan(5));
        }

        var cookie = cookieForNode(peerNode);

        // 4. Peer answers our challenge and poses its own.
        var reply = await ReadFrameAsync(stream, lengthPrefix, ct).ConfigureAwait(false);
        if (reply.Length != 21 || reply[0] != (byte)'r')
            throw new HandshakeException("malformed challenge reply");
        var peerChallenge = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(1));
        var expected = Digest(ourChallenge, cookie);
        if (!CryptographicOperations.FixedTimeEquals(reply.AsSpan(5), expected))
            throw new HandshakeException($"bad cookie in challenge reply from {peerNode}");

        // 5. Answer theirs.
        var ack = new byte[17];
        ack[0] = (byte)'a';
        Digest(peerChallenge, cookie).CopyTo(ack, 1);
        await WriteFrameAsync(stream, ack, lengthPrefix, ct).ConfigureAwait(false);

        return new HandshakeResult(peerNode, Negotiate(localFlags, ExpandMandatoryDigest(peerFlags)), peerCreation);
    }

    /// <summary>Runs the handshake as the side that opened the TCP connection.</summary>
    public static async Task<HandshakeResult> ConnectAsync(
        Stream stream,
        string localNode,
        uint localCreation,
        DistributionFlags localFlags,
        string expectedPeerNode,
        string cookie,
        int lengthPrefix = TcpLengthPrefix,
        CancellationToken ct = default)
    {
        // 1. Announce ourselves.
        await WriteFrameAsync(stream, BuildNewName(localFlags, localCreation, localNode), lengthPrefix, ct).ConfigureAwait(false);

        // 2. Status.
        var statusMsg = await ReadFrameAsync(stream, lengthPrefix, ct).ConfigureAwait(false);
        if (statusMsg.Length < 1 || statusMsg[0] != (byte)'s')
            throw new HandshakeException("expected a status message");
        var status = Encoding.ASCII.GetString(statusMsg, 1, statusMsg.Length - 1);
        switch (status)
        {
            case "ok":
            case "ok_simultaneous":
                break;
            case "alive":
                // The peer still has a live connection to us; decline to take it over.
                await SendStatusAsync(stream, "false", lengthPrefix, ct).ConfigureAwait(false);
                throw new HandshakeException($"{expectedPeerNode} reports an existing connection to {localNode}");
            default:
                throw new HandshakeException($"{expectedPeerNode} rejected the connection: {status}");
        }

        // 3. Their challenge.
        var challengeMsg = await ReadFrameAsync(stream, lengthPrefix, ct).ConfigureAwait(false);
        if (challengeMsg.Length < 19 || challengeMsg[0] != (byte)'N')
            throw new HandshakeException("malformed challenge message");
        var peerFlags = (DistributionFlags)BinaryPrimitives.ReadUInt64BigEndian(challengeMsg.AsSpan(1));
        var peerChallenge = BinaryPrimitives.ReadUInt32BigEndian(challengeMsg.AsSpan(9));
        var peerCreation = BinaryPrimitives.ReadUInt32BigEndian(challengeMsg.AsSpan(13));
        var nameLen = BinaryPrimitives.ReadUInt16BigEndian(challengeMsg.AsSpan(17));
        // The name message parser checks this; the challenge parser did not, so a peer could claim
        // a longer name than it sent. EPMD is unauthenticated, so what answers on the port it named
        // is not necessarily what we meant to reach.
        if (challengeMsg.Length < 19 + nameLen)
            throw new HandshakeException("the challenge message claims a longer node name than it carries");
        var peerNode = Encoding.UTF8.GetString(challengeMsg, 19, nameLen);

        if (peerNode != expectedPeerNode)
            throw new HandshakeException($"expected to reach {expectedPeerNode} but the node identified as {peerNode}");

        // 4. Answer, and challenge back.
        var ourChallenge = NextChallenge();
        var reply = new byte[21];
        reply[0] = (byte)'r';
        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(1), ourChallenge);
        Digest(peerChallenge, cookie).CopyTo(reply, 5);
        await WriteFrameAsync(stream, reply, lengthPrefix, ct).ConfigureAwait(false);

        // 5. Verify their answer.
        var ack = await ReadFrameAsync(stream, lengthPrefix, ct).ConfigureAwait(false);
        if (ack.Length != 17 || ack[0] != (byte)'a')
            throw new HandshakeException("malformed challenge ack");
        if (!CryptographicOperations.FixedTimeEquals(ack.AsSpan(1), Digest(ourChallenge, cookie)))
            throw new HandshakeException($"bad cookie in challenge ack from {peerNode}");

        return new HandshakeResult(peerNode, Negotiate(localFlags, ExpandMandatoryDigest(peerFlags)), peerCreation);
    }

    // --- message builders ---------------------------------------------------

    private static byte[] BuildNewName(DistributionFlags flags, uint creation, string node)
    {
        var name = Encoding.UTF8.GetBytes(node);
        var msg = new byte[1 + 8 + 4 + 2 + name.Length];
        msg[0] = (byte)'N';
        BinaryPrimitives.WriteUInt64BigEndian(msg.AsSpan(1), (ulong)flags);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(9), creation);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(13), (ushort)name.Length);
        name.CopyTo(msg, 15);
        return msg;
    }

    private static byte[] BuildNewChallenge(DistributionFlags flags, uint challenge, uint creation, string node)
    {
        var name = Encoding.UTF8.GetBytes(node);
        var msg = new byte[1 + 8 + 4 + 4 + 2 + name.Length];
        msg[0] = (byte)'N';
        BinaryPrimitives.WriteUInt64BigEndian(msg.AsSpan(1), (ulong)flags);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(9), challenge);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(13), creation);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(17), (ushort)name.Length);
        name.CopyTo(msg, 19);
        return msg;
    }

    private static (DistributionFlags Flags, string Node, uint Creation, bool IsOldFormat) ParseName(byte[] msg)
    {
        if (msg.Length < 1) throw new HandshakeException("empty name message");

        switch (msg[0])
        {
            case (byte)'N':
            {
                if (msg.Length < 15) throw new HandshakeException("truncated 'N' name message");
                var flags = (DistributionFlags)BinaryPrimitives.ReadUInt64BigEndian(msg.AsSpan(1));
                var creation = BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(9));
                var nameLen = BinaryPrimitives.ReadUInt16BigEndian(msg.AsSpan(13));
                if (msg.Length < 15 + nameLen) throw new HandshakeException("truncated node name");
                return (flags, Encoding.UTF8.GetString(msg, 15, nameLen), creation, false);
            }
            case (byte)'n':
            {
                // Pre-OTP-23: 32-bit flags, no creation, name runs to the end of the frame.
                if (msg.Length < 7) throw new HandshakeException("truncated 'n' name message");
                var flags = (DistributionFlags)BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(3));
                return (flags, Encoding.UTF8.GetString(msg, 7, msg.Length - 7), 0, true);
            }
            default:
                throw new HandshakeException($"unexpected handshake message tag '{(char)msg[0]}'");
        }
    }

    private static Task SendStatusAsync(Stream stream, string status, int lengthPrefix, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes("s" + status);
        return WriteFrameAsync(stream, bytes, lengthPrefix, ct);
    }

    // --- flag helpers -------------------------------------------------------

    /// <summary>OTP 25+ may send one digest bit standing in for the whole OTP 25 mandatory set.</summary>
    private static DistributionFlags ExpandMandatoryDigest(DistributionFlags flags) =>
        flags.HasFlag(DistributionFlags.Mandatory25Digest)
            ? flags | DistributionFlags.ExtendedReferences | DistributionFlags.FunTags |
              DistributionFlags.ExtendedPidsPorts | DistributionFlags.NewFunTags | DistributionFlags.ExportPtrTag |
              DistributionFlags.BitBinaries | DistributionFlags.NewFloats | DistributionFlags.Utf8Atoms |
              DistributionFlags.MapTag | DistributionFlags.BigCreation | DistributionFlags.Handshake23
            : flags;

    /// <summary>A capability is in play only if both ends offered it.</summary>
    private static DistributionFlags Negotiate(DistributionFlags ours, DistributionFlags theirs) => ours & theirs;

    // --- challenge / digest -------------------------------------------------

    private static uint NextChallenge()
    {
        Span<byte> b = stackalloc byte[4];
        RandomNumberGenerator.Fill(b);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    /// <summary>OTP's <c>gen_digest/2</c>: md5(cookie ++ integer_to_list(Challenge)).</summary>
    internal static byte[] Digest(uint challenge, string cookie) =>
        MD5.HashData(Encoding.ASCII.GetBytes(cookie + challenge.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    // --- handshake framing, 2 or 4 bytes depending on the transport ---------

    private static async Task<byte[]> ReadFrameAsync(Stream stream, int lengthPrefix, CancellationToken ct)
    {
        var header = new byte[lengthPrefix];
        await ReadExactAsync(stream, header, ct).ConfigureAwait(false);

        var length = lengthPrefix == 2
            ? BinaryPrimitives.ReadUInt16BigEndian(header)
            : BinaryPrimitives.ReadUInt32BigEndian(header);

        if (length > 64 * 1024)
            throw new HandshakeException($"a handshake frame of {length} bytes is implausible");

        var body = new byte[length];
        await ReadExactAsync(stream, body, ct).ConfigureAwait(false);
        return body;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] body, int lengthPrefix, CancellationToken ct)
    {
        var frame = new byte[lengthPrefix + body.Length];
        if (lengthPrefix == 2) BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)body.Length);
        else BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, lengthPrefix);

        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("the distribution peer closed the connection");
            read += n;
        }
    }
}

/// <summary>Thrown when the distribution handshake fails, most often a cookie mismatch.</summary>
public sealed class HandshakeException : Exception
{
    /// <summary>Creates the exception with an explanatory message.</summary>
    public HandshakeException(string message) : base(message) { }
}
