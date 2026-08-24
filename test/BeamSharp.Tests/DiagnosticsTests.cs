using BeamSharp.Node;
using BeamSharp.Terms;
using Xunit;

namespace BeamSharp.Tests;

/// <summary>
/// The two commonest first-run problems with this library are a cookie mismatch and the TLS versus
/// plaintext framing mismatch. Both used to arrive as a bare "no connection to X".
/// </summary>
public sealed class DiagnosticsTests
{
    [RequiresEpmdFact]
    public async Task A_failed_call_carries_why_the_connection_could_not_be_made()
    {
        await using var node = new ErlangNode($"bs_diag@{NodeName.LocalShortHost}",
            new ErlangNodeOptions { Cookie = "diagnostics-cookie" });
        await node.StartAsync();

        // Nothing of this name is registered with EPMD, which is one of the several distinct causes
        // that all used to collapse into false and then into a bare IOException.
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
        // Deliberately not a word: the first attempt used "supersecret", whose "sup" turns up
        // inside "the supplied options" and failed for the wrong reason.
        const string Cookie = "qzx7-vault-token-9f3a";

        await using var node = new ErlangNode($"bs_cookie@{NodeName.LocalShortHost}",
            new ErlangNodeOptions { Cookie = Cookie, Log = lines.Add });
        await node.StartAsync();

        // Asserting the line is there first, so this cannot pass by logging nothing at all.
        Assert.Contains(lines, line => line.Contains("listening on port", StringComparison.Ordinal));

        // The old message printed the cookie's first three characters, or the whole of a shorter one.
        Assert.All(lines, line =>
            Assert.DoesNotContain(Cookie[..3], line, StringComparison.Ordinal));
    }
}
