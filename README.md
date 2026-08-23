# BeamSharp — an Erlang distribution node for .NET

[![CI](https://github.com/Besselking/BeamSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/Besselking/BeamSharp/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A .NET implementation of the Erlang distribution protocol. It registers with EPMD, performs the
OTP 23+ handshake, and speaks the same signals a BEAM node does — so from Elixir it looks like an
ordinary node:

```elixir
Node.ping(:"csharp@myhost")                                     #=> :pong
GenServer.call({:calculator, :"csharp@myhost"}, {:add, 40, 2})  #=> 42
GenServer.cast({:calculator, :"csharp@myhost"}, {:log, "hi"})   #=> :ok
send({:printer, :"csharp@myhost"}, {:hello, self()})
Process.monitor({:calculator, :"csharp@myhost"})
:rpc.call(:"csharp@myhost", CSharp, :add, [2, 3])               #=> 5
:erpc.call(:"csharp@myhost", :csharp, :reverse, ["stressed"])   #=> "desserts"
```

None of that needs anything special on the Elixir side: no shim library, no custom transport, no
protocol negotiation. It is the standard distribution protocol, so standard clients just work.

Verified by hand against **Erlang/OTP 29** and **Elixir 1.20** on **.NET 10**. CI runs the same
interop suite against OTP 26, 27, 28 and 29 — 26 is the floor, because that is where `V4_NC` and
`UNLINK_ID` became mandatory flags.

The closest prior art is [Erlang.NET](https://github.com/takayuki/Erlang.NET), a manual port of Java's
jinterface. BeamSharp is a fresh implementation against modern OTP instead: `async`/`await` and
channels rather than blocking threads, the OTP 26+ mandatory flag set, the alias-based `gen_server`
reply path introduced in OTP 24, and the spawn-request rpc path that `:rpc.call/4` has used since
OTP 23.

## Quick start

```bash
dotnet run --project samples/BeamSharp.Server -- "csharp@$(hostname -s)" testcookie
```

Then, in another terminal:

```bash
iex --sname client --cookie testcookie
```

```elixir
Node.ping(:"csharp@myhost")
GenServer.call({:calculator, :"csharp@myhost"}, {:add, 40, 2})
```

## Using the library

```csharp
using BeamSharp.Node;
using BeamSharp.Terms;

await using var node = new ErlangNode($"csharp@{NodeName.LocalShortHost}", "testcookie");

// Behaves like a GenServer to any Elixir caller.
node.RegisterGenServer("calculator", new Calculator());

// Or a bare mailbox — the equivalent of a spawned process that just receives.
var printer = node.CreateMailbox("printer");

// Exposed to :rpc.call/4 and :erpc.call/4.
node.RpcHandler = new RpcRegistry()
    .Add("Elixir.CSharp", "add", 2, a => Erl.Int(a[0].AsLong() + a[1].AsLong()));

await node.StartAsync();

await foreach (var message in printer.Messages.ReadAllAsync())
    Console.WriteLine($"{message.Sender} sent {message.Term}");

sealed class Calculator : ErlangGenServer
{
    public override ValueTask<ErlTerm?> HandleCallAsync(ErlTerm request, GenCallFrom from, CancellationToken ct)
        => ValueTask.FromResult<ErlTerm?>(request switch
        {
            ErlTuple { Arity: 3 } t when t[0].IsAtom("add") => Erl.Int(t[1].AsLong() + t[2].AsLong()),
            _ => Erl.Tuple(Erl.Error, Erl.Atom("unknown_request"))
        });
}
```

Returning `null` from `HandleCallAsync` is the equivalent of `{:noreply, state}`: hold on to the
`GenCallFrom` and answer later with `node.ReplyAsync(from, reply)`.

The same node calls **into** Elixir:

```csharp
await node.ConnectAsync("exserver@myhost");

var sum = await node.CallAsync("echo_server", "exserver@myhost", Erl.Tuple(Erl.Atom("add"), 20, 22));
await node.CastAsync("echo_server", "exserver@myhost", Erl.Atom("tick"));
var doubled = await node.RpcAsync("exserver@myhost", "Elixir.Maths", "double", [Erl.Int(21)]);
```

## Terms

Erlang terms are modelled explicitly rather than mapped onto .NET primitives, because the
distinctions matter: an atom, a binary and a charlist are three different things that would all
collapse into `string`.

| Erlang / Elixir | C# |
| --- | --- |
| `:ok` | `Erl.Atom("ok")` |
| `42`, `2**100` | `Erl.Int(42)` — arbitrary precision |
| `3.14` | `Erl.Float(3.14)` |
| `"text"` (Elixir string) | `Erl.String("text")` → `ErlBinary` |
| `~c"text"` (charlist) | `Erl.CharList("text")` → `ErlList` |
| `{1, :two}` | `Erl.Tuple(Erl.Int(1), Erl.Atom("two"))` |
| `[1, 2]` | `Erl.List(Erl.Int(1), Erl.Int(2))` |
| `[:alias \| ref]` | `Erl.ImproperList([...], tail)` |
| `%{a: 1}` | `Erl.Map(("a", Erl.Int(1)))` |
| `#PID<...>`, `#Reference<...>` | `ErlPid`, `ErlRef` |
| `<<1::3>>` | `ErlBitstring` |

All terms compare by value and hash structurally, so they work as dictionary keys — which Erlang
maps require, since any term can be a key.

## What is implemented

- **External Term Format** — full encode/decode, including bignums, bitstrings, maps, improper
  lists, pids, ports, references and export funs. Closures round-trip as opaque bytes.
- **EPMD** — registration (keeping the socket open), lookup and listing.
- **Handshake** — the OTP 23+ `'N'` messages with 64-bit flags and the MD5 cookie challenge, in
  both directions. Pre-OTP-23 `'n'` peers are accepted too.
- **Signals** — `SEND`, `SEND_SENDER`, `REG_SEND`, `ALIAS_SEND`, `LINK`, `UNLINK_ID`/`_ACK`,
  `EXIT`/`EXIT2` (with and without payload), `MONITOR_P`, `DEMONITOR_P`, `MONITOR_P_EXIT`,
  `SPAWN_REQUEST`/`SPAWN_REPLY`, and keepalive ticks.
- **`gen_server` semantics** — including the OTP 24+ reply path, where the caller's tag is
  `[:alias | ref]` and the reply has to travel back through a process alias rather than to the
  caller's pid. Calls to an unregistered name get a `:noproc` `DOWN` instead of hanging.
- **`:rpc.call/4` and `:erpc.call/4`** — both route through a distribution spawn request in OTP 23
  and later, so one handler serves both, and exceptions surface on the caller's side the way a
  raising Erlang function would.

## Serializing C# objects

`BeamSharp.Serialization` is a separate package, so the core stays free of reflection. It maps plain
C# objects onto terms, and the default shape is chosen to look native on the far side: a type
becomes a map with snake_case atom keys.

```csharp
record Person(string FirstName, int Age);

ErlSerializer.Serialize(new Person("Ada", 36));   // %{first_name: "Ada", age: 36}
```

Add one attribute and it stops being a map that resembles a struct, and becomes one:

```csharp
[ErlStruct("BeamSharp.Person")]
record Person(string FirstName, int Age, string? Email = null, Status Status = Status.Active);
```

```elixir
{:ok, person} = GenServer.call({:directory, :"csharp@myhost"}, {:find, "ada"})
#=> {:ok, %BeamSharp.Person{first_name: "Ada", age: 36, email: "ada@example.com", status: :active}}

%BeamSharp.Person{first_name: name} = person     # patterns work, because it really is a struct
```

An Elixir struct is a map carrying `__struct__`, so writing that key is all it takes. The interop
suite asserts this against a live Elixir node rather than against our own reader.

| C# | Erlang / Elixir |
| --- | --- |
| `record` / `class` | map with snake_case atom keys |
| `[ErlStruct("MyApp.X")]` | `%MyApp.X{}` |
| `[ErlRecord("point")]` | `{point, 3, 4}` — an Erlang record |
| `enum Status.InProgress` | `:in_progress` |
| `null` | `nil` (or `:undefined`) |
| `string` | binary; `[ErlAsAtom]` for an atom |
| `byte[]` | binary, not a list of integers |
| `List<T>`, `T[]` | list |
| `Dictionary<K,V>` | map |
| `(int, string)` | `{1, "two"}` |
| `DateTime`, `Guid` | ISO 8601 / UUID binary |
| `TimeSpan` | integer microseconds |

Naming, key kind, null representation and the rest are configurable through `ErlSerializerOptions`.
`[ErlProperty]`, `[ErlIgnore]`, `[ErlConvert]` and `[ErlAsAtom]` cover the per-member cases.

### Extending it

There is exactly one extension point, `ErlConverter<T>`:

```csharp
sealed class MoneyConverter : ErlConverter<Money>
{
    public override ErlTerm Write(Money v, ErlSerializerOptions o) =>
        Erl.Tuple(Erl.Atom("money"), Erl.Int((long)(v.Amount * 100)), Erl.Atom(v.Currency));

    public override Money Read(ErlTerm t, ErlSerializerOptions o) => /* ... */;
}

options.Converters.Add(new MoneyConverter());
```

Built-in conversions, the reflection fallback for plain objects, and everything the source generator
emits are all the same kind of thing. That is what lets generated code replace reflection without a
second API.

### Packages

| Package | What it is |
| --- | --- |
| `BeamSharp` | The node: EPMD, handshake, signals, terms |
| `BeamSharp.Serialization` | Object mapping and the source generator. No reflection anywhere in it |
| `BeamSharp.Serialization.Reflection` | The reflection fallback. Opt in by referencing it |
| `BeamSharp.Extensions.Hosting` | DI, `IHostedService`, `ILogger` and metrics |

The split is the whole trimming story: the core serializer contains no reflection at all, so there
is nothing to annotate, suppress or hope the trimmer keeps. An app that does not reference the
reflection package cannot accidentally depend on it.

### Reflection, when you want it

```csharp
var options = new ErlSerializerOptions().AddReflectionFallback();
var term = ErlSerializer.Serialize(anything, options);   // or ErlReflection.Default
```

That single entry point carries `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`, so if you
publish trimmed or AOT you get exactly one warning, at your own call, naming the fix.

### NativeAOT, when you want that instead

List your types on a context and the generator writes the converters at compile time:

```csharp
[ErlSerializable(typeof(Person))]
[ErlSerializable(typeof(Order))]
[ErlSerializable(typeof(Person[]))]      // collections, enums and tuples can be declared directly
internal partial class AppTerms : ErlSerializerContext;

var term = ErlSerializer.Serialize(person, AppTerms.Default);
```

A type nobody declared fails at the call site, naming itself and both ways out:

```
no converter is registered for MyApp.Invoice. Either declare it on a generated
ErlSerializerContext with [ErlSerializable(typeof(Invoice))], or reference the
BeamSharp.Serialization.Reflection package and call options.AddReflectionFallback().
```

The generator also writes out converter instantiations for the member types it reaches — enums,
`List<T>`, `Dictionary<K,V>`, `T?`, tuples. That matters more than it looks: reaching those through
`MakeGenericType` leaves the native code for the instantiation unreachable, and the app dies at
runtime with *"missing native code or metadata"*. Writing `new ErlCollectionConverter<List<Person>,
Person>()` roots it.

The two can be layered. A context consults its generated converters first, so adding the fallback
behind one gives you compile-time converters for the hot types and reflection for the rest — useful
when you are not publishing AOT and just want the speed.

### How the AOT claim is checked

`test/run_aot_probe.sh` publishes a console app with `PublishAot=true`, fails if the publish emits a
single trim or AOT warning, and runs the native binary. The probe project does not reference the
reflection package at all, so there is no fallback present that could quietly take over — an
undeclared type can only fail. CI runs it on every push.

### The generator's contract is equivalence

Generated code must produce *the same terms reflection does*, so the tests compare the two against
each other rather than each against a separately written expectation — a duplicated description can
drift, an equivalence check cannot:

```csharp
Assert.Equal(
    ErlSerializer.Serialize(value, reflectionOptions),
    ErlSerializer.Serialize(value, GeneratedContext.Default));
```

That holds across every shape and every option, because names are resolved when a converter is
built rather than when it is generated. Errors match too, down to the message.

Diagnostics are compile-time where they can be: `BS1001` (context is not `partial`), `BS1002` (does
not derive from `ErlSerializerContext`), `BS1003` (no constructor the deserializer could use),
`BS1004` (a type no converter can be generated for), `BS1005` (`[ErlRecord]` with inherited members,
whose order is not guaranteed to match).

## Hosting

`BeamSharp.Extensions.Hosting` runs a node for the lifetime of the application:

```csharp
builder.Services
    .AddBeamSharpNode(options =>
    {
        options.NodeName = "orders@myhost";
        options.Cookie = builder.Configuration["Erlang:Cookie"];
    })
    .AddGenServer<OrderServer>("orders")
    .AddRpcHandler(sp => sp.GetRequiredService<OrderRpc>());
```

Handlers are built from the container, and everything is registered **before** the listener accepts
its first connection, so a peer can never arrive to find a node still wiring itself up. Injecting
`ErlangNode` works once the host has started; asking for it earlier throws rather than handing back
a half-built one.

Diagnostics go where you expect: node events through `ILogger`, and connection counts through
`System.Diagnostics.Metrics` on the `BeamSharp` meter, so `dotnet-counters` and OpenTelemetry pick
them up with no extra wiring.

`AddBeamSharpNode(IConfiguration)` binds the whole thing from `appsettings.json`. That overload is
annotated `[RequiresUnreferencedCode]`, because configuration binding is reflective — use the
`Action<BeamSharpOptions>` overload under AOT.

## Performance

Measured with BenchmarkDotNet on an M-series Mac, .NET 10. `bench/BeamSharp.Benchmarks`.

Generated converters against the reflection fallback, same objects:

| | Reflection | Generated | |
| --- | ---: | ---: | --- |
| Read a flat record | 400 ns / 792 B | **169 ns / 176 B** | 2.4× faster, 4.5× less garbage |
| Read a nested record | 1,851 ns / 3,296 B | **793 ns / 1,112 B** | 2.3× faster, 3× less garbage |
| Write a flat record | 393 ns / 1,040 B | **300 ns / 896 B** | 1.3× faster |
| Write a nested record | 1,749 ns / 4,600 B | **1,425 ns / 4,096 B** | 1.2× faster |

Reading gains far more than writing, and the reason is worth knowing: generated reads call a
constructor directly where reflection goes through `Activator.CreateInstance` and `SetValue`, while
both write paths still do a converter lookup per member and build the same entry list. Writing is
where the remaining headroom is.

Raw codec throughput, which sets the ceiling for everything above it:

| | Time | Allocated |
| --- | ---: | ---: |
| Encode a `gen_server` call | 129 ns | 440 B |
| Decode a `gen_server` call | 233 ns | 808 B |
| Encode 200 maps | 24.2 µs | 54.8 KB |
| Decode 200 maps | 53.9 µs | 168 KB |

```bash
dotnet run --project bench/BeamSharp.Benchmarks -c Release -- --filter '*'
```

### Encrypted distribution

Set `Tls` and the transport matches an Erlang peer started with `-proto_dist inet_tls`:

```csharp
var node = new ErlangNode("csharp@myhost", new ErlangNodeOptions
{
    Cookie = cookie,
    Tls = ErlangTlsOptions.FromPemFiles("csharp.crt", "csharp.key", caCertificatePath: "ca.crt")
});
```

TLS wraps the connection before a single distribution byte is written, so the handshake, the cookie
challenge and every message afterwards travel inside it. EPMD is unaffected and stays in the clear —
it only ever learns a node's name and port.

Two details worth knowing, because getting either wrong produces a connection that hangs rather than
one that explains itself:

- **Handshake framing differs by transport.** `inet_tcp_dist` uses a 2-byte length prefix during the
  handshake and 4 bytes after; `inet_tls_dist` uses 4 throughout. Handled automatically, but it is
  why a TLS node and a plaintext node cannot talk to each other.
- **Both ends must agree.** There is no negotiation and no fallback, by design. A plaintext peer
  dialling a TLS node is refused, and the reverse too — verified in both directions.

Defaults follow Erlang's: client certificates are required (`fail_if_no_peer_cert`), and the peer's
certificate must chain to `TrustedRoots` (`cacertfile`). Hostname verification is **off**, matching
Erlang, because node certificates are usually issued per node with names that do not match the host
in a node name. That means any certificate your CA issued can act as any node — the normal model for
a cluster of mutually trusted peers. Set `VerifyPeerHostname` if your CA also signs certificates you
would not want impersonating a node.

`ValidateRemoteCertificate`, `ConfigureServer` and `ConfigureClient` are there when you need to
pin, or to reach for something the shorthand does not expose.

## Security

Without TLS, Erlang distribution authenticates with a shared secret — the cookie — over an MD5
challenge, and then sends every message in the clear. There is no per-message authentication and no
encryption.
Any peer that can reach the distribution port and knows the cookie can send to any registered name
on this node, monitor it, link to it, and invoke whatever you exposed through `RpcHandler`. That is
true of a real BEAM node too; it is a property of the protocol, not of this implementation.

So treat the distribution port the way you would treat an unauthenticated admin socket:

- Turn on TLS. See [Encrypted distribution](#encrypted-distribution); everything below still
  applies, but an encrypted, mutually authenticated transport removes the worst of it.
- Keep it off untrusted networks. `BindAddress` defaults to `0.0.0.0`; set it to a private
  interface, or keep the node behind a firewall or inside a private network segment.
- Use a long, random cookie, and do not commit it. The `testcookie` in the samples is for local
  experimentation only.
- For cross-host traffic, use TLS, or tunnel it (WireGuard, an SSH tunnel, a service mesh).
- Expose narrowly through `RpcHandler`. It runs whatever you register, so register only what you
  are willing to have any cluster peer call.

The Erlang Ecosystem Foundation's [notes on the distribution protocol and
EPMD](https://security.erlef.org/secure_coding_and_deployment_hardening/distribution.html) are worth
reading before putting this anywhere shared.

## Limitations

**The node is hidden by default.** This matches what jinterface and C nodes do. Messaging, calls,
monitors and links are unaffected; the node just shows up under `Node.list(:hidden)` rather than
`Node.list()`.

You can set `Visibility = NodeVisibility.Visible`, and `Node.list/0`, `Node.ping/1` and
`GenServer.call/3` all work — but the `global` name registry does not: a visible peer expects us to
take part in its distributed lock and sync protocol, and `:global.sync/0` on that peer will hang
waiting for us. Unless you need to appear in `Node.list/0`, leave the node hidden.

**The core serializer cannot serialize a type it has never been told about.** That is the point of
the split rather than an oversight: declare it on a context, or add the reflection package. The
error message says both.

**Not implemented:** the `global` name registry, the distribution atom cache
(`DFLAG_DIST_HDR_ATOM_CACHE`) and message fragmentation (`DFLAG_FRAGMENTS`) — the latter two are
negotiated away, which is legal and costs only some bandwidth on repeated atoms. Calling arbitrary code via `spawn_request` is limited to the
`erpc` entry points that `:rpc.call` and `:erpc.call` use.

**A C# mailbox is not a process.** It does not die on its own, so a linked or monitoring peer only
hears about it when you close the mailbox. `TrapExit` defaults to `true`, delivering incoming exits
as `{'EXIT', From, Reason}` messages rather than tearing the mailbox down.

## Layout

```
src/BeamSharp/
  Terms/       external term format: model, encoder, decoder
  Epmd/        port mapper client
  Protocol/    flags, opcodes, handshake, framed connection
  Node/        node, mailboxes, gen_server and rpc dispatch
  Networking/  host name resolution
src/BeamSharp.Serialization/
  Converters/  built-ins, collections, and the reflection fallback for plain objects
src/BeamSharp.Serialization.Generator/
               the Roslyn generator, shipped inside the serialization package as an analyzer
src/BeamSharp.Serialization.Reflection/
               the reflection fallback, kept apart so an AOT app cannot reach it
src/BeamSharp.Extensions.Hosting/
               DI, IHostedService, ILogger and metrics
bench/BeamSharp.Benchmarks/
               generated vs reflection, and raw codec throughput
samples/
  BeamSharp.Server   a .NET node for Elixir to call into
  BeamSharp.Client   a .NET node that calls into Elixir
test/
  BeamSharp.Tests               unit tests over the codec and protocol
  BeamSharp.Serialization.Tests unit tests over the object mapping and the generator
  BeamSharp.Aot.Probe            a NativeAOT console app proving the generated path needs no reflection
  BeamSharp.Extensions.Hosting.Tests  node lifetime under a real host
  gen_fixtures.escript          regenerates fixtures.txt from a real Erlang runtime
  elixir_client.exs             34 checks driving the .NET node from Elixir
  elixir_structs.exs            the Elixir structs the C# records map onto
  elixir_server.exs             a plain Elixir GenServer for the .NET node to call
  run_integration.sh            runs both directions end to end
  run_aot_probe.sh              publishes and runs the AOT probe
  run_tls_integration.sh        the same interop suite over an encrypted transport
  gen_certs.sh                  throwaway CA and node certificates for the TLS test
```

## Testing

```bash
dotnet test
```

187 unit tests. The codec ones assert against byte vectors captured from a real Erlang runtime
(`test/fixtures.txt`, regenerated by `test/gen_fixtures.escript`) rather than against our own
encoder, so a shared misunderstanding of the format cannot pass.

Both parsers that read from a socket are fuzzed, and the fuzzing is generative rather than random
bytes alone — random bytes die at the first tag, so they barely reach anything that recurses.

The **term decoder** takes 50,000 random inputs, every truncation of six real fixtures, 8,000
single-byte mutations and 2,000 generated term trees, all of which must produce `ErlDecodeException`
and nothing else. Element counts are checked against the bytes actually remaining, so a six-byte
frame claiming a hundred million elements allocates nothing, and nesting is bounded at 256 levels,
because a 40 KB frame can nest twenty thousand deep and a stack overflow cannot be caught.

The **handshake parser** matters more than either, because it reads from anyone who can open a
socket *before* the cookie has authenticated them. It is fuzzed with random bytes, with well-formed
messages carrying hostile length fields, and with every truncation of a valid exchange, across both
the 2-byte and 4-byte framings. Hostile input has to fail as a handshake error, an end of stream or
an I/O error — never as an index or arithmetic exception, which would mean a parser reading past
what it checked.

```bash
test/run_integration.sh
```

Starts the C# node and an Elixir node and runs 34 checks inbound and 11 outbound, covering calls,
casts, sends, monitors, links, exits, rpc, error propagation, concurrency, and C# objects arriving
as Elixir structs. Needs `elixir` and `epmd` on `PATH`.

```bash
test/run_tls_integration.sh
```

Runs the inbound suite again with the Elixir node on `-proto_dist inet_tls` and the C# node on TLS,
using a throwaway CA. It also checks that a plaintext peer is turned away, since encryption a peer
can decline is not encryption.

```bash
test/run_aot_probe.sh
```

Publishes a console app with `PublishAot=true`, fails if the publish emits a single trim or AOT
warning, and runs the resulting native binary.

## Protocol references

The wire details were taken from the OTP 29 sources rather than from memory:
`kernel/include/dist.hrl` (flag values), `kernel/src/dist_util.erl` (handshake and digest),
`kernel/src/erl_epmd.erl` (EPMD framing), `stdlib/src/gen.erl` (the alias-based call/reply protocol)
and `kernel/src/erpc.erl` (the spawn-request rpc path). Term tags were confirmed by inspecting
`term_to_binary/1` output.
