using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using BeamSharp.Epmd;
using BeamSharp.Networking;
using BeamSharp.Protocol;
using BeamSharp.Security;
using BeamSharp.Terms;

namespace BeamSharp.Node;

/// <summary>
/// A C# node on an Erlang cluster. It registers with EPMD, performs the distribution handshake and
/// speaks the same signals a real node does, so an Elixir peer can <c>send/2</c>, <c>GenServer.call/3</c>,
/// monitor, link and <c>:rpc.call</c> into it without knowing it is not talking to the BEAM.
/// </summary>
public sealed class ErlangNode : IAsyncDisposable
{
    private static readonly ErlAtom Unused = new("");

    private readonly ErlangNodeOptions _options;
    private readonly EpmdClient _epmd;
    private readonly CancellationTokenSource _cts = new();

    private readonly ConcurrentDictionary<string, DistConnection> _connections = new();
    private readonly ConcurrentDictionary<string, Mailbox> _registered = new();
    private readonly ConcurrentDictionary<ErlPid, Mailbox> _mailboxes = new();
    private readonly ConcurrentDictionary<ErlRef, PendingCall> _pendingCalls = new();
    private readonly ConcurrentDictionary<string, Exception> _lastConnectFailures = new();
    private readonly ConcurrentDictionary<ErlRef, Mailbox> _aliases = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly string _cookieSource;
    private readonly string? _cookieWarning;
    private int _handshakesInFlight;

    /// <summary>How long to wait after a failed accept before trying again.</summary>
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(100);
    private readonly List<Task> _hostedServers = [];

    private TcpListener? _listener;
    private int _disposed;
    private uint _pidId;
    private uint _pidSerial;
    private long _refCounter;
    private int _started;

    /// <summary>Creates a node called <paramref name="nodeName"/>, in the form <c>alive@host</c>.</summary>
    public ErlangNode(string nodeName, ErlangNodeOptions? options = null)
    {
        Name = NodeName.Parse(nodeName);
        _options = options ?? new ErlangNodeOptions();
        if (_options.Cookie is { } configured)
        {
            Cookie = configured;
            _cookieSource = "the supplied options";
        }
        else
        {
            Cookie = ReadCookieFile(out var cookiePath, out var permissionWarning)
                ?? throw new InvalidOperationException(
                    "no cookie was supplied and ~/.erlang.cookie could not be read");
            _cookieSource = cookiePath;
            _cookieWarning = permissionWarning;
        }
        _epmd = new EpmdClient(_options.EpmdHost, _options.EpmdPort);
    }

    /// <summary>Creates a node with an explicit cookie and otherwise default options.</summary>
    public ErlangNode(string nodeName, string cookie) : this(nodeName, new ErlangNodeOptions { Cookie = cookie }) { }

    /// <summary>This node's name, e.g. <c>csharp@myhost</c>.</summary>
    public NodeName Name { get; }

    /// <summary>The magic cookie this node authenticates with.</summary>
    public string Cookie { get; }

    /// <summary>Creation number handed out by EPMD; it makes pids from different node incarnations distinct.</summary>
    public uint Creation { get; private set; }

    /// <summary>The TCP port the distribution listener ended up on.</summary>
    public int Port { get; private set; }

    /// <summary>True when the distribution transport is encrypted.</summary>
    public bool UsesTls => _options.Tls is not null;

    /// <summary>
    /// TLS carries the handshake in 4-byte frames throughout, where plain TCP uses 2-byte frames
    /// until the handshake finishes. Mismatching this hangs the connection instead of failing it.
    /// </summary>
    private int HandshakePrefix =>
        UsesTls ? Handshake.TlsLengthPrefix : Handshake.TcpLengthPrefix;

    /// <summary>Names of the peers currently connected.</summary>
    public IReadOnlyCollection<string> ConnectedNodes => _connections.Keys.ToArray();

    /// <summary>The live connections, so tests can put bytes on the wire a well-behaved peer never would.</summary>
    internal IReadOnlyDictionary<string, DistConnection> Connections => _connections;

    /// <summary>Handles incoming <c>:rpc.call/4</c> and <c>:erpc.call/4</c>. Null rejects them.</summary>
    public IErlangRpcHandler? RpcHandler { get; set; }

    /// <summary>Raised when a peer connects.</summary>
    public event Action<string>? NodeUp;

    /// <summary>Raised when a peer goes away.</summary>
    public event Action<string, Exception?>? NodeDown;

    // ------------------------------------------------------------------ start

    /// <summary>Binds the listener, registers with EPMD and starts accepting connections.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("node already started");

