using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2LobbyEnhance;

    /// <summary>
    /// Lets a hosted room bind to the save of the <em>next</em> run that starts in it, instead of staying frozen on
    /// the save of the run that just finished
    ///
    /// The deadlock, all upstream: a hosted room keeps a <c>HostedRoomSession</c> for the whole sitting, and that
    /// session remembers the save key it bound to (<c>BoundSaveKey</c>, LanConnectLobbyRuntime.cs:156). Every
    /// <c>save_event</c> hands that key to the writer as <c>BindingTarget.ExpectedSaveKey</c> (:2150) and the writer
    /// refuses any other save outright (:55-61 of LanConnectCurrentSaveBindingWriter.cs). The only code that ever
    /// updates the key is the writer's own success callback (:2152), so once a <em>different</em> save shows up the
    /// room can never bind to it again — the callback that would heal it is behind the refusal.
    ///
    /// "新开一局" hits exactly that, because it deliberately keeps the session: the new run's first save has a new
    /// key (LanConnectMultiplayerSaveRoomBinding.BuildSaveKey hashes the run's <c>StartTime</c>, :319-331, so a new
    /// run can never reuse the old one), the write is refused with <c>save_key_mismatch</c>, and the save stays
    /// unbound. A later "读档多人游戏" then has no binding to read and stops by telling the player it cannot
    /// confirm the original protocol.
    ///
    /// Clearing the key is enough, and it is the whole repair: with an empty <c>ExpectedSaveKey</c> the writer takes
    /// its keyless branch (:62-73), which the new run satisfies — <c>RunManager</c> assigns exactly the lobby's
    /// service (RunManager.cs:388 -> :523), i.e. the service this session kept, and a brand-new save key has no
    /// stored binding to trip the explicit-LAN guard. The first successful persist then writes the new key back
    /// (:2152) and every later save of that run matches normally.
    ///
    /// The moment is <c>RunManager.SetUpNewMultiplayer</c> rather than the 新开一局 click: that method is the first
    /// thing a new multiplayer run does (NGame.cs:1172) and it runs before <c>NGame.StartRun</c> (:1173) touches
    /// the save system, so the clearing is guaranteed to be earlier than the new run's first <c>save_event</c> —
    /// while the click itself is only "the lobby was rebuilt", with a whole character-select screen in between and
    /// nothing that says the player will ever embark. Patching here also fixes the same deadlock for a new run
    /// started through the vanilla menus in a kept room, not just through our button.
    ///
    /// Only the room that owns this run is touched: the session (or host origin) must hold the very service the new
    /// run is being set up on. A client's begin-run, a singleplayer run and a fresh room all leave it alone.
    /// </summary>
internal sealed class NewRunSaveBindingFix : ICompatFix
{
    private const string RuntimeTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyRuntime";
    private const string RunManagerTypeName = "MegaCrit.Sts2.Core.Runs.RunManager";

    private const string Tag = "new-run-save-binding: ";

    /// <summary>Resolved once by <see cref="Applies"/>; the prefix only reads it</summary>
    private static Shape? _shape;

    private static readonly object LogGate = new();

    private static string? _lastFailure;

    public string Id => "new-run-save-binding";

    /// <summary>
    /// Same group as <see cref="NewRunFromGameOverFix"/>: this is the second half of that feature — without it the
    /// new run works but its save can never be hosted again, which is what the field report is about
    /// </summary>
    public FixGroup Group => FixGroup.NewRun;

    public bool Applies(in CompatSnapshot snapshot, out string reason)
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
            // The contract is "never throw here": without a reason string the startup log shows a failure where a
            // reasoned skip is the honest answer.
            reason = $"missing: anchor capture threw {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    public void Apply(Harmony harmony, in CompatSnapshot snapshot)
    {
        Shape shape = _shape
            ?? throw new InvalidOperationException(
                "new-run-save-binding: Apply ran without a captured shape; Applies must be consulted first.");

        harmony.Patch(
            shape.SetUpNewMultiplayer,
            prefix: new HarmonyMethod(typeof(NewRunSaveBindingFix), nameof(SetUpNewMultiplayerPrefix))
            {
                priority = Priority.First
            });

        Entry.Info(
            $"{Tag}a hosted room now re-binds when the next run starts in it (clears " +
            $"{RuntimeTypeName}.HostedRoomSession.BoundSaveKey before the new save exists).");

        if (shape.OptionalNote != null)
        {
            Entry.Warn(Tag + shape.OptionalNote);
        }
    }

