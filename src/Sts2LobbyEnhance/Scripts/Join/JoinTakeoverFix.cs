using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Connection;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Saves;

namespace Sts2LobbyEnhance;

/// <summary>
/// Repairs #1 and #2: LAN Connect's managed join flow.
///
/// The broken code lives in the async state machine
/// <c>LanConnectLobbyManagedJoinFlow.&lt;BeginAsync&gt;d__23.MoveNext</c>, which reads the removed field
/// <c>InitialGameInfoMessage.gameMode</c> and, one step later, writes the removed field
/// <c>ClientLobbyJoinRequestMessage.maxAscensionUnlocked</c>.
///
/// A method that references a member which no longer exists cannot be JITted, and Harmony cannot patch
/// it either: building a patch requires reading the IL, and resolving the metadata token throws before
/// any prefix, postfix or transpiler exists. So we do not touch the body.
///
/// Instead we patch the *async stub* — ordinary IL with no removed members — and return false, which
/// stops <c>AsyncTaskMethodBuilder.Start</c> from ever running, so the state machine is never compiled.
/// In its place we run the game's own <see cref="JoinFlow"/>, which already speaks the 0.113 handshake
/// and builds <c>JoinResult</c> from the response messages. That is precisely what the upstream port
/// does, minus the field that no longer exists.
///
/// What we deliberately keep from the host mod, because the rest of LAN Connect depends on it:
/// <list type="bullet">
/// <item>the net service comes from <c>LanConnectNetGameServiceFactory</c>, so the same constructor
/// strategy (versioned or parameterless) is used as everywhere else in the mod;</item>
/// <item>tail_v1 sessions get the extension frame bound, otherwise a tail_v1 host stalls waiting for it;</item>
/// <item>the mod's <c>CancelToken</c> still aborts the join.</item>
/// </list>
///
/// Everything after <c>BeginAsync</c> returns — attaching the joined client to the runtime, pushing the
/// follow-up screen, reporting connection events — stays in the original caller and is untouched.
///
/// Three upstream behaviours that the first version of this takeover dropped, restored here:
/// <list type="bullet">
/// <item><b>Cancel-aware disconnect reason.</b> Upstream disconnects a failed join with
/// <c>CancelledJoin</c> when its own cancel gesture fired and <c>InternalError</c> otherwise
/// (source <c>LanConnectLobbyManagedJoinFlow.cs:174</c>, shipped DLL
/// <c>dec-official/LanConnectLobbyManagedJoinFlow.cs:148</c>:
/// <c>NetError reason = (NetError)(CancelToken.IsCancellationRequested ? 6 : 202);</c>).</item>
/// <item><b>tail_v1 rejection reporting.</b> A tail_v1 host can reject a frame and disconnect with a
/// generic <c>ModMismatch</c>; the real code (<c>lan_extension_missing</c>, …) is parked on the tail
/// runtime and has to be taken off the disconnect event (source <c>:568-641</c>, shipped DLL
/// <c>:524-604</c>). Without this the user only ever sees "Unexpectedly disconnected from host while
/// joining. Reason: ModMismatch".</item>
/// <item><b>Log levels.</b> Upstream raises Network/Actions/GameSync for the duration of a join and
/// restores them in its own <c>finally</c> (source <c>:67-69</c>/<c>:187-189</c>, shipped DLL
/// <c>:74-76</c>/<c>:159-161</c>).</item>
/// </list>
/// </summary>
internal sealed class JoinTakeoverFix : ICompatFix
{
    private const string FlowTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyManagedJoinFlow";
    private const string TailRuntimeTypeName = "Sts2LanConnect.Scripts.LanConnectTailMessageRuntime";
    private const string TailProfileName = "TailV1";
    private const string ProfileTypeName = "Sts2LanConnect.Scripts.LanConnectProtocolProfile";
    private const string ProtocolExceptionTypeName = "Sts2LanConnect.Scripts.LanConnectProtocolException";
    private const string TakeRejectionMethodName = "TryTakeValidatedRejection";
    private const string FactoryTypeName = "Sts2LanConnect.Scripts.LanConnectNetGameServiceFactory";
    private const string CodecTypeName = "Sts2LanConnect.Scripts.LanConnectSidecarFrameCodec";

