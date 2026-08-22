# erpc — an Erlang distribution node in C#

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

Verified against **Erlang/OTP 29** and **Elixir 1.20** on **.NET 10**.

## Quick start

```bash
dotnet run --project samples/Erpc.Sample -- "csharp@$(hostname -s)" testcookie
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
using Erlang.Distribution.Node;
using Erlang.Distribution.Terms;

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

## Limitations

**The node is hidden by default.** This matches what jinterface and C nodes do. Messaging, calls,
monitors and links are unaffected; the node just shows up under `Node.list(:hidden)` rather than
`Node.list()`.

You can set `Visibility = NodeVisibility.Visible`, and `Node.list/0`, `Node.ping/1` and
`GenServer.call/3` all work — but the `global` name registry does not: a visible peer expects us to
take part in its distributed lock and sync protocol, and `:global.sync/0` on that peer will hang
waiting for us. Unless you need to appear in `Node.list/0`, leave the node hidden.

**Not implemented:** the `global` name registry, the distribution atom cache
(`DFLAG_DIST_HDR_ATOM_CACHE`) and message fragmentation (`DFLAG_FRAGMENTS`) — the latter two are
negotiated away, which is legal and costs only some bandwidth on repeated atoms. There is no TLS
transport, and calling arbitrary code via `spawn_request` is limited to the `erpc` entry points that
`:rpc.call` and `:erpc.call` use.

**A C# mailbox is not a process.** It does not die on its own, so a linked or monitoring peer only
hears about it when you close the mailbox. `TrapExit` defaults to `true`, delivering incoming exits
as `{'EXIT', From, Reason}` messages rather than tearing the mailbox down.

## Layout

```
src/Erlang.Distribution/
  Terms/      external term format: model, encoder, decoder
  Epmd/       port mapper client
  Protocol/   flags, opcodes, handshake, framed connection
  Node/       node, mailboxes, gen_server and rpc dispatch
  Net/        host name resolution
samples/
  Erpc.Sample  a C# node for Elixir to call into
  Erpc.Client  a C# node that calls into Elixir
test/
  Erlang.Distribution.Tests   unit tests over the codec and protocol
  gen_fixtures.escript        regenerates fixtures.txt from a real Erlang runtime
  elixir_client.exs           26 checks driving the C# node from Elixir
  elixir_server.exs           a plain Elixir GenServer for the C# node to call
  run_integration.sh          runs both directions end to end
```

## Testing

```bash
dotnet test
```

73 unit tests. The codec ones assert against byte vectors captured from a real Erlang runtime
(`test/fixtures.txt`, regenerated by `test/gen_fixtures.escript`) rather than against our own
encoder, so a shared misunderstanding of the format cannot pass.

```bash
test/run_integration.sh
```

Starts the C# node and an Elixir node and runs 26 checks inbound and 11 outbound, covering calls,
casts, sends, monitors, links, exits, rpc, error propagation and concurrency. Needs `elixir` and
`epmd` on `PATH`.

## Protocol references

The wire details were taken from the OTP 29 sources rather than from memory:
`kernel/include/dist.hrl` (flag values), `kernel/src/dist_util.erl` (handshake and digest),
`kernel/src/erl_epmd.erl` (EPMD framing), `stdlib/src/gen.erl` (the alias-based call/reply protocol)
and `kernel/src/erpc.erl` (the spawn-request rpc path). Term tags were confirmed by inspecting
`term_to_binary/1` output.
