using BeamSharp.Epmd;
using BeamSharp.Node;
using BeamSharp.Security;
using BeamSharp.Serialization;
using BeamSharp.Terms;

// A C# node that an Elixir peer can talk to as if it were just another BEAM node.
//
//   dotnet run --project samples/BeamSharp.Server -- [nodename] [cookie]

var flagValues = args.SkipWhile(a => a != "--tls").Skip(1).Take(1).ToArray();
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && !flagValues.Contains(a)).ToArray();
var nodeName = positional.Length > 0 ? positional[0] : $"csharp@{NodeName.LocalShortHost}";
var cookie = positional.Length > 1 ? positional[1] : null;

// --tls <dir> turns on encrypted distribution, matching an Erlang peer started with
// -proto_dist inet_tls. The directory is the one test/gen_certs.sh writes.
var certificateDirectory = args.SkipWhile(a => a != "--tls").Skip(1).FirstOrDefault();
var tls = certificateDirectory is null
    ? null
    : ErlangTlsOptions.FromPemFiles(
        Path.Combine(certificateDirectory, "csharp.crt"),
        Path.Combine(certificateDirectory, "csharp.key"),
        Path.Combine(certificateDirectory, "ca.crt"));

var node = new ErlangNode(nodeName, new ErlangNodeOptions
{
    Cookie = cookie,
    Tls = tls,
    // Hidden by default. --visible joins the mesh, which also means peers expect us to speak
    // the `global` name-registry protocol that this library does not implement.
    Visibility = args.Contains("--visible") ? NodeVisibility.Visible : NodeVisibility.Hidden,
    Log = line => Console.WriteLine($"[dist] {line}")
});

node.NodeUp += peer => Console.WriteLine($"--> {peer} connected");
node.NodeDown += (peer, error) => Console.WriteLine($"<-- {peer} disconnected ({error?.Message ?? "clean"})");

// A GenServer. From Elixir: GenServer.call({:calculator, :"csharp@host"}, {:add, 1, 2})
node.RegisterGenServer("calculator", new Calculator(node));

// Plain C# objects, mapped to and from terms. From Elixir these arrive as real %BeamSharp.Person{}
// structs, so the caller cannot tell they were built by a .NET process.
node.RegisterGenServer("directory", new Directory());

// A plain mailbox, the equivalent of a spawned process that just receives.
var printer = node.CreateMailbox("printer");
_ = Task.Run(async () =>
{
    await foreach (var message in printer.Messages.ReadAllAsync())
        Console.WriteLine($"printer got {message.Term}" +
                          (message.Sender is null ? "" : $" from {message.Sender}"));
});

// Exposed to :rpc.call/4 and :erpc.call/4.
node.RpcHandler = new RpcRegistry()
    .Add("Elixir.CSharp", "add", 2, a => Erl.Int(a[0].AsLong() + a[1].AsLong()))
    .Add("Elixir.CSharp", "info", 0, _ => Erl.Map(
        ("runtime", Erl.String(".NET " + Environment.Version)),
        ("os", Erl.String(Environment.OSVersion.ToString())),
        ("pid", Erl.Int(Environment.ProcessId))))
    .Add("csharp", "reverse", 1, a => Erl.String(new string(a[0].AsText().Reverse().ToArray())));

await node.StartAsync();

Console.WriteLine($$"""

    Node   : {{node.Name}}
    TLS    : {{(node.UsesTls ? "on" : "off")}}
    Port   : {{node.Port}}
    Cookie : {{node.Cookie}}

    From  iex --sname client --cookie {{node.Cookie}}

      Node.ping(:"{{node.Name}}")
      GenServer.call({:calculator, :"{{node.Name}}"}, {:add, 40, 2})
      GenServer.call({:calculator, :"{{node.Name}}"}, :slow)
      GenServer.cast({:calculator, :"{{node.Name}}"}, {:log, "hi"})
      send({:printer, :"{{node.Name}}"}, {:hello, self()})
      :rpc.call(:"{{node.Name}}", CSharp, :add, [2, 3])
      :erpc.call(:"{{node.Name}}", :csharp, :reverse, ["stressed"])

    Ctrl-C to stop.

    """);

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.TrySetResult();
};
await stop.Task;
await node.DisposeAsync();

/// <summary>Answers the calls an Elixir GenServer client would make.</summary>
internal sealed class Calculator(ErlangNode node) : ErlangGenServer
{
    public override async ValueTask<ErlTerm?> HandleCallAsync(ErlTerm request, GenCallFrom from, CancellationToken ct)
    {
        switch (request)
        {
            case ErlTuple { Arity: 3 } t when t[0].IsAtom("add"):
                return Erl.Int(t[1].AsLong() + t[2].AsLong());

            case ErlTuple { Arity: 2 } t when t[0].IsAtom("echo"):
                return t[1];

            case ErlAtom { Name: "who" }:
                return Erl.Tuple(Erl.Atom("csharp"), Erl.String(node.Name.Full));

            case ErlAtom { Name: "slow" }:
                // Reply later, the equivalent of returning {:noreply, state} and calling
                // GenServer.reply/2 once the work is done.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(500, ct);
                    await node.ReplyAsync(from, Erl.Atom("worth_the_wait"), ct: ct);
                }, ct);
                return null;

