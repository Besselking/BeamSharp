using System.Net.Sockets;
using BeamSharp.Epmd;
using BeamSharp.Node;
using BeamSharp.Protocol;
using Xunit;

namespace BeamSharp.Tests;

/// <summary>
/// Hiddenness lives in the handshake, not in the EPMD registration: a node is hidden exactly when
/// it leaves <c>DFLAG_PUBLISHED</c> out of the flags it offers. A peer that reads the flag expects
/// the node to answer <c>global:sync/0</c>, and hangs waiting when it does not.
/// </summary>
public sealed class NodeVisibilityTests
{
    private const string Cookie = "visibility-cookie";

    [Fact]
    public void The_default_flags_publish_nothing_on_their_own()
    {
        // Visibility is the only thing that may set this bit, so it cannot also ride in on Default.
        Assert.False(DistributionFlags.Default.HasFlag(DistributionFlags.Published));
    }

    [RequiresEpmdFact]
    public async Task A_hidden_node_does_not_offer_Published()
    {
        Assert.False((await HandshakeWith(NodeVisibility.Hidden, "bs_vis_hidden"))
            .HasFlag(DistributionFlags.Published));
    }

    [RequiresEpmdFact]
    public async Task A_visible_node_offers_Published()
    {
        Assert.True((await HandshakeWith(NodeVisibility.Visible, "bs_vis_shown"))
            .HasFlag(DistributionFlags.Published));
    }

    /// <summary>
    /// Runs a real handshake against a node and returns what the two sides settled on. The result
    /// is the intersection of both offers, so dialling with <c>Published</c> set makes its presence
    /// in the result mean the node offered it too.
    /// </summary>
    private static async Task<DistributionFlags> HandshakeWith(NodeVisibility visibility, string aliveName)
    {
        await using var node = new ErlangNode($"{aliveName}@{NodeName.LocalShortHost}",
            new ErlangNodeOptions { Cookie = Cookie, Visibility = visibility });
        await node.StartAsync();

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", node.Port);
        await using var stream = client.GetStream();

        var result = await Handshake.ConnectAsync(
            stream,
            $"bs_vis_probe@{NodeName.LocalShortHost}",
            localCreation: 1,
            DistributionFlags.Default | DistributionFlags.Published,
            node.Name.Full,
            Cookie);

        return result.Flags;
    }
}
