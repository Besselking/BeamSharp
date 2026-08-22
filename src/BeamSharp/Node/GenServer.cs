using BeamSharp.Terms;

namespace BeamSharp.Node;

/// <summary>
/// The <c>From</c> half of a <c>gen_server</c> call. Hold on to it to reply later; the tag is opaque
/// and must be echoed back exactly, because OTP uses it to route the reply through a process alias.
/// </summary>
public readonly record struct GenCallFrom(ErlPid Caller, ErlTerm Tag)
{
    /// <summary>
    /// The reference the reply has to be addressed to. OTP 24+ wraps it as <c>[alias | Ref]</c>;
    /// null means the caller wants the reply sent to its pid instead.
    /// </summary>
    public ErlRef? Alias => Tag switch
    {
        // [alias | Ref]
        ErlList { Count: 1 } l when l[0].IsAtom("alias") && l.Tail is ErlRef r => r,
        // [[alias | Ref] | _] — used by send_request/receive_response
        ErlList { Count: >= 1 } outer when outer[0] is ErlList { Count: 1 } inner
                                           && inner[0].IsAtom("alias") && inner.Tail is ErlRef r2 => r2,
        _ => null
    };
}

/// <summary>
/// Handles the messages an Elixir <c>GenServer</c> client would send. Implement this and register it
/// under a name; from Elixir it is indistinguishable from a real GenServer.
/// </summary>
public interface IErlangGenServer
{
    /// <summary>
    /// Handles <c>GenServer.call/3</c>. Return the reply, or null to answer later via
    /// <see cref="ErlangNode.ReplyAsync"/> — the equivalent of <c>{:noreply, state}</c>.
    /// </summary>
    ValueTask<ErlTerm?> HandleCallAsync(ErlTerm request, GenCallFrom from, CancellationToken ct);

    /// <summary>Handles <c>GenServer.cast/2</c>.</summary>
    ValueTask HandleCastAsync(ErlTerm request, CancellationToken ct);

    /// <summary>Handles anything else that lands in the mailbox, including <c>send/2</c>, exits and downs.</summary>
    ValueTask HandleInfoAsync(ErlMessage message, CancellationToken ct);
}

/// <summary>Convenience base class: override only what you need.</summary>
public abstract class ErlangGenServer : IErlangGenServer
{
    public abstract ValueTask<ErlTerm?> HandleCallAsync(ErlTerm request, GenCallFrom from, CancellationToken ct);

    public virtual ValueTask HandleCastAsync(ErlTerm request, CancellationToken ct) => ValueTask.CompletedTask;

    public virtual ValueTask HandleInfoAsync(ErlMessage message, CancellationToken ct) => ValueTask.CompletedTask;
}

/// <summary>Runs a <see cref="IErlangGenServer"/> against a mailbox.</summary>
internal sealed class GenServerHost
{
    private readonly Mailbox _mailbox;
    private readonly IErlangGenServer _handler;
    private readonly Action<string>? _log;

    public GenServerHost(Mailbox mailbox, IErlangGenServer handler, Action<string>? log)
    {
        _mailbox = mailbox;
        _handler = handler;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _mailbox.Messages.ReadAllAsync(ct).ConfigureAwait(false))
                await DispatchAsync(message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log?.Invoke($"gen_server '{_mailbox.RegisteredName}' stopped: {ex}");
        }
    }

    private async Task DispatchAsync(ErlMessage message, CancellationToken ct)
    {
        try
        {
            if (message.Term.IsTagged("$gen_call", out var call) && call.Arity == 3 &&
                call[1] is ErlTuple { Arity: 2 } fromTuple && fromTuple[0] is ErlPid caller)
            {
                var from = new GenCallFrom(caller, fromTuple[1]);
                var reply = await _handler.HandleCallAsync(call[2], from, ct).ConfigureAwait(false);
                if (reply is not null) await _mailbox.ReplyAsync(from, reply, ct).ConfigureAwait(false);
                return;
            }

            if (message.Term.IsTagged("$gen_cast", out var cast) && cast.Arity == 2)
            {
                await _handler.HandleCastAsync(cast[1], ct).ConfigureAwait(false);
                return;
            }

            await _handler.HandleInfoAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A crashing handler must not take down the pump; log it the way OTP would report it.
            _log?.Invoke($"gen_server '{_mailbox.RegisteredName}' handler raised: {ex}");
        }
    }
}