            case ErlTuple { Arity: 2 } t when t[0].IsAtom("spawn"):
            {
                // Creates another mailbox so the caller can monitor or link to it.
                var name = t[1].AsText();
                var mailbox = node.CreateMailbox(name);
                _ = Task.Run(async () =>
                {
                    await foreach (var m in mailbox.Messages.ReadAllAsync(ct))
                        Console.WriteLine($"{name} got {m.Term}");
                }, ct);
                return Erl.Tuple(Erl.Ok, mailbox.Pid);
            }

            case ErlTuple { Arity: 3 } t when t[0].IsAtom("kill"):
            {
                // Closes it, which sends the exits and downs a dying process would send.
                var victim = node.Whereis(t[1].AsText());
                if (victim is null) return Erl.Tuple(Erl.Error, Erl.Atom("noproc"));
                await node.CloseMailboxAsync(victim, t[2]);
                return Erl.Ok;
            }

            case ErlAtom { Name: "crash" }:
                throw new InvalidOperationException("deliberate crash");

            default:
                return Erl.Tuple(Erl.Error, Erl.Tuple(Erl.Atom("unknown_request"), request));
        }
    }

    public override ValueTask HandleCastAsync(ErlTerm request, CancellationToken ct)
    {
        Console.WriteLine($"calculator cast: {request}");
        return ValueTask.CompletedTask;
    }

    public override ValueTask HandleInfoAsync(ErlMessage message, CancellationToken ct)
    {
        Console.WriteLine($"calculator info: {message.Term}");
        return ValueTask.CompletedTask;
    }
}


/// <summary>A C# record that Elixir sees as <c>%BeamSharp.Person{}</c>.</summary>
[ErlStruct("BeamSharp.Person")]
internal record Person(string FirstName, int Age, string? Email = null, Status Status = Status.Active);

internal enum Status
{
    Active,
    OnLeave
}

/// <summary>
/// Converters generated at compile time. The sample uses these rather than reflection so the
/// interop suite exercises the generated path against a real Elixir node.
/// </summary>
[ErlSerializable(typeof(Person))]
[ErlSerializable(typeof(Person[]))]
[ErlSerializable(typeof((ErlAtom, Person)))]
[ErlSerializable(typeof((ErlAtom, ErlAtom)))]
internal partial class SampleTerms : ErlSerializerContext;

/// <summary>Serves objects rather than hand-built terms.</summary>
internal sealed class Directory : ErlangGenServer
{
    private static readonly Person[] People =
    [
        new("Ada", 36, "ada@example.com"),
        new("Grace", 45, Status: Status.OnLeave),
        new("Alan", 41, "alan@example.com")
    ];

    public override ValueTask<ErlTerm?> HandleCallAsync(ErlTerm request, GenCallFrom from, CancellationToken ct)
    {
        switch (request)
        {
            case ErlAtom { Name: "all" }:
                return Reply(ErlSerializer.Serialize(People, SampleTerms.Default));

            case ErlTuple { Arity: 2 } t when t[0].IsAtom("find"):
            {
                var name = t[1].AsText();
                var match = People.FirstOrDefault(p =>
                    string.Equals(p.FirstName, name, StringComparison.OrdinalIgnoreCase));

                return Reply(match is null
                    ? ErlSerializer.Serialize((Erl.Atom("error"), Erl.Atom("not_found")), SampleTerms.Default)
                    : ErlSerializer.Serialize((Erl.Atom("ok"), match), SampleTerms.Default));
            }

            case ErlTuple { Arity: 2 } t when t[0].IsAtom("echo"):
            {
                // Round trip through a real C# object rather than echoing the term back.
                var person = ErlSerializer.Deserialize<Person>(t[1], SampleTerms.Default);
                return Reply(ErlSerializer.Serialize(person, SampleTerms.Default));
            }

            case ErlTuple { Arity: 2 } t when t[0].IsAtom("birthday"):
            {
                var person = ErlSerializer.Deserialize<Person>(t[1], SampleTerms.Default);
                return Reply(ErlSerializer.Serialize(person with { Age = person.Age + 1 }, SampleTerms.Default));
            }

            default:
                return Reply(Erl.Tuple(Erl.Error, Erl.Atom("unknown_request")));
        }
    }

    private static ValueTask<ErlTerm?> Reply(ErlTerm term) => ValueTask.FromResult<ErlTerm?>(term);
}
