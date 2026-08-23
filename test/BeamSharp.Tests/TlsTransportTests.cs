using System.Security.Cryptography.X509Certificates;
using BeamSharp.Node;
using BeamSharp.Security;
using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Tests;

/// <summary>
/// Two real nodes over a real TLS connection. The negative cases matter most: encryption a peer can
/// decline, or a certificate nobody checks, would leave the suite just as green.
/// </summary>
public sealed class TlsTransportTests : IDisposable
{
    private const string Cookie = "tls-test-cookie";

    private readonly X509Certificate2 _ca = TestCertificates.CreateAuthority("BeamSharp Test CA");
    private readonly X509Certificate2 _rogueCa = TestCertificates.CreateAuthority("Some Other CA");
    private readonly List<ErlangNode> _nodes = [];

    private ErlangTlsOptions Trusting(X509Certificate2 authority, string commonName)
    {
        var options = new ErlangTlsOptions { Certificate = TestCertificates.Issue(authority, commonName) };
        options.TrustedRoots.Add(_ca);
        return options;
    }

    private async Task<ErlangNode> StartAsync(string alive, ErlangTlsOptions? tls)
    {
        var node = new ErlangNode($"{alive}@{NodeName.LocalShortHost}", new ErlangNodeOptions
        {
            Cookie = Cookie,
            Tls = tls
        });

        _nodes.Add(node);
        await node.StartAsync();
        return node;
    }

    [RequiresEpmdFact]
    public async Task Two_nodes_trusting_the_same_authority_can_talk()
    {
        var server = await StartAsync("bs_tls_server", Trusting(_ca, "server"));
        server.RegisterGenServer("echo", new EchoServer());

        var client = await StartAsync("bs_tls_client", Trusting(_ca, "client"));

        var reply = await client.CallAsync("echo", server.Name.Full, Erl.String("over tls"),
            TimeSpan.FromSeconds(10));

        Assert.Equal(Erl.String("over tls"), reply);
        Assert.True(server.UsesTls);
        Assert.Contains(client.Name.Full, server.ConnectedNodes);
    }

    [RequiresEpmdFact]
    public async Task A_certificate_from_an_untrusted_authority_is_refused()
    {
        var server = await StartAsync("bs_tls_strict", Trusting(_ca, "server"));
        server.RegisterGenServer("echo", new EchoServer());

        // Correct cookie, valid certificate — but issued by an authority the server does not trust.
        var impostorTls = new ErlangTlsOptions { Certificate = TestCertificates.Issue(_rogueCa, "impostor") };
        impostorTls.TrustedRoots.Add(_ca);

        var impostor = await StartAsync("bs_tls_impostor", impostorTls);

        Assert.False(await impostor.ConnectAsync(server.Name.Full));
        Assert.Empty(server.ConnectedNodes);
    }

    [RequiresEpmdFact]
    public async Task A_server_certificate_from_an_untrusted_authority_is_refused_by_the_client()
    {
        // The other direction: the dialling node must check who it reached, not only be checked.
        var rogueTls = new ErlangTlsOptions { Certificate = TestCertificates.Issue(_rogueCa, "rogue") };
        rogueTls.TrustedRoots.Add(_rogueCa);

        var rogueServer = await StartAsync("bs_tls_rogue", rogueTls);
        var client = await StartAsync("bs_tls_careful", Trusting(_ca, "client"));

        Assert.False(await client.ConnectAsync(rogueServer.Name.Full));
    }

    [RequiresEpmdFact]
    public async Task A_plaintext_node_cannot_reach_a_tls_node()
    {
        // Encryption a peer can simply decline is not encryption.
        var server = await StartAsync("bs_tls_only", Trusting(_ca, "server"));
        var plaintext = await StartAsync("bs_plain_dialer", tls: null);

        Assert.False(await plaintext.ConnectAsync(server.Name.Full));
        Assert.Empty(server.ConnectedNodes);
    }

    [RequiresEpmdFact]
    public async Task A_tls_node_cannot_reach_a_plaintext_node()
    {
        var server = await StartAsync("bs_plain_only", tls: null);
        var client = await StartAsync("bs_tls_dialer", Trusting(_ca, "client"));

        Assert.False(await client.ConnectAsync(server.Name.Full));
    }

    [Fact]
    public void Client_certificates_are_required_by_default()
    {
        // Erlang calls this fail_if_no_peer_cert. Without it TLS gives an encrypted channel to
        // anyone, leaving the cookie as the only thing keeping strangers out.
        var options = new ErlangTlsOptions();

        Assert.True(options.RequireClientCertificate);
        Assert.False(options.VerifyPeerHostname);
    }

    public void Dispose()
    {
        foreach (var node in _nodes) node.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _ca.Dispose();
        _rogueCa.Dispose();
    }

    private sealed class EchoServer : ErlangGenServer
    {
        public override ValueTask<ErlTerm?> HandleCallAsync(
            ErlTerm request, GenCallFrom from, CancellationToken ct) =>
            ValueTask.FromResult<ErlTerm?>(request);
    }
}
