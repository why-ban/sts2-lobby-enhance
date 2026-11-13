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

    /// <summary>
    /// Reflection anchors for the tail-rejection read, resolved once in <see cref="Apply"/>.
    ///
    /// They are deliberately *optional*: <see cref="TryTakeValidatedRejection"/> is a best-effort improvement
    /// over the previous behaviour (a generic "Unexpectedly disconnected ... ModMismatch" for every tail
    /// failure), so a missing anchor is a warning and a fallback, not a startup failure. That is the opposite
    /// of <see cref="BindTailIfRequired"/>, where a missing anchor really does break the handshake.
    /// </summary>
    private static MethodInfo? _takeValidatedRejection;
    private static ConstructorInfo? _protocolExceptionCtor;
    private static Type? _tailRuntimeType;

    public string Id => "join-flow";

    public bool Applies(in CompatSnapshot snapshot) =>
        snapshot.InitialGameInfoLostGameMode || snapshot.JoinRequestLostMaxAscensionUnlocked;

    public void Apply(Harmony harmony, in CompatSnapshot snapshot)
    {
        Assembly host = Reflect.FindAssembly(Entry.HostModAssemblyName)
            ?? throw new InvalidOperationException("Host mod assembly is not loaded.");

        Type flowType = Reflect.RequireType(host, FlowTypeName);
        MethodInfo beginAsync = Reflect.RequireMethodByName(flowType, "BeginAsync", 2);

        // Guard against silently patching something that is not the method we audited.
        ParameterInfo[] parameters = beginAsync.GetParameters();
        if (parameters[0].ParameterType != typeof(object) || parameters[1].ParameterType != typeof(SceneTree))
        {
            throw new MissingMethodException(
                flowType.FullName,
                $"BeginAsync(object, SceneTree) — found ({parameters[0].ParameterType.Name}, {parameters[1].ParameterType.Name})");
        }

        harmony.Patch(
            beginAsync,
            prefix: new HarmonyMethod(typeof(JoinTakeoverFix), nameof(BeginAsyncPrefix))
            {
                priority = Priority.First
            });

        ResolveTailRejectionAnchors(host);

        Entry.Info("join-flow: patched LanConnectLobbyManagedJoinFlow.BeginAsync to run the vanilla JoinFlow.");
    }

    /// <summary>
    /// Resolves <c>LanConnectTailMessageRuntime.TryTakeValidatedRejection(INetGameService, ulong, out failure)</c>
    /// and the <c>LanConnectProtocolException(LanConnectProtocolFailure)</c> constructor. Shipped DLL:
    /// <c>dec-official/LanConnectTailMessageRuntime.cs:770</c> and
    /// <c>dec-official/LanConnectProtocolException.cs:9</c>. Any miss is logged once and leaves the anchors
    /// null, which makes the runtime path fall back to exactly the behaviour we had before.
    /// </summary>
    private static void ResolveTailRejectionAnchors(Assembly host)
    {
        try
        {
            Type runtimeType = Reflect.RequireType(host, TailRuntimeTypeName);
            MethodInfo take = Reflect.RequireMethodByName(runtimeType, TakeRejectionMethodName, 3);

            ParameterInfo[] parameters = take.GetParameters();
            if (!parameters[0].ParameterType.IsAssignableFrom(typeof(NetClientGameService))
                || parameters[1].ParameterType != typeof(ulong)
                || !parameters[2].ParameterType.IsByRef)
            {
                throw new MissingMethodException(
                    runtimeType.FullName,
                    $"{TakeRejectionMethodName}({parameters[0].ParameterType.Name}, {parameters[1].ParameterType.Name}, out ...) " +
                    "— expected (INetGameService, ulong, out LanConnectProtocolFailure?)");
            }

            Type failureType = parameters[2].ParameterType.GetElementType()
                ?? throw new MissingMethodException(runtimeType.FullName, $"{TakeRejectionMethodName} out parameter type");

            Type exceptionType = Reflect.RequireType(host, ProtocolExceptionTypeName);
            ConstructorInfo exceptionCtor = exceptionType
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(ctor =>
                    ctor.GetParameters() is { Length: 1 } ctorParameters
                    && ctorParameters[0].ParameterType == failureType)
                ?? throw new MissingMethodException(
                    exceptionType.FullName,
                    $"{exceptionType.Name}({failureType.Name})");

            _tailRuntimeType = runtimeType;
            _takeValidatedRejection = take;
            _protocolExceptionCtor = exceptionCtor;

            Entry.Info(
                "join-flow: tail rejections will be reported with their own code — " +
                $"{runtimeType.Name}.{TakeRejectionMethodName} and {exceptionType.Name}({failureType.Name}) resolved.");
        }
        catch (Exception exception)
        {
            _tailRuntimeType = null;
            _takeValidatedRejection = null;
            _protocolExceptionCtor = null;
            Entry.Warn(
                "join-flow: could not resolve the tail-rejection anchors " +
                $"({exception.GetType().Name}: {exception.Message}). A tail_v1 disconnect will keep surfacing " +
                "as the generic 'Unexpectedly disconnected from host while joining. Reason: ...' instead of " +
                "its real rejection code; every other part of the join is unaffected.");
        }
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

        try
        {
            NetClientGameService netService = CreateClientService();
            Reflect.SetPropertyValue(flow, "NetService", netService);
            Entry.Info($"join-flow: net service created ({netService.GetType().Name}).");

            bool tailBound = BindTailIfRequired(flow, netService);
            JoinFlow joinFlow = new(netService, initializer);
            LinkCancellation(flow, joinFlow);

            // tail_v1 only: a rejection code travels on the disconnect event, not on the message stream
            // (upstream source :629-641 / shipped DLL :595-604). Take it here, while the tail binding still
            // exists — JoinFlow's own handler will turn the same disconnect into a generic
            // ClientConnectionFailedException, and the take is destructive.
            // Typed as Exception on purpose: LanConnectProtocolException lives in the host mod assembly, which
            // this project deliberately does not reference (see Core/Reflect.cs). The runtime type is what the
            // caller's `catch (LanConnectProtocolException)` branch matches, and `throw` preserves it.
            Exception? rejection = null;
            void OnDisconnected(NetErrorInfo info) => rejection ??= TryTakeTailRejection(netService, tailBound, info);

            if (tailBound)
            {
                netService.Disconnected += OnDisconnected;
            }

            try
            {
                return await AwaitJoinAsync(joinFlow, sceneTree, tailBound);
            }
            catch (Exception exception)
            {
                if (netService.IsConnected)
                {
                    netService.Disconnect(ModDisconnectReason(flow));
                }

                // Only a failed join releases the binding (this is the upstream behaviour).
                if (tailBound)
                {
                    UnbindTail(netService);
                }

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
                    netService.Disconnected -= OnDisconnected;
                }
            }
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
        Assembly host = Reflect.FindAssembly(Entry.HostModAssemblyName)
            ?? throw new InvalidOperationException("Host mod assembly is not loaded.");

        Type factory = Reflect.RequireType(host, "Sts2LanConnect.Scripts.LanConnectNetGameServiceFactory");
        object? created = Reflect.InvokeStatic(factory, "CreateClient");

        return created as NetClientGameService
               ?? throw new InvalidOperationException(
                   $"{factory.FullName}.CreateClient() returned {created?.GetType().Name ?? "<null>"}, " +
                   "expected NetClientGameService.");
    }

    /// <summary>
    /// tail_v1 carries an extra extension frame. A tail_v1 host holds the lobby-join request until it
    /// arrives, so a client that skips this binding would stall until the handshake times out. Binding
    /// is therefore mandatory whenever the frozen selection says tail_v1 — a failure here is fatal, not
    /// something to log and continue past.
    /// </summary>
    private static bool BindTailIfRequired(object flow, NetClientGameService netService)
    {
        object? selection = Reflect.GetField(flow, "_protocolSelection");
        if (selection == null)
        {
            // The simple constructor path leaves the selection unset; nothing to bind.
            return false;
        }

        Assembly host = Reflect.FindAssembly(Entry.HostModAssemblyName)
            ?? throw new InvalidOperationException("Host mod assembly is not loaded.");

        object? profile = selection.GetType()
            .GetProperty("Profile", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(selection)
            ?? throw new InvalidOperationException(
                $"{selection.GetType().FullName}.Profile returned null; cannot tell whether this join is tail_v1.");

        // Upstream compares the enum directly: `_protocolSelection?.Profile == LanConnectProtocolProfile.TailV1`
        // (source LanConnectLobbyManagedJoinFlow.cs:78, shipped DLL dec-official:84). We cannot name the type,
        // so we resolve the enum member's own constant and compare the underlying integers. Comparing
        // `ToString()` instead — what this used to do — silently returned false on any rename and skipped the
        // extension frame, which stalls a tail_v1 join until the handshake times out.
        if (Convert.ToInt32(profile, CultureInfo.InvariantCulture) != ResolveTailProfileValue(host))
        {
            return false;
        }

        Type runtimeType = Reflect.RequireType(host, TailRuntimeTypeName);

        object offer = Reflect.GetField(flow, "_protocolOffer")
            ?? throw new InvalidOperationException(
                "tail_v1 join has no frozen protocol offer; refusing to continue without the extension frame.");
        object? nonce = Reflect.GetField(flow, "_protocolFlowNonce")
            ?? throw new InvalidOperationException(
                "tail_v1 join has no protocol flow nonce; refusing to continue without the extension frame.");
        object? peerNativeBusTypeId = Reflect.GetField(flow, "_peerNativeBusTypeId");

        object shared = Reflect.RequireStaticPropertyValue(runtimeType, "Shared");

        // Do NOT call BindClient: its nonce parameter is ReadOnlySpan<byte>, which cannot be boxed, so
        // MethodInfo.Invoke always throws ArgumentException. Bind is the private overload BindClient
        // delegates to, and it takes byte[] — the same value, without the span.
        object bus = Reflect.RequireMethodByName(runtimeType, "GetMessageBus", 1)
            .Invoke(shared, new[] { (object?)netService })
            ?? throw new InvalidOperationException("Tail runtime could not resolve the message bus for the client.");

        byte[] nonceBytes = nonce as byte[]
            ?? throw new InvalidOperationException(
                $"tail_v1 join nonce is {nonce.GetType().Name}, expected byte[].");

        int expectedNonceBytes = ReadFlowNonceBytes(host);
        if (nonceBytes.Length != expectedNonceBytes)
        {
            throw new InvalidOperationException(
                $"tail_v1 join nonce is {nonceBytes.Length} bytes, expected {expectedNonceBytes}.");
        }

        Reflect.RequireMethodByName(runtimeType, "Bind", 7).Invoke(
            shared,
            new object?[] { netService, bus, offer, selection, false, nonceBytes, peerNativeBusTypeId });

        Entry.Info("join-flow: tail_v1 extension frame bound.");
        return true;
    }

    /// <summary>Reads <c>LanConnectSidecarFrameCodec.FlowNonceBytes</c>, the nonce length BindClient enforces.</summary>
    private static int ReadFlowNonceBytes(Assembly host)
    {
        Type codec = Reflect.RequireType(host, "Sts2LanConnect.Scripts.LanConnectSidecarFrameCodec");
        FieldInfo? field = codec.GetField(
            "FlowNonceBytes",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        if (field?.GetRawConstantValue() is int value)
        {
            return value;
        }

        throw new MissingFieldException(codec.FullName, "FlowNonceBytes");
    }

    /// <summary>
    /// Reads <c>LanConnectProtocolProfile.TailV1</c>'s own constant value (1 in 0.6.3,
    /// <c>dec-official/LanConnectProtocolProfile.cs:3-7</c>) instead of hardcoding it, and throws rather than
    /// guessing when the member is gone: a tail_v1 join that silently skips the extension frame hangs, which
    /// is a far worse failure mode than a loud one.
    /// </summary>
    private static int ResolveTailProfileValue(Assembly host)
    {
        Type profileType = Reflect.RequireType(host, ProfileTypeName);
        FieldInfo field = profileType.GetField(
                             TailProfileName,
                             BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                         ?? throw new MissingFieldException(profileType.FullName, TailProfileName);

        if (field.GetRawConstantValue() is not { } constant)
        {
            throw new MissingFieldException(
                profileType.FullName,
                $"{TailProfileName} (the member exists but is not a constant)");
        }

        return Convert.ToInt32(constant, CultureInfo.InvariantCulture);
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
        MethodInfo? take = _takeValidatedRejection;
        ConstructorInfo? exceptionCtor = _protocolExceptionCtor;

        if (!tailBound || take == null || exceptionCtor == null)
        {
            // Not a tail session, or Apply already warned that the anchors are missing.
            return null;
        }

        try
        {
            object shared = RequireSharedRuntime();
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

    private static object RequireSharedRuntime()
    {
        if (_tailRuntimeType == null)
        {
            throw new InvalidOperationException(
                "The tail runtime type was not resolved; tail rejections cannot be read.");
        }

        return Reflect.RequireStaticPropertyValue(_tailRuntimeType, "Shared");
    }

    private static void UnbindTail(NetClientGameService netService)
    {
        Assembly host = Reflect.FindAssembly(Entry.HostModAssemblyName)
            ?? throw new InvalidOperationException("Host mod assembly is not loaded.");
        Type runtimeType = Reflect.RequireType(host, TailRuntimeTypeName);

        object shared = Reflect.RequireStaticPropertyValue(runtimeType, "Shared");
        Reflect.RequireMethodByName(runtimeType, "Unbind", 1).Invoke(shared, new object?[] { netService });
    }

    /// <summary>
    /// The host mod's own <c>CancellationTokenSource</c> (<c>dec-official/LanConnectLobbyManagedJoinFlow.cs:54</c>).
    /// Throws when it is not one: silently continuing would leave the join un-cancellable.
    /// </summary>
    private static CancellationTokenSource RequireModCancelToken(object flow) =>
        Reflect.GetPropertyValue(flow, "CancelToken") is CancellationTokenSource token
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
