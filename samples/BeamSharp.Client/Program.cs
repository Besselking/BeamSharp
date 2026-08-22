using BeamSharp.Node;
using BeamSharp.Terms;

// Drives the other direction: a C# node calling into an ordinary Elixir GenServer.
//
//   dotnet run --project samples/BeamSharp.Client -- <peer-node> [cookie]

var peer = args.Length > 0 ? args[0] : $"exserver@{NodeName.LocalShortHost}";
var cookie = args.Length > 1 ? args[1] : null;

await using var node = new ErlangNode($"csclient@{NodeName.LocalShortHost}", new ErlangNodeOptions
{
    Cookie = cookie,
    Log = line => Console.WriteLine($"[dist] {line}")
});

await node.StartAsync();

var passed = 0;
var failed = 0;

async Task CheckAsync(string name, Func<Task<(bool Ok, string Detail)>> body)
{
    try
    {
        var (ok, detail) = await body();
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
        if (ok) passed++; else failed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL  {name}  threw {ex.GetType().Name}: {ex.Message}");
        failed++;
    }
}

Console.WriteLine($"\n=== {node.Name} calling {peer} ===\n");

await CheckAsync("connect", async () =>
{
    var ok = await node.ConnectAsync(peer);
    return (ok, ok ? "connected" : "failed");
});

await CheckAsync("GenServer.call {:add, 20, 22}", async () =>
{
    var reply = await node.CallAsync("echo_server", peer, Erl.Tuple(Erl.Atom("add"), 20, 22),
        TimeSpan.FromSeconds(5));
    return (reply.Equals(Erl.Int(42)), reply.ToString());
});

await CheckAsync("round-tripping a nested term through Elixir", async () =>
{
    var sent = Erl.Map(
        ("name", Erl.String("erpc")),
        ("values", Erl.List(Erl.Int(1), Erl.Float(2.5), Erl.Atom("three"))),
        ("nested", Erl.Tuple(Erl.Int(-7), Erl.CharList("chars"), Erl.Binary([1, 2, 3]))));
    var reply = await node.CallAsync("echo_server", peer, Erl.Tuple(Erl.Atom("echo"), sent),
        TimeSpan.FromSeconds(5));
    return (reply.Equals(sent), reply.Equals(sent) ? "identical" : $"got {reply}");
});

await CheckAsync("call :whoami", async () =>
{
    var reply = await node.CallAsync("echo_server", peer, Erl.Atom("whoami"), TimeSpan.FromSeconds(5));
    return (reply.IsTagged("elixir", out _), reply.ToString());
});

await CheckAsync("cast then read it back", async () =>
{
    await node.CastAsync("echo_server", peer, Erl.Tuple(Erl.Atom("from_csharp"), Erl.Int(1)));
    await Task.Delay(200);
    var reply = await node.CallAsync("echo_server", peer, Erl.Atom("casts"), TimeSpan.FromSeconds(5));
    var expected = Erl.List(Erl.Tuple(Erl.Atom("from_csharp"), Erl.Int(1)));
    return (reply.Equals(expected), reply.ToString());
});

await CheckAsync("send/2 into the server, reply into our mailbox", async () =>
{
    var mailbox = node.CreateMailbox();
    await mailbox.SendAsync("echo_server", peer, Erl.Tuple(Erl.Atom("ping"), mailbox.Pid));
    var reply = await mailbox.ReceiveAsync(TimeSpan.FromSeconds(5));
    return (reply is { } m && m.Term.IsTagged("pong", out _), reply?.Term.ToString() ?? "timed out");
});

await CheckAsync(":rpc.call into Elixir", async () =>
{
    var reply = await node.RpcAsync(peer, "Elixir.Maths", "double", [Erl.Int(21)], TimeSpan.FromSeconds(5));
    return (reply.Equals(Erl.Int(42)), reply.ToString());
});

await CheckAsync(":rpc.call with binaries", async () =>
{
    var reply = await node.RpcAsync(peer, "Elixir.Maths", "concat",
        [Erl.String("erl"), Erl.String("ang")], TimeSpan.FromSeconds(5));
    return (reply.Equals(Erl.String("erlang")), reply.ToString());
});

await CheckAsync("a crashing GenServer surfaces as an exit", async () =>
{
    try
    {
        var reply = await node.CallAsync("echo_server", peer, Erl.Atom("boom"), TimeSpan.FromSeconds(5));
        return (false, $"expected an exit, got {reply}");
    }
    catch (ErlangExitException ex)
    {
        return (true, $"exit {ex.Reason.ToString()[..Math.Min(40, ex.Reason.ToString().Length)]}...");
    }
});

await CheckAsync("call to a name that is not registered", async () =>
{
    try
    {
        await node.CallAsync("no_such_server", peer, Erl.Atom("hello"), TimeSpan.FromSeconds(3));
        return (false, "expected an exit");
    }
    catch (ErlangExitException ex)
    {
        return (ex.Reason.IsAtom("noproc"), ex.Reason.ToString());
    }
});

await CheckAsync("100 concurrent calls", async () =>
{
    var tasks = Enumerable.Range(1, 100).Select(i =>
        node.CallAsync("echo_server", peer, Erl.Tuple(Erl.Atom("add"), i, i), TimeSpan.FromSeconds(10)));
    var replies = await Task.WhenAll(tasks);
    var ok = replies.Select((r, i) => r.Equals(Erl.Int((i + 1) * 2))).All(x => x);
    return (ok, ok ? "100/100 correct" : "mismatched results");
});

Console.WriteLine($"\n=== {passed}/{passed + failed} passed ===");
return failed == 0 ? 0 : 1;
