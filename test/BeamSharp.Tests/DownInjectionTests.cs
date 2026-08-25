using BeamSharp.Node;
using BeamSharp.Protocol;
using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Tests;

/// <summary>
/// A DOWN says a process we were watching has gone. Nothing in this library watches a remote
/// process except a call, which tracks its own reference, so a DOWN that matches no call concerns
/// nothing here.
/// </summary>
public sealed class DownInjectionTests : IDisposable
{
    private const string Cookie = "down-injection-cookie";
    private readonly List<ErlangNode> _nodes = [];

    private async Task<ErlangNode> StartAsync(string alive)
    {
        var node = new ErlangNode($"{alive}@{NodeName.LocalShortHost}",
            new ErlangNodeOptions { Cookie = Cookie });
        _nodes.Add(node);
        await node.StartAsync();
        return node;
    }

    [RequiresEpmdFact]
    public async Task A_peer_cannot_deliver_a_down_for_a_monitor_it_set_itself()
    {
        var victim = await StartAsync("bs_down_victim");
        var peer = await StartAsync("bs_down_peer");

        var mailbox = victim.CreateMailbox("watched");
        Assert.True(await peer.ConnectAsync(victim.Name.Full));

        var connection = peer.Connections[victim.Name.Full];
        var reference = peer.NextRef();
        var watcher = peer.NextPid();

        // The peer monitors the victim's mailbox, which is ordinary and leaves the victim holding
        // the reference as an incoming monitor.
        await connection.SendAsync(new ErlTuple(
            new ErlInt((int)DistOp.MonitorP), watcher, Erl.Atom("watched"), reference));
        await Task.Delay(200);

        // Then quotes that same reference back as a DOWN. It describes the victim's own mailbox as
        // dead, to the victim, on the strength of a monitor the peer set.
        await connection.SendAsync(new ErlTuple(
            new ErlInt((int)DistOp.MonitorPExit), watcher, mailbox.Pid, reference, Erl.Atom("killed")));

        var delivered = await mailbox.ReceiveAsync(TimeSpan.FromSeconds(2));
        Assert.True(delivered is null,
            $"the mailbox was handed {delivered?.Term}, which nothing here ever asked to watch");
    }

    [Fact]
    public async Task Pids_stay_unique_across_the_serial_rollover()
    {
        await using var node = new ErlangNode($"bs_pids@{NodeName.LocalShortHost}", Cookie);

        // Every pid this node hands out has to be distinct, and the id and serial in one pid have to
        // be the pair that call was given rather than a mix of two.
        var pids = new System.Collections.Concurrent.ConcurrentBag<ErlPid>();
        Parallel.For(0, 20_000, _ => pids.Add(node.NextPid()));

        Assert.Equal(20_000, pids.Distinct().Count());
    }

    public void Dispose()
    {
        foreach (var node in _nodes) node.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
