using System.Numerics;
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
            await connection.SendAsync(new ErlTuple(RandomOpcode(rng), RandomField(rng)));

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

    /// <summary>
    /// An opcode a peer might send. This used to be <c>rng.Next(0, 300)</c>, which is why the
    /// dispatcher's OverflowException on a bignum opcode survived the fuzzer: the generator could
    /// not produce one. An opcode is a term like any other, so it has to be fuzzed like one.
    /// </summary>
    private static ErlInt RandomOpcode(Random rng) => rng.Next(8) switch
    {
        0 => new ErlInt(rng.Next(0, 300)),
        1 => new ErlInt(-rng.Next(1, 300)),
        2 => new ErlInt(int.MaxValue),
        3 => new ErlInt(int.MinValue),
        4 => new ErlInt(BigInteger.Pow(2, 70)),
        5 => new ErlInt(-BigInteger.Pow(2, 70)),
        6 => new ErlInt(BigInteger.Pow(2, 2048)),
        _ => new ErlInt((BigInteger)int.MaxValue + 1)
    };

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

    /// <summary>
    /// <see cref="DistMessage.Op"/> reads three peer-supplied things in a row — that the control is
    /// a tuple, that its first element is an integer, and that the integer fits in an int. None of
    /// those held by construction, so all three were checked.
    /// </summary>
    [Fact]
    public void An_operation_that_is_not_a_well_formed_opcode_reads_as_null()
    {
        Assert.Null(new DistMessage(Erl.Atom("not_a_tuple"), null).Op);
        Assert.Null(new DistMessage(new ErlTuple(), null).Op);
        Assert.Null(new DistMessage(new ErlTuple(Erl.Atom("not_an_int")), null).Op);
        Assert.Null(new DistMessage(new ErlTuple(new ErlInt(BigInteger.Pow(2, 70))), null).Op);

        Assert.Equal(DistOp.RegSend, new DistMessage(new ErlTuple(new ErlInt(6)), null).Op);
    }
}
