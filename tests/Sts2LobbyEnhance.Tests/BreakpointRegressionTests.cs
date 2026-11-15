using Xunit;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Locks the fact this project rests on since upstream <b>0.6.4</b>: the shipped sts2_lan_connect 0.6.4
/// binary has <b>zero</b> member references that Slay the Spire 2 0.113 cannot resolve.
///
/// History, kept as the regression: 0.6.3 referenced exactly six members 0.113 no longer provides —
/// <c>InitialGameInfoMessage.gameMode</c>, <c>ClientLobbyJoinRequestMessage.maxAscensionUnlocked</c>,
/// <c>NetHostGameService.DisconnectClient</c>, <c>NetHostGameService.ConnectedPeers</c>,
/// <c>RelicPickingResult.GenerateRelicFight</c> and <c>SaveManager.UpdateProgressWithRunData</c> (tokens 0x0A000E6A,
/// 0x0A000E45, 0x0A000CB3, 0x0A000CBE, 0x0A000B3C, 0x0A000F10 in that build). Those six were the reason this
/// mod existed; 0.6.4 adapts all of them itself (reflection over both shapes), so the six repairs were
/// deleted in 2.0.0. The theory below fails if any of the six ever turns back into an unresolvable
/// reference: that would mean the upstream compatibility layer regressed and the matching fix has to
/// come back.
/// </summary>
public sealed class BreakpointRegressionTests
{
    /// <summary>
    /// The six breaks 0.6.3 had, in the order the project documented them. They must all resolve in 0.6.4,
    /// so this list is now an "absent" set rather than an "expected" one.
    /// </summary>
    private static readonly (string Type, string Member)[] HistoricalBreaks =
    [
        ("InitialGameInfoMessage", "gameMode"),
        ("ClientLobbyJoinRequestMessage", "maxAscensionUnlocked"),
        ("NetHostGameService", "DisconnectClient"),
        ("NetHostGameService", "get_ConnectedPeers"),
        ("RelicPickingResult", "GenerateRelicFight"),
        ("SaveManager", "UpdateProgressWithRunData")
    ];

    public static TheoryData<string, string> HistoricalBreakpoints
    {
        get
        {
            TheoryData<string, string> data = [];
            foreach ((string type, string member) in HistoricalBreaks)
            {
                data.Add(type, member);
            }

            return data;
        }
    }

    [Fact]
    public void PinnedOfficialBuildIsTheOneUnderTest()
    {
        ScanReport report = OfficialModScan.Require064();

        Assert.Equal(TestPaths.PinnedOfficial064Size, report.AssemblySize);
        Assert.Equal(TestPaths.PinnedOfficial064Sha256, report.AssemblySha256);
    }

    [Fact]
    public void ScanCoversEveryMetadataTableThatCanCarryABrokenSignature()
    {
        ScanReport report = OfficialModScan.Require064();

        // Not pinned to exact counts: 0.6.4 is a different build and the numbers change every release. The
        // floors are what a partially working scanner cannot clear — a table it stopped reading reports 0.
        // (0.6.3, for scale: 4,263 member references / 7,455 field definitions / 11,056 method definitions.)
        Assert.True(report.MemberReferenceCount > 1_000, $"Only {report.MemberReferenceCount} member references were scanned.");
        Assert.True(report.FieldDefinitionCount > 1_000, $"Only {report.FieldDefinitionCount} field definitions were scanned.");
        Assert.True(report.MethodDefinitionCount > 1_000, $"Only {report.MethodDefinitionCount} method definitions were scanned.");
        Assert.Equal(
            report.MemberReferenceCount + report.FieldDefinitionCount + report.MethodDefinitionCount,
            report.CandidateCount);
    }

    [Fact]
    public void NoReferenceIsUnresolvableAgainstTheGame()
    {
        ScanReport report = OfficialModScan.Require064();

        string actual = string.Join(
            Environment.NewLine,
            report.GenuineBreaks.OrderBy(reference => reference.ShortName, StringComparer.Ordinal)
                .Select(reference => "  " + reference.Describe()));

        Assert.True(
            report.GenuineBreaks.Count == 0,
            $"0.6.4 is supposed to adapt every 0.113 API change itself, but {report.GenuineBreaks.Count} " +
            $"reference(s) still cannot be resolved: {Environment.NewLine}{actual}" +
            $"{Environment.NewLine}{OfficialModScan.Context(report)}");

        Assert.Empty(report.Unexplained);
    }

