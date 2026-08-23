using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace BeamSharp.Security;

/// <summary>
/// TLS for the distribution transport, the equivalent of running a node with
/// <c>-proto_dist inet_tls</c>.
/// <para>
/// TLS wraps the connection before any distribution byte is written, so the handshake, the cookie
/// challenge and every message afterwards travel inside it. EPMD is unaffected and stays in the
/// clear — it only ever learns a node's name and port.
/// </para>
/// </summary>
public sealed class ErlangTlsOptions
{
    /// <summary>
    /// This node's certificate and private key. Presented when accepting, and also when dialling,
    /// because distribution TLS is normally mutual.
    /// </summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>
    /// The certificate authorities a peer's certificate must chain to — the equivalent of Erlang's
    /// <c>cacertfile</c>. When set, the system trust store is not consulted, which is what you want
    /// for a private cluster CA.
    /// </summary>
    public X509Certificate2Collection TrustedRoots { get; } = [];

    /// <summary>
    /// Require the dialling peer to present a certificate. On by default: without it, TLS gives you
    /// an encrypted channel to anyone at all, and the cookie becomes the only thing keeping strangers
    /// out. Erlang calls this <c>fail_if_no_peer_cert</c>.
    /// </summary>
    public bool RequireClientCertificate { get; set; } = true;

    /// <summary>
    /// Also require the peer's certificate to name the host it was reached at.
    /// <para>
    /// Off by default, matching Erlang, because node certificates are usually issued per node with
    /// names that do not match the host in a node name. Leaving it off means any certificate your
    /// CA issued can act as any node, which is the normal model for a cluster whose members are
    /// mutually trusted peers. Turn it on if your CA also signs certificates you would not want
    /// impersonating a node.
    /// </para>
    /// </summary>
    public bool VerifyPeerHostname { get; set; }

    /// <summary>Protocol versions to allow. TLS 1.2 and 1.3 by default.</summary>
    public SslProtocols Protocols { get; set; } = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>Whether to check certificate revocation lists. Off by default, as Erlang has it.</summary>
    public bool CheckCertificateRevocation { get; set; }

    /// <summary>Replaces the default peer validation entirely. Use for pinning, or your own policy.</summary>
    public RemoteCertificateValidationCallback? ValidateRemoteCertificate { get; set; }

    /// <summary>Last word on the server-side options, applied after everything else.</summary>
    public Action<SslServerAuthenticationOptions>? ConfigureServer { get; set; }

    /// <summary>Last word on the client-side options, applied after everything else.</summary>
    public Action<SslClientAuthenticationOptions>? ConfigureClient { get; set; }

    /// <summary>
    /// Loads a certificate and key from PEM files, the format Erlang's <c>certfile</c> and
    /// <c>keyfile</c> use.
    /// </summary>
    /// <param name="certificatePath">PEM certificate, optionally with its chain.</param>
    /// <param name="privateKeyPath">PEM private key. Defaults to the certificate file.</param>
    /// <param name="caCertificatePath">PEM file of authorities to trust.</param>
    public static ErlangTlsOptions FromPemFiles(
        string certificatePath, string? privateKeyPath = null, string? caCertificatePath = null)
    {
        var options = new ErlangTlsOptions
        {
            Certificate = LoadPemCertificate(certificatePath, privateKeyPath ?? certificatePath)
        };

        if (caCertificatePath is not null)
            options.TrustedRoots.ImportFromPemFile(caCertificatePath);

        return options;
    }

    /// <summary>
    /// Reads a PEM certificate and key, then round-trips it through PKCS#12.
    /// <para>
    /// The round trip is not ceremony: a certificate built straight from PEM carries an ephemeral
    /// key that Windows refuses to use for server authentication. Exporting and reimporting gives
    /// it a key handle that works on every platform.
    /// </para>
    /// </summary>
    private static X509Certificate2 LoadPemCertificate(string certificatePath, string privateKeyPath)
    {
        using var fromPem = X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);
        return X509CertificateLoader.LoadPkcs12(fromPem.Export(X509ContentType.Pkcs12), password: null);
    }

    internal SslServerAuthenticationOptions BuildServerOptions()
    {
        var options = new SslServerAuthenticationOptions
        {
            ServerCertificate = Certificate,
            ClientCertificateRequired = RequireClientCertificate,
            EnabledSslProtocols = Protocols,
            CertificateRevocationCheckMode = CheckCertificateRevocation
                ? X509RevocationMode.Online
                : X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = ValidateRemoteCertificate ?? ValidatePeer
        };

        ConfigureServer?.Invoke(options);
        return options;
    }

    internal SslClientAuthenticationOptions BuildClientOptions(string targetHost)
    {
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            EnabledSslProtocols = Protocols,
            CertificateRevocationCheckMode = CheckCertificateRevocation
                ? X509RevocationMode.Online
                : X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = ValidateRemoteCertificate ?? ValidatePeer
        };

        if (Certificate is not null) options.ClientCertificates = [Certificate];

        ConfigureClient?.Invoke(options);
        return options;
    }

    /// <summary>
    /// Checks the peer chains to one of <see cref="TrustedRoots"/>, and only checks the name when
    /// <see cref="VerifyPeerHostname"/> asks for it.
    /// </summary>
    private bool ValidatePeer(object sender, X509Certificate? certificate, X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (certificate is null)
            return !RequireClientCertificate && sender is not SslStream { IsServer: false };

        var nameMismatch = errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch);
        if (nameMismatch && VerifyPeerHostname) return false;

        // Nothing to check against means falling back to the platform's own verdict.
        if (TrustedRoots.Count == 0)
            return errors == SslPolicyErrors.None ||
                   (!VerifyPeerHostname && errors == SslPolicyErrors.RemoteCertificateNameMismatch);

        using var verification = new X509Chain
        {
            ChainPolicy =
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = CheckCertificateRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck,
                VerificationFlags = X509VerificationFlags.NoFlag
            }
        };

        verification.ChainPolicy.CustomTrustStore.AddRange(TrustedRoots);
        if (chain is not null)
            foreach (var element in chain.ChainElements)
                verification.ChainPolicy.ExtraStore.Add(element.Certificate);

        return verification.Build(new X509Certificate2(certificate));
    }
}
