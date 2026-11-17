using System.Collections;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;

namespace Sts2LobbyEnhance;

    /// <summary>
    /// Re-baselines the client's Tail roster authority when the host publishes a roster that no longer contains
    /// somebody the client still remembers
    ///
    /// Upstream has no handling for a player leaving. <c>LanConnectTailMessageRuntime.CommitHostSnapshot</c> has
    /// exactly four call sites and not one of them subtracts a member, so after a kick the client keeps the roster
    /// it accepted at join time while the host keeps publishing the smaller one. Every later roster-bearing
    /// message then fails inside <c>LanConnectRosterAuthorityState.Accept</c>, and the failure is not limited to
    /// starting the run:
    ///
    /// <list type="bullet">
    /// <item><b>LobbyBeginRun</b> — <c>RestoreBeginRun</c> is the only caller that uses <c>StateTransition</c>,
    /// whose branch demands identical membership and real slots and rejects the smaller roster with
    /// "State-transition snapshot changed roster membership or real slot assignments."</item>
    /// <item><b>PlayerJoined</b> — <c>RestorePlayerJoined</c> passes <c>MembershipMutation</c> with the
    /// authoritative set derived from the client's own stale <c>Current</c>, so the ghost member makes the set
    /// comparison fail with "Roster snapshot differs from the authoritative connected-peer set."</item>
    /// <item><b>A reload / rejoin</b> — <c>AcceptRoster</c> can only pick <c>Bootstrap</c> while
    /// <c>Current == null</c>, so the next RoomJoin goes through <c>CurrentState</c> and demands a byte-for-byte
    /// repeat of the accepted revision.</item>
    /// </list>
    ///
    /// So the trigger is not "this is a begin-run", it is "the host says somebody is gone and we still have
    /// them", i.e. <c>Current ∖ Candidate</c> is not empty on the player-id axis. That one predicate holds for
    /// all three uses above, and it is also the right wording: the host is authoritative about who is in the
    /// room. A snapshot that only *adds* members leaves <c>Current</c> fully covered and is deliberately left
    /// alone; so is a repeated snapshot, which is how upstream's own happy paths stay byte-identical.
    ///
    /// The trap, and the one thing this repair must not get wrong: <c>Reset()</c> alone does not fix anything.
    /// It drops <c>_current</c>, and upstream then refuses the very snapshot we are holding, because with no
    /// accepted state a <c>StateTransition</c>/<c>MembershipMutation</c> snapshot may not initialize the state
    /// ("Transition and mutation snapshots cannot initialize roster authority state."). The use argument has to
    /// become <c>Bootstrap</c> in the same breath as the reset, which is why the prefix rewrites the use value
    /// through <c>__args</c> instead of just calling <c>Reset()</c>.
    ///
    /// The same reasoning applies to the authoritative membership set a mutation snapshot carries: the caller
    /// builds it from <c>Current</c>, i.e. from the state being dropped, so it is stale by construction and is
    /// cleared together with the reset.
    ///
    /// Client side only. <c>Accept</c> is unreachable on a host binding to begin with, because
    /// <c>ValidateIncomingCore</c> rejects every session-selection message whose binding is a host and hosts
    /// publish through <c>CommitHostSnapshot</c> instead. The prefix still looks the authority up in the tail
    /// runtime's live bindings and steps aside when the owning service is not a <c>NetClientGameService</c>, so a
    /// future caller that sends a roster to the host cannot be made to accept a client-authored one by this
    /// repair.
    /// </summary>
internal sealed class RosterRebaselineFix : ICompatFix
{
    private const string AuthorityTypeName = "Sts2LanConnect.Scripts.LanConnectRosterAuthorityState";
    private const string SnapshotTypeName = "Sts2LanConnect.Scripts.LanConnectRosterSnapshot";
    private const string SnapshotUseTypeName = "Sts2LanConnect.Scripts.LanConnectRosterSnapshotUse";
    private const string CarrierTypeName = "Sts2LanConnect.Scripts.LanConnectRosterPlayerCarrier";
    private const string TailRuntimeTypeName = "Sts2LanConnect.Scripts.LanConnectTailMessageRuntime";