    [Theory]
    [MemberData(nameof(HistoricalBreakpoints))]
    public void EachHistoricalBreakIsResolvedNow(string typeName, string memberName)
    {
        ScanReport report = OfficialModScan.Require064();
        string historical = $"{typeName}::{memberName}";

        UnresolvedReference? match = report.GenuineBreaks
            .SingleOrDefault(reference => reference.ShortName == historical);

        Assert.True(
            match is null,
            $"{historical} is unresolvable again in the pinned 0.6.4 build " +
            $"({match?.TokenText} {match?.FailureKind}: {match?.FailureMessage}). " +
            "Upstream regressed the 0.113 compatibility layer for this member, so the repair that used to " +
            $"cover it has to come back. {OfficialModScan.Context(report)}");
    }

    [Fact]
    public void NoBreakAppearsThatTheProjectDoesNotDocument()
    {
        ScanReport report = OfficialModScan.Require064();

        HashSet<string> documented = HistoricalBreaks
            .Select(entry => $"{entry.Type}::{entry.Member}")
            .ToHashSet(StringComparer.Ordinal);

        string[] undocumented = report.GenuineBreaks
            .Select(reference => reference.ShortName)
            .Where(name => !documented.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            undocumented.Length == 0,
            "Unresolvable reference(s) the project has never seen: " + string.Join(", ", undocumented) +
            $". Re-derive the design before ignoring them. {OfficialModScan.Context(report)}");
    }

    [Fact]
    public void EveryUnresolvedReferenceIsExplainedOrAbsent()
    {
        ScanReport report = OfficialModScan.Require064();

        // Generic instantiations (List<Player>::Add) always look unresolvable to Module.ResolveMember, which
        // cannot be handed the type arguments. They are tolerated only in that one table and are never
        // allowed to hide a genuine break — the split is what makes "0 breaks" trustworthy.
        Assert.All(
            report.GenericContextOnly,
            reference => Assert.Equal("MemberReference", reference.Table));

        Assert.True(
            report.Unexplained.Count == 0,
            "Probe could not explain " + report.Unexplained.Count + " unresolved reference(s): " +
            string.Join("; ", report.Unexplained.Select(reference => reference.Describe())) +
            $". {OfficialModScan.Context(report)}");

        Assert.Equal(
            report.GenuineBreaks.Count + report.GenericContextOnly.Count + report.Unexplained.Count,
            report.UnresolvedCount);
    }

    [Fact]
    public void EveryHistoricallyBrokenTypeStillExistsInTheGame()
    {
        ScanReport report = OfficialModScan.Require064();
        string gameAssembly = Path.Combine(report.GameAssemblyDirectory, "sts2.dll");

        using GameMetadata game = GameMetadata.Open(gameAssembly);

        // The six breaks were signature changes on types 0.113 still has. If one of those types ever
        // disappears, 0.6.4's reflection-based adaptation cannot work either and this is no longer a
        // signature problem.
        (string Namespace, string Type)[] declaringTypes =
        [
            ("MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby", "InitialGameInfoMessage"),
            ("MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby", "ClientLobbyJoinRequestMessage"),
            ("MegaCrit.Sts2.Core.Multiplayer", "NetHostGameService"),
            ("MegaCrit.Sts2.Core.Entities.TreasureRelicPicking", "RelicPickingResult"),
            ("MegaCrit.Sts2.Core.Saves", "SaveManager")
        ];

        List<string> missing = [];
        foreach ((string @namespace, string typeName) in declaringTypes)
        {
            if (game.FindType(@namespace, typeName) is null)
            {
                missing.Add(@namespace + "." + typeName);
            }
        }

        Assert.True(
            missing.Count == 0,
            "Declaring type(s) no longer exist in the game: " + string.Join(", ", missing) +
            $". {OfficialModScan.Context(report)}");
    }
}
