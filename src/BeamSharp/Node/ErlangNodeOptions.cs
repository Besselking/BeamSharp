using BeamSharp.Epmd;
using BeamSharp.Protocol;
using BeamSharp.Security;

namespace BeamSharp.Node;

/// <summary>Configuration for an <see cref="ErlangNode"/>.</summary>
public sealed class ErlangNodeOptions
{
    /// <summary>
    /// The magic cookie. When null, the contents of <c>~/.erlang.cookie</c> are used, which is what a
    /// local <c>iex --sname</c> session will be using too.
    /// </summary>
    public string? Cookie { get; set; }

    /// <summary>TCP port to listen on. 0 lets the OS choose, which is what OTP nodes do.</summary>
    public int Port { get; set; }

    /// <summary>Address to bind the distribution listener to.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// Hidden (the default) behaves like a C node or a jinterface node. See
    /// <see cref="NodeVisibility"/> for what changes if you make it visible.
    /// </summary>
    public NodeVisibility Visibility { get; set; } = NodeVisibility.Hidden;

    /// <summary>
    /// How long a connection may take to get through TLS negotiation and the distribution
    /// handshake before it is dropped, matching OTP's <c>net_kernel:connecttime()</c>.
    /// </summary>
    /// <remarks>
    /// Without a bound here a peer that connects and then says nothing holds the attempt open
    /// forever, and so does a mismatched transport: a TLS dialler reaching a plaintext node makes
    /// that node read a length prefix out of a ClientHello and wait for bytes that never arrive.
    /// </remarks>
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(7);

    /// <summary>
    /// How many inbound handshakes may be in flight at once. Sockets arriving past the limit are
    /// closed rather than queued.
    /// </summary>
    /// <remarks>
    /// Every attempt costs a task and, with TLS configured, an asymmetric-crypto operation, all of
    /// it before the peer has proved it holds the cookie. <see cref="HandshakeTimeout"/> bounds how
    /// long any one attempt lasts; this bounds how many there can be.
    /// </remarks>
    public int MaxConcurrentHandshakes { get; set; } = 64;

    /// <summary>Must match the peer's <c>net_ticktime</c> (60 seconds by default in OTP).</summary>
    public TimeSpan TickTime { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Host EPMD runs on.</summary>
    public string EpmdHost { get; set; } = "127.0.0.1";

    /// <summary>EPMD port; defaults to <c>ERL_EPMD_PORT</c> or 4369.</summary>
    public int? EpmdPort { get; set; }

    /// <summary>
    /// Encrypts the distribution transport, matching a peer started with
    /// <c>-proto_dist inet_tls</c>. Null leaves the connection in the clear.
    /// </summary>
    /// <remarks>
    /// Both ends must agree: a TLS node and a plaintext node cannot talk to each other, and the
    /// failure looks like a connection that stalls rather than one that explains itself.
    /// </remarks>
    public ErlangTlsOptions? Tls { get; set; }

    /// <summary>Capabilities to advertise during the handshake.</summary>
    public DistributionFlags Flags { get; set; } = DistributionFlags.Default;

    /// <summary>
    /// Registers a built-in <c>net_kernel</c> responder so <c>Node.ping/1</c> and
    /// <c>:net_adm.ping/1</c> answer <c>:pong</c>.
    /// </summary>
    public bool ProvideNetKernel { get; set; } = true;

    /// <summary>Receives log lines. Defaults to discarding them.</summary>
    public Action<string>? Log { get; set; }
}