        _listener = new TcpListener(IPAddress.Parse(_options.BindAddress), _options.Port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        var registration = await _epmd.RegisterAsync(Name.Alive, Port, _options.Visibility, ct).ConfigureAwait(false);
        Creation = registration.Creation;
        // Where the cookie came from, never any part of the cookie: the hosting package routes this
        // callback into ILogger, which ships it off the box. The source is the more useful half in
        // any case, since reading the wrong file is the common first-run problem, not mistyping.
        Log($"{Name} listening on port {Port}, creation {Creation}, cookie from {_cookieSource}");

        if (_cookieWarning is not null) Log(_cookieWarning);

        if (_options.ProvideNetKernel) RegisterGenServer("net_kernel", new NetKernelServer());

        // Deliberately not the caller's token: the listener lives as long as the node, not as long
        // as the call that started it.
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token), CancellationToken.None);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                // Only a stopped node stops accepting. The registration with EPMD outlives any one
                // accept error, so abandoning the loop advertises a port nothing answers on: the
                // node looks alive and every ping times out. Errors like running out of descriptors
                // pass, and the backoff keeps a permanent one from spinning.
                Log($"accept failed: {ex.Message}");
                try
                {
                    await Task.Delay(AcceptRetryDelay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }

                continue;
            }

            // A handshake costs a task, a timeout, and under TLS an asymmetric-crypto operation,
            // all of it before the peer has proved it holds the cookie. Refuse past the limit
            // rather than queueing: a caller that cannot get in now would rather be told.
            if (Interlocked.Increment(ref _handshakesInFlight) > _options.MaxConcurrentHandshakes)
            {
                Interlocked.Decrement(ref _handshakesInFlight);
                Log("refusing an inbound connection: too many handshakes already in flight");
                client.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    client.NoDelay = true;

                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    attempt.CancelAfter(_options.HandshakeTimeout);

                    var stream = await WrapForServerAsync(client, attempt.Token).ConfigureAwait(false);
                    var handshake = await Handshake
                        .AcceptAsync(stream, Name.Full, Creation, _options.Flags, _ => Cookie,
                            HandshakePrefix, attempt.Token)
                        .ConfigureAwait(false);
                    await AttachConnectionAsync(new DistConnection(client, stream, handshake, _options.TickTime))
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Log($"inbound handshake timed out after {_options.HandshakeTimeout.TotalSeconds:0}s");
                    client.Dispose();
                }
                catch (Exception ex)
                {
                    Log($"inbound handshake failed: {ex.Message}");
                    client.Dispose();
                }
                finally
                {
                    Interlocked.Decrement(ref _handshakesInFlight);
                }
            }, ct);
        }
    }

    // ------------------------------------------------------------ connections

    /// <summary>Connects to a peer node, or returns the existing connection.</summary>
    public async Task<bool> ConnectAsync(string peerNode, CancellationToken ct = default)
    {
        if (_connections.ContainsKey(peerNode)) return true;

        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connections.ContainsKey(peerNode)) return true;

            var peer = NodeName.Parse(peerNode);
            var epmd = new EpmdClient(peer.Host, _options.EpmdPort);
            var info = await epmd.LookupAsync(peer.Alive, ct).ConfigureAwait(false);
            if (info is null)
            {
                // The commonest failure of the lot, and the one that reaches here without an
                // exception to carry, so it gets one made for it.
                var unknown = new IOException(
                    $"EPMD on {peer.Host} does not know a node called '{peer.Alive}'");
                RecordConnectFailure(peerNode, unknown);
                Log(unknown.Message);
                return false;
            }

            var client = await HostResolver.ConnectAsync(peer.Host, info.Port, ct).ConfigureAwait(false);

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(_options.HandshakeTimeout);

            var stream = await WrapForClientAsync(client, peer.Host, attempt.Token).ConfigureAwait(false);
            var handshake = await Handshake
                .ConnectAsync(stream, Name.Full, Creation, _options.Flags, peerNode, Cookie,
                    HandshakePrefix, attempt.Token)
                .ConfigureAwait(false);
            await AttachConnectionAsync(new DistConnection(client, stream, handshake, _options.TickTime))
                .ConfigureAwait(false);
            _lastConnectFailures.TryRemove(peerNode, out _);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var timeout = new TimeoutException(
                $"connecting to {peerNode} timed out after {_options.HandshakeTimeout.TotalSeconds:0}s");
            RecordConnectFailure(peerNode, timeout);
            Log(timeout.Message);
            return false;
        }
        catch (Exception ex)
        {
            RecordConnectFailure(peerNode, ex);
            Log($"connecting to {peerNode} failed: {ex.Message}");
            return false;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Negotiates TLS before a single distribution byte is exchanged, which is what
    /// <c>inet_tls_dist</c> does. Without TLS configured this is the raw socket.
    /// </summary>
    private async Task<Stream> WrapForServerAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        if (_options.Tls is not { } tls) return stream;

        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(tls.BuildServerOptions(), ct).ConfigureAwait(false);
            Log($"inbound TLS established: {ssl.SslProtocol}, {ssl.NegotiatedCipherSuite}");
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Stream> WrapForClientAsync(TcpClient client, string targetHost, CancellationToken ct)
    {
        var stream = client.GetStream();
        if (_options.Tls is not { } tls) return stream;

        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(tls.BuildClientOptions(targetHost), ct).ConfigureAwait(false);
            Log($"outbound TLS established: {ssl.SslProtocol}, {ssl.NegotiatedCipherSuite}");
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task AttachConnectionAsync(DistConnection connection)
    {
        connection.OnMessage = HandleControlMessageAsync;
        connection.Closed = (c, error) =>
        {
            _connections.TryRemove(new KeyValuePair<string, DistConnection>(c.PeerNode, c));
            FailPendingCallsFor(c.PeerNode);
            Log($"disconnected from {c.PeerNode}{(error is null ? "" : $": {error.Message}")}");
            NodeDown?.Invoke(c.PeerNode, error);
        };

        // Inbound handshakes complete on unsynchronised tasks, so two from one peer can reach here
        // at once and only the atomic operations say anything true about which won. A connection
        // dropped from the dictionary without being disposed keeps its read, write and tick loops
        // running against a socket nobody holds.
        while (!_connections.TryAdd(connection.PeerNode, connection))
        {
            if (_connections.TryGetValue(connection.PeerNode, out var existing) && !existing.IsClosed)
            {
                // Simultaneous connect: keep the one already established.
                await connection.DisposeAsync().ConfigureAwait(false);
                return;
            }

            // Dead, but its Closed callback may not have run yet. Evict exactly this one, so a live
            // replacement racing us here is never the casualty.
            if (existing is not null)
                _connections.TryRemove(new KeyValuePair<string, DistConnection>(connection.PeerNode, existing));
        }

        connection.Start();
        Log($"connected to {connection.PeerNode} (flags {connection.Flags})");
        NodeUp?.Invoke(connection.PeerNode);
    }

    private async Task<DistConnection> RequireConnectionAsync(string node, CancellationToken ct)
    {
        if (_connections.TryGetValue(node, out var existing) && !existing.IsClosed) return existing;
        if (await ConnectAsync(node, ct).ConfigureAwait(false) &&
            _connections.TryGetValue(node, out var fresh)) return fresh;

        // ConnectAsync reports a bad cookie, an unknown node, a TLS mismatch, a DNS failure and a
        // refused connection all as false, and the reason reaches only the log callback, which
        // defaults to discarding. The two commonest first-run problems here are a cookie mismatch
        // and the TLS/plaintext framing mismatch the README warns about, so the explanation is
        // worth carrying to whoever gets the throw.
        throw new IOException($"no connection to {node}", _lastConnectFailures.GetValueOrDefault(node));
    }

    /// <summary>
    /// Remembers why the last attempt at <paramref name="node"/> failed, so the throw that follows
    /// can carry it. Only ever one exception per peer, replaced each time.
    /// </summary>
    private void RecordConnectFailure(string node, Exception error) =>
        _lastConnectFailures[node] = error;

    // --------------------------------------------------------------- mailboxes

    /// <summary>
    /// Creates a mailbox — the C# equivalent of spawning a process. Pass a name to register it so
    /// remote code can address it as <c>{:name, :"node@host"}</c>.
    /// </summary>
    /// <param name="registeredName">A name remote code can address, or null for an unnamed mailbox.</param>
    /// <param name="capacity">
    /// Zero, the default, means unbounded, matching an Erlang process mailbox — but the emulator has
    /// <c>max_heap_size</c> behind it and this does not, so a peer sending faster than the handler
    /// drains grows the inbox until the process dies. A positive value bounds it by dropping the
    /// <em>oldest</em> message, not by refusing the newest.
    /// </param>
    public Mailbox CreateMailbox(string? registeredName = null, int capacity = 0)
    {
        var mailbox = new Mailbox(this, NextPid(), registeredName, capacity);
        _mailboxes[mailbox.Pid] = mailbox;

        if (registeredName is not null && !_registered.TryAdd(registeredName, mailbox))
        {
            _mailboxes.TryRemove(mailbox.Pid, out _);
            throw new InvalidOperationException($"the name '{registeredName}' is already registered on this node");
        }

        return mailbox;
    }

    /// <summary>Registers a handler that behaves like an Elixir <c>GenServer</c> under the given name.</summary>
    /// <param name="name">The name remote code addresses the server by.</param>
    /// <param name="handler">The callbacks invoked for calls, casts and plain messages.</param>
    /// <param name="capacity">See <see cref="CreateMailbox"/>; zero means unbounded.</param>
    public Mailbox RegisterGenServer(string name, IErlangGenServer handler, int capacity = 0)
    {
        var mailbox = CreateMailbox(name, capacity);
        var host = new GenServerHost(mailbox, handler, _options.Log);
        lock (_hostedServers) _hostedServers.Add(Task.Run(() => host.RunAsync(_cts.Token)));
        return mailbox;
    }

    /// <summary>Looks up a locally registered mailbox.</summary>
    public Mailbox? Whereis(string name) => _registered.GetValueOrDefault(name);

    /// <summary>Closes a mailbox and notifies whoever was linked to or monitoring it.</summary>
    /// <remarks>
    /// Returns once the notifications are queued, not once they are on the wire. This runs on a read
    /// loop when it is an inbound exit that closed the mailbox, and waiting for a peer to make room
    /// there is what stops that loop reading.
    /// </remarks>
    public ValueTask CloseMailboxAsync(Mailbox mailbox, ErlTerm reason)
    {
        CloseMailbox(mailbox, reason);
        return ValueTask.CompletedTask;
    }

    private void CloseMailbox(Mailbox mailbox, ErlTerm reason)
    {
        if (!mailbox.MarkClosed()) return;

        _mailboxes.TryRemove(mailbox.Pid, out _);
        if (mailbox.RegisteredName is not null)
            _registered.TryRemove(new KeyValuePair<string, Mailbox>(mailbox.RegisteredName, mailbox));

        foreach (var (reference, watcher) in mailbox.IncomingMonitors)
            TrySignal(watcher.Node, new ErlTuple(
                new ErlInt((int)DistOp.MonitorPExit), mailbox.Pid, watcher, reference, reason));

        foreach (var linked in mailbox.Links.Keys)
            TrySignal(linked.Node, new ErlTuple(
                new ErlInt((int)DistOp.Exit), mailbox.Pid, linked, reason));

        mailbox.IncomingMonitors.Clear();
        mailbox.Links.Clear();
        mailbox.CompleteInbox();
    }

    // ----------------------------------------------------------------- sending

    /// <summary>Sends a message to a remote pid — the equivalent of <c>send(pid, message)</c>.</summary>
    public async Task SendAsync(ErlPid to, ErlTerm message, ErlPid? from = null, CancellationToken ct = default)
    {
        if (to.Node == Name.Full)
        {
            DeliverLocal(to, message, from);
            return;
        }

        var connection = await RequireConnectionAsync(to.Node, ct).ConfigureAwait(false);
        var control = connection.Flags.HasFlag(DistributionFlags.SendSender) && from is not null
            ? new ErlTuple(new ErlInt((int)DistOp.SendSender), from, to)
            : new ErlTuple(new ErlInt((int)DistOp.Send), Unused, to);
        await connection.SendAsync(control, message, ct).ConfigureAwait(false);
    }

    /// <summary>Sends to a registered name on another node — <c>send({:name, :"node@host"}, message)</c>.</summary>
    public async Task SendAsync(string name, string node, ErlTerm message, ErlPid? from = null,
        CancellationToken ct = default)
    {
        if (node == Name.Full)
        {
            if (_registered.TryGetValue(name, out var local)) local.TryDeliver(message, from);
            return;
        }

        var connection = await RequireConnectionAsync(node, ct).ConfigureAwait(false);
        var control = new ErlTuple(
            new ErlInt((int)DistOp.RegSend), from ?? NextPid(), Unused, new ErlAtom(name));
        await connection.SendAsync(control, message, ct).ConfigureAwait(false);
    }

    /// <summary>Sends to a process alias, which is how a <c>gen_server</c> reply gets home in OTP 24+.</summary>
    public async Task SendToAliasAsync(ErlRef alias, ErlTerm message, ErlPid? from = null,
        CancellationToken ct = default)
    {
        if (alias.Node == Name.Full)
        {
            if (_aliases.TryGetValue(alias, out var local)) local.TryDeliver(message, from);
            return;
        }

        var connection = await RequireConnectionAsync(alias.Node, ct).ConfigureAwait(false);
        var control = new ErlTuple(new ErlInt((int)DistOp.AliasSend), from ?? NextPid(), alias);
        await connection.SendAsync(control, message, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Replies to a <c>gen_server</c> call, matching OTP's <c>gen:reply/2</c>: an alias tag goes back
    /// through the alias, anything else goes to the caller's pid.
    /// </summary>
    public Task ReplyAsync(GenCallFrom from, ErlTerm reply, ErlPid? self = null, CancellationToken ct = default)
    {
        var message = new ErlTuple(from.Tag, reply);
        return from.Alias is { } alias
            ? SendToAliasAsync(alias, message, self, ct)
            : SendAsync(from.Caller, message, self, ct);
    }

    /// <summary>
    /// Sends a signal to whichever connection already has <paramref name="node"/>, if any.
    /// </summary>
    /// <remarks>
    /// Called while handling an inbound frame, and often for a different peer than the one the frame
    /// came from, so it must not wait for room: otherwise one peer that stops reading wedges the
    /// read loop of every connection with something to tell it.
    /// </remarks>
    private void TrySignal(string node, ErlTerm control, ErlTerm? payload = null)
    {
        if (_connections.TryGetValue(node, out var connection) && !connection.IsClosed)
            connection.TrySendSignal(control, payload);
    }

    private void DeliverLocal(ErlPid to, ErlTerm message, ErlPid? from)
    {
        if (_mailboxes.TryGetValue(to, out var mailbox)) mailbox.TryDeliver(message, from);
    }

    // ------------------------------------------------------- outgoing requests

    private sealed record PendingCall(TaskCompletionSource<ErlTerm> Completion, string Node, ErlPid Self);

    /// <summary>
    /// Calls a remote <c>gen_server</c> registered under <paramref name="name"/> on
    /// <paramref name="node"/>, using the same alias-and-monitor protocol OTP uses.
    /// </summary>
    public Task<ErlTerm> CallAsync(string name, string node, ErlTerm request, TimeSpan? timeout = null,
        CancellationToken ct = default) =>
        CallCoreAsync(new ErlAtom(name), node, request, timeout, ct);

    /// <summary>Calls a remote <c>gen_server</c> by pid.</summary>
    public Task<ErlTerm> CallAsync(ErlPid pid, ErlTerm request, TimeSpan? timeout = null,
        CancellationToken ct = default) =>
        CallCoreAsync(pid, pid.Node, request, timeout, ct);

    private async Task<ErlTerm> CallCoreAsync(ErlTerm target, string node, ErlTerm request, TimeSpan? timeout,
        CancellationToken ct)
    {
        var connection = await RequireConnectionAsync(node, ct).ConfigureAwait(false);
        var self = NextPid();
        var tagRef = NextRef();
        var completion = new TaskCompletionSource<ErlTerm>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCalls[tagRef] = new PendingCall(completion, node, self);

        try
        {
            // Monitor first so a dead or missing server produces a DOWN instead of hanging.
            await connection.SendAsync(new ErlTuple(
                new ErlInt((int)DistOp.MonitorP), self, target, tagRef), ct: ct).ConfigureAwait(false);

            var from = new ErlTuple(self, new ErlList([new ErlAtom("alias")], tagRef));
            var message = new ErlTuple(new ErlAtom("$gen_call"), from, request);

            var control = target is ErlPid pid
                ? new ErlTuple(new ErlInt((int)DistOp.SendSender), self, pid)
                : new ErlTuple(new ErlInt((int)DistOp.RegSend), self, Unused, target);
            await connection.SendAsync(control, message, ct).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (timeout is { } t) cts.CancelAfter(t);
            // ConfigureAwait on the registration too: this is a library, and disposing it must not
            // resume on a caller's synchronization context.
            var registration = cts.Token.Register(() => completion.TrySetCanceled(cts.Token));
            await using (registration.ConfigureAwait(false))
                return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"call to {target} on {node} timed out");
        }
        finally
        {
            _pendingCalls.TryRemove(tagRef, out _);
            TrySignal(node, new ErlTuple(
                new ErlInt((int)DistOp.DemonitorP), self, target, tagRef));
        }
    }

    /// <summary>Sends a <c>GenServer.cast/2</c> to a remote server.</summary>
    public Task CastAsync(string name, string node, ErlTerm request, CancellationToken ct = default) =>
        SendAsync(name, node, new ErlTuple(new ErlAtom("$gen_cast"), request), null, ct);

    /// <summary>
    /// Calls <c>Module:Function(Args)</c> on a remote node through its <c>rex</c> server, the same
    /// path <c>:rpc.call/4</c> has always used.
    /// </summary>
    public async Task<ErlTerm> RpcAsync(string node, string module, string function, ErlTerm[] args,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var groupLeader = NextPid();
        var request = new ErlTuple(
            new ErlAtom("call"), new ErlAtom(module), new ErlAtom(function), new ErlList(args), groupLeader);
        var result = await CallAsync("rex", node, request, timeout, ct).ConfigureAwait(false);
        if (result.IsTagged("badrpc", out var bad) && bad.Arity == 2)
            throw new ErlangRpcException(bad[1]);
        return result;
    }

    private void FailPendingCallsFor(string node)
    {
        foreach (var (key, pending) in _pendingCalls)
        {
            if (pending.Node != node) continue;
            _pendingCalls.TryRemove(key, out _);
            pending.Completion.TrySetException(new IOException($"connection to {node} was lost"));
        }
    }

    // ------------------------------------------------------- inbound dispatch

    private async Task HandleControlMessageAsync(DistConnection connection, DistMessage message)
    {
        if (message.Control is not ErlTuple { Arity: >= 1 } control || control[0] is not ErlInt op)
        {
            Log($"ignoring malformed control message {message.Control}");
            return;
        }

        // The opcode is a term the peer chose, so it can be a bignum. Casting it before the arity
        // guard below let an OverflowException reach the read loop and drop the connection — the
        // exact outcome that guard was added to prevent.
        if (op.AsIntOrNull is not { } code)
        {
            Log($"ignoring control message with an out-of-range operation {op.Value}");
            return;
        }

        var operation = (DistOp)code;

        // The dispatcher reads fields out of the tuple by position, so the arity has to be checked
        // before it does. A peer past the cookie is still not one that has to be well behaved, and
        // an index out of range here would propagate to the read loop and drop the connection.
        if (control.Arity < MinimumArity(operation))
        {
            Log($"ignoring {operation} control message with only {control.Arity} elements");
            return;
        }

        switch (operation)
        {
            case DistOp.Send or DistOp.SendTt:
                if (control[2] is ErlPid to && message.Payload is { } body) DeliverLocal(to, body, null);
                break;

            case DistOp.SendSender or DistOp.SendSenderTt:
                if (control[2] is ErlPid to2 && message.Payload is { } body2)
                    DeliverLocal(to2, body2, control[1] as ErlPid);
                break;

            case DistOp.RegSend or DistOp.RegSendTt:
                if (control[3] is ErlAtom name && message.Payload is { } body3 &&
                    _registered.TryGetValue(name.Name, out var target))
                    target.TryDeliver(body3, control[1] as ErlPid);
                break;

            case DistOp.AliasSend or DistOp.AliasSendTt:
                if (control[2] is ErlRef alias) HandleAliasSend(alias, message.Payload, control[1] as ErlPid);
                break;

            case DistOp.MonitorP:
                HandleMonitor(connection, control);
                break;

            case DistOp.DemonitorP:
                if (control[3] is ErlRef demonitorRef) RemoveIncomingMonitor(demonitorRef);
                break;

            case DistOp.MonitorPExit:
                if (control[3] is ErlRef downRef)
                    HandleDown(downRef, control[1], control.Arity > 4 ? control[4] : ErlAtom.NoProc);
                break;

            case DistOp.PayloadMonitorPExit:
                if (control[3] is ErlRef downRef2)
                    HandleDown(downRef2, control[1], message.Payload ?? ErlAtom.NoProc);
                break;

            case DistOp.Link:
                HandleLink(control);
                break;

            case DistOp.UnlinkOld:
                if (control[2] is ErlPid unlinkTarget && _mailboxes.TryGetValue(unlinkTarget, out var unlinkBox) &&
                    control[1] is ErlPid unlinkFrom)
                    unlinkBox.Links.TryRemove(unlinkFrom, out _);
                break;

            case DistOp.UnlinkId:
                HandleUnlinkId(connection, control);
                break;

            case DistOp.UnlinkIdAck:
                break; // nothing to undo: we drop the link as soon as we send UNLINK_ID

            case DistOp.Exit or DistOp.Exit2 or DistOp.ExitTt or DistOp.Exit2Tt:
                HandleExit(control[2], control[1],
                    control.Arity > 3 ? control[3] : ErlAtom.Normal);
                break;

            case DistOp.PayloadExit or DistOp.PayloadExit2 or DistOp.PayloadExitTt or DistOp.PayloadExit2Tt:
                HandleExit(control[2], control[1], message.Payload ?? ErlAtom.Normal);
                break;

            case DistOp.SpawnRequest or DistOp.SpawnRequestTt:
                // User code runs here, so keep it off the connection's read loop.
                _ = Task.Run(() => HandleSpawnRequestAsync(connection, control, message.Payload));
                break;

            case DistOp.SpawnReply or DistOp.SpawnReplyTt:
                break; // we never issue spawn requests

            case DistOp.GroupLeader or DistOp.NodeLink:
                break;

            default:
                Log($"unhandled control message {message.Control}");
                break;
        }
    }

    /// <summary>
    /// How many elements a control tuple must have before its fields can be read.
    /// <para>
    /// These come from the control message layouts in the distribution protocol; an operation this
    /// node does not act on needs only the opcode, since it is dropped anyway.
    /// </para>
    /// </summary>
    private static int MinimumArity(DistOp op) => op switch
    {
        DistOp.Send or DistOp.SendSender or DistOp.AliasSend => 3,
        DistOp.SendTt or DistOp.SendSenderTt or DistOp.AliasSendTt => 4,
        DistOp.RegSend => 4,
        DistOp.RegSendTt => 5,
        DistOp.Link or DistOp.UnlinkOld or DistOp.GroupLeader => 3,
        DistOp.Exit or DistOp.Exit2 => 4,
        DistOp.ExitTt or DistOp.Exit2Tt => 5,
        DistOp.PayloadExit or DistOp.PayloadExit2 => 3,
        DistOp.PayloadExitTt or DistOp.PayloadExit2Tt => 4,
        DistOp.MonitorP or DistOp.DemonitorP => 4,
        DistOp.MonitorPExit => 5,
        DistOp.PayloadMonitorPExit => 4,
        DistOp.UnlinkId or DistOp.UnlinkIdAck => 4,
        DistOp.SpawnRequest => 6,
        DistOp.SpawnRequestTt => 7,
        DistOp.SpawnReply => 5,
        DistOp.SpawnReplyTt => 6,
        _ => 1
    };

    private void HandleAliasSend(ErlRef alias, ErlTerm? payload, ErlPid? sender)
    {
        if (payload is null) return;

        // A reply to one of our own gen_server calls: {[alias|Ref], Reply}.
        if (_pendingCalls.TryGetValue(alias, out var pending) &&
            payload is ErlTuple { Arity: 2 } replyTuple)
        {
            _pendingCalls.TryRemove(alias, out _);
            pending.Completion.TrySetResult(replyTuple[1]);
            return;
        }

        if (_aliases.TryGetValue(alias, out var mailbox)) mailbox.TryDeliver(payload, sender);
    }

    private void HandleDown(ErlRef reference, ErlTerm monitored, ErlTerm reason)
    {
        if (_pendingCalls.TryRemove(reference, out var pending))
        {
            pending.Completion.TrySetException(new ErlangExitException(reason));
            return;
        }

        // A monitor a mailbox set itself: hand it the standard 'DOWN' tuple.
        foreach (var mailbox in _mailboxes.Values)
        {
            if (!mailbox.IncomingMonitors.ContainsKey(reference)) continue;
            mailbox.TryDeliver(new ErlTuple(
                new ErlAtom("DOWN"), reference, new ErlAtom("process"), monitored, reason), null);
            return;
        }
    }

    private void HandleMonitor(DistConnection connection, ErlTuple control)
    {
        if (control[1] is not ErlPid watcher || control[3] is not ErlRef reference) return;

        var target = control[2] switch
        {
            ErlAtom named => _registered.GetValueOrDefault(named.Name),
            ErlPid pid => _mailboxes.GetValueOrDefault(pid),
            _ => null
        };

        if (target is null || target.IsClosed)
        {
            // Exactly what makes GenServer.call to a missing name fail fast instead of hanging.
            connection.TrySendSignal(new ErlTuple(
                new ErlInt((int)DistOp.MonitorPExit), control[2], watcher, reference, ErlAtom.NoProc));
            return;
        }

        target.IncomingMonitors[reference] = watcher;
    }

    private void RemoveIncomingMonitor(ErlRef reference)
    {
        foreach (var mailbox in _mailboxes.Values)
            if (mailbox.IncomingMonitors.TryRemove(reference, out _))
                return;
    }

    private void HandleLink(ErlTuple control)
    {
        if (control[1] is not ErlPid from || control[2] is not ErlPid toPid) return;

        if (_mailboxes.TryGetValue(toPid, out var mailbox) && !mailbox.IsClosed)
            mailbox.Links[from] = 0;
        else
            TrySignal(from.Node, new ErlTuple(
                new ErlInt((int)DistOp.Exit), toPid, from, ErlAtom.NoProc));
    }

    private void HandleUnlinkId(DistConnection connection, ErlTuple control)
    {
        // {35, Id, FromPid, ToPid}; the ack echoes all three back.
        if (control.Arity < 4 || control[2] is not ErlPid from || control[3] is not ErlPid toPid) return;

        if (_mailboxes.TryGetValue(toPid, out var mailbox)) mailbox.Links.TryRemove(from, out _);

        connection.TrySendSignal(new ErlTuple(
            new ErlInt((int)DistOp.UnlinkIdAck), control[1], from, toPid));
    }

    private void HandleExit(ErlTerm toTerm, ErlTerm fromTerm, ErlTerm reason)
    {
        if (toTerm is not ErlPid toPid || !_mailboxes.TryGetValue(toPid, out var mailbox)) return;

        if (fromTerm is ErlPid fromPid) mailbox.Links.TryRemove(fromPid, out _);

        if (mailbox.TrapExit)
            mailbox.TryDeliver(new ErlTuple(new ErlAtom("EXIT"), fromTerm, reason), fromTerm as ErlPid);
        else if (!reason.IsAtom("normal"))
            CloseMailbox(mailbox, reason);
    }

    // -------------------------------------------------------- spawn requests

    private async Task HandleSpawnRequestAsync(DistConnection connection, ErlTuple control, ErlTerm? payload)
    {
        // {29, ReqId, From, GroupLeader, {Module, Function, Arity}, OptList}, payload = ArgList
        if (control.Arity < 6 || control[1] is not ErlRef reqId || control[2] is not ErlPid from) return;

        var options = control[5] as ErlList ?? ErlList.Empty;
        var wantsMonitor = options.Items.ToArray().Any(o => o.IsAtom("monitor"));
        var wantsLink = options.Items.ToArray().Any(o => o.IsAtom("link"));
        var replyMode = options.Items.ToArray()
            .Select(o => o.IsTagged("reply", out var t) && t.Arity == 2 ? t[1].ToString() : null)
            .FirstOrDefault(v => v is not null) ?? "yes";

        var mfa = control[4] as ErlTuple;
        var args = (payload as ErlList)?.ToArray() ?? [];

        async Task ReplyErrorAsync(string reason)
        {
            if (replyMode is "no" or "success_only") return;
            await connection.SendAsync(new ErlTuple(
                new ErlInt((int)DistOp.SpawnReply), reqId, from, new ErlInt(0), new ErlAtom(reason))).ConfigureAwait(false);
        }

        if (mfa is not { Arity: 3 } || mfa[0] is not ErlAtom module || mfa[1] is not ErlAtom function)
        {
            await ReplyErrorAsync("badarg").ConfigureAwait(false);
            return;
        }

        // rpc:call/4 and erpc:call/4 both arrive as erpc:execute_call/4 with the result reference first.
        var isErpcCall = module.Name == "erpc" && function.Name == "execute_call" && args.Length == 4;
        var isErpcCast = module.Name == "erpc" && function.Name == "execute_cast" && args.Length == 3;

        if (!isErpcCall && !isErpcCast)
        {
            await ReplyErrorAsync("notsup").ConfigureAwait(false);
            return;
        }

        if (RpcHandler is null)
        {
            await ReplyErrorAsync("notsup").ConfigureAwait(false);
            return;
        }

        var spawned = NextPid();
        var flags = (wantsLink ? 1 : 0) | (wantsMonitor ? 2 : 0);
        if (replyMode is not ("no" or "error_only"))
            await connection.SendAsync(new ErlTuple(
                new ErlInt((int)DistOp.SpawnReply), reqId, from, new ErlInt(flags), spawned)).ConfigureAwait(false);

        var resultRef = isErpcCall ? args[0] : null;
        var offset = isErpcCall ? 1 : 0;
        var callModule = (args[offset] as ErlAtom)?.Name ?? "";
        var callFunction = (args[offset + 1] as ErlAtom)?.Name ?? "";
        var callArgs = (args[offset + 2] as ErlList)?.ToArray() ?? [];

        ErlTerm exitReason;
        try
        {
            var value = await RpcHandler.InvokeAsync(callModule, callFunction, callArgs, _cts.Token)
                .ConfigureAwait(false);
            exitReason = resultRef is null
                ? ErlAtom.Normal
                : new ErlTuple(resultRef, new ErlAtom("return"), value);
        }
        catch (ErlangRpcException ex)
        {
            exitReason = resultRef is null
                ? ErlAtom.Normal
                : new ErlTuple(resultRef, new ErlAtom("error"), ex.Reason, ErlList.Empty);
        }
        catch (Exception ex)
        {
            var reason = new ErlTuple(new ErlAtom("csharp_error"), new ErlBinary(ex.Message));
            exitReason = resultRef is null
                ? ErlAtom.Normal
                : new ErlTuple(resultRef, new ErlAtom("error"), reason, ErlList.Empty);
        }

        // The spawned "process" exits with the result; erpc reads it off the monitor's DOWN signal.
        if (wantsMonitor)
            await connection.SendAsync(new ErlTuple(
                new ErlInt((int)DistOp.MonitorPExit), spawned, from, reqId, exitReason)).ConfigureAwait(false);
        else if (wantsLink)
            await connection.SendAsync(new ErlTuple(
                new ErlInt((int)DistOp.Exit), spawned, from, exitReason)).ConfigureAwait(false);
    }

    // -------------------------------------------------------------- utilities

    /// <summary>Allocates a fresh pid on this node.</summary>
    public ErlPid NextPid()
    {
        var id = Interlocked.Increment(ref _pidId);
        if (id == 0) Interlocked.Increment(ref _pidSerial);
        return new ErlPid(Name.Full, id, Volatile.Read(ref _pidSerial), Creation);
    }

    /// <summary>Allocates a fresh reference on this node.</summary>
    public ErlRef NextRef()
    {
        var n = (ulong)Interlocked.Increment(ref _refCounter);
        return new ErlRef(Name.Full, Creation,
            [(uint)(n & 0xFFFFFFFF), (uint)(n >> 32), (uint)Random.Shared.Next()]);
    }

    private static string? ReadCookieFile(out string path, out string? permissionWarning)
    {
        permissionWarning = null;
        path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".erlang.cookie");
        if (!File.Exists(path)) return null;

        // Erlang refuses a cookie file that is not owner-only. Whether that is worth refusing over
        // is the operator's call, so this reports rather than blocks -- and only where the POSIX
        // mode means anything, which is not Windows.
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != UnixFileMode.None)
                permissionWarning =
                    $"{path} is readable beyond its owner ({mode}); Erlang refuses a cookie file " +
                    "that is not owner-only, and the cookie is the whole of the authentication";
        }

        return File.ReadAllText(path).Trim();
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    /// <summary>Stops the node. Safe to call more than once, as the dispose contract requires.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();

        foreach (var connection in _connections.Values) await connection.DisposeAsync().ConfigureAwait(false);
        _connections.Clear();

        await _epmd.DisposeAsync().ConfigureAwait(false);
        _connectLock.Dispose();
        _cts.Dispose();
    }
}

/// <summary>Raised when a remote process a call depended on exited.</summary>
public sealed class ErlangExitException : Exception
{
    public ErlangExitException(ErlTerm reason) : base($"the remote process exited with reason {reason}") =>
        Reason = reason;

    /// <summary>The Erlang exit reason.</summary>
    public ErlTerm Reason { get; }
}

/// <summary>Answers <c>net_adm:ping/1</c> so <c>Node.ping/1</c> returns <c>:pong</c>.</summary>
internal sealed class NetKernelServer : ErlangGenServer
{
    public override ValueTask<ErlTerm?> HandleCallAsync(ErlTerm request, GenCallFrom from, CancellationToken ct) =>
        ValueTask.FromResult<ErlTerm?>(request.IsTagged("is_auth", out _) ? new ErlAtom("yes") : new ErlAtom("error"));
}
