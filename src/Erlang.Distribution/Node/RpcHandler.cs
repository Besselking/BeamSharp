using System.Collections.Concurrent;
using Erlang.Distribution.Terms;

namespace Erlang.Distribution.Node;

/// <summary>
/// Serves the incoming half of <c>:rpc.call/4</c> and <c>:erpc.call/4</c>. Both route through a
/// distribution spawn request in OTP 23 and later, so a single handler covers them.
/// </summary>
public interface IErlangRpcHandler
{
    /// <summary>
    /// Runs <c>Module:Function(Args)</c>. Throwing produces the same shape of error on the caller's
    /// side that a raising Erlang function would.
    /// </summary>
    ValueTask<ErlTerm> InvokeAsync(string module, string function, IReadOnlyList<ErlTerm> args, CancellationToken ct);
}

/// <summary>A dictionary of exported functions, keyed by module, name and arity.</summary>
public sealed class RpcRegistry : IErlangRpcHandler
{
    private readonly ConcurrentDictionary<(string Module, string Function, int Arity),
        Func<IReadOnlyList<ErlTerm>, CancellationToken, ValueTask<ErlTerm>>> _functions = new();

    /// <summary>Exports a function that Erlang can call as <c>Module:Function/Arity</c>.</summary>
    public RpcRegistry Add(string module, string function, int arity,
        Func<IReadOnlyList<ErlTerm>, CancellationToken, ValueTask<ErlTerm>> implementation)
    {
        _functions[(module, function, arity)] = implementation;
        return this;
    }

    /// <summary>Exports a synchronous function.</summary>
    public RpcRegistry Add(string module, string function, int arity,
        Func<IReadOnlyList<ErlTerm>, ErlTerm> implementation) =>
        Add(module, function, arity, (args, _) => ValueTask.FromResult(implementation(args)));

    public ValueTask<ErlTerm> InvokeAsync(string module, string function, IReadOnlyList<ErlTerm> args,
        CancellationToken ct)
    {
        if (_functions.TryGetValue((module, function, args.Count), out var impl))
            return impl(args, ct);

        // Same shape Erlang produces for a call to a function that does not exist.
        throw new ErlangRpcException(new ErlTuple(
            new ErlAtom("undef"),
            new ErlList(new ErlTerm[]
            {
                new ErlTuple(new ErlAtom(module), new ErlAtom(function), new ErlInt(args.Count), ErlList.Empty)
            })));
    }
}

/// <summary>Thrown to send a specific Erlang error term back to the caller.</summary>
public sealed class ErlangRpcException : Exception
{
    public ErlangRpcException(ErlTerm reason) : base(reason.ToString()) => Reason = reason;

    /// <summary>The Erlang term used as the error reason.</summary>
    public ErlTerm Reason { get; }
}
