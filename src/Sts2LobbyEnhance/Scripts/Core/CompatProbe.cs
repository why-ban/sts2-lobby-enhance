using System.Reflection;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2LobbyEnhance;

/// <summary>
/// Detects, by reflection over the running game assembly only, which of the six 0.113 changes are in
/// effect. No side effects: nothing is JITted, nothing is patched, so probing is safe even when the
/// patch turns out not to be needed.
/// </summary>
internal static class CompatProbe
{
    internal static CompatSnapshot Capture()
    {
        return new CompatSnapshot(
            InitialGameInfoLostGameMode: !Reflect.HasField(typeof(InitialGameInfoMessage), "gameMode"),
            JoinRequestLostMaxAscensionUnlocked: !Reflect.HasField(typeof(ClientLobbyJoinRequestMessage), "maxAscensionUnlocked"),
            DisconnectClientGainedParameter: DisconnectClientHasDebugReasonParameter(),
            ConnectedPeersChangedType: ConnectedPeersIsNoLongerList(),
            GenerateRelicFightGainedParameter: GenerateRelicFightNeedsRelicIndex(),
            UpdateProgressGainedReturnValue: UpdateProgressReturnsResult());
    }

    // 0.111: void DisconnectClient(ulong, NetError, bool)
    // 0.113: void DisconnectClient(ulong, NetError, bool, string? debugReason)
    // An added *optional* parameter still changes the metadata signature, so the 0.111 call site's
    // MemberRef stops resolving even though the source still compiles.
    private static bool DisconnectClientHasDebugReasonParameter()
    {
        MethodInfo[] overloads = typeof(NetHostGameService)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => string.Equals(method.Name, "DisconnectClient", StringComparison.Ordinal))
            .ToArray();

        return overloads.Length > 0
               && overloads.Any(method => method.GetParameters().Length == 4)
               && overloads.All(method => method.GetParameters().Length != 3);
    }

    // 0.111: List<NetClientData> ConnectedPeers
    // 0.113: IReadOnlyList<NetClientData> ConnectedPeers
    // Return type is part of the signature, so the old MemberRef fails.
    private static bool ConnectedPeersIsNoLongerList()
    {
        System.Reflection.PropertyInfo? property = typeof(NetHostGameService)
            .GetProperty("ConnectedPeers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (property == null)
        {
            return false;
        }

        Type type = property.PropertyType;
        return !(type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>));
    }

    // 0.111: GenerateRelicFight(List<Player>, RelicModel, Func<RelicPickingFightMove>)
    // 0.113: GenerateRelicFight(List<Player>, RelicModel, int relicIndex, Func<RelicPickingFightMove>)
    private static bool GenerateRelicFightNeedsRelicIndex()
    {
        MethodInfo[] overloads = typeof(RelicPickingResult)
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => string.Equals(method.Name, "GenerateRelicFight", StringComparison.Ordinal))
            .ToArray();

        return overloads.Length > 0
               && overloads.Any(method => method.GetParameters().Length == 4)
               && overloads.All(method => method.GetParameters().Length != 3);
    }

    // 0.111: void UpdateProgressWithRunData(SerializableRun, bool)
    // 0.113: PostRunProgressUpdateResult UpdateProgressWithRunData(SerializableRun, bool)
    private static bool UpdateProgressReturnsResult()
    {
        MethodInfo? method = typeof(SaveManager)
            .GetMethod("UpdateProgressWithRunData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        return method != null && method.ReturnType != typeof(void);
    }
}
