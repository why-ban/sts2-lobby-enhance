using Xunit;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Runs the metadata scan at most once per test process and turns "an input is missing" into a skip
/// with an actionable reason instead of a failure.
/// </summary>
internal static class OfficialModScan
{
    private static readonly Lazy<Attempt> LazyAttempt = new(Run, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The scan of the official 0.6.3 DLL. Skips the calling test when the inputs are absent.</summary>
    internal static ScanReport Require()
    {
        Attempt attempt = LazyAttempt.Value;
        if (attempt.Report is null)
        {
            Assert.Skip(attempt.SkipReason);
        }

        // Reached only when the scan really ran: this is what TestRunLedger counts, so "the test passed"
        // and "the test executed its assertions" are the same statement.
        TestRunLedger.Record();
        return attempt.Report;
    }

    /// <summary>Short description of what was scanned, appended to assertion messages.</summary>
    internal static string Context(ScanReport report) =>
        $"scanned {report.AssemblyPath} ({report.AssemblySize} B, sha256 {report.AssemblySha256}) " +
        $"against {report.GameAssemblyDirectory}";

    private static Attempt Run()
    {
        if (TestPaths.GameDirectory is null)
        {
            return Attempt.Skipped($"Game data directory not found. {TestPaths.GameDirectoryHint}");
        }

        if (TestPaths.OfficialModDll is null)
        {
            return Attempt.Skipped($"Official sts2_lan_connect DLL not found. {TestPaths.OfficialModHint}");
        }

        try
        {
            return Attempt.Scanned(BreakpointScanner.Scan(TestPaths.OfficialModDll, TestPaths.GameDirectory));
        }
        catch (ScanInputMissingException exception)
        {
            return Attempt.Skipped(exception.Message);
        }
        catch (BadImageFormatException exception)
        {
            return Attempt.Skipped($"Could not read the scanned assembly: {exception.Message}");
        }
    }

    private sealed record Attempt(ScanReport? Report, string SkipReason)
    {
        internal static Attempt Scanned(ScanReport report) => new(report, string.Empty);

        internal static Attempt Skipped(string reason) => new(null, reason);
    }
}
