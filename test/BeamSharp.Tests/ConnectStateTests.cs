using System.Collections;
using System.Reflection;
using BeamSharp.Node;
using Xunit;

namespace BeamSharp.Tests;

/// <summary>
/// Dialling state is keyed by peer name, and a peer chooses the name: it puts one in the caller pid
/// of a <c>$gen_call</c>, and replying to that pid dials whatever it says. Anything kept per name
/// is therefore something a peer can grow, so nothing may outlive the dial that created it.
/// </summary>
public sealed class ConnectStateTests
{
    [RequiresEpmdFact]
    public async Task Dialling_names_that_go_nowhere_leaves_nothing_behind()
    {
        await using var node = new ErlangNode($"bs_dialstate@{NodeName.LocalShortHost}",
            new ErlangNodeOptions { Cookie = "dial-state-cookie" });
        await node.StartAsync();

        // Names EPMD will not know, which is the cheap failure: it answers at once, so this is the
        // path a peer can drive hardest.
        for (var i = 0; i < 40; i++)
            Assert.False(await node.ConnectAsync($"bs_absent_{i}@{NodeName.LocalShortHost}"));

        Assert.Equal(0, PerPeerEntries(node));
    }

    [RequiresEpmdFact]
    public async Task A_reached_peer_leaves_nothing_behind_either()
    {
        await using var target = new ErlangNode($"bs_dialtarget@{NodeName.LocalShortHost}",
            new ErlangNodeOptions { Cookie = "dial-state-cookie" });
        await target.StartAsync();

        await using var node = new ErlangNode($"bs_dialler2@{NodeName.LocalShortHost}",
            new ErlangNodeOptions { Cookie = "dial-state-cookie" });
        await node.StartAsync();

        Assert.True(await node.ConnectAsync(target.Name.Full));
        Assert.Equal(0, PerPeerEntries(node));
    }

    [RequiresEpmdFact]
    public async Task Permits_churning_under_concurrent_dials_still_hold_one_peer_to_one_dial()
    {
        await using var target = new ErlangNode($"bs_churntarget@{NodeName.LocalShortHost}",
            new ErlangNodeOptions { Cookie = "dial-state-cookie" });
        await target.StartAsync();

        await using var node = new ErlangNode($"bs_churn@{NodeName.LocalShortHost}",
            new ErlangNodeOptions { Cookie = "dial-state-cookie" });
        await node.StartAsync();

        // Dialling a name that fails fast creates and retires a permit as quickly as the node can,
        // which is the window where a caller can be holding one that has just left the dictionary.
        var absent = $"bs_churn_absent@{NodeName.LocalShortHost}";
        for (var round = 0; round < 20; round++)
        {
            var dials = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => node.ConnectAsync(absent)));
            Assert.All(dials, Assert.False);
        }

        // Two callers holding different permits for one peer would each dial it, so the connection
        // count is what says the churn never handed out a second permit.
        var live = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => node.ConnectAsync(target.Name.Full)));

        Assert.All(live, Assert.True);
        Assert.Single(node.ConnectedNodes);
        Assert.Single(target.ConnectedNodes);
        Assert.Equal(0, PerPeerEntries(node));
    }

    /// <summary>
    /// Counts everything the node holds keyed by peer name, whatever it is called. Reading the
    /// fields directly is the only way to see retention that has no other effect until it has gone
    /// on long enough to matter.
    /// </summary>
    private static int PerPeerEntries(ErlangNode node)
    {
        var total = 0;
        var counted = 0;
        foreach (var field in typeof(ErlangNode)
                     .GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            var type = field.FieldType;
            if (!type.IsGenericType ||
                type.GetGenericTypeDefinition() != typeof(System.Collections.Concurrent.ConcurrentDictionary<,>) ||
                type.GetGenericArguments()[0] != typeof(string)) continue;

            // _connections and _registered are keyed by name too, but hold a live connection and a
            // mailbox this node made itself, neither of which a peer can conjure.
            if (field.Name is "_connections" or "_registered") continue;

            counted++;
            total += ((ICollection)field.GetValue(node)!).Count;
        }

        Assert.True(counted > 0, "no per-peer dictionaries found; this test has lost track of the fields it guards");
        return total;
    }
}