    private const string Tag = "roster-rebaseline: ";

    /// <summary>
    /// Resolved once by <see cref="Applies"/>; <see cref="AcceptPrefix"/> only reads it
    /// </summary>
    private static Shape? _shape;

    /// <summary>
    /// Re-baselining is rare (it needs an actual departure), but the same state can be republished message after
    /// message. The signatures are remembered so a stuck session logs the fact once instead of once per frame.
    /// Bounded: past this many distinct signatures the set is dropped, because a case that differs from sixteen
    /// earlier ones is worth re-reporting.
    /// </summary>
    private const int MaxLoggedSignatures = 16;

    private static readonly object LogGate = new();

    private static readonly HashSet<string> LoggedSignatures = new(StringComparer.Ordinal);

    public string Id => "roster-rebaseline";

    public FixGroup Group => FixGroup.Roster;

    public bool Applies(out string reason)
    {
        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = "host mod '" + Entry.HostModAssemblyName + "' is not loaded";
            return false;
        }

        try
        {
            if (!TryCapture(host, out Shape shape, out string missing))
            {
                reason = "missing: " + missing;
                return false;
            }

            _shape = shape;
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            // The contract is "never throw here": a probe that dies has no reason string to show the user, and
            // Entry would report a failure where a skip with a reason is the honest answer.
            reason = $"missing: anchor capture threw {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    public void Apply(Harmony harmony)
    {
        Shape shape = _shape
            ?? throw new InvalidOperationException(
                "roster-rebaseline: Apply ran without a captured shape; Applies must be consulted first.");

        harmony.Patch(
            shape.Accept,
            prefix: new HarmonyMethod(typeof(RosterRebaselineFix), nameof(AcceptPrefix))
            {
                priority = Priority.First
            });

        Entry.Info(
            $"{Tag}watching {AuthorityTypeName}.Accept for a roster that lost members the client still holds.");

        if (shape.OptionalNote != null)
        {
            Entry.Warn(Tag + shape.OptionalNote);
        }
    }

    /// <summary>
    /// Every path returns <c>true</c>, so the original call always runs. A roster message that this repair
    /// cannot understand must still reach upstream, which is the only thing that knows what to do with it. The
    /// alternative is swallowing a join or a begin-run because a diagnostic blew up.
    ///
    /// The parameters are read and written through <c>__args</c> because two of them are types internal to the
    /// host mod (the snapshot and its use enum) and a <c>ref</c> parameter cannot be declared for a type that
    /// only exists at runtime.
    /// </summary>
    // ReSharper disable once InconsistentNaming — Harmony binds by the exact special-parameter name.
    public static bool AcceptPrefix(object? __instance, object?[]? __args)
    {
        try
        {
            if (Rebaseline(__instance, __args))
            {
                return true;
            }
        }
        catch (Exception exception)
        {
            WarnOnce(
                "failed:" + exception.GetType().FullName + ":" + exception.Message,
                $"{Tag}a roster snapshot could not be re-baselined, so the host mod handles it unmodified: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }

        return true;
    }

    /// <summary>
    /// <c>true</c> when a re-baseline was performed. Every early <c>false</c> means "upstream's behaviour stands".
    /// </summary>
    private static bool Rebaseline(object? __instance, object?[]? __args)
    {
        Shape? shape = _shape;
        if (shape == null || __instance == null || __args == null || __args.Length < 3)
        {
            return false;
        }

        // 0 = transportSenderPeerId, 1 = snapshot, 2 = use, 3 = authoritativeMembership, 4 = connectedPeerId.
        if (__args[1] is not { } snapshot || __args[2] is not { } useValue)
        {
            return false;
        }

        int use = Convert.ToInt32(useValue);
        if (shape.Current.GetValue(__instance) is not { } current)
        {
            // Nothing accepted yet, so there is no stale membership to be behind on; a first snapshot is exactly
            // what upstream expects.
            return false;
        }

        HashSet<ulong> currentIds = PlayerIdsOf(shape, current);
        HashSet<ulong> candidateIds = PlayerIdsOf(shape, snapshot);
        int currentCount = currentIds.Count;
        int candidateCount = candidateIds.Count;

        // Upstream's own check is "the revision must increase by exactly one", and when it fails the client is
        // dropped with a message that only says ModMismatch. Printing both revisions on every acceptance is what
        // makes that failure readable from a player log.
        string revisionNote = "cur.rev=" + ReadRevision(current) + " cand.rev=" + ReadRevision(snapshot);
        WarnOnce("accept:" + use + ":" + revisionNote + ":" + currentCount + ":" + candidateCount,
            Tag + "accept use=" + shape.NameOf(use) + " " + revisionNote +
            " members " + currentCount + " -> " + candidateCount);

        // "we hold somebody the host does not": that is the whole trigger. A snapshot that only adds members
        // keeps every current id and is left to upstream.
        currentIds.ExceptWith(candidateIds);
        if (currentIds.Count == 0)
        {
            return false;
        }

        if (IsHostSide(shape, __instance))
        {
            return false;
        }

        // Order matters: without the use rewrite the reset itself turns the snapshot into one upstream refuses
        // ("Transition and mutation snapshots cannot initialize roster authority state").
        shape.Reset.Invoke(__instance, null);
        __args[2] = Enum.ToObject(shape.UseType, shape.Bootstrap);

        // Derived from the state that was just dropped, so it is stale by construction.
        if (__args.Length > 3)
        {
            __args[3] = null;
        }

        string departed = string.Join(",", currentIds.OrderBy(id => id));
        string detail = use == shape.StateTransition
            ? $"BeginRun 名册减员（旧 {currentCount} 人 → 新 {candidateCount} 人，缺少 {departed}），已重新基线"
            : $"名册减员后收到 {shape.NameOf(use)}（旧 {currentCount} → 新 {candidateCount}，缺少 {departed}），已重新基线";

        WarnOnce($"rebaseline:{use}:{currentCount}:{candidateCount}:{departed}", Tag + detail);
        return true;
    }

    private static string ReadRevision(object? roster)
    {
        if (roster == null)
        {
            return "?";
        }

        try
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type type = roster.GetType();
            object? value = type.GetProperty("Revision", any)?.GetValue(roster)
                            ?? type.GetField("revision", any)?.GetValue(roster)
                            ?? type.GetProperty("revision", any)?.GetValue(roster)
                            ?? type.GetField("Revision", any)?.GetValue(roster);
            return value?.ToString() ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    private static HashSet<ulong> PlayerIdsOf(Shape shape, object snapshot)
    {
        var ids = new HashSet<ulong>();
        if (shape.SnapshotPlayers.GetValue(snapshot) is not IEnumerable players)
        {
            return ids;
        }

        foreach (object? player in players)
        {
            if (player != null && shape.CarrierPlayerId.GetValue(player) is ulong id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// <c>false</c> when the authority could not be tied to a live binding, or when its binding's service is a
    /// client service. "Could not tie it to anything" is deliberately treated as a client: the only callers of
    /// <c>Accept</c> are the restore handlers, which <c>ValidateIncomingCore</c> only runs for a non-host binding,
    /// and an out-of-process probe drives the authority directly.
    /// </summary>
    private static bool IsHostSide(Shape shape, object authority)
    {
        if (shape.Bindings is not { } lookup)
        {
            return false;
        }

        if (lookup.Shared.GetValue(null) is not { } runtime
            || lookup.Bindings.GetValue(runtime) is not IDictionary bindings)
        {
            return false;
        }

        foreach (object? binding in bindings.Values)
        {
            if (binding == null || !ReferenceEquals(lookup.Roster.GetValue(binding), authority))
            {
                continue;
            }

            return lookup.Service.GetValue(binding) is not NetClientGameService;
        }

        return false;
    }

    private static void WarnOnce(string signature, string message)
    {
        lock (LogGate)
        {
            if (!LoggedSignatures.Add(signature))
            {
                return;
            }

            if (LoggedSignatures.Count > MaxLoggedSignatures)
            {
                LoggedSignatures.Clear();
                LoggedSignatures.Add(signature);
            }
        }

        Entry.Warn(message);
    }

    /// <summary>
    /// Resolves every anchor before anything is patched, and reports all misses at once.
    ///
    /// The host types are named by full name and never with <c>typeof</c>: they are <c>internal</c> to the host
    /// mod, which is not referenced at compile time. The optional block is the client/host discriminator — see
    /// <see cref="Shape.OptionalNote"/> for why losing it must not cost the repair.
    /// </summary>
    private static bool TryCapture(Assembly host, out Shape shape, out string reason)
    {
        shape = new Shape();
        var missing = new List<string>();

        Type? authority = ReflectScope.HostType(host, AuthorityTypeName, missing);
        Type? snapshotType = ReflectScope.HostType(host, SnapshotTypeName, missing);
        Type? useType = ReflectScope.HostType(host, SnapshotUseTypeName, missing);
        Type? carrierType = ReflectScope.HostType(host, CarrierTypeName, missing);

        if (authority == null || snapshotType == null || useType == null || carrierType == null)
        {
            reason = string.Join(", ", missing);
            return false;
        }

        shape.UseType = useType;
        CaptureUseValues(useType, shape, missing);

        shape.Accept = RequireAccept(authority, snapshotType, useType, missing)!;
        shape.Current = RequireCurrent(authority, snapshotType, missing)!;
        shape.Reset = RequireReset(authority, missing)!;
        shape.SnapshotPlayers = RequirePlayers(snapshotType, carrierType, missing)!;
        shape.CarrierPlayerId = RequirePlayerId(carrierType, missing)!;

        CaptureBindings(host, shape);

        reason = string.Join(", ", missing);
        return missing.Count == 0;
    }

    private static MethodInfo? RequireAccept(Type authority, Type snapshotType, Type useType, List<string> missing)
    {
        MethodInfo? accept = ReflectScope.MethodOf(authority, "Accept", 5, missing);
        if (accept == null)
        {
            return null;
        }

        ParameterInfo[] parameters = accept.GetParameters();
        bool shaped = !accept.IsStatic
                      && accept.ReturnType == typeof(void)
                      && parameters[0].ParameterType == typeof(ulong)
                      && parameters[1].ParameterType == snapshotType
                      && parameters[2].ParameterType == useType
                      && parameters[4].ParameterType == typeof(ulong?);

        if (!shaped)
        {
            missing.Add(
                $"{authority.FullName}.Accept(ulong, {snapshotType.Name}, {useType.Name}, " +
                $"IReadOnlyCollection<ulong>, ulong?) — found ({Describe(parameters)})");
            return null;
        }

        return accept;
    }

    private static PropertyInfo? RequireCurrent(Type authority, Type snapshotType, List<string> missing)
    {
        PropertyInfo? current = ReflectScope.PropertyOf(authority, "Current", missing);
        if (current == null)
        {
            return null;
        }

        if (current.PropertyType != snapshotType || current.GetGetMethod(nonPublic: true) is not { IsStatic: false })
        {
            missing.Add(
                $"{authority.FullName}.Current (readable {snapshotType.Name} instance property, found " +
                $"{current.PropertyType.Name} static={current.GetGetMethod(nonPublic: true)?.IsStatic.ToString() ?? "none"})");
            return null;
        }

        return current;
    }

    private static MethodInfo? RequireReset(Type authority, List<string> missing)
    {
        MethodInfo? reset = ReflectScope.MethodOf(authority, "Reset", 0, missing);
        if (reset == null)
        {
            return null;
        }

        if (reset.IsStatic || reset.ReturnType != typeof(void))
        {
            missing.Add($"{authority.FullName}.Reset() (instance void, found static={reset.IsStatic} " +
                        $"returning {reset.ReturnType.Name})");
            return null;
        }

        return reset;
    }

    private static PropertyInfo? RequirePlayers(Type snapshotType, Type carrierType, List<string> missing)
    {
        PropertyInfo? players = ReflectScope.PropertyOf(snapshotType, "Players", missing);
        if (players == null)
        {
            return null;
        }

        Type[] arguments = players.PropertyType.IsGenericType
            ? players.PropertyType.GetGenericArguments()
            : Type.EmptyTypes;

        if (arguments.Length != 1 || arguments[0] != carrierType || !typeof(IEnumerable).IsAssignableFrom(players.PropertyType))
        {
            missing.Add(
                $"{snapshotType.FullName}.Players (IReadOnlyList<{carrierType.Name}>, found {players.PropertyType.Name})");
            return null;
        }

        return players;
    }

    private static PropertyInfo? RequirePlayerId(Type carrierType, List<string> missing)
    {
        PropertyInfo? playerId = ReflectScope.PropertyOf(carrierType, "PlayerId", missing);
        if (playerId == null)
        {
            return null;
        }

        if (playerId.PropertyType != typeof(ulong))
        {
            missing.Add($"{carrierType.FullName}.PlayerId (ulong, found {playerId.PropertyType.Name})");
            return null;
        }

        return playerId;
    }

    /// <summary>
    /// Resolves the four <c>LanConnectRosterSnapshotUse</c> members by name and refuses a <c>Bootstrap</c> whose
    /// value upstream would still treat as a transition, because that combination cannot be re-baselined at all.
    /// </summary>
    private static void CaptureUseValues(Type useType, Shape shape, List<string> missing)
    {
        int? bootstrap = TryEnumValue(useType, "Bootstrap", missing);
        int? currentState = TryEnumValue(useType, "CurrentState", missing);
        int? transition = TryEnumValue(useType, "StateTransition", missing);
        int? mutation = TryEnumValue(useType, "MembershipMutation", missing);

        if (bootstrap == null || currentState == null || transition == null || mutation == null)
        {
            return;
        }

        if (bootstrap == transition || bootstrap == mutation)
        {
            missing.Add(
                $"{useType.FullName}.Bootstrap (value {bootstrap}, which upstream rejects as a re-baseline " +
                $"because it still hits the transition/mutation guard)");
            return;
        }

        shape.Bootstrap = bootstrap.Value;
        shape.CurrentState = currentState.Value;
        shape.StateTransition = transition.Value;
        shape.MembershipMutation = mutation.Value;
    }

    private static int? TryEnumValue(Type useType, string name, List<string> missing)
    {
        try
        {
            if (Enum.GetNames(useType).Contains(name, StringComparer.Ordinal))
            {
                return Convert.ToInt32(Enum.Parse(useType, name, ignoreCase: false));
            }
        }
        catch (Exception)
        {
            // Falls through to the same report as "the member is gone": either way it is unusable.
        }

        missing.Add($"{useType.FullName}.{name}");
        return null;
    }

    /// <summary>
    /// The client/host discriminator, resolved best-effort. Losing it costs the host-side veto, not the repair:
    /// <c>Accept</c> is already unreachable on a host binding, so the guard is a second line of defence and is
    /// reported instead of being allowed to disable a repair that would otherwise work.
    /// </summary>
    private static void CaptureBindings(Assembly host, Shape shape)
    {
        try
        {
            Type? tailRuntime = Reflect.TryType(host, TailRuntimeTypeName);
            if (tailRuntime == null)
            {
                shape.OptionalNote = NoBindingLookup("type " + TailRuntimeTypeName);
                return;
            }

            PropertyInfo? shared = ReflectScope.StaticProperty(new List<string>(), tailRuntime, "Shared");
            FieldInfo? bindings = ReflectScope.Field(new List<string>(), tailRuntime, "_bindings");
            if (shared == null || bindings == null || shared.GetGetMethod(nonPublic: true) is not { IsStatic: true })
            {
                shape.OptionalNote = NoBindingLookup($"{TailRuntimeTypeName}.Shared/_bindings");
                return;
            }

            Type[] arguments = bindings.FieldType.IsGenericType
                ? bindings.FieldType.GetGenericArguments()
                : Type.EmptyTypes;

            if (arguments.Length != 2 || !typeof(IDictionary).IsAssignableFrom(bindings.FieldType))
            {
                shape.OptionalNote = NoBindingLookup($"{TailRuntimeTypeName}._bindings (not a dictionary)");
                return;
            }

            Type bindingType = arguments[1];
            FieldInfo? roster = ReflectScope.Field(new List<string>(), bindingType, "_roster");
            PropertyInfo? service = ReflectScope.Property(new List<string>(), bindingType, "Service");

            if (roster == null || service == null)
            {
                shape.OptionalNote = NoBindingLookup($"{bindingType.Name}._roster/Service");
                return;
            }

            shape.Bindings = new BindingLookup(shared, bindings, roster, service);
        }
        catch (Exception exception)
        {
            shape.OptionalNote = NoBindingLookup($"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string NoBindingLookup(string detail) =>
        $"the client/host check is unavailable ({detail}), so a roster snapshot reaching a host binding would " +
        "also be re-baselined. Re-baselining itself is unaffected: Accept is only reached from the client-side " +
        "restore handlers.";

    private static string Describe(ParameterInfo[] parameters) =>
        string.Join(", ", parameters.Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name));

    /// <summary>
    /// Every anchor <see cref="AcceptPrefix"/> needs. Resolved in <see cref="Applies"/>; never re-resolved at
    /// patch time.
    /// </summary>
    internal sealed class Shape
    {
        internal MethodInfo Accept = null!;

        internal PropertyInfo Current = null!;

        internal MethodInfo Reset = null!;

        internal PropertyInfo SnapshotPlayers = null!;

        internal PropertyInfo CarrierPlayerId = null!;

        internal Type UseType = null!;

        internal int Bootstrap;

        internal int CurrentState;

        internal int StateTransition;

        internal int MembershipMutation;

        /// <summary>Optional: present when the tail runtime's live bindings could be resolved.</summary>
        internal BindingLookup? Bindings;

        internal string? OptionalNote;

        internal string NameOf(int use) =>
            use == Bootstrap ? "Bootstrap"
            : use == CurrentState ? "CurrentState"
            : use == StateTransition ? "StateTransition"
            : use == MembershipMutation ? "MembershipMutation"
            : "use#" + use;
    }

    /// <summary>
    /// The four members that tie an authority instance back to the binding (and therefore the service) that owns
    /// it. Read-only: <c>RequireRoster()</c> is deliberately not called, because it creates an authority where
    /// none exists yet and this lookup must not have side effects.
    /// </summary>
    internal sealed class BindingLookup
    {
        internal BindingLookup(
            PropertyInfo shared,
            FieldInfo bindings,
            FieldInfo roster,
            PropertyInfo service)
        {
            Shared = shared;
            Bindings = bindings;
            Roster = roster;
            Service = service;
        }

        internal PropertyInfo Shared { get; }

        internal FieldInfo Bindings { get; }

        internal FieldInfo Roster { get; }

        internal PropertyInfo Service { get; }
    }
}