    // Game-side names compared as strings, never typeof(): a probe that mentions them with typeof() cannot be
    // JITted once they are renamed, which is exactly what the probe has to survive.
    private const string SceneTreeTypeName = "Godot.SceneTree";
    private const string CancelTokenTypeName = "System.Threading.CancellationTokenSource";
    private const string NetClientServiceTypeName = "MegaCrit.Sts2.Core.Multiplayer.NetClientGameService";

    /// <summary>Same member filter the rest of the file uses for reflective lookups.</summary>
    private const BindingFlags AnyMember =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Every anchor this repair needs, resolved once in <see cref="Apply"/> before anything is patched.
    ///
    /// The first version of this fix resolved fifteen of these lazily *inside* the join, so a renamed member
    /// surfaced as an exception in the middle of a join while the log already said "applied: join-flow". Nothing
    /// in here is resolved after <see cref="Apply"/> returns (2026-11-14 gating audit, A1/A2).
    ///
    /// <see cref="TakeValidatedRejection"/> and <see cref="ProtocolExceptionCtor"/> stay optional on purpose: they
    /// only make a tail_v1 disconnect report its real rejection code instead of a generic ModMismatch, so their
    /// absence is a warning, not a reason to skip. Everything else is required — without it the handshake breaks.
    /// </summary>
    internal sealed class Shape
    {
        internal MethodInfo BeginAsync = null!;

        internal MethodInfo CreateClient = null!;

        internal PropertyInfo NetService = null!;

        internal PropertyInfo CancelToken = null!;

        internal FieldInfo ProtocolSelection = null!;

        internal FieldInfo ProtocolOffer = null!;

        internal FieldInfo ProtocolFlowNonce = null!;

        internal FieldInfo PeerNativeBusTypeId = null!;

        internal PropertyInfo SelectionProfile = null!;

        internal int TailProfileValue;

        internal int FlowNonceBytes;

        internal Type TailRuntimeType = null!;

        internal PropertyInfo Shared = null!;

        internal MethodInfo GetMessageBus = null!;

        internal MethodInfo Bind = null!;

        internal MethodInfo Unbind = null!;

        internal MethodInfo? TakeValidatedRejection;

        internal ConstructorInfo? ProtocolExceptionCtor;

        /// <summary>Set when only the optional tail-rejection anchors are missing; logged by <see cref="Apply"/>.</summary>
        internal string? OptionalNote;
    }

    internal static Shape? _shape;

    public string Id => "join-flow";

    public FixGroup Group => FixGroup.Join;

    /// <summary>
    /// Needed only when this game build no longer has the two members the shipped
    /// <c>LanConnectLobbyManagedJoinFlow.BeginAsync</c> was compiled against. That pair is this repair's own
    /// precondition, not a global "is this 0.113" gate: if a future build restores either member, the host mod's
    /// own flow JITs again and taking it over would only drop its validation.
    /// </summary>
    public bool Applies(in CompatSnapshot snapshot, out string reason)
    {
        if (!snapshot.InitialGameInfoLostGameMode && !snapshot.JoinRequestLostMaxAscensionUnlocked)
        {
            reason = "this build still has InitialGameInfoMessage.gameMode and " +
                     "ClientLobbyJoinRequestMessage.maxAscensionUnlocked, so the host mod's own join flow runs";
            return false;
        }

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
                "join-flow: Apply ran without a captured shape; Applies must be consulted first.");

        harmony.Patch(
            shape.BeginAsync,
            prefix: new HarmonyMethod(typeof(JoinTakeoverFix), nameof(BeginAsyncPrefix))
            {
                priority = Priority.First
            });

        if (shape.OptionalNote != null)
        {
            Entry.Warn("join-flow: " + shape.OptionalNote);
        }
        else
        {
            Entry.Info(
                "join-flow: tail rejections will be reported with their own code — " +
                $"{shape.TailRuntimeType.Name}.{TakeRejectionMethodName} and the " +
                "LanConnectProtocolException(LanConnectProtocolFailure) constructor resolved.");
        }

