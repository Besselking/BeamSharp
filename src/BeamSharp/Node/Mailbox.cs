using System.Collections.Concurrent;
using System.Threading.Channels;
using BeamSharp.Terms;

namespace BeamSharp.Node;

/// <summary>A message that arrived in a mailbox.</summary>
/// <param name="Term">The message itself, exactly as the sender wrote it.</param>
/// <param name="Sender">The sending process, when the distribution protocol told us who it was.</param>
public readonly record struct ErlMessage(ErlTerm Term, ErlPid? Sender);

/// <summary>
/// The C# stand-in for an Erlang process: it owns a pid, may hold a registered name, and has an
/// inbox. Signals follow Erlang's conventions — a link exit shows up as <c>{'EXIT', From, Reason}</c>
/// and a monitor firing shows up as <c>{'DOWN', Ref, process, Object, Reason}</c>.
/// </summary>
public sealed class Mailbox : IAsyncDisposable
{
    private readonly ErlangNode _node;
    private readonly Channel<ErlMessage> _inbox;

    internal readonly ConcurrentDictionary<ErlRef, ErlPid> IncomingMonitors = new();
    internal readonly ConcurrentDictionary<ErlPid, byte> Links = new();

    private int _closed;

    internal Mailbox(ErlangNode node, ErlPid pid, string? registeredName, int capacity)
    {
        _node = node;
        Pid = pid;
        RegisteredName = registeredName;
        _inbox = capacity > 0
            ? Channel.CreateBounded<ErlMessage>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false
            })
            : Channel.CreateUnbounded<ErlMessage>();
    }

    /// <summary>This mailbox's pid, which is what remote processes send to.</summary>
    public ErlPid Pid { get; }

    /// <summary>The registered name, if this mailbox was created with one.</summary>
    public string? RegisteredName { get; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// When true (the default) an incoming exit signal is delivered as an <c>{'EXIT', From, Reason}</c>
    /// message instead of closing the mailbox — the equivalent of <c>process_flag(trap_exit, true)</c>.
    /// </summary>
    public bool TrapExit { get; set; } = true;

    /// <summary>The stream of incoming messages.</summary>
    public ChannelReader<ErlMessage> Messages => _inbox.Reader;

    /// <summary>Waits for the next message. Throws <see cref="OperationCanceledException"/> if cancelled.</summary>
    public ValueTask<ErlMessage> ReceiveAsync(CancellationToken ct = default) => _inbox.Reader.ReadAsync(ct);

    /// <summary>Waits for the next message, returning null if none arrives within <paramref name="timeout"/>.</summary>
    public async Task<ErlMessage?> ReceiveAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await _inbox.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Sends a message to a remote pid, with this mailbox as the sender.</summary>
    public Task SendAsync(ErlPid to, ErlTerm message, CancellationToken ct = default) =>
        _node.SendAsync(to, message, Pid, ct);

    /// <summary>Sends a message to a registered name on another node.</summary>
    public Task SendAsync(string name, string node, ErlTerm message, CancellationToken ct = default) =>
        _node.SendAsync(name, node, message, Pid, ct);

    /// <summary>Answers a <c>gen_server</c> call, honouring whichever reply tag the caller used.</summary>
    public Task ReplyAsync(GenCallFrom from, ErlTerm reply, CancellationToken ct = default) =>
        _node.ReplyAsync(from, reply, Pid, ct);

    internal bool TryDeliver(ErlTerm term, ErlPid? sender) =>
        !IsClosed && _inbox.Writer.TryWrite(new ErlMessage(term, sender));

    internal bool MarkClosed() => Interlocked.Exchange(ref _closed, 1) == 0;

    internal void CompleteInbox() => _inbox.Writer.TryComplete();

    /// <summary>Unregisters the mailbox and tells anything linked to or monitoring it that it is gone.</summary>
    public ValueTask DisposeAsync() => _node.CloseMailboxAsync(this, ErlAtom.Normal);
}
