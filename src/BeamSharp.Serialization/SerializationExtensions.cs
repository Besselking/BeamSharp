using BeamSharp.Node;
using BeamSharp.Terms;

namespace BeamSharp.Serialization;

/// <summary>
/// Typed wrappers over the node API, so ordinary C# objects can be sent and received without
/// building terms by hand.
/// </summary>
public static class SerializationExtensions
{
    /// <summary>Sends an object to a remote pid, serialized with the default options.</summary>
    public static Task SendAsync<T>(this ErlangNode node, ErlPid to, T message,
        ErlSerializerOptions? options = null, ErlPid? from = null, CancellationToken ct = default) =>
        node.SendAsync(to, ErlSerializer.Serialize(message, options), from, ct);

    /// <summary>Sends an object to a registered name on another node.</summary>
    public static Task SendAsync<T>(this ErlangNode node, string name, string peerNode, T message,
        ErlSerializerOptions? options = null, ErlPid? from = null, CancellationToken ct = default) =>
        node.SendAsync(name, peerNode, ErlSerializer.Serialize(message, options), from, ct);

    /// <summary>Calls a remote <c>gen_server</c>, serializing the request and deserializing the reply.</summary>
    public static async Task<TReply> CallAsync<TRequest, TReply>(this ErlangNode node, string name,
        string peerNode, TRequest request, ErlSerializerOptions? options = null, TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var reply = await node.CallAsync(name, peerNode, ErlSerializer.Serialize(request, options), timeout, ct)
            .ConfigureAwait(false);
        return ErlSerializer.Deserialize<TReply>(reply, options);
    }

    /// <summary>Sends a <c>GenServer.cast/2</c> carrying a serialized object.</summary>
    public static Task CastAsync<T>(this ErlangNode node, string name, string peerNode, T request,
        ErlSerializerOptions? options = null, CancellationToken ct = default) =>
        node.CastAsync(name, peerNode, ErlSerializer.Serialize(request, options), ct);

    /// <summary>Answers a <c>gen_server</c> call with a serialized object.</summary>
    public static Task ReplyAsync<T>(this ErlangNode node, GenCallFrom from, T reply,
        ErlSerializerOptions? options = null, ErlPid? self = null, CancellationToken ct = default) =>
        node.ReplyAsync(from, ErlSerializer.Serialize(reply, options), self, ct);

    /// <summary>Reads the message body as <typeparamref name="T"/>.</summary>
    public static T Deserialize<T>(this ErlMessage message, ErlSerializerOptions? options = null) =>
        ErlSerializer.Deserialize<T>(message.Term, options);

    /// <summary>Reads the message body as <typeparamref name="T"/>, or reports that it does not fit.</summary>
    public static bool TryDeserialize<T>(this ErlMessage message, out T value,
        ErlSerializerOptions? options = null) =>
        ErlSerializer.TryDeserialize(message.Term, out value, options);

    /// <summary>Reads a term as <typeparamref name="T"/>.</summary>
    public static T Deserialize<T>(this ErlTerm term, ErlSerializerOptions? options = null) =>
        ErlSerializer.Deserialize<T>(term, options);
}