        Entry.Info("join-flow: patched LanConnectLobbyManagedJoinFlow.BeginAsync to run the vanilla JoinFlow.");
    }

    /// <summary>
    /// Resolves every anchor before a single patch is installed. Never throws, never invokes anything, never
    /// touches Godot: pure metadata over the already-loaded host assembly. All misses are collected, so one log
    /// line lists everything that has to be fixed instead of the first thing that blew up.
    ///
    /// The game-side types (<c>Godot.SceneTree</c>, <c>CancellationTokenSource</c>, <c>NetClientGameService</c>)
    /// are compared by name on purpose: a probe that names them with typeof() cannot be JITted once they are
    /// renamed, which is the exact scenario this probe exists to survive (2026-11-14 gating audit, A1).
    /// </summary>
    private static bool TryCapture(Assembly host, out Shape shape, out string reason)
    {
        shape = new Shape();
        var missing = new List<string>();

        Type? flowType = TypeOf(host, FlowTypeName, missing);
        if (flowType != null)
        {
            MethodInfo? beginAsync = ReflectScope.MethodOf(flowType, "BeginAsync", 2, missing);
            if (beginAsync != null)
            {
                ParameterInfo[] parameters = beginAsync.GetParameters();
                if (parameters[0].ParameterType != typeof(object)
                    || !string.Equals(parameters[1].ParameterType.FullName, SceneTreeTypeName, StringComparison.Ordinal))
                {
                    missing.Add($"{FlowTypeName}.BeginAsync(object, {SceneTreeTypeName})");
                }
                else
                {
                    shape.BeginAsync = beginAsync;
                }
            }

            shape.NetService = ReflectScope.PropertyOf(flowType, "NetService", missing)!;
            shape.CancelToken = ReflectScope.PropertyOf(flowType, "CancelToken", missing)!;
            shape.ProtocolSelection = ReflectScope.FieldOf(flowType, "_protocolSelection", missing)!;
            shape.ProtocolOffer = ReflectScope.FieldOf(flowType, "_protocolOffer", missing)!;
            shape.ProtocolFlowNonce = ReflectScope.FieldOf(flowType, "_protocolFlowNonce", missing)!;
            shape.PeerNativeBusTypeId = ReflectScope.FieldOf(flowType, "_peerNativeBusTypeId", missing)!;

            if (shape.NetService != null && shape.NetService.GetSetMethod(nonPublic: true) == null)
            {
                missing.Add($"{FlowTypeName}.NetService setter");
            }

            if (shape.CancelToken != null
                && !string.Equals(shape.CancelToken.PropertyType.FullName, CancelTokenTypeName, StringComparison.Ordinal))
            {
                missing.Add(
                    $"{FlowTypeName}.CancelToken is {shape.CancelToken.PropertyType.FullName}, " +
                    $"expected {CancelTokenTypeName}");
            }

            if (shape.ProtocolFlowNonce != null && shape.ProtocolFlowNonce.FieldType != typeof(byte[]))
            {
                missing.Add(
                    $"{FlowTypeName}._protocolFlowNonce is {shape.ProtocolFlowNonce.FieldType.Name}, " +
                    "expected Byte[]");
            }
        }

        // ---- the frozen selection's profile: the enum member TailV1 and the Profile property on the selection
        Type? profileType = TypeOf(host, ProfileTypeName, missing);
        if (profileType != null)
        {
            if (!profileType.IsEnum)
            {
                missing.Add($"{ProfileTypeName} is not an enum");
            }
            else
            {
                FieldInfo? tailField = profileType.GetField(TailProfileName, AnyMember);
                if (tailField?.GetRawConstantValue() is int tailValue)
                {
                    shape.TailProfileValue = tailValue;
                }
                else
                {
                    missing.Add($"{ProfileTypeName}.{TailProfileName} (literal const)");
                }
            }

            if (shape.ProtocolSelection != null)
            {
                Type selectionType = shape.ProtocolSelection.FieldType;
                PropertyInfo? profile = ReflectScope.PropertyOf(selectionType, "Profile", missing);
                if (profile != null)
                {
                    if (profile.PropertyType != profileType)
                    {
                        missing.Add(
                            $"{selectionType.FullName}.Profile is {profile.PropertyType.FullName}, " +
                            $"expected {ProfileTypeName}");
                    }
                    else
                    {
                        shape.SelectionProfile = profile;
                    }
                }
            }
        }

        // ---- net client factory: shape only, never called from here
        Type? factory = TypeOf(host, FactoryTypeName, missing);
        if (factory != null)
        {
            MethodInfo? createClient = factory
                .GetMethods(AnyMember)
                .FirstOrDefault(method =>
                    method.IsStatic
                    && method.GetParameters().Length == 0
                    && string.Equals(method.Name, "CreateClient", StringComparison.Ordinal));

            if (createClient == null)
            {
                missing.Add($"{FactoryTypeName}.CreateClient()");
            }
            else if (!string.Equals(createClient.ReturnType.FullName, NetClientServiceTypeName, StringComparison.Ordinal))
            {
                missing.Add(
                    $"{FactoryTypeName}.CreateClient() returns {createClient.ReturnType.FullName}, " +
                    $"expected {NetClientServiceTypeName}");
            }
            else
            {
                shape.CreateClient = createClient;
            }
        }

        // ---- nonce length constant
        Type? codec = TypeOf(host, CodecTypeName, missing);
        if (codec != null)
        {
            FieldInfo? nonceField = codec.GetField("FlowNonceBytes", AnyMember);
            if (nonceField?.GetRawConstantValue() is int nonceBytes)
            {
                shape.FlowNonceBytes = nonceBytes;
            }
            else
            {
                missing.Add($"{CodecTypeName}.FlowNonceBytes (literal const)");
            }
        }

        // ---- tail runtime
        Type? runtimeType = TypeOf(host, TailRuntimeTypeName, missing);
        if (runtimeType != null)
        {
            shape.TailRuntimeType = runtimeType;
            shape.Shared = ReflectScope.PropertyOf(runtimeType, "Shared", missing)!;
            shape.GetMessageBus = ReflectScope.MethodOf(runtimeType, "GetMessageBus", 1, missing)!;
            shape.Bind = ReflectScope.MethodOf(runtimeType, "Bind", 7, missing)!;
            shape.Unbind = ReflectScope.MethodOf(runtimeType, "Unbind", 1, missing)!;

            if (shape.Shared != null && shape.Shared.GetGetMethod(nonPublic: true) is not { IsStatic: true })
            {
                missing.Add($"{TailRuntimeTypeName}.Shared is not a static property");
            }
        }

        CaptureOptionalTailRejectionAnchors(host, runtimeType, shape);

        reason = string.Join(", ", missing);
        return missing.Count == 0;
    }

    /// <summary>
    /// The two anchors that only improve the disconnect message. A miss is recorded on
    /// <see cref="Shape.OptionalNote"/> and logged by <see cref="Apply"/>; it never makes the repair skip.
    /// </summary>
    private static void CaptureOptionalTailRejectionAnchors(Assembly host, Type? runtimeType, Shape shape)
    {
        if (runtimeType == null)
        {
            return;
        }

        try
        {
            MethodInfo? take = runtimeType
                .GetMethods(AnyMember)
                .FirstOrDefault(method =>
                    string.Equals(method.Name, TakeRejectionMethodName, StringComparison.Ordinal)
                    && method.GetParameters() is { Length: 3 } parameters
                    && !parameters[0].ParameterType.IsValueType
                    && !parameters[0].ParameterType.IsByRef
                    && parameters[1].ParameterType == typeof(ulong)
                    && parameters[2].ParameterType.IsByRef);

            if (take == null)
            {
                shape.OptionalNote = OptionalNote("no matching " + TakeRejectionMethodName + " overload");
                return;
            }

            Type? failureType = take.GetParameters()[2].ParameterType.GetElementType();
            if (failureType == null)
            {
                shape.OptionalNote = OptionalNote(TakeRejectionMethodName + " out parameter type is unresolved");
                return;
            }

            Type? exceptionType = host.GetType(ProtocolExceptionTypeName, throwOnError: false, ignoreCase: false);
            ConstructorInfo? exceptionCtor = exceptionType?
                .GetConstructors(AnyMember)
                .FirstOrDefault(ctor =>
                    ctor.GetParameters() is { Length: 1 } ctorParameters
                    && ctorParameters[0].ParameterType == failureType);

            if (exceptionCtor == null)
            {
                shape.OptionalNote = OptionalNote(
                    $"no {ProtocolExceptionTypeName}({failureType.Name}) constructor");
                return;
            }

            shape.TakeValidatedRejection = take;
            shape.ProtocolExceptionCtor = exceptionCtor;
        }
        catch (Exception exception)
        {
            shape.OptionalNote = OptionalNote($"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string OptionalNote(string detail) =>
        "could not resolve the tail-rejection anchors (" + detail + "). A tail_v1 disconnect will keep " +
        "surfacing as the generic 'Unexpectedly disconnected from host while joining. Reason: ...' instead of " +
        "its real rejection code; every other part of the join is unaffected.";

    private static Type? TypeOf(Assembly host, string fullName, List<string> missing)
    {
        Type? type = host.GetType(fullName, throwOnError: false, ignoreCase: false);
        if (type == null)
        {
            missing.Add("type " + fullName);
        }

        return type;
    }

    // ReSharper disable once UnusedParameter.Local — Harmony binds by position.
    private static bool BeginAsyncPrefix(
        object __instance,
        object initializer,
        SceneTree sceneTree,
        ref Task<JoinResult> __result)
    {
        __result = RunAsync(__instance, (IClientConnectionInitializer)initializer, sceneTree);
        return false;
    }

    private static async Task<JoinResult> RunAsync(
        object flow,
        IClientConnectionInitializer initializer,
        SceneTree sceneTree)
    {
        // Upstream raises the network/action/gamesync log levels for the duration of a join and restores them
        // in its own finally (source LanConnectLobbyManagedJoinFlow.cs:67-69 and :187-189; the shipped DLL does
        // the same with raw enum values, dec-official:74-76 and :159-161).
        //
        // What this actually adds: the game's own JoinFlow.Begin already raises the same three levels
        // (JoinFlow.cs:54-59), but it only restores them on the *failure* path (JoinFlow.cs:151-153 inside the
        // catch) and leaves them raised when the join succeeds. Upstream restores them either way, so a
        // successful join on an unpatched LAN Connect ends with Network/Actions/GameSync back at Info and ours
        // did not. The restore below is the part that was missing.
        //
        // Fully qualified on purpose: this file also imports Godot (for SceneTree), and Godot.Logger would make
        // the bare name ambiguous (CS0104). Upstream writes it the same way for the same reason —
        // source LanConnectLobbyManagedJoinFlow.cs:67-69 spells out MegaCrit.Sts2.Core.Logging.Logger.
        MegaCrit.Sts2.Core.Logging.Logger.logLevelTypeMap[LogType.Network] = LogLevel.Debug;
        MegaCrit.Sts2.Core.Logging.Logger.logLevelTypeMap[LogType.Actions] = LogLevel.VeryDebug;
        MegaCrit.Sts2.Core.Logging.Logger.logLevelTypeMap[LogType.GameSync] = LogLevel.VeryDebug;

        Shape shape = _shape
            ?? throw new InvalidOperationException(
                "join-flow: the shape was not captured, so the BeginAsync prefix cannot run.");

        NetClientGameService? netService = null;
        bool tailBound = false;
        bool released = false;

        // Idempotent release (2026-11-14 gating audit, A3). Two paths reach it: a throw between
        // CreateClientService and the await (BindTailIfRequired, LinkCancellation) and a throw out of the await.
        // Before this existed, the first path leaked a fully constructed NetClientGameService that was already
        // published on the host mod's flow as NetService.
        void Release()
        {
            if (released || netService == null)
            {
                return;
            }

            released = true;

            try
            {
                if (netService.IsConnected)
                {
                    netService.Disconnect(ModDisconnectReason(flow));
                }
            }
            catch (Exception exception)
            {
                Entry.Warn(
                    "join-flow: could not disconnect the failed join's net service " +
                    $"({exception.GetType().Name}: {exception.Message}); still releasing the tail binding.");
            }

            try
            {
                // Only a failed join releases the binding (this is the upstream behaviour).
                if (tailBound)
                {
                    UnbindTail(netService);
                }
            }
            catch (Exception exception)
            {
                Entry.Warn(
                    "join-flow: could not release the tail binding of the failed join " +
                    $"({exception.GetType().Name}: {exception.Message}).");
            }
        }

        try
        {
            NetClientGameService service = CreateClientService();
            netService = service;
            shape.NetService.SetValue(flow, service);
            Entry.Info($"join-flow: net service created ({service.GetType().Name}).");

            tailBound = BindTailIfRequired(flow, service);
            JoinFlow joinFlow = new(service, initializer);
            LinkCancellation(flow, joinFlow);

            // tail_v1 only: a rejection code travels on the disconnect event, not on the message stream
            // (upstream source :629-641 / shipped DLL :595-604). Take it here, while the tail binding still
            // exists — JoinFlow's own handler will turn the same disconnect into a generic
            // ClientConnectionFailedException, and the take is destructive.
            // Typed as Exception on purpose: LanConnectProtocolException lives in the host mod assembly, which
            // this project deliberately does not reference (see Core/Reflect.cs). The runtime type is what the
            // caller's `catch (LanConnectProtocolException)` branch matches, and `throw` preserves it.
            Exception? rejection = null;
            void OnDisconnected(NetErrorInfo info) => rejection ??= TryTakeTailRejection(service, tailBound, info);

            if (tailBound)
            {
                service.Disconnected += OnDisconnected;
            }

            try
            {
                return await AwaitJoinAsync(joinFlow, sceneTree, tailBound);
            }
            catch (Exception exception)
            {
                Release();

                if (rejection != null && exception is not OperationCanceledException)
                {
                    Entry.Warn(
                        $"join-flow: reporting the tail rejection instead of the generic " +
                        $"'{exception.GetType().Name}: {exception.Message}' the disconnect produced.");
                    throw rejection;
                }

                throw;
            }
            finally
            {
                if (tailBound)
                {
                    service.Disconnected -= OnDisconnected;
                }
            }
        }
        catch (Exception) when (netService != null)
        {
            // Only reachable for a throw *before* the await; Release() is a no-op if the inner catch ran.
            Release();
            throw;
        }
        finally
        {
            MegaCrit.Sts2.Core.Logging.Logger.logLevelTypeMap[LogType.Network] = LogLevel.Info;
            MegaCrit.Sts2.Core.Logging.Logger.logLevelTypeMap[LogType.Actions] = LogLevel.Info;
            MegaCrit.Sts2.Core.Logging.Logger.logLevelTypeMap[LogType.GameSync] = LogLevel.Info;
        }
    }

    /// <summary>The successful half of the takeover, split out so the tail-rejection plumbing stays readable.</summary>
    private static async Task<JoinResult> AwaitJoinAsync(JoinFlow joinFlow, SceneTree sceneTree, bool tailBound)
    {
        JoinResult result = await joinFlow.Begin(sceneTree);
        Entry.Info(
            $"join-flow: accepted state={result.sessionState} mode={result.gameMode} " +
            $"isCustom={result.isCustom}");
        if (tailBound)
        {
            // The binding is per-session, not per-handshake: it has to survive the join. The mid-run restart
            // later reuses this very session and needs the extension frame in both directions (the host holds
            // its LoadJoinRequest until our frame arrives, and we hold its LoadJoinResponse until its frame
            // arrives). Releasing it here leaves the session unable to speak tail at all — which is exactly
            // what the 2026-11-13 field report showed (`prepare skipped ... writerBindings=0` plus an inbound
            // LanConnectNativeBusMessage with no handler). It is released by the mod's own session Dispose,
            // i.e. when the room session really ends, which is also what the upstream flow does.
            Entry.Info("join-flow: tail_v1 extension frame stays bound for the rest of the session.");
        }

        return result;
    }

    /// <summary>Uses the host mod's own factory so the same constructor strategy is kept.</summary>
    private static NetClientGameService CreateClientService()
    {
        Shape shape = _shape
            ?? throw new InvalidOperationException("join-flow: the shape was not captured.");

        object? created = shape.CreateClient.Invoke(null, null);
        return created as NetClientGameService
               ?? throw new InvalidOperationException(
                   $"{shape.CreateClient.DeclaringType?.FullName}.CreateClient() returned " +
                   $"{created?.GetType().Name ?? "<null>"}, expected NetClientGameService.");
    }

    /// <summary>
    /// tail_v1 carries an extra extension frame. A tail_v1 host holds the lobby-join request until it
    /// arrives, so a client that skips this binding would stall until the handshake times out. Binding
    /// is therefore mandatory whenever the frozen selection says tail_v1 — a failure here is fatal, not
    /// something to log and continue past.
    /// </summary>
    private static bool BindTailIfRequired(object flow, NetClientGameService netService)
    {
        Shape shape = _shape
            ?? throw new InvalidOperationException("join-flow: the shape was not captured.");

        object? selection = shape.ProtocolSelection.GetValue(flow);
        if (selection == null)
        {
            // The simple constructor path leaves the selection unset; nothing to bind.
            return false;
        }

        object? profile = shape.SelectionProfile.GetValue(selection)
            ?? throw new InvalidOperationException(
                $"{shape.SelectionProfile.DeclaringType?.FullName}.Profile returned null; " +
                "cannot tell whether this join is tail_v1.");

        // The enum member's own constant is resolved once in TryCapture and compared as an integer, so a rename
        // of either the enum or the member is a startup skip instead of a silently unbound extension frame
        // (which stalls a tail_v1 join until the handshake times out).
        if (Convert.ToInt32(profile, CultureInfo.InvariantCulture) != shape.TailProfileValue)
        {
            return false;
        }

        object offer = shape.ProtocolOffer.GetValue(flow)
            ?? throw new InvalidOperationException(
                "tail_v1 join has no frozen protocol offer; refusing to continue without the extension frame.");
        object? nonce = shape.ProtocolFlowNonce.GetValue(flow)
            ?? throw new InvalidOperationException(
                "tail_v1 join has no protocol flow nonce; refusing to continue without the extension frame.");
        object? peerNativeBusTypeId = shape.PeerNativeBusTypeId.GetValue(flow);

        object shared = shape.Shared.GetValue(null)
            ?? throw new InvalidOperationException($"{TailRuntimeTypeName}.Shared returned null.");

        // Do NOT call BindClient: its nonce parameter is ReadOnlySpan<byte>, which cannot be boxed, so
        // MethodInfo.Invoke always throws ArgumentException. Bind is the private overload BindClient
        // delegates to, and it takes byte[] — the same value, without the span.
        object bus = shape.GetMessageBus.Invoke(shared, new[] { (object?)netService })
            ?? throw new InvalidOperationException("Tail runtime could not resolve the message bus for the client.");

        byte[] nonceBytes = nonce as byte[]
            ?? throw new InvalidOperationException(
                $"tail_v1 join nonce is {nonce.GetType().Name}, expected byte[].");

        if (nonceBytes.Length != shape.FlowNonceBytes)
        {
            throw new InvalidOperationException(
                $"tail_v1 join nonce is {nonceBytes.Length} bytes, expected {shape.FlowNonceBytes}.");
        }

        shape.Bind.Invoke(
            shared,
            new object?[] { netService, bus, offer, selection, false, nonceBytes, peerNativeBusTypeId });

        Entry.Info("join-flow: tail_v1 extension frame bound.");
        return true;
    }


    /// <summary>
    /// The reason upstream disconnects with when a join fails: <c>CancelledJoin</c> (6) when the host mod's own
    /// cancel gesture fired, <c>InternalError</c> (202) otherwise. Source
    /// <c>LanConnectLobbyManagedJoinFlow.cs:174</c>, shipped DLL
    /// <c>dec-official/LanConnectLobbyManagedJoinFlow.cs:148</c>:
    /// <c>NetError reason = (NetError)(CancelToken.IsCancellationRequested ? 6 : 202);</c>
    /// (<c>NetError.cs:39</c> and <c>:99</c>).
    /// </summary>
    private static NetError ModDisconnectReason(object flow)
    {
        try
        {
            return RequireModCancelToken(flow).IsCancellationRequested
                ? NetError.CancelledJoin
                : NetError.InternalError;
        }
        catch (Exception exception)
        {
            // A failed reflection lookup must not change the disconnect reason we would otherwise have sent.
            Entry.Warn(
                "join-flow: could not read the host mod's CancelToken while disconnecting a failed join " +
                $"({exception.GetType().Name}: {exception.Message}); using InternalError.");
            return NetError.InternalError;
        }
    }

    /// <summary>
    /// Reads and *removes* the protocol failure the tail runtime parked for this peer, mirroring upstream's
    /// <c>TryTakeTailRejection</c> (source :629-641, shipped DLL :595-604): tail_v1 only, live net service
    /// only, and the take is destructive — a second call returns false.
    /// </summary>
    private static Exception? TryTakeTailRejection(
        NetClientGameService netService,
        bool tailBound,
        NetErrorInfo info)
    {
        Shape? shape = _shape;
        MethodInfo? take = shape?.TakeValidatedRejection;
        ConstructorInfo? exceptionCtor = shape?.ProtocolExceptionCtor;

        if (!tailBound || shape == null || take == null || exceptionCtor == null)
        {
            // Not a tail session, or Apply already warned that the anchors are missing.
            return null;
        }

        try
        {
            object shared = shape.Shared.GetValue(null)
                ?? throw new InvalidOperationException("The tail runtime Shared property returned null.");
            object?[] arguments = { netService, netService.HostNetId, null };
            bool taken = take.Invoke(shared, arguments) is bool value && value;
            if (!taken || arguments[2] is not { } failure)
            {
                return null;
            }

            Entry.Warn(
                $"join-flow: the disconnect ({info.GetReason()}) carried a validated tail rejection " +
                $"[{DescribeFailure(failure)}]; that is the real reason the join failed.");
            return exceptionCtor.Invoke(new[] { failure }) as Exception;
        }
        catch (Exception exception)
        {
            Entry.Warn(
                "join-flow: could not read the tail rejection for this disconnect " +
                $"({exception.GetType().Name}: {exception.Message}); falling back to the plain disconnect reason.");
            return null;
        }
    }

    /// <summary>Never throws: a diagnostic string must not cost us the rejection we just took.</summary>
    private static string DescribeFailure(object failure)
    {
        try
        {
            string code = Reflect.GetPropertyValue(failure, "Code") as string ?? "<none>";
            string detail = Reflect.GetPropertyValue(failure, "Detail") as string ?? "<none>";
            return $"{code}: {detail}";
        }
        catch (Exception exception)
        {
            return $"<unreadable failure: {exception.GetType().Name}>";
        }
    }

    private static void UnbindTail(NetClientGameService netService)
    {
        Shape shape = _shape
            ?? throw new InvalidOperationException("join-flow: the shape was not captured.");

        object shared = shape.Shared.GetValue(null)
            ?? throw new InvalidOperationException($"{TailRuntimeTypeName}.Shared returned null.");
        shape.Unbind.Invoke(shared, new object?[] { netService });
    }

    /// <summary>
    /// The host mod's own <c>CancellationTokenSource</c> (<c>dec-official/LanConnectLobbyManagedJoinFlow.cs:54</c>).
    /// Throws when it is not one: silently continuing would leave the join un-cancellable.
    /// </summary>
    private static CancellationTokenSource RequireModCancelToken(object flow) =>
        _shape?.CancelToken.GetValue(flow) is CancellationTokenSource token
            ? token
            : throw new InvalidOperationException(
                "LanConnectLobbyManagedJoinFlow.CancelToken is not a CancellationTokenSource; " +
                "the join could no longer be cancelled by the host mod.");

    /// <summary>Keeps the host mod's CancelToken meaningful: its cancel gesture must abort our flow too.</summary>
    private static void LinkCancellation(object flow, JoinFlow joinFlow)
    {
        CancellationTokenSource modToken = RequireModCancelToken(flow);

        if (modToken.IsCancellationRequested)
        {
            joinFlow.CancelToken.Cancel();
            return;
        }

        modToken.Token.Register(
            static state =>
            {
                try
                {
                    ((JoinFlow)state!).CancelToken.Cancel();
                }
                catch
                {
                    // Cancellation must never throw into the caller's continuation.
                }
            },
            joinFlow);
    }
}
