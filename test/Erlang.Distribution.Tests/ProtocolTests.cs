using Erlang.Distribution.Node;
using Erlang.Distribution.Protocol;
using Erlang.Distribution.Terms;
using Xunit;

namespace Erlang.Distribution.Tests;

public class ProtocolTests
{
    [Theory]
    [InlineData("digest_12345", 12345u, "testcookie")]
    [InlineData("digest_4294967295", 4294967295u, "secret")]
    public void Challenge_digest_matches_otp_gen_digest(string fixture, uint challenge, string cookie)
    {
        // dist_util:gen_digest/2 is md5(cookie ++ integer_to_list(Challenge)), with the challenge
        // treated as unsigned — getting that wrong fails every handshake with a bad-cookie error.
        Assert.Equal(Fixtures.Get(fixture), Handshake.Digest(challenge, cookie));
    }

    [Fact]
    public void Advertised_flags_cover_everything_otp_26_makes_mandatory()
    {
        Assert.Equal(DistributionFlags.None,
            DistributionFlags.Mandatory & ~DistributionFlags.Default);
    }

    [Fact]
    public void Advertised_flags_leave_out_the_atom_cache_and_fragmentation()
    {
        // Both are optimisations we deliberately decline, because declining keeps every frame
        // a self-contained pass-through message.
        Assert.False(DistributionFlags.Default.HasFlag(DistributionFlags.DistHdrAtomCache));
        Assert.False(DistributionFlags.Default.HasFlag(DistributionFlags.Fragments));
    }

    [Fact]
    public void Mandatory_flag_values_match_dist_hrl()
    {
        // Cross-checked against kernel/include/dist.hrl in OTP 29.
        Assert.Equal(0x03070F94UL | (0x04UL << 32), (ulong)DistributionFlags.Mandatory);
    }

    [Theory]
    [InlineData("foo@bar", "foo", "bar", true)]
    [InlineData("foo@bar.example.com", "foo", "bar.example.com", false)]
    public void Parses_node_names(string input, string alive, string host, bool isShort)
    {
        var name = NodeName.Parse(input);

        Assert.Equal(alive, name.Alive);
        Assert.Equal(host, name.Host);
        Assert.Equal(isShort, name.IsShort);
        Assert.Equal(input, name.Full);
    }

    [Theory]
    [InlineData("noatsign")]
    [InlineData("@host")]
    [InlineData("alive@")]
    public void Rejects_malformed_node_names(string input)
    {
        Assert.Throws<ArgumentException>(() => NodeName.Parse(input));
    }

    [Fact]
    public void Reads_the_alias_out_of_an_otp24_style_call_tag()
    {
        // gen:call/4 sends {'$gen_call', {Self, [alias | Mref]}, Request} and expects the reply
        // to come back addressed to the alias rather than to the pid.
        var alias = new ErlRef("caller@host", 7, [1, 2, 3]);
        var from = new GenCallFrom(
            new ErlPid("caller@host", 1, 0, 7),
            Erl.ImproperList([Erl.Atom("alias")], alias));

        Assert.Equal(alias, from.Alias);
    }

    [Fact]
    public void Reads_the_alias_out_of_a_send_request_style_tag()
    {
        var alias = new ErlRef("caller@host", 7, [4, 5, 6]);
        var from = new GenCallFrom(
            new ErlPid("caller@host", 1, 0, 7),
            Erl.List(Erl.ImproperList([Erl.Atom("alias")], alias), Erl.Atom("label")));

        Assert.Equal(alias, from.Alias);
    }

    [Fact]
    public void Falls_back_to_the_caller_pid_for_a_plain_tag()
    {
        var from = new GenCallFrom(
            new ErlPid("caller@host", 1, 0, 7),
            new ErlRef("caller@host", 7, [1]));

        Assert.Null(from.Alias);
    }

    [Fact]
    public void Control_message_opcodes_match_the_erts_distribution_protocol()
    {
        Assert.Equal(2, (int)DistOp.Send);
        Assert.Equal(6, (int)DistOp.RegSend);
        Assert.Equal(19, (int)DistOp.MonitorP);
        Assert.Equal(21, (int)DistOp.MonitorPExit);
        Assert.Equal(22, (int)DistOp.SendSender);
        Assert.Equal(29, (int)DistOp.SpawnRequest);
        Assert.Equal(31, (int)DistOp.SpawnReply);
        Assert.Equal(33, (int)DistOp.AliasSend);
        Assert.Equal(35, (int)DistOp.UnlinkId);
    }
}
