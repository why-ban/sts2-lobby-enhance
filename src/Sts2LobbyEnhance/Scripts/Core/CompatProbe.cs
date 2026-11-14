using System.Reflection;

namespace Sts2LobbyEnhance;

    /// <summary>
    /// Reads the six 0.113 shape changes out of the running game, one observation at a time
    ///
    /// Each observation runs in its own try/catch and degrades to <c>false</c> on its own, and types are
    /// looked up by name: a <c>typeof</c> on a renamed type throws inside the probe itself, which would abort
    /// <see cref="Entry.Execute"/> before a single repair was considered
    /// </summary>
internal static class CompatProbe
{
    private const string GameAssemblyName = "sts2";

    private const string InitialGameInfoTypeName =
        "MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby.InitialGameInfoMessage";

    private const string JoinRequestTypeName =
        "MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby.ClientLobbyJoinRequestMessage";

    private const string NetHostTypeName = "MegaCrit.Sts2.Core.Multiplayer.NetHostGameService";

    private const string RelicPickingTypeName =
        "MegaCrit.Sts2.Core.Entities.TreasureRelicPicking.RelicPickingResult";

    private const string SaveManagerTypeName = "MegaCrit.Sts2.Core.Saves.SaveManager";

    private static readonly string[] ProbedTypeNames =
    {
        InitialGameInfoTypeName,
        JoinRequestTypeName,
        NetHostTypeName,
        RelicPickingTypeName,
        SaveManagerTypeName
    };

    private static readonly HashSet<string> Reported = new(StringComparer.Ordinal);

    private static readonly object ReportedGate = new();

    /// <summary>
    /// Where a "I could not answer this" line goes, and <see cref="Entry"/> installs its own logger here
    ///
    /// A hook rather than a direct <c>Entry.Info</c> call because Core is compiled on its own by
    /// <c>tools/probe-check</c>, which has no <c>Entry</c>
    /// </summary>
    internal static Action<string>? Logger { get; set; }

    /// <summary>
    /// Never throws: a probe that cannot answer reports itself once and answers <c>false</c>, which means
    /// "this change is not visible", never "the mod is broken"
    /// </summary>
    internal static CompatSnapshot Capture()
    {
        try
        {
            Assembly? game = GameAssembly();

            return new CompatSnapshot(
                InitialGameInfoLostGameMode: Observe(
                    "InitialGameInfoMessage.gameMode", game, InitialGameInfoLostGameMode),
                JoinRequestLostMaxAscensionUnlocked: Observe(
                    "ClientLobbyJoinRequestMessage.maxAscensionUnlocked", game, JoinRequestLostMaxAscensionUnlocked),
                DisconnectClientGainedParameter: Observe(
                    "NetHostGameService.DisconnectClient", game, DisconnectClientGainedParameter),
                ConnectedPeersChangedType: Observe(
                    "NetHostGameService.ConnectedPeers", game, ConnectedPeersChangedType),
                GenerateRelicFightGainedParameter: Observe(
                    "RelicPickingResult.GenerateRelicFight", game, GenerateRelicFightGainedParameter),
                UpdateProgressGainedReturnValue: Observe(
                    "SaveManager.UpdateProgressWithRunData", game, UpdateProgressGainedReturnValue));
        }
        catch (Exception exception)
        {
            // Belt and braces: a snapshot must always exist, even if this class has a bug of its own.
            ReportOnce(
                "snapshot",
                $"probing failed ({exception.GetType().Name}: {exception.Message}); every flag is false");
            return default;
        }
    }

    // 0.111: InitialGameInfoMessage has a gameMode field.
    // 0.113: the field is gone; the mode travels in the join response instead.
    private static bool? InitialGameInfoLostGameMode(Assembly game)
    {
        Type? type = Reflect.TryType(game, InitialGameInfoTypeName);
        return type == null ? null : Reflect.TryField(type, "gameMode") == null;
    }

    // 0.111: ClientLobbyJoinRequestMessage.maxAscensionUnlocked.
    // 0.113: removed; the host derives it from the save.
    private static bool? JoinRequestLostMaxAscensionUnlocked(Assembly game)
    {
        Type? type = Reflect.TryType(game, JoinRequestTypeName);
        return type == null ? null : Reflect.TryField(type, "maxAscensionUnlocked") == null;
    }

    // 0.111: void DisconnectClient(ulong, NetError, bool)
    // 0.113: void DisconnectClient(ulong, NetError, bool, string? debugReason)
    // An added *optional* parameter still changes the metadata signature, so the 0.111 call site's
    // MemberRef stops resolving even though the source still compiles.
    private static bool? DisconnectClientGainedParameter(Assembly game)
    {
        Type? type = Reflect.TryType(game, NetHostTypeName);
        if (type == null)
        {
            return null;
        }

        MethodInfo[] overloads = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => string.Equals(method.Name, "DisconnectClient", StringComparison.Ordinal))
            .ToArray();

