using Xunit;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Verifies what 0.113 changed directly against the <b>game</b> assembly: the six signature changes 0.6.3
/// could not resolve, and the message shapes they moved.
///
/// This file no longer documents a requirement of the patch: upstream 0.6.4 adapts all six itself, so the
/// repairs that needed these seams are gone. What it still does is name the game-side shape of each member,
/// so a future game build that reshapes one of them is diagnosed in one place instead of surfacing as a
/// mysterious reflection failure. <see cref="BreakpointRegressionTests"/> is the test that keeps the
/// official build honest (0.6.4 has no unresolvable references left).
/// </summary>
public sealed class GameApiShapeTests
{
    private const string LobbyMessages = "MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby";
    private const string Multiplayer = "MegaCrit.Sts2.Core.Multiplayer";
    private const string RelicPicking = "MegaCrit.Sts2.Core.Entities.TreasureRelicPicking";
    private const string Saves = "MegaCrit.Sts2.Core.Saves";
    private const string EntitiesSave = "MegaCrit.Sts2.Core.Entities.Save";

    [Fact]
    public void InitialGameInfoMessageLostItsGameModeField()
    {
        using GameMetadata game = OpenGame();

        Assert.NotNull(game.FindType(LobbyMessages, "InitialGameInfoMessage"));
        Assert.False(
            game.HasField(LobbyMessages, "InitialGameInfoMessage", "gameMode"),
            "InitialGameInfoMessage.gameMode is expected to be gone in 0.113.");

        // What the message carries instead. Pinned so a future reshuffle is noticed here first.
        Assert.Equal(
            ["sessionState", "connectionFailureReason"],
            game.GetFieldNames(LobbyMessages, "InitialGameInfoMessage"));
    }

    [Fact]
    public void ClientLobbyJoinRequestMessageSplitItsAscensionField()
    {
        using GameMetadata game = OpenGame();

        Assert.NotNull(game.FindType(LobbyMessages, "ClientLobbyJoinRequestMessage"));
        Assert.False(
            game.HasField(LobbyMessages, "ClientLobbyJoinRequestMessage", "maxAscensionUnlocked"),
            "ClientLobbyJoinRequestMessage.maxAscensionUnlocked is expected to be gone in 0.113.");

        // 0.113 replaced the single ascension field with a per-mode pair.
        Assert.True(game.HasField(LobbyMessages, "ClientLobbyJoinRequestMessage", "maxStandardAscension"));
        Assert.True(game.HasField(LobbyMessages, "ClientLobbyJoinRequestMessage", "maxBlitzAscension"));
    }

    [Fact]
    public void NetHostGameServiceDisconnectClientGainedADebugReason()
    {
        using GameMetadata game = OpenGame();

        IReadOnlyList<int> parameterCounts = game.GetMethodParameterCounts(
            Multiplayer,
            "NetHostGameService",
            "DisconnectClient");

        Assert.Equal([4], parameterCounts);
        Assert.DoesNotContain(3, parameterCounts);
    }

    [Fact]
    public void NetHostGameServiceConnectedPeersBecameReadOnly()
    {
        using GameMetadata game = OpenGame();

        string? propertyType = game.GetPropertyType(Multiplayer, "NetHostGameService", "ConnectedPeers");

        Assert.NotNull(propertyType);
        Assert.StartsWith("System.Collections.Generic.IReadOnlyList`1<", propertyType, StringComparison.Ordinal);
        Assert.Contains("MegaCrit.Sts2.Core.Entities.Multiplayer.NetClientData", propertyType, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Collections.Generic.List`1<", propertyType, StringComparison.Ordinal);
    }

    [Fact]
    public void RelicPickingResultGenerateRelicFightNeedsARelicIndex()
    {
        using GameMetadata game = OpenGame();

        IReadOnlyList<int> parameterCounts = game.GetMethodParameterCounts(
            RelicPicking,
            "RelicPickingResult",
            "GenerateRelicFight");

        Assert.Equal([4], parameterCounts);
        Assert.DoesNotContain(3, parameterCounts);
    }

    [Fact]
    public void SaveManagerUpdateProgressWithRunDataReturnsAResult()
    {
        using GameMetadata game = OpenGame();

        IReadOnlyList<string> returnTypes = game.GetMethodReturnTypes(
            Saves,
            "SaveManager",
            "UpdateProgressWithRunData");

        string returnType = Assert.Single(returnTypes);
        Assert.NotEqual("void", returnType);
        Assert.Equal($"{EntitiesSave}.PostRunProgressUpdateResult", returnType);
    }

    [Fact]
    public void JoinResponseMessageCarriesGameModeAndIsCustom()
    {
        using GameMetadata game = OpenGame();

        // The 0.113 shape of this message: the two mode fields sit between dailyTime and ascension. Pinned
        // as a game fact — a rename or reorder here changes what a hand-written body may emit.
        Assert.True(game.HasField(LobbyMessages, "ClientLobbyJoinResponseMessage", "gameMode"));
        Assert.True(game.HasField(LobbyMessages, "ClientLobbyJoinResponseMessage", "isCustom"));

        Assert.Equal(
            ["playersInLobby", "dailyTime", "gameMode", "isCustom", "ascension", "seed", "modifiers"],
            game.GetFieldNames(LobbyMessages, "ClientLobbyJoinResponseMessage"));
    }

    [Fact]
    public void PostRunProgressUpdateResultTypeExists()
    {
        using GameMetadata game = OpenGame();

        Assert.NotNull(game.FindType(EntitiesSave, "PostRunProgressUpdateResult"));
    }

    [Fact]
    public void JoinFlowBeginTakesASceneTree()
    {
        using GameMetadata game = OpenGame();

        string fullName = Assert.Single(game.FindTypesBySimpleName("JoinFlow"));
        Assert.Equal("MegaCrit.Sts2.Core.Multiplayer.Game.JoinFlow", fullName);

        // The game's own join entry point: one SceneTree parameter and a result, which is what any takeover
        // of the host mod's join flow would have to call.
        Assert.Equal([1], game.GetMethodParameterCounts(
            "MegaCrit.Sts2.Core.Multiplayer.Game",
            "JoinFlow",
            "Begin"));
    }

    /// <summary>Opens the game assembly, skipping the calling test when it is not installed.</summary>
    private static GameMetadata OpenGame()
    {
        string? gameAssembly = TestPaths.GameAssembly;
        if (gameAssembly is null || !File.Exists(gameAssembly))
        {
            Assert.Skip($"sts2.dll not found. {TestPaths.GameDirectoryHint}");
        }

        // Reached only when the assembly was actually found: TestRunLedger counts these 9 cases.
        TestRunLedger.Record();
        return GameMetadata.Open(gameAssembly!);
    }
}
