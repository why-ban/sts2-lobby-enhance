using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Xunit;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Counts how many game-dependent cases actually reached their assertions in this process.
///
/// The suite skips a case when the game DLL or the official mod DLL is missing, and a skipped case still
/// exits 0, so a green run on a machine without the game proves nothing. `GameDependentCoverageTests`
/// turns that into a failure by comparing this count against
/// `ExpectedGameDependentTestCount`.
///
/// The name comes from the stack: every skip guard calls `Record()` from a helper, so
/// `[CallerMemberName]` would record the helper's name and collapse whole classes into one entry.
/// </summary>
internal static class TestRunLedger
{
    /// <summary>
    /// How many game-dependent cases the suite executes when every input is present. Hard-coded on purpose:
    /// adding or removing such a case has to be a deliberate edit here, and the coverage test fails when the
    /// count drifts in either direction.
    /// </summary>
    internal const int ExpectedGameDependentTestCount = 30;

    private static readonly ConcurrentDictionary<string, byte> Executed = new(StringComparer.Ordinal);

    /// <summary>Number of distinct game-dependent cases that got past their skip guards.</summary>
    internal static int ExecutedCount => Executed.Count;

    internal static IReadOnlyList<string> ExecutedNames =>
        Executed.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>Called by a skip guard once the guard has been passed.</summary>
    internal static void Record()
    {
        string? name = CurrentCaseName();
        if (name != null)
        {
            Executed.TryAdd(name, 0);
        }
    }

    private static string? CurrentCaseName()
    {
        // xUnit reports the case (a theory row counts separately), which is the granularity the threshold is
        // expressed in. The stack is only a fallback for a run without an ambient test context.
        string? display = Xunit.TestContext.Current.Test?.TestDisplayName;
        if (!string.IsNullOrEmpty(display))
        {
            return display;
        }

        return CurrentTestName();
    }

    private static string? CurrentTestName()
    {
        // Skip Record itself and its caller so a helper frame is never mistaken for the case.
        foreach (StackFrame frame in new StackTrace(1, fNeedFileInfo: false).GetFrames())
        {
            MethodBase? method = frame.GetMethod();
            if (method == null)
            {
                continue;
            }

            if (method.GetCustomAttributes(typeof(FactAttribute), inherit: true).Length == 0 &&
                method.GetCustomAttributes(typeof(TheoryAttribute), inherit: true).Length == 0)
            {
                continue;
            }

            return method.DeclaringType?.Name + "." + method.Name;
        }

        return null;
    }
}