    /// <summary>
    /// Bound by position, not by name: Harmony's name binding silently stops matching if the game renames the
    /// parameter, and a prefix that never runs looks exactly like a run with nothing to clear. The position itself
    /// is pinned by <see cref="Applies"/>, which checks all four parameter types.
    ///
    /// Never throws and never vetoes the original: a run that starts without the clearing is the old bug, while a
    /// run that does not start at all is much worse.
    /// </summary>
    // ReSharper disable once InconsistentNaming — Harmony binds by the exact special-parameter name.
    public static void SetUpNewMultiplayerPrefix(StartRunLobby? __1)
    {
        try
        {
            Shape? shape = _shape;
            if (shape == null || __1 == null)
            {
                return;
            }

            object? runtime = shape.Instance.GetValue(null);
            if (runtime == null)
            {
                // The host mod is not hosting anything in this process; nothing to re-baseline.
                return;
            }

            INetGameService service = __1.NetService;
            ClearSessionBinding(shape, runtime, service);
            ClearHostOriginBinding(shape, runtime, service);
        }
        catch (Exception exception)
        {
            WarnOnce(
                "failed:" + exception.GetType().FullName + ":" + exception.Message,
                $"{Tag}could not clear the room's bound save key before the new run; the new save will not be " +
                $"hosted until the room is re-created: {exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// The <c>hosted_room</c> target — the one the field log showed being refused. Only the session that owns the
    /// service this run starts on is touched.
    /// </summary>
    private static void ClearSessionBinding(Shape shape, object runtime, INetGameService service)
    {
        if (shape.ActiveSession.GetValue(runtime) is not { } session)
        {
            return;
        }

        if (shape.SessionNetService.GetValue(session) is not INetGameService sessionService
            || !ReferenceEquals(sessionService, service))
        {
            return;
        }

        if (shape.BoundSaveKey.GetValue(session) is not string staleKey || string.IsNullOrWhiteSpace(staleKey))
        {
            return;
        }

        shape.BoundSaveKey.SetValue(session, null);
        Entry.Info(
            $"{Tag}cleared the hosted room's bound save key before the new run (was {staleKey}), so the new " +
            "run's first save can bind to this room.");
    }

    /// <summary>
    /// The <c>host_origin</c> target (LanConnectLobbyRuntime.cs:2185). It only applies when no room session exists,
    /// so it cannot be the path the field report hit — cleared with the same rule so a room whose session is gone
    /// does not keep the same deadlock alive.
    /// </summary>
    private static void ClearHostOriginBinding(Shape shape, object runtime, INetGameService service)
    {
        if (shape.HostOrigin is not { } originField || shape.OriginSaveKey is not { } originSaveKey)
        {
            return;
        }

        if (originField.GetValue(runtime) is not { } origin)
        {
            return;
        }

        if (shape.OriginNetService?.GetValue(origin) is not INetGameService originService
            || !ReferenceEquals(originService, service))
        {
            return;
        }

        if (originSaveKey.GetValue(origin) is not string staleKey || string.IsNullOrWhiteSpace(staleKey))
        {
            return;
        }

        originSaveKey.SetValue(origin, null);
        Entry.Info(
            $"{Tag}cleared the host origin's save key before the new run (was {staleKey}), so the new run's " +
            "first save can bind.");
    }

    private static void WarnOnce(string signature, string message)
    {
        lock (LogGate)
        {
            if (string.Equals(_lastFailure, signature, StringComparison.Ordinal))
            {
                return;
            }

            _lastFailure = signature;
        }

        Entry.Warn(message);
    }

    /// <summary>
    /// Resolves every anchor before anything is patched and reports all misses at once.
    ///
    /// The host-mod types are reached by full name and never with <c>typeof</c>: they are <c>internal</c> and the
    /// host mod is deliberately not referenced at compile time. The game side is named by full name too, but its
    /// players are compared as <c>typeof</c> values — those types are referenced at compile time anyway, and a
    /// name-string comparison of <c>DateTimeOffset?</c> would embed an assembly version.
    /// </summary>
    private static bool TryCapture(Assembly host, out Shape shape, out string reason)
    {
        shape = new Shape();
        var missing = new List<string>();

        Type? runtimeType = ReflectScope.HostType(host, RuntimeTypeName, missing);
        shape.Instance = runtimeType == null
            ? null!
            : ReflectScope.StaticProperty(missing, runtimeType, "Instance")!;

        if (shape.Instance != null
            && (shape.Instance.GetGetMethod(nonPublic: true) is not { IsStatic: true }
                || shape.Instance.PropertyType != runtimeType))
        {
            missing.Add($"{RuntimeTypeName}.Instance (static {runtimeType?.Name} property)");
            shape.Instance = null!;
        }

        FieldInfo? activeSession = ReflectScope.Field(missing, runtimeType, "_activeSession");
        if (activeSession != null)
        {
            shape.ActiveSession = activeSession;
            shape.BoundSaveKey = RequireStringProperty(activeSession.FieldType, "BoundSaveKey", missing)!;
            shape.SessionNetService = RequireServiceProperty(activeSession.FieldType, "NetService", missing)!;
        }

        Type? runManager = ReflectScope.GameType(RunManagerTypeName, missing);
        MethodInfo? setUp = runManager == null
            ? null
            : ReflectScope.MethodOf(runManager, "SetUpNewMultiplayer", 4, missing);
        if (setUp != null && !HasNewMultiplayerShape(setUp, out string actual))
        {
            missing.Add($"{RunManagerTypeName}.SetUpNewMultiplayer({actual}) — expected " +
                        "(RunState, StartRunLobby, bool, DateTimeOffset?)");
            setUp = null;
        }

        shape.SetUpNewMultiplayer = setUp!;

        CaptureHostOrigin(runtimeType, shape);

        reason = string.Join(", ", missing);
        return missing.Count == 0;
    }

    private static bool HasNewMultiplayerShape(MethodInfo method, out string actual)
    {
        ParameterInfo[] parameters = method.GetParameters();
        actual = Describe(parameters);

        return !method.IsStatic
               && method.ReturnType == typeof(void)
               && parameters.Length == 4
               && parameters[0].ParameterType == typeof(RunState)
               && parameters[1].ParameterType == typeof(StartRunLobby)
               && parameters[2].ParameterType == typeof(bool)
               && parameters[3].ParameterType == typeof(DateTimeOffset?);
    }

    private static PropertyInfo? RequireStringProperty(Type owner, string name, List<string> missing)
    {
        PropertyInfo? property = ReflectScope.Property(missing, owner, name);
        if (property == null)
        {
            return null;
        }

        if (property.PropertyType != typeof(string) || !property.CanWrite || property.GetSetMethod(nonPublic: true) is null)
        {
            missing.Add(
                $"{owner.FullName}.{name} (writable string, found {property.PropertyType.Name} " +
                $"CanWrite={property.CanWrite})");
            return null;
        }

        return property;
    }

    private static PropertyInfo? RequireServiceProperty(Type owner, string name, List<string> missing)
    {
        PropertyInfo? property = ReflectScope.Property(missing, owner, name);
        if (property == null)
        {
            return null;
        }

        if (!typeof(INetGameService).IsAssignableFrom(property.PropertyType))
        {
            missing.Add($"{owner.FullName}.{name} (INetGameService, found {property.PropertyType.Name})");
            return null;
        }

        return property;
    }

    /// <summary>
    /// The second target is optional on purpose: <c>host_origin</c> only exists when no room session does, which is
    /// not the reported case, so losing those anchors must not cost the room re-baseline.
    /// </summary>
    private static void CaptureHostOrigin(Type? runtimeType, Shape shape)
    {
        try
        {
            var problems = new List<string>();
            FieldInfo? hostOrigin = ReflectScope.Field(problems, runtimeType, "_hostOrigin");
            if (hostOrigin == null || hostOrigin.FieldType.IsGenericType)
            {
                shape.OptionalNote = Note(problems.Count > 0 ? string.Join(", ", problems) : "_hostOrigin is generic");
                return;
            }

            var originProblems = new List<string>();
            PropertyInfo? saveKey = RequireStringProperty(hostOrigin.FieldType, "SaveKey", originProblems);
            PropertyInfo? netService = RequireServiceProperty(hostOrigin.FieldType, "NetService", originProblems);

            if (saveKey == null || netService == null)
            {
                shape.OptionalNote = Note(string.Join(", ", originProblems));
                return;
            }

            shape.HostOrigin = hostOrigin;
            shape.OriginSaveKey = saveKey;
            shape.OriginNetService = netService;
        }
        catch (Exception exception)
        {
            shape.OptionalNote = Note($"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string Note(string detail) =>
        $"the host_origin target is not re-baselined ({detail}). A room session is unaffected; a host whose room " +
        "was already stopped keeps the old behaviour for that run.";

    private static string Describe(ParameterInfo[] parameters) =>
        string.Join(", ", parameters.Select(parameter => parameter.ParameterType.Name));

    /// <summary>
    /// Every anchor the prefix needs. Resolved in <see cref="Applies"/>, never re-resolved while patched.
    /// </summary>
    internal sealed class Shape
    {
        /// <summary><c>LanConnectLobbyRuntime.Instance</c> (:657).</summary>
        internal PropertyInfo Instance = null!;

        /// <summary><c>_activeSession</c> (:587), the hosted room this process owns.</summary>
        internal FieldInfo ActiveSession = null!;

        /// <summary><c>HostedRoomSession.BoundSaveKey</c> (:156) — read at :2150, written at :2152.</summary>
        internal PropertyInfo BoundSaveKey = null!;

        /// <summary><c>HostedRoomSession.NetService</c> (:142) — the identity that proves the room owns this run.</summary>
        internal PropertyInfo SessionNetService = null!;

        /// <summary><c>RunManager.SetUpNewMultiplayer(RunState, StartRunLobby, bool, DateTimeOffset?)</c> (RunManager.cs:381).</summary>
        internal MethodInfo SetUpNewMultiplayer = null!;

        internal FieldInfo? HostOrigin;

        internal PropertyInfo? OriginSaveKey;

        internal PropertyInfo? OriginNetService;

        internal string? OptionalNote;
    }
}
