using Erlang.Distribution.Epmd;
using Erlang.Distribution.Protocol;

namespace Erlang.Distribution.Node;

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

    /// <summary>Must match the peer's <c>net_ticktime</c> (60 seconds by default in OTP).</summary>
    public TimeSpan TickTime { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Host EPMD runs on.</summary>
    public string EpmdHost { get; set; } = "127.0.0.1";

    /// <summary>EPMD port; defaults to <c>ERL_EPMD_PORT</c> or 4369.</summary>
    public int? EpmdPort { get; set; }

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
