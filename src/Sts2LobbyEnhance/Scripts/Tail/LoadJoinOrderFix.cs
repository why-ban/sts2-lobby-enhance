using System.Collections;
using System.Reflection;
using HarmonyLib;

namespace Sts2LobbyEnhance;

    /// <summary>
    /// Load-join member order. <c>RestoreLoadJoinPlayers</c> (dec-official LanConnectTailMessageRuntime.cs:1275)
    /// requires <c>message.playersAlreadyConnected</c> to equal the Tail roster position by position, but the
    /// two lists are sorted by different keys: the host fills the list in *join* order, while the roster
    /// carriers are stamped with each player's index in <c>SerializableRun.Players</c> - the save order - by
    /// <c>BuildLoadJoinCarriers</c> (:1282-1294), and <c>LanConnectRosterCodec.Encode</c> sorts carriers by
    /// (RealSlotId, PlayerId). Same set, different order, so <c>SequenceEqual</c> fails and every client whose
    /// group did not join in save order is rejected with "Loaded-lobby vanilla membership disagrees with the
    /// Tail roster."
    ///
    /// The repair re-sorts the incoming list into save order before that comparison runs. It edits the list
    /// object in place: the callee reads the list reference its own copy of the struct already holds, while a
    /// field written into the boxed message would never reach it. The restored order is unaffected either way -
    /// upstream replaces the field with the save-order list at :1208 - so only the positional check changes.
    /// </summary>
internal sealed class LoadJoinOrderFix : ICompatFix
{
    private const string RuntimeTypeName = "Sts2LanConnect.Scripts.LanConnectTailMessageRuntime";
    private const string BindingTypeName = "Binding";
    private const string SnapshotTypeName = "Sts2LanConnect.Scripts.LanConnectRosterSnapshot";
    private const string RestoreMethodName = "RestoreLoadJoin";
    private const string MessageTypeName =
        "MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby.ClientLoadJoinResponseMessage";
    private const string RunTypeName = "MegaCrit.Sts2.Core.Saves.SerializableRun";
    private const string SavedPlayerTypeName = "MegaCrit.Sts2.Core.Saves.Runs.SerializablePlayer";
    private const string LobbyPlayerTypeName = "MegaCrit.Sts2.Core.Entities.Multiplayer.LoadRunLobbyPlayer";
    private const string ListTypeName = "System.Collections.Generic.List`1";

    private const BindingFlags AnyMember =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Every anchor this repair needs, resolved once in <see cref="Applies"/> before the prefix is installed.
    /// The five member lookups are what the prefix reads at runtime, so a miss there is a miss of the repair
    /// itself, not of an optional extra.
    /// </summary>
    internal sealed class Shape
    {
        internal MethodInfo RestoreLoadJoin = null!;

        /// <summary>The list the callee compares against, held by the message struct.</summary>
        internal FieldInfo AlreadyConnected = null!;

        internal FieldInfo SerializableRun = null!;

        /// <summary>A property in 0.113 and a field in other builds, so both are accepted.</summary>
        internal MemberInfo SavedPlayers = null!;

        internal MemberInfo SavedPlayerNetId = null!;

        internal MemberInfo ConnectedPlayerId = null!;
    }

    /// <summary>Written by <see cref="Applies"/> and read by <see cref="Apply"/>; never re-resolved after a patch.</summary>
    internal static Shape? _shape;

    private static int _warned;

    /// <summary>
    /// The last reorder this repair performed, or null when none was ever necessary. Diagnostic only: it tells
    /// a rejection that still reaches the user apart from "the prefix never ran".
    /// </summary>
    public static string? LastReorder { get; private set; }

    public string Id => "load-join-order";

    public FixGroup Group => FixGroup.LoadOrder;

    /// <summary>
    /// Not gated on a <see cref="CompatSnapshot"/> flag: this is an ordering disagreement between the host mod
    /// and itself, independent of what 0.113 changed. Whether the repair is possible is answered by the anchors.
    /// </summary>
    public bool Applies(in CompatSnapshot snapshot, out string reason)
    {
        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = "host mod '" + Entry.HostModAssemblyName + "' is not loaded";
            return false;
        }

        if (!TryCapture(host, out Shape shape, out string missing))
        {
            reason = "missing: " + missing;
            return false;
        }

