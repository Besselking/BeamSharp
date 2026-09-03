using System.Diagnostics;
using BeamSharp.Node;
using Xunit;

namespace BeamSharp.Tests;

/// <summary>
/// A dial holds its permit across an EPMD lookup, a TCP connect, a TLS handshake and the
/// distribution handshake. One permit for the node makes each of those wait for the last, and the
/// wait is bounded only by the EPMD and handshake timeouts.
/// </summary>
public sealed class ConcurrentConnectTests
{
    [RequiresEpmdFact]
    public async Task An_unreachable_peer_does_not_delay_dialling_a_reachable_one()
    {
        await using var target = new ErlangNode($"bs_reachable@{NodeName.LocalShortHost}",
            new ErlangNodeOptions { Cookie = "concurrent-cookie" });
        await target.StartAsync();

        await using var node = new ErlangNode($"bs_dialler@{NodeName.LocalShortHost}",
            new ErlangNodeOptions { Cookie = "concurrent-cookie" });
        await node.StartAsync();

        // A peer whose host swallows packets rather than refusing them, so the EPMD lookup for it
        // runs all the way to its timeout. The lookup goes to the peer's own host, so this holds
        // only the permit for this peer.
        var blackHole = node.ConnectAsync("bs_black_hole@10.255.255.1");

        // Long enough for the black-hole dial to be well inside its lookup.
        await Task.Delay(250);

        var sw = Stopwatch.StartNew();
        Assert.True(await node.ConnectAsync(target.Name.Full));
        sw.Stop();

        // The black-hole lookup alone is a 5s EPMD timeout, so anything near that means this dial
        // waited for it.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"reaching a live peer took {sw.Elapsed.TotalSeconds:0.0}s while another peer was unreachable");

        Assert.False(await blackHole);
    }

    [RequiresEpmdFact]
    public async Task Two_dials_at_one_peer_still_take_it_in_turns()
    {
        await using var target = new ErlangNode($"bs_once@{NodeName.LocalShortHost}",
            new ErlangNodeOptions { Cookie = "concurrent-cookie" });
        await target.StartAsync();

        await using var node = new ErlangNode($"bs_racer@{NodeName.LocalShortHost}",
            new ErlangNodeOptions { Cookie = "concurrent-cookie" });
        await node.StartAsync();

        // Splitting the permit per peer must not let one peer be dialled twice at once: the second
        // dial has to find the connection the first made, not build a second one beside it.
        var dials = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => node.ConnectAsync(target.Name.Full)));

        Assert.All(dials, Assert.True);
        Assert.Single(node.ConnectedNodes);
        NodeWait.ForConnectionCount(target, 1);
    }
}