        if (overloads.Length == 0)
        {
            // The type exists but no such method: renamed or moved, so this probe cannot answer.
            return null;
        }

        return overloads.Any(method => method.GetParameters().Length == 4)
               && overloads.All(method => method.GetParameters().Length != 3);
    }

    // 0.111: List<NetClientData> ConnectedPeers
    // 0.113: IReadOnlyList<NetClientData> ConnectedPeers
    // Return type is part of the signature, so the old MemberRef fails.
    private static bool? ConnectedPeersChangedType(Assembly game)
    {
        Type? type = Reflect.TryType(game, NetHostTypeName);
        PropertyInfo? property = type == null ? null : Reflect.TryProperty(type, "ConnectedPeers");
        if (property == null)
        {
            return null;
        }

        Type propertyType = property.PropertyType;
        return !(propertyType.IsGenericType && propertyType.GetGenericTypeDefinition() == typeof(List<>));
    }

    // 0.111: GenerateRelicFight(List<Player>, RelicModel, Func<RelicPickingFightMove>)
    // 0.113: GenerateRelicFight(List<Player>, RelicModel, int relicIndex, Func<RelicPickingFightMove>)
    // Diagnostic only: no repair consumes this flag, but it is still reported so the log stays complete.
    private static bool? GenerateRelicFightGainedParameter(Assembly game)
    {
        Type? type = Reflect.TryType(game, RelicPickingTypeName);
        if (type == null)
        {
            return null;
        }

        MethodInfo[] overloads = type
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => string.Equals(method.Name, "GenerateRelicFight", StringComparison.Ordinal))
            .ToArray();

        if (overloads.Length == 0)
        {
            return null;
        }

        return overloads.Any(method => method.GetParameters().Length == 4)
               && overloads.All(method => method.GetParameters().Length != 3);
    }

    // 0.111: void UpdateProgressWithRunData(SerializableRun, bool)
    // 0.113: PostRunProgressUpdateResult UpdateProgressWithRunData(SerializableRun, bool)
    private static bool? UpdateProgressGainedReturnValue(Assembly game)
    {
        Type? type = Reflect.TryType(game, SaveManagerTypeName);
        MethodInfo? method = type == null ? null : Reflect.TryMethodByName(type, "UpdateProgressWithRunData");
        return method == null ? null : method.ReturnType != typeof(void);
    }

    /// <summary>
    /// <c>null</c> from the probe means "this build does not let me answer", which counts as "the change is
    /// not present" so one renamed member cannot take the other five observations with it
    /// </summary>
    private static bool Observe(string label, Assembly? game, Func<Assembly, bool?> probe)
    {
        if (game == null)
        {
            ReportOnce(label, $"game assembly '{GameAssemblyName}' is not loaded; assuming the change is absent");
            return false;
        }

        try
        {
            bool? answer = probe(game);
            if (answer.HasValue)
            {
                return answer.Value;
            }

            ReportOnce(label, "its type or member was not found; assuming the change is absent");
            return false;
        }
        catch (Exception exception)
        {
            ReportOnce(
                label,
                $"probing threw {exception.GetType().Name}: {exception.Message}; assuming the change is absent");
            return false;
        }
    }

    /// <summary>
    /// Name first (<c>sts2</c>), then an explicit load, then a scan for the probed types: a harness that loads
    /// only the mod DLL has not pulled the game in yet, and a renamed game assembly degrades into "type not
    /// found" per probe instead of "assembly not found" for all six
    /// </summary>
    private static Assembly? GameAssembly()
    {
        Assembly? named = Reflect.FindAssembly(GameAssemblyName);
        if (named != null)
        {
            return named;
        }

        try
        {
            named = Assembly.Load(new AssemblyName(GameAssemblyName));
        }
        catch (Exception)
        {
        }

        if (named != null)
        {
            return named;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || assembly == typeof(CompatProbe).Assembly)
            {
                continue;
            }

            foreach (string typeName in ProbedTypeNames)
            {
                if (Reflect.TryType(assembly, typeName) != null)
                {
                    return assembly;
                }
            }
        }

        return null;
    }

    private static void ReportOnce(string label, string detail)
    {
        lock (ReportedGate)
        {
            if (!Reported.Add(label))
            {
                return;
            }
        }

        try
        {
            Logger?.Invoke($"compat probe '{label}': {detail}");
        }
        catch (Exception)
        {
            // Reporting must never fail a probe: the logger reaches the game's logging stack, and a host
            // with no game assembly loaded fails to even JIT that call, so the line is simply lost
        }
    }
}
