using BeamSharp.Node;
using BeamSharp.Protocol;
using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Tests;

/// <summary>
/// Erlang distribution trusts every peer that holds the cookie, so none of this is a security
/// boundary. It is still worth doing: the node a call went to is already recorded on the call,
/// checking it costs one comparison, and a peer answering the wrong call is as likely to be buggy
/// as hostile.
/// </summary>
public sealed class PeerScopedSignalTests : IDisposable
{
    private const string Cookie = "peer-scope-cookie";
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
    public async Task A_node_that_was_never_called_cannot_answer_the_call()
    {
        var victim = await StartAsync("bs_scope_victim");
        var honest = await StartAsync("bs_scope_honest");
        var attacker = await StartAsync("bs_scope_attacker");

        // The honest server hands the test the reference the victim is really waiting on, and holds
        // its answer until asked. Guessing the reference would test how random references are, which
        // is a separate property: the claim here is that knowing one is not enough by itself.
        var slow = new SlowServer();
        honest.RegisterGenServer("slow", slow);

        Assert.True(await victim.ConnectAsync(honest.Name.Full));
        Assert.True(await victim.ConnectAsync(attacker.Name.Full));

        var call = victim.CallAsync("slow", honest.Name.Full, Erl.Atom("ping"), TimeSpan.FromSeconds(20));
        var alias = await slow.Alias.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A node the victim never called, answering a call it was never part of.
        var toVictim = attacker.Connections[victim.Name.Full];
        await toVictim.SendAsync(
            new ErlTuple(new ErlInt((int)DistOp.AliasSend), attacker.NextPid(), alias),
            new ErlTuple(alias, Erl.Atom("zero")));

        // And a forged DOWN for the same call, which used to fail it outright.
        await toVictim.SendAsync(new ErlTuple(
            new ErlInt((int)DistOp.MonitorPExit), attacker.NextPid(), victim.NextPid(), alias,
            Erl.Atom("killed")));

        await Task.Delay(300);
        slow.Release.TrySetResult();

        // The honest server's answer: not the attacker's, and not an exit.
        Assert.Equal(Erl.Atom("pong"), await call);
    }

    [Fact]
    public async Task References_do_not_walk_a_predictable_path()
    {
        await using var node = new ErlangNode($"bs_refs@{NodeName.LocalShortHost}", Cookie);
        var refs = Enumerable.Range(0, 64).Select(_ => node.NextRef()).ToList();

        // Two of the three words used to be a counter and the third came from a non-cryptographic
        // PRNG, so the first stepped by one and the second never moved at all.
        Assert.Equal(64, refs.Select(r => r.Ids[0]).Distinct().Count());
        Assert.Equal(64, refs.Select(r => r.Ids[1]).Distinct().Count());

        // The emulator masks the low word of a reference it creates to 18 bits.
        Assert.All(refs, r => Assert.True(r.Ids[0] <= 0x3FFFF, $"word 0 was {r.Ids[0]:X}"));

        // Still all distinct, which the counter word guarantees outright rather than by chance.
        Assert.Equal(64, refs.Distinct().Count());
    }

    public void Dispose()
    {
        foreach (var node in _nodes) node.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class SlowServer : ErlangGenServer
    {
        public readonly TaskCompletionSource<ErlRef> Alias =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public readonly TaskCompletionSource Release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<ErlTerm?> HandleCallAsync(
            ErlTerm request, GenCallFrom from, CancellationToken ct)
        {
            if (from.Alias is { } alias) Alias.TrySetResult(alias);
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            return Erl.Atom("pong");
        }
    }
}
