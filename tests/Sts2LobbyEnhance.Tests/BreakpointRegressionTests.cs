using Xunit;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Locks the central fact of this project: the shipped sts2_lan_connect 0.6.3 binary references exactly
/// six members that Slay the Spire 2 0.113 no longer provides, and they are the six the patch repairs.
///
/// Why this is a test rather than a note in a document: the whole patch design (which methods are dead,
/// which seams are usable, which fixes must exist) follows from this set. If a future official build
/// changes it, the patch has to be re-derived — silently continuing would ship a mod that patches
/// nothing, or worse, patches the wrong seam.
/// </summary>
public sealed class BreakpointRegressionTests
{
    /// <summary>
    /// The six breaks, in the order the project documents them. <c>Token</c> is the metadata token in the
    /// pinned official build; it is the stable handle for finding the same reference again.
    /// </summary>
    private static readonly (string Type, string Member, int Token)[] DocumentedBreaks =
    [
        ("InitialGameInfoMessage", "gameMode", 0x0A000E6A),
        ("ClientLobbyJoinRequestMessage", "maxAscensionUnlocked", 0x0A000E45),
        ("NetHostGameService", "DisconnectClient", 0x0A000CB3),
        ("NetHostGameService", "get_ConnectedPeers", 0x0A000CBE),
        ("RelicPickingResult", "GenerateRelicFight", 0x0A000B3C),
        ("SaveManager", "UpdateProgressWithRunData", 0x0A000F10)
    ];

    public static TheoryData<string, string, int> DocumentedBreakpoints
    {
        get
        {
            TheoryData<string, string, int> data = [];
            foreach ((string type, string member, int token) in DocumentedBreaks)
            {
                data.Add(type, member, token);
            }

            return data;
        }
    }

    [Fact]
    public void PinnedOfficialBuildIsTheOneUnderTest()
    {
        ScanReport report = OfficialModScan.Require();

        Assert.Equal(TestPaths.PinnedOfficialModSize, report.AssemblySize);
        Assert.Equal(TestPaths.PinnedOfficialModSha256, report.AssemblySha256);
    }

    [Fact]
    public void ScanCoversEveryMetadataTableThatCanCarryABrokenSignature()
    {
        ScanReport report = OfficialModScan.Require();

        // Pinned counts for the build identified by PinnedOfficialModSha256. They are not cosmetic: a
        // scanner that silently stops reading one table would still report "6 breaks" and pass every
        // other test in this file.
        Assert.Equal(4_263, report.MemberReferenceCount);
        Assert.Equal(7_455, report.FieldDefinitionCount);
        Assert.Equal(11_056, report.MethodDefinitionCount);
        Assert.Equal(22_774, report.CandidateCount);
    }

    [Fact]
    public void ExactlySixReferencesAreGenuinelyBroken()
    {
        ScanReport report = OfficialModScan.Require();

        string actual = string.Join(
            Environment.NewLine,
            report.GenuineBreaks.OrderBy(reference => reference.ShortName, StringComparer.Ordinal)
                .Select(reference => "  " + reference.Describe()));

        Assert.True(
            report.GenuineBreaks.Count == 6,
            $"Expected exactly 6 unresolvable references, found {report.GenuineBreaks.Count}. " +
            $"{OfficialModScan.Context(report)}{Environment.NewLine}{actual}");

        Assert.Empty(report.Unexplained);
    }

    [Theory]
    [MemberData(nameof(DocumentedBreakpoints))]
    public void EachDocumentedBreakIsReported(string typeName, string memberName, int token)
    {
        ScanReport report = OfficialModScan.Require();
        string expected = $"{typeName}::{memberName}";

        UnresolvedReference? match = report.GenuineBreaks
            .SingleOrDefault(reference => reference.ShortName == expected);

        Assert.True(
            match is not null,
            $"{expected} is not reported as a break. Reported: " +
            string.Join(", ", report.GenuineBreaks.Select(reference => reference.ShortName)) +
            $". {OfficialModScan.Context(report)}");

        Assert.Equal(token, match!.Token);
        Assert.Equal("MemberReference", match.Table);
        Assert.False(string.IsNullOrWhiteSpace(match.FailureMessage));
    }

    [Fact]
    public void NoBreakAppearsThatTheProjectDoesNotDocument()
    {
        ScanReport report = OfficialModScan.Require();

        HashSet<string> documented = DocumentedBreaks
            .Select(entry => $"{entry.Type}::{entry.Member}")
            .ToHashSet(StringComparer.Ordinal);

        string[] undocumented = report.GenuineBreaks
            .Select(reference => reference.ShortName)
            .Where(name => !documented.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            undocumented.Length == 0,
            "Undocumented break(s) found: " + string.Join(", ", undocumented) +
            $". The patch design must be re-derived before these can be ignored. {OfficialModScan.Context(report)}");
    }

    [Fact]
    public void GenericInstantiationNoiseIsFullyExplained()
    {
        ScanReport report = OfficialModScan.Require();

        // Module.ResolveMember cannot resolve a member of a generic instantiation (List<Player>::Add)
        // without being handed the type arguments, so those references always look broken. They are
        // filtered out only because each one was confirmed to name a member its declaring type really
        // declares; this test is what keeps that filter honest.
        Assert.Equal(360, report.GenericContextOnly.Count);
        Assert.All(
            report.GenericContextOnly,
            reference => Assert.Equal("MemberReference", reference.Table));

        Assert.True(
            report.Unexplained.Count == 0,
            "Probe could not explain " + report.Unexplained.Count + " unresolved reference(s): " +
            string.Join("; ", report.Unexplained.Select(reference => reference.Describe())) +
            $". {OfficialModScan.Context(report)}");

        Assert.Equal(6 + 360, report.UnresolvedCount);
    }

    [Fact]
    public void EveryBrokenDeclaringTypeStillExistsInTheGame()
    {
        ScanReport report = OfficialModScan.Require();
        string gameAssembly = Path.Combine(report.GameAssemblyDirectory, "sts2.dll");

        using GameMetadata game = GameMetadata.Open(gameAssembly);

        // A break whose declaring *type* is gone would mean the patch cannot work by reflection at all:
        // it would have to synthesise the type instead of re-binding to a changed member. All six are
        // signature changes on types that still exist.
        List<string> missing = [];
        foreach (UnresolvedReference reference in report.GenuineBreaks)
        {
            int lastDot = reference.DeclaringTypeDisplay.LastIndexOf('.');
            string @namespace = lastDot < 0 ? string.Empty : reference.DeclaringTypeDisplay[..lastDot];
            string typeName = lastDot < 0
                ? reference.DeclaringTypeDisplay
                : reference.DeclaringTypeDisplay[(lastDot + 1)..];

            if (game.FindType(@namespace, typeName) is null)
            {
                missing.Add(reference.DeclaringTypeDisplay);
            }
        }

        Assert.True(
            missing.Count == 0,
            "Declaring type(s) no longer exist in the game: " + string.Join(", ", missing) +
            $". {OfficialModScan.Context(report)}");
    }
}