        _shape = shape;
        reason = string.Empty;
        return true;
    }

    public void Apply(Harmony harmony, in CompatSnapshot snapshot)
    {
        Shape shape = _shape
            ?? throw new InvalidOperationException(
                "load-join-order: Apply ran without a captured shape; Applies must be consulted first.");

        harmony.Patch(
            shape.RestoreLoadJoin,
            prefix: new HarmonyMethod(typeof(LoadJoinOrderFix), nameof(RestoreLoadJoinPrefix))
            {
                priority = Priority.First
            });

        Entry.Info(
            "load-join-order: playersAlreadyConnected is re-sorted into save order before the membership " +
            "comparison runs, so a group that joined out of save order can load the run.");
    }

    /// <summary>
    /// Re-sorts the message's already-connected list into <c>SerializableRun.Players</c> order, in place.
    ///
    /// Never throws: this runs inside the host mod's load-join path, where an exception is reported as a
    /// protocol failure and costs the client its connection. On any surprise the list is left untouched and the
    /// original method decides, which is exactly the behaviour this repair is not allowed to make worse.
    /// </summary>
    public static bool RestoreLoadJoinPrefix(object boxedMessage)
    {
        try
        {
            Reorder(boxedMessage);
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                Entry.Warn(
                    "load-join-order: reordering playersAlreadyConnected failed, so the join response is " +
                    $"passed through unchanged: {exception.GetType().Name}: {exception.Message}");
            }
        }

        // Always let the original run: it performs the restore and the roster accept.
        return true;
    }

    private static void Reorder(object? boxedMessage)
    {
        Shape? shape = _shape;
        if (shape == null || boxedMessage == null)
        {
            return;
        }

        if (shape.AlreadyConnected.GetValue(boxedMessage) is not IList connected || connected.Count == 0)
        {
            return;
        }

        object? run = shape.SerializableRun.GetValue(boxedMessage);
        if (run == null || Read(shape.SavedPlayers, run) is not IList saved || saved.Count == 0)
        {
            return;
        }

        int count = connected.Count;
        var slots = new int[count];
        var players = new object?[count];

        for (int index = 0; index < count; index++)
        {
            object? player = connected[index];
            if (player == null)
            {
                return;
            }

            ulong playerId = (ulong)Read(shape.ConnectedPlayerId, player)!;
            int slot = IndexOfSavedPlayer(shape, saved, playerId);

            // Absent from SerializableRun.Players means the two sides really disagree. Reordering would replace
            // a genuine inconsistency with a different error, so leave the list alone and let upstream report it.
            if (slot < 0)
            {
                return;
            }

            slots[index] = slot;
            players[index] = player;
        }

        // Stable, like the codec's (RealSlotId, PlayerId) sort: equal keys keep their relative order.
        int[] target = Enumerable.Range(0, count).OrderBy(index => slots[index]).ToArray();
        int firstDifference = -1;
        for (int index = 0; index < count; index++)
        {
            if (target[index] != index)
            {
                firstDifference = index;
                break;
            }
        }

        if (firstDifference < 0)
        {
            return;
        }

        var before = new List<ulong>(count);
        var after = new List<ulong>(count);
        for (int index = 0; index < count; index++)
        {
            before.Add((ulong)Read(shape.ConnectedPlayerId, connected[index]!)!);
            after.Add((ulong)Read(shape.ConnectedPlayerId, players[target[index]]!)!);
        }

        for (int index = 0; index < count; index++)
        {
            connected[index] = players[target[index]];
        }

        LastReorder =
            $"join order -> save order: {count} player(s), first difference at position {firstDifference + 1} " +
            $"(1-based); [{string.Join(",", before)}] -> [{string.Join(",", after)}]";
        Entry.Info("load-join-order: " + LastReorder);
    }

    private static int IndexOfSavedPlayer(Shape shape, IList saved, ulong playerId)
    {
        for (int index = 0; index < saved.Count; index++)
        {
            object? candidate = saved[index];
            if (candidate != null && (ulong)Read(shape.SavedPlayerNetId, candidate)! == playerId)
            {
                return index;
            }
        }

        return -1;
    }

    private static object? Read(MemberInfo member, object target) => member switch
    {
        FieldInfo field => field.GetValue(target),
        PropertyInfo property => property.GetValue(target),
        _ => throw new InvalidOperationException(
            "load-join-order: " + member.DeclaringType?.FullName + "." + member.Name + " is not a field or property.")
    };

    /// <summary>
    /// Resolves every anchor without throwing, collecting all misses into one reason string. Types are matched
    /// by name because a <c>typeof</c> on a renamed game type fails this whole assembly to load, which trades
    /// one anchor for the entire mod.
    /// </summary>
    private static bool TryCapture(Assembly host, out Shape shape, out string missing)
    {
        shape = new Shape();
        var problems = new List<string>();

        try
        {
            Capture(host, shape, problems);
        }
        catch (Exception exception)
        {
            problems.Add(exception.GetType().Name + ": " + exception.Message);
        }

        missing = string.Join(", ", problems);
        return problems.Count == 0;
    }

    private static void Capture(Assembly host, Shape shape, List<string> problems)
    {
        Type? runtimeType = ReflectScope.HostType(host, RuntimeTypeName, problems);
        Type? snapshotType = ReflectScope.HostType(host, SnapshotTypeName, problems);
        Type? messageType = ReflectScope.GameType(MessageTypeName, problems);
        Type? runType = ReflectScope.GameType(RunTypeName, problems);
        Type? savedPlayerType = ReflectScope.GameType(SavedPlayerTypeName, problems);
        Type? lobbyPlayerType = ReflectScope.GameType(LobbyPlayerTypeName, problems);

        Type? bindingType = runtimeType?.GetNestedType(BindingTypeName, AnyMember);
        if (runtimeType != null && bindingType == null)
        {
            problems.Add($"{RuntimeTypeName}.{BindingTypeName}");
        }

        if (runtimeType != null && messageType != null && snapshotType != null && bindingType != null)
        {
            MethodInfo? restore = ReflectScope.Exact(
                runtimeType,
                RestoreMethodName,
                requireStatic: true,
                parameterTypeNames: new[] { typeof(object), messageType, snapshotType, bindingType }
                    .Select(type => type.FullName ?? type.Name)
                    .ToArray(),
                returnTypeName: "System.Void",
                problems);

            if (restore != null)
            {
                // The prefix binds its argument by name, so a rename would silently hand it a different one.
                ReflectScope.RequireParameterNames(problems, restore, "boxedMessage");
                shape.RestoreLoadJoin = restore;
            }
        }

        if (messageType != null)
        {
            FieldInfo? connected = ReflectScope.Field(problems, messageType, "playersAlreadyConnected");
            if (connected != null
                && CheckListType(problems, connected.FieldType, lobbyPlayerType, LobbyPlayerTypeName,
                    $"{MessageTypeName}.playersAlreadyConnected"))
            {
                shape.AlreadyConnected = connected;
            }

            FieldInfo? run = ReflectScope.Field(problems, messageType, "serializableRun");
            if (run != null)
            {
                if (runType != null && run.FieldType != runType)
                {
                    problems.Add(
                        $"{MessageTypeName}.serializableRun is {run.FieldType.FullName}, expected {RunTypeName}");
                }
                else
                {
                    shape.SerializableRun = run;
                }
            }
        }

        if (runType != null && savedPlayerType != null)
        {
            MemberInfo? players = TryMember(runType, "Players", out Type? playersType);
            if (players == null || playersType == null)
            {
                problems.Add($"{RunTypeName}.Players");
            }
            else if (CheckListType(problems, playersType, savedPlayerType, SavedPlayerTypeName, $"{RunTypeName}.Players"))
            {
                shape.SavedPlayers = players;
            }
        }

        if (savedPlayerType != null)
        {
            MemberInfo? netId = RequireMember(problems, savedPlayerType, "NetId");
            if (netId != null)
            {
                shape.SavedPlayerNetId = netId;
            }
        }

        if (lobbyPlayerType != null)
        {
            MemberInfo? id = RequireMember(problems, lobbyPlayerType, "id");
            if (id != null)
            {
                shape.ConnectedPlayerId = id;
            }
        }
    }

    /// <summary>A public <c>ulong</c> member by that name, field or property.</summary>
    private static MemberInfo? RequireMember(List<string> problems, Type owner, string name)
    {
        MemberInfo? member = TryMember(owner, name, out Type? memberType);
        if (member == null || memberType == null)
        {
            problems.Add($"{owner.FullName}.{name}");
            return null;
        }

        if (memberType != typeof(ulong))
        {
            problems.Add($"{owner.FullName}.{name} is {memberType.FullName}, expected System.UInt64");
            return null;
        }

        return member;
    }

    private static bool CheckListType(
        List<string> problems,
        Type memberType,
        Type? elementType,
        string elementTypeName,
        string label)
    {
        if (!memberType.IsGenericType || memberType.GetGenericTypeDefinition().FullName != ListTypeName)
        {
            problems.Add($"{label} is {memberType.FullName}, expected List<{elementTypeName}>");
            return false;
        }

        Type actual = memberType.GetGenericArguments()[0];
        if (elementType != null && actual != elementType)
        {
            problems.Add($"{label} is List<{actual.FullName}>, expected List<{elementTypeName}>");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Property first, field second, and only one problem line when both miss: the two probes are run with a
    /// throwaway problem list so an absent property does not also report an absent field.
    /// </summary>
    private static MemberInfo? TryMember(Type owner, string name, out Type? memberType)
    {
        var scratch = new List<string>();

        PropertyInfo? property = ReflectScope.Property(scratch, owner, name);
        if (property != null)
        {
            memberType = property.PropertyType;
            return property;
        }

        FieldInfo? field = ReflectScope.Field(scratch, owner, name);
        memberType = field?.FieldType;
        return field;
    }
}
