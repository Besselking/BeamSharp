using System.Buffers.Binary;
using System.Net.Sockets;
using BeamSharp.Networking;
using System.Text;

namespace BeamSharp.Epmd;

/// <summary>How the node advertises itself to the rest of the cluster.</summary>
public enum NodeVisibility : byte
{
    /// <summary>
    /// Appears in <c>Node.list()</c> and takes part in the fully connected mesh. Also means other nodes
    /// expect us to speak the <c>global</c> name-registry protocol, which this library does not implement.
    /// </summary>
    Visible = (byte)'M',

    /// <summary>
    /// A hidden node, the same choice jinterface and C nodes make. Messaging, monitors and links behave
    /// identically; the node just does not join the mesh and shows up under <c>Node.list(:hidden)</c>.
    /// </summary>
    Hidden = (byte)'H'
}

/// <summary>Result of registering with EPMD.</summary>
public sealed record EpmdRegistration(uint Creation);

/// <summary>What EPMD knows about some other node.</summary>
public sealed record EpmdNodeInfo(string Name, int Port, NodeVisibility Visibility, int HighestVersion, int LowestVersion);

/// <summary>
/// Talks to the Erlang Port Mapper Daemon. The registration socket must stay open for as long as the
/// node is alive: EPMD unregisters a node the moment its connection drops.
/// </summary>
public sealed class EpmdClient : IAsyncDisposable
{
    private const byte Alive2Req = (byte)'x';
    private const byte Alive2Resp = (byte)'y';
    private const byte Alive2XResp = (byte)'v';
    private const byte Port2Req = (byte)'z';
    private const byte Port2Resp = (byte)'w';
    private const byte NamesReq = (byte)'n';

    private const ushort DistVersionHigh = 6;
    private const ushort DistVersionLow = 6;

    private readonly string _host;
    private readonly int _port;
    private TcpClient? _registration;

    public EpmdClient(string host = "127.0.0.1", int? port = null)
    {
        _host = host;
        _port = port ?? DefaultPort;
    }

    /// <summary>The EPMD port, honouring <c>ERL_EPMD_PORT</c> just like OTP does.</summary>
    public static int DefaultPort =>
        int.TryParse(Environment.GetEnvironmentVariable("ERL_EPMD_PORT"), out var p) ? p : 4369;

    /// <summary>
    /// Registers <paramref name="aliveName"/> (the part before the '@') as listening on
    /// <paramref name="listenPort"/> and keeps the connection open.
    /// </summary>
    public async Task<EpmdRegistration> RegisterAsync(
        string aliveName,
        int listenPort,
        NodeVisibility visibility = NodeVisibility.Hidden,
        CancellationToken ct = default)
    {
        if (_registration is not null) throw new InvalidOperationException("already registered with EPMD");

        var client = await HostResolver.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        var stream = client.GetStream();

        var name = Encoding.UTF8.GetBytes(aliveName);
        var body = new byte[1 + 2 + 1 + 1 + 2 + 2 + 2 + name.Length + 2];
        var w = body.AsSpan();
        w[0] = Alive2Req;
        BinaryPrimitives.WriteUInt16BigEndian(w[1..], (ushort)listenPort);
        w[3] = (byte)visibility;
        w[4] = 0; // protocol: tcp/ipv4
        BinaryPrimitives.WriteUInt16BigEndian(w[5..], DistVersionHigh);
        BinaryPrimitives.WriteUInt16BigEndian(w[7..], DistVersionLow);
        BinaryPrimitives.WriteUInt16BigEndian(w[9..], (ushort)name.Length);
        name.CopyTo(w[11..]);
        BinaryPrimitives.WriteUInt16BigEndian(w[(11 + name.Length)..], 0); // no extra

        await WriteFramedAsync(stream, body, ct).ConfigureAwait(false);

        var tag = new byte[1];
        await ReadExactAsync(stream, tag, ct).ConfigureAwait(false);

        uint creation;
        switch (tag[0])
        {
            case Alive2XResp:
            {
                var resp = new byte[5];
                await ReadExactAsync(stream, resp, ct).ConfigureAwait(false);
                if (resp[0] != 0) throw new EpmdException($"EPMD refused registration of '{aliveName}' (likely a duplicate name)");
                creation = BinaryPrimitives.ReadUInt32BigEndian(resp.AsSpan(1));
                break;
            }
            case Alive2Resp:
            {
                var resp = new byte[3];
                await ReadExactAsync(stream, resp, ct).ConfigureAwait(false);
                if (resp[0] != 0) throw new EpmdException($"EPMD refused registration of '{aliveName}' (likely a duplicate name)");
                creation = BinaryPrimitives.ReadUInt16BigEndian(resp.AsSpan(1));
                break;
            }
            default:
                client.Dispose();
                throw new EpmdException($"unexpected EPMD registration response tag {tag[0]}");
        }

        _registration = client;
        return new EpmdRegistration(creation);
    }

