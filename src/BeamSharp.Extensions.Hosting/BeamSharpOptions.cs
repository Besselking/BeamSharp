using BeamSharp.Epmd;
using BeamSharp.Node;
using BeamSharp.Protocol;

namespace BeamSharp.Extensions.Hosting;

/// <summary>
/// Node settings, bindable from configuration.
/// <para>
/// This mirrors <see cref="ErlangNodeOptions"/> but with a node name and plain properties, so a
/// whole node can be configured from appsettings.json without writing code.
/// </para>
/// </summary>
public sealed class BeamSharpOptions
{
    /// <summary>The node name, <c>alive@host</c>. Defaults to the entry assembly name on this host.</summary>
    public string? NodeName { get; set; }

    /// <summary>The magic cookie. Falls back to <c>~/.erlang.cookie</c> when not set.</summary>
    public string? Cookie { get; set; }

    /// <summary>TCP port for the distribution listener. 0 lets the OS choose.</summary>
    public int Port { get; set; }

    /// <summary>Address to bind the listener to.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Hidden by default, matching what C and Java nodes do.</summary>
    public NodeVisibility Visibility { get; set; } = NodeVisibility.Hidden;

    /// <summary>Must match the peer's <c>net_ticktime</c>.</summary>
    public TimeSpan TickTime { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Host EPMD runs on.</summary>
    public string EpmdHost { get; set; } = "127.0.0.1";

    /// <summary>EPMD port; defaults to <c>ERL_EPMD_PORT</c> or 4369.</summary>
    public int? EpmdPort { get; set; }

    /// <summary>Answers <c>Node.ping/1</c>.</summary>
    public bool ProvideNetKernel { get; set; } = true;

    /// <summary>Peers to connect to at startup.</summary>
    public IList<string> ConnectTo { get; } = [];

    internal ErlangNodeOptions ToNodeOptions(Action<string> log) => new()
    {
        Cookie = Cookie,
        Port = Port,
        BindAddress = BindAddress,
        Visibility = Visibility,
        TickTime = TickTime,
        EpmdHost = EpmdHost,
        EpmdPort = EpmdPort,
        ProvideNetKernel = ProvideNetKernel,
        Flags = DistributionFlags.Default,
        Log = log
    };

    internal string ResolveNodeName() =>
        NodeName ?? $"{System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name?.ToLowerInvariant() ?? "dotnet"}" +
                    $"@{BeamSharp.Node.NodeName.LocalShortHost}";
}
