using BeamSharp.Node;
using BeamSharp.Protocol;
using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Tests;

/// <summary>
/// Every message between nodes carries a control tuple, and the dispatcher reads fields out of it by
/// position. A peer past the cookie is still not a peer that has to be well behaved: it may be an
/// older version, a different implementation, or simply broken.
/// </summary>
public sealed class ControlMessageFuzzTests : IDisposable
{
    private const string Cookie = "control-fuzz-cookie";
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
    public async Task Malformed_control_tuples_do_not_disturb_the_connection()
    {
        var receiver = await StartAsync("bs_ctl_receiver");
        receiver.RegisterGenServer("echo", new EchoServer());

        var sender = await StartAsync("bs_ctl_sender");
        Assert.True(await sender.ConnectAsync(receiver.Name.Full));

        var connection = sender.Connections[receiver.Name.Full];
        var rng = new Random(31337);

        // Every opcode the dispatcher knows, at every arity from 1 up, with junk in the fields.
        foreach (var op in Enum.GetValues<DistOp>())
        {
            for (var arity = 1; arity <= 7; arity++)
            {
                var items = new ErlTerm[arity];
                items[0] = new ErlInt((int)op);
                for (var i = 1; i < arity; i++) items[i] = RandomField(rng);

                await connection.SendAsync(new ErlTuple(items), Erl.Atom("payload"));
            }
        }

        // Junk in the opcode slot too.
        for (var i = 0; i < 200; i++)
            await connection.SendAsync(new ErlTuple(new ErlInt(rng.Next(0, 300)), RandomField(rng)));

        // A control message that is not a tuple at all.
        await connection.SendAsync(Erl.Atom("not_a_tuple"));
        await connection.SendAsync(Erl.List(Erl.Int(1), Erl.Int(2)));
        await connection.SendAsync(new ErlTuple());

        // The connection has to have survived all of that, and still work.
        await Task.Delay(300);

        Assert.False(connection.IsClosed);
        Assert.Contains(sender.Name.Full, receiver.ConnectedNodes);

        var reply = await sender.CallAsync("echo", receiver.Name.Full, Erl.String("still alive"),
            TimeSpan.FromSeconds(10));
        Assert.Equal(Erl.String("still alive"), reply);
    }

    private static ErlTerm RandomField(Random rng) => rng.Next(7) switch
    {
        0 => new ErlInt(rng.Next()),
        1 => Erl.Atom($"a{rng.Next(10)}"),
        2 => Erl.String("junk"),
        3 => new ErlPid("nowhere@nohost", (uint)rng.Next(), 0, 1),
        4 => new ErlRef("nowhere@nohost", 1, [(uint)rng.Next()]),
        5 => Erl.List(Erl.Int(1)),
        _ => Erl.Tuple(Erl.Atom("nested"), Erl.Int(rng.Next()))
    };

    public void Dispose()
    {
        foreach (var node in _nodes) node.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class EchoServer : ErlangGenServer
    {
        public override ValueTask<ErlTerm?> HandleCallAsync(
            ErlTerm request, GenCallFrom from, CancellationToken ct) =>
            ValueTask.FromResult<ErlTerm?>(request);
    }
}