    /// <summary>Looks up a node by its alive name. Returns null when EPMD does not know it.</summary>
    public async Task<EpmdNodeInfo?> LookupAsync(string aliveName, CancellationToken ct = default)
    {
        using var client = await HostResolver.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        var stream = client.GetStream();

        var name = Encoding.UTF8.GetBytes(aliveName);
        var body = new byte[1 + name.Length];
        body[0] = Port2Req;
        name.CopyTo(body, 1);
        await WriteFramedAsync(stream, body, ct).ConfigureAwait(false);

        var head = new byte[2];
        await ReadExactAsync(stream, head, ct).ConfigureAwait(false);
        if (head[0] != Port2Resp) throw new EpmdException($"unexpected EPMD lookup response tag {head[0]}");
        if (head[1] != 0) return null; // not registered

        var fixedPart = new byte[8];
        await ReadExactAsync(stream, fixedPart, ct).ConfigureAwait(false);
        var port = BinaryPrimitives.ReadUInt16BigEndian(fixedPart);
        var visibility = (NodeVisibility)fixedPart[2];
        var high = BinaryPrimitives.ReadUInt16BigEndian(fixedPart.AsSpan(4));
        var low = BinaryPrimitives.ReadUInt16BigEndian(fixedPart.AsSpan(6));

        var nlen = new byte[2];
        await ReadExactAsync(stream, nlen, ct).ConfigureAwait(false);
        var nameBytes = new byte[BinaryPrimitives.ReadUInt16BigEndian(nlen)];
        await ReadExactAsync(stream, nameBytes, ct).ConfigureAwait(false);

        return new EpmdNodeInfo(Encoding.UTF8.GetString(nameBytes), port, visibility, high, low);
    }

    /// <summary>Lists every node EPMD currently knows about, as name/port pairs.</summary>
    public async Task<IReadOnlyList<(string Name, int Port)>> NamesAsync(CancellationToken ct = default)
    {
        using var client = await HostResolver.ConnectAsync(_host, _port, ct).ConfigureAwait(false);
        var stream = client.GetStream();
        await WriteFramedAsync(stream, [NamesReq], ct).ConfigureAwait(false);

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
        var all = ms.ToArray();
        if (all.Length < 4) return [];

        var results = new List<(string, int)>();
        foreach (var line in Encoding.UTF8.GetString(all, 4, all.Length - 4).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "name <alive> at port 12345"
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5 && int.TryParse(parts[^1], out var p)) results.Add((parts[1], p));
        }
        return results;
    }

    private static async Task WriteFramedAsync(NetworkStream stream, byte[] body, CancellationToken ct)
    {
        var frame = new byte[2 + body.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)body.Length);
        body.CopyTo(frame, 2);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) throw new EpmdException("EPMD closed the connection unexpectedly");
            read += n;
        }
    }

    public ValueTask DisposeAsync()
    {
        _registration?.Dispose();
        _registration = null;
        return ValueTask.CompletedTask;
    }
}

public sealed class EpmdException : Exception
{
    public EpmdException(string message) : base(message) { }
}
