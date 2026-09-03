using BeamSharp.Node;
using Xunit;

namespace BeamSharp.Tests;

/// <summary>
/// Waiting for the far side of a connection to catch up.
/// <para>
/// A dial finishes when the dialler's half of the handshake does: <c>ConnectAsync</c> attaches the
/// connection before it returns, so the dialler's own count is settled the moment it does. The peer
/// being dialled is not. The accept loop hands each inbound socket to a detached task, and that task
/// still has its half of the handshake to finish and its own connection to attach — which is why
/// <c>AttachConnectionAsync</c> says inbound handshakes complete on unsynchronised tasks.
/// </para>
/// <para>
/// So a test that dials and then reads the acceptor's connections is racing that gap rather than
/// testing anything, and will lose it on a loaded runner sooner or later.
/// </para>
/// </summary>
internal static class NodeWait
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits for <paramref name="node"/> to hold exactly <paramref name="expected"/> connections.
    /// <para>
    /// Exactly, rather than at least: a count that overshoots never matches, so a second connection
    /// built where one was meant to be still fails this — waiting for the far side to arrive must
    /// not become an excuse to stop counting what arrives.
    /// </para>
    /// </summary>
    public static void ForConnectionCount(ErlangNode node, int expected)
    {
        Assert.True(
            SpinWait.SpinUntil(() => node.ConnectedNodes.Count == expected, Limit),
            $"waited {Limit.TotalSeconds:0}s for {node.Name.Full} to hold {expected} connection(s); " +
            $"it settled on {node.ConnectedNodes.Count}: [{string.Join(", ", node.ConnectedNodes)}]");
    }
}
