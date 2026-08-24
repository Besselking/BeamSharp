using BeamSharp.Node;
using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Tests;

/// <summary>
/// The two commonest first-run problems with this library are a cookie mismatch and the TLS versus
/// plaintext framing mismatch, and neither is diagnosable from "no connection to X" alone.
/// </summary>
public sealed class DiagnosticsTests
{
    [RequiresEpmdFact]
    public async Task A_failed_call_carries_why_the_connection_could_not_be_made()
    {
        await using var node = new ErlangNode($"bs_diag@{NodeName.LocalShortHost}",
            new ErlangNodeOptions { Cookie = "diagnostics-cookie" });
        await node.StartAsync();

        // Nothing of this name is registered with EPMD: one of the several distinct causes that
        // ConnectAsync reports identically, as false.
        var ex = await Assert.ThrowsAsync<IOException>(() =>
            node.CallAsync("nowhere", $"bs_diag_missing@{NodeName.LocalShortHost}",
                Erl.Atom("ping"), TimeSpan.FromSeconds(5)));

        Assert.NotNull(ex.InnerException);
        Assert.Contains("bs_diag_missing", ex.InnerException.Message, StringComparison.Ordinal);
    }

    [RequiresEpmdFact]
    public async Task The_startup_log_does_not_carry_any_of_the_cookie()
    {
        var lines = new List<string>();
        // Deliberately not a word. A cookie like "supersecret" shares its first three characters
        // with "the supplied options", which this logs by design, so the assertion below would trip
        // on the wrong thing.
        const string Cookie = "qzx7-vault-token-9f3a";

        await using var node = new ErlangNode($"bs_cookie@{NodeName.LocalShortHost}",
            new ErlangNodeOptions { Cookie = Cookie, Log = lines.Add });
        await node.StartAsync();

        // Asserting the line is there first, so this cannot pass by logging nothing at all.
        Assert.Contains(lines, line => line.Contains("listening on port", StringComparison.Ordinal));

        // Three characters is what an abbreviated cookie would leak, and the whole of a short one.
        Assert.All(lines, line =>
            Assert.DoesNotContain(Cookie[..3], line, StringComparison.Ordinal));
    }
}
