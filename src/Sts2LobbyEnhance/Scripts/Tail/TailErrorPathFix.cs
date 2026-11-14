using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace Sts2LobbyEnhance;

/// <summary>
/// Repairs #3 and #4: the tail_v1 error paths.
///
/// <c>LanConnectTailMessageRuntime.AbortActiveBinding</c> and <c>RejectAndDisconnect</c> are the only
/// two methods in the host mod that reference the 0.113-changed
/// <c>NetHostGameService.ConnectedPeers</c> (List to IReadOnlyList) and
/// <c>NetHostGameService.DisconnectClient</c> (three parameters to four). Like the join flow they cannot
/// be patched at all — Harmony cannot even read their IL — but unlike the join flow they have no async
/// stub to intercept.
///
/// They do, however, have readable callers. So we redirect every call to them at the IL level and run a
/// faithful reimplementation written against the 0.113 signatures. The bodies below are line-for-line
/// ports of the upstream originals; the only deliberate difference is that the host API calls use the
/// new signatures.
///
/// These paths only run when something has already gone wrong (protocol rejection, transport failure),
/// which is exactly when a crash is least acceptable: it would take the whole session down instead of
/// disconnecting one peer.
/// </summary>
internal sealed class TailErrorPathFix : ICompatFix
{
    private const string RuntimeTypeName = "Sts2LanConnect.Scripts.LanConnectTailMessageRuntime";
    private const string BindingTypeName = "Binding";
    private const string ProtocolFailureTypeName = "Sts2LanConnect.Scripts.LanConnectProtocolFailure";
    private const string KickPatchesTypeName = "Sts2LanConnect.Scripts.LanConnectRemoteLobbyPlayerPatches";
    private const string LobbyRuntimeTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyRuntime";
    private const string KickTargetTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyKickTarget";

    /// <summary>Same member filter as <see cref="Reflect"/> uses, for the reflective lookups below.</summary>
    private const BindingFlags AnyMember =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Every anchor this repair needs, resolved once in <see cref="Apply"/> before anything is patched.
    ///
    /// The two patched methods are matched by *parameter types*, not by arity: <see cref="Reflect"/>'s
    /// <c>RequireMethodByName</c> only compares parameter counts (Core/Reflect.cs:60-76), and a same-arity rename
    /// upstream would otherwise bind the redirect to the wrong method and silently rewrite its call sites.
    ///
    /// The kick anchors are optional: they repair a different upstream feature (the 1.5 s delayed disconnect on
    /// kick), so losing them must not cost the tail error paths. Their absence is a note, not a skip.
    /// </summary>
    internal sealed class Shape
    {
        internal Type RuntimeType = null!;

        internal Type BindingType = null!;

        internal MethodInfo Abort = null!;

        internal MethodInfo Reject = null!;

        internal FieldInfo Sync = null!;

        internal FieldInfo Bindings = null!;

        internal FieldInfo WriterBindings = null!;

        internal FieldInfo OutgoingRejections = null!;

        internal MethodInfo ClearPendingOutgoing = null!;

        internal MethodInfo TryMarkTerminated = null!;

        internal MethodInfo ClearNative = null!;

        internal PropertyInfo BindingService = null!;

        internal PropertyInfo BindingWriter = null!;

        internal PropertyInfo BindingMessageBus = null!;

        internal PropertyInfo BindingBoundPeerIds = null!;

        internal MethodInfo? ScheduleDelayedDisconnect;

        internal MethodInfo? GetHostNetService;

        internal MethodInfo? CreateKickDisconnectAction;

        internal string? OptionalNote;
    }

    internal static Shape? _shape;

    /// <summary>
    /// The two host methods <see cref="RedirectTranspiler"/> rewrites calls to. Harmony binds a transpiler
    /// through <c>HarmonyMethod(Type, string)</c>, which only accepts a static method, so the targets are
    /// handed over through these fields instead of a closure. They are written once in <see cref="Apply"/>
    /// and only read while Harmony runs the transpiler (synchronously, inside <c>harmony.Patch</c>).
    /// </summary>
    private static MethodInfo? _abortTarget;

    private static MethodInfo? _rejectTarget;

    /// <summary>
    /// How many call instructions <see cref="RedirectTranspiler"/> has rewritten. A patched caller that
    /// contributes zero here means Harmony attached the patch but the rewrite did not take effect — the one
    /// outcome that must never be reported as success.
    /// </summary>
    private static int _rewrittenCallInstructions;

    /// <summary>
    /// The last protocol failure the tail runtime rejected a frame with, as "<c>code: detail</c>", or null when
    /// nothing has been rejected in this process. Timeout paths quote it so a hang is never reported without the
    /// reason that caused it.
    /// </summary>
    public static string? LastRejection { get; private set; }

    /// <summary>
    /// One line for a timeout report. "none" is itself informative: it means the tail runtime never rejected
    /// anything, so the frame either never arrived or was dropped before validation — a different bug from a
    /// rejection, and previously indistinguishable in the log.
    /// </summary>
    public static string DescribeLastRejection() =>
        LastRejection == null
            ? "tailRejection=none (no frame was rejected, so it never reached the tail validation)."
            : $"tailRejection={LastRejection}";

    public string Id => "tail-error-paths";

    public FixGroup Group => FixGroup.Tail;

    /// <summary>
    /// Needed when the two members the redirected bodies use have changed: <c>DisconnectClient</c>'s fourth
    /// parameter and <c>ConnectedPeers</c> no longer being a <c>List&lt;NetClientData&gt;</c>. Those two flags are
    /// this repair's own precondition, not a global "is this 0.113" gate: if a future build restores either
    /// signature, the host mod's own methods JIT again and the redirect becomes dead weight.
    /// </summary>
    public bool Applies(in CompatSnapshot snapshot, out string reason)
    {
        if (!snapshot.DisconnectClientGainedParameter && !snapshot.ConnectedPeersChangedType)
        {
            reason = "NetHostGameService.DisconnectClient still takes three parameters and ConnectedPeers is " +
                     "still a List<NetClientData>, so the tail runtime's own methods JIT without help";
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
                "tail-error-paths: Apply ran without a captured shape; Applies must be consulted first.");

        // Plan every call-site rewrite before installing any of them (2026-11-14 gating audit, A2). The previous
        // order patched while it walked, so a throw halfway through left the earlier callers patched and the
        // later ones not — a state only the group-level UnpatchAll could clean up.
        var plans = new List<(MethodBase Caller, int Rewrites)>();
        var unreadable = new List<string>();

        foreach (MethodBase caller in EnumerateMethods(shape.RuntimeType))
        {
            List<CodeInstruction>? original;
            try
            {
                original = PatchProcessor.GetOriginalInstructions(caller);
            }
            catch (Exception)
            {
                // Open generic types and the two broken methods themselves land here.
                unreadable.Add(caller.Name);
                continue;
            }

            int rewrites = CountRedirectable(original, shape.Abort, shape.Reject);
            if (rewrites == 0)
            {
                continue;
            }

            plans.Add((caller, rewrites));
        }

        if (plans.Count == 0)
        {
            // Fail alone rather than install nothing: the interface contract prefers a repair that reports itself
            // broken over one that silently does nothing while the log claims the feature is there.
            throw new InvalidOperationException(
                "tail-error-paths: no readable caller of AbortActiveBinding/RejectAndDisconnect still calls them " +
                $"({unreadable.Count} unreadable/generic method(s)); the tail runtime no longer routes anything " +
                "through the two methods this repair replaces.");
        }

        _abortTarget = shape.Abort;
        _rejectTarget = shape.Reject;
        _rewrittenCallInstructions = 0;

        foreach (var plan in plans)
        {
            int before = _rewrittenCallInstructions;

            harmony.Patch(
                plan.Caller,
                transpiler: new HarmonyMethod(typeof(TailErrorPathFix), nameof(RedirectTranspiler))
                {
                    priority = Priority.First
                });

            // Verify per call site, not in total. Two callers can cancel out (one short, one long) and the sum
            // then hides a method that still calls the broken 0.111 original — the crash would resurface only
            // when that particular error path runs. A mismatch throws: Entry catches it and unpatchAll's this
            // whole group, which restores every caller's original IL, so the loud failure is also a clean one.
            int actual = _rewrittenCallInstructions - before;
            if (actual != plan.Rewrites)
            {
                throw new InvalidOperationException(
                    $"{Id}: {plan.Caller.DeclaringType?.Name}::{plan.Caller.Name} was expected to call the two " +
                    $"replaced methods {plan.Rewrites} time(s) but the transpiler rewrote {actual}. Another " +
                    "transpiler probably changed this method first; the redirect cannot be trusted.");
            }
        }

        Entry.Info(
            $"tail-error-paths: redirected {plans.Count} call site method(s) " +
            $"[{string.Join(", ", plans.Select(plan => plan.Caller.DeclaringType?.Name + "::" + plan.Caller.Name))}] " +
            $"(skipped {unreadable.Count} unreadable/generic, rewrote {_rewrittenCallInstructions} call instruction(s)).");

        InstallKickDisconnectRedirect(harmony, shape);
    }

    /// <summary>
    /// Resolves every anchor before anything is patched. Never throws, never invokes, never touches Godot: pure
    /// metadata over the already-loaded host assembly. All misses are collected into one reason string.
    ///
    /// The two patched methods are matched by parameter type, not by arity: Core/Reflect.cs:60-76 only compares
    /// parameter counts, and a same-arity rename upstream would otherwise bind the redirect to the wrong method.
    /// </summary>
    private static bool TryCapture(Assembly host, out Shape shape, out string reason)
    {
        shape = new Shape();
        var missing = new List<string>();

        Type? runtimeType = host.GetType(RuntimeTypeName, throwOnError: false, ignoreCase: false);
        if (runtimeType == null)
        {
            missing.Add("type " + RuntimeTypeName);
            reason = string.Join("; ", missing);
            return false;
        }

        shape.RuntimeType = runtimeType;

        Type? bindingType = runtimeType.GetNestedType(BindingTypeName, AnyMember);
        if (bindingType == null)
        {
            missing.Add($"{RuntimeTypeName}.{BindingTypeName}");
        }
        else
        {
            shape.BindingType = bindingType;
        }

        CapturePatchedMethods(runtimeType, bindingType, shape, missing);
        CaptureRuntimeState(runtimeType, bindingType, shape, missing);
        CaptureOptionalKickAnchors(host, shape);

        reason = string.Join(", ", missing);
        return missing.Count == 0;
    }

    private static void CapturePatchedMethods(Type runtimeType, Type? bindingType, Shape shape, List<string> missing)
    {
        MethodInfo? abort = ReflectScope.MethodOf(runtimeType, "AbortActiveBinding", 3, missing);
        if (abort != null)
        {
            ParameterInfo[] parameters = abort.GetParameters();
            if (bindingType == null
                || parameters[0].ParameterType != bindingType
                || parameters[1].ParameterType != typeof(string)
                || parameters[2].ParameterType != typeof(Exception))
            {
                missing.Add(
                    $"{RuntimeTypeName}.AbortActiveBinding({bindingType?.Name ?? "?"}, string, Exception) — found " +
                    $"({Describe(parameters)})");
            }
            else
            {
                shape.Abort = abort;
            }
        }

        MethodInfo? reject = ReflectScope.MethodOf(runtimeType, "RejectAndDisconnect", 3, missing);
        if (reject != null)
        {
            ParameterInfo[] parameters = reject.GetParameters();
            if (bindingType == null
                || parameters[0].ParameterType != bindingType
                || parameters[1].ParameterType != typeof(ulong)
                || !string.Equals(parameters[2].ParameterType.FullName, ProtocolFailureTypeName, StringComparison.Ordinal))
            {
                missing.Add(
                    $"{RuntimeTypeName}.RejectAndDisconnect({bindingType?.Name ?? "?"}, ulong, " +
                    $"{ProtocolFailureTypeName}) — found ({Describe(parameters)})");
            }
            else
            {
                shape.Reject = reject;
            }
        }
    }

    private static void CaptureRuntimeState(Type runtimeType, Type? bindingType, Shape shape, List<string> missing)
    {
        shape.Sync = ReflectScope.FieldOf(runtimeType, "_sync", missing)!;
        shape.Bindings = ReflectScope.FieldOf(runtimeType, "_bindings", missing)!;
        shape.WriterBindings = ReflectScope.FieldOf(runtimeType, "_writerBindings", missing)!;
        shape.OutgoingRejections = ReflectScope.FieldOf(runtimeType, "_outgoingRejections", missing)!;
        shape.ClearPendingOutgoing = ReflectScope.MethodOf(runtimeType, "ClearPendingOutgoing", 1, missing)!;

        // [_ThreadStatic] private static: GetOrCreateRejectionStack reads it with GetValue(null). If upstream ever
        // makes it an instance field that call throws TargetException, and nothing above it in the rejection path
        // catches — every inbound failure would rethrow out of the message pump. Cheap to assert, so assert it
        // here instead of relying on a Debug-only check that Release builds compile away.
        if (shape.OutgoingRejections != null && !shape.OutgoingRejections.IsStatic)
        {
            missing.Add($"{RuntimeTypeName}._outgoingRejections (static, found an instance field)");
        }

        if (shape.OutgoingRejections != null)
        {
            Type stackType = shape.OutgoingRejections.FieldType;
            if (ReflectScope.MethodOf(stackType, "Push", 1, new List<string>()) == null)
            {
                missing.Add($"{stackType.FullName}.Push/1");
            }

            if (ReflectScope.MethodOf(stackType, "Pop", 0, new List<string>()) == null)
            {
                missing.Add($"{stackType.FullName}.Pop/0");
            }
        }

        if (bindingType == null)
        {
            return;
        }

        shape.BindingService = ReflectScope.PropertyOf(bindingType, "Service", missing)!;
        shape.BindingWriter = ReflectScope.PropertyOf(bindingType, "Writer", missing)!;
        shape.BindingMessageBus = ReflectScope.PropertyOf(bindingType, "MessageBus", missing)!;
        shape.BindingBoundPeerIds = ReflectScope.PropertyOf(bindingType, "BoundPeerIds", missing)!;
        shape.TryMarkTerminated = ReflectScope.MethodOf(bindingType, "TryMarkTerminated", 0, missing)!;
        shape.ClearNative = ReflectScope.MethodOf(bindingType, "ClearNative", 0, missing)!;
    }

    /// <summary>
    /// The kick anchors repair a different upstream feature, so they are optional: losing them must not cost the
    /// tail error paths. A miss is recorded on <see cref="Shape.OptionalNote"/> and logged by <see cref="Apply"/>.
    /// </summary>
    private static void CaptureOptionalKickAnchors(Assembly host, Shape shape)
    {
        try
        {
            Type? patchesType = host.GetType(KickPatchesTypeName, throwOnError: false, ignoreCase: false);
            if (patchesType == null)
            {
                shape.OptionalNote = KickNote("type " + KickPatchesTypeName);
                return;
            }

            MethodInfo? schedule = ReflectScope.MethodOf(patchesType, "ScheduleDelayedDisconnect", 3, new List<string>());
            if (schedule == null)
            {
                shape.OptionalNote = KickNote($"{KickPatchesTypeName}.ScheduleDelayedDisconnect/3");
                return;
            }

            ParameterInfo[] parameters = schedule.GetParameters();
            if (!string.Equals(parameters[0].ParameterType.FullName, LobbyRuntimeTypeName, StringComparison.Ordinal)
                || parameters[1].ParameterType != typeof(ulong)
                || !string.Equals(parameters[2].ParameterType.FullName, KickTargetTypeName, StringComparison.Ordinal))
            {
                shape.OptionalNote = KickNote(
                    $"{KickPatchesTypeName}.ScheduleDelayedDisconnect({LobbyRuntimeTypeName}, ulong, " +
                    $"{KickTargetTypeName}) — found ({Describe(parameters)})");
                return;
            }

            Type? lobbyRuntime = host.GetType(LobbyRuntimeTypeName, throwOnError: false, ignoreCase: false);
            MethodInfo? getHostService = lobbyRuntime == null
                ? null
                : ReflectScope.MethodOf(lobbyRuntime, "GetHostNetService", 0, new List<string>());
            MethodInfo? createAction = lobbyRuntime == null
                ? null
                : ReflectScope.MethodOf(lobbyRuntime, "CreateKickDisconnectAction", 1, new List<string>());

            if (getHostService == null || createAction == null)
            {
                shape.OptionalNote = KickNote(
                    $"{LobbyRuntimeTypeName}.GetHostNetService/0 or CreateKickDisconnectAction/1");
                return;
            }

            shape.ScheduleDelayedDisconnect = schedule;
            shape.GetHostNetService = getHostService;
            shape.CreateKickDisconnectAction = createAction;
        }
        catch (Exception exception)
        {
            shape.OptionalNote = KickNote($"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static string KickNote(string detail) =>
        "kick redirect not installed (" + detail + "). Kicking a player still runs the host mod's own path, which " +
        "cannot be JITted against this game build and will throw instead of disconnecting; the tail error paths " +
        "are unaffected.";

    private static string Describe(ParameterInfo[] parameters) =>
        string.Join(", ", parameters.Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name));

    private static IEnumerable<MethodBase> EnumerateMethods(Type type)
    {
        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly;

        var pending = new Queue<Type>();
        pending.Enqueue(type);
        var seen = new HashSet<Type>();

        while (pending.Count > 0)
        {
            Type current = pending.Dequeue();
            if (!seen.Add(current))
            {
                continue;
            }

            foreach (MethodBase method in current.GetMethods(flags))
            {
                if (!method.IsAbstract && method.GetMethodBody() != null)
                {
                    yield return method;
                }
            }

            foreach (Type nested in current.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                pending.Enqueue(nested);
            }
        }
    }

    /// <summary>
    /// Counts the call instructions <see cref="RedirectTranspiler"/> would rewrite. This is the dry run: it lets
    /// <see cref="Apply"/> prove a caller is worth patching before any patch is installed (A2).
    /// </summary>
    private static int CountRedirectable(IEnumerable<CodeInstruction> instructions, MethodInfo abort, MethodInfo reject)
    {
        int count = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (Classify(instruction, abort, reject) != Redirect.None)
            {
                count++;
            }
        }

        return count;
    }

    private static bool SameMethod(MethodInfo left, MethodInfo right) =>
        left.MetadataToken == right.MetadataToken && left.Module == right.Module;

    private enum Redirect
    {
        None,
        Abort,
        Reject
    }

    /// <summary>
    /// The one predicate the dry run and the real pass both use, so they can never disagree about what counts.
    ///
    /// It checks the opcode, not just the operand: `ldftn` also carries a MethodInfo, and replacing a function
    /// pointer with `call` changes the stack shape (call consumes the receiver and arguments and pushes nothing,
    /// ldftn pushes a pointer and consumes nothing) — the method would throw InvalidProgramException on every
    /// execution. `callvirt` is kept as `callvirt` for the same reason (null-check semantics).
    /// </summary>
    private static Redirect Classify(CodeInstruction instruction, MethodInfo abort, MethodInfo reject)
    {
        if (instruction.opcode != OpCodes.Call && instruction.opcode != OpCodes.Callvirt)
        {
            return Redirect.None;
        }

        if (instruction.operand is not MethodInfo called)
        {
            return Redirect.None;
        }

        if (SameMethod(called, abort))
        {
            return Redirect.Abort;
        }

        return SameMethod(called, reject) ? Redirect.Reject : Redirect.None;
    }

    /// <summary>
    /// Swaps the call target while leaving the argument stack untouched: the replacements take the same
    /// number of stack slots in the same order, only the receiver is widened to <see cref="object"/>.
    /// </summary>
    private static IEnumerable<CodeInstruction> RedirectTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo abort = _abortTarget
            ?? throw new InvalidOperationException(
                "AbortActiveBinding call target is not set; the transpiler ran outside Apply.");
        MethodInfo reject = _rejectTarget
            ?? throw new InvalidOperationException(
                "RejectAndDisconnect call target is not set; the transpiler ran outside Apply.");

        MethodInfo abortReplacement = typeof(TailErrorPathFix).GetMethod(nameof(AbortActiveBinding))!;
        MethodInfo rejectReplacement = typeof(TailErrorPathFix).GetMethod(nameof(RejectAndDisconnect))!;

        foreach (CodeInstruction instruction in instructions)
        {
            Redirect redirect = Classify(instruction, abort, reject);
            if (redirect == Redirect.None)
            {
                yield return instruction;
                continue;
            }

            _rewrittenCallInstructions++;

            // Keep the original opcode (call stays call, callvirt stays callvirt) and carry the original
            // instruction's labels and exception-block boundaries across. `new CodeInstruction(opcode, operand)`
            // starts with empty label/block lists, so dropping them would leave any branch target that lands on
            // this instruction unmarked — Harmony then fails the emit, or worse, produces an illegal branch.
            var replacement = new CodeInstruction(
                instruction.opcode,
                redirect == Redirect.Abort ? abortReplacement : rejectReplacement);
            replacement.labels.AddRange(instruction.labels);
            replacement.blocks.AddRange(instruction.blocks);
            yield return replacement;
        }
    }

    // ------------------------------------------------------------------ replacements

    /// <summary>
    /// Reads a string member off the host mod's <c>LanConnectProtocolFailure</c> without taking a compile-time
    /// dependency on it. Returns a placeholder instead of throwing: this runs inside an error path, and losing the
    /// reason because the shape moved would put us back to a silent hang.
    /// </summary>
    private static string DescribeFailureMember(object failure, string member)
    {
        try
        {
            Type type = failure.GetType();

            // Property first, but only when it exists. Reflect.GetPropertyValue throws MissingMemberException
            // for an absent member (Reflect.cs:38-40), so the old "??"-fallback never reached the field
            // lookup; worse, a property that exists and holds null (LanConnectProtocolFailure.Detail is
            // declared with a null default, LanConnectProtocolFailure.cs:6) fell through to GetField and
            // was reported as "<unreadable Detail: MissingMemberException>" instead of "<none>".
            PropertyInfo? property = type.GetProperty(member, AnyMember);
            if (property != null)
            {
                return DescribeValue(property.GetValue(failure));
            }

            FieldInfo? field = type.GetField(member, AnyMember);
            if (field != null)
            {
                return DescribeValue(field.GetValue(failure));
            }

            return $"<unreadable {member}: MissingMemberException>";
        }
        catch (Exception exception)
        {
            return $"<unreadable {member}: {exception.GetType().Name}>";
        }
    }

    private static string DescribeValue(object? value) =>
        value?.ToString() is { Length: > 0 } text ? text : "<none>";

    /// <summary>
    /// Port of <c>LanConnectTailMessageRuntime.RejectAndDisconnect</c>.
    /// The only change from upstream is <c>DisconnectClient(peer, reason, now, debugReason)</c>.
    /// </summary>
    public static void RejectAndDisconnect(object runtime, object binding, ulong senderPeerId, object failure)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(failure);

        Shape shape = _shape
            ?? throw new InvalidOperationException(
                "tail-error-paths: the shape was not captured, so the redirect cannot run.");

        object? service = shape.BindingService.GetValue(binding);

        // Upstream's rejection is completely silent: it sends a rejection message and disconnects, and nothing in
        // the log says which validation refused the frame. That made a 2026-11-13 field report (client receives
        // ClientLoadJoinResponseMessage + its extension frame, then hangs for 15 s with no explanation) impossible
        // to diagnose from logs alone. Record and log the code/detail so the reason is always visible, and keep it
        // in LastRejection so a caller that times out can quote it instead of only reporting the timeout.
        string rejectionCode = DescribeFailureMember(failure, "Code");
        string rejectionDetail = DescribeFailureMember(failure, "Detail");
        LastRejection = $"{rejectionCode}: {rejectionDetail}";
        Entry.Error(
            $"tail-error-paths: the tail runtime rejected a frame from peer {senderPeerId} " +
            $"(service={service?.GetType().Name ?? "<null>"}); reason={LastRejection}. " +
            "The frame was received but will not reach its handler.");

        if (service is NetHostGameService host)
        {
            // Upstream pushes a failure onto a per-runtime Stack before sending, so that the send path can
            // recognise its own rejection message. Stack<T> is not an IList, so drive it through reflection
            // and never replace an existing stack — doing so would drop pending rejections.
            object? rejections = GetOrCreateRejectionStack(runtime);
            MethodInfo? push = rejections?.GetType().GetMethod("Push");
            MethodInfo? pop = rejections?.GetType().GetMethod("Pop");

            // A null stack means the probe found _outgoingRejections to be non-static. Degrade to "send the
            // rejection without the call-context marker": upstream's send path then cannot recognise its own
            // message, but the peer is still disconnected, which is the part that matters.
            bool useContext = rejections != null && push != null && pop != null;
            if (useContext)
            {
                push!.Invoke(rejections, new[] { failure });
            }

            try
            {
                host.SendMessage(
                    new MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby.InitialGameInfoMessage
                    {
                        connectionFailureReason = ConnectionFailureReason.ModMismatch
                    },
                    senderPeerId);
            }
            finally
            {
                if (useContext)
                {
                    object? popped = pop!.Invoke(rejections, null);
                    if (!ReferenceEquals(popped, failure))
                    {
                        throw new InvalidOperationException("Tail rejection call context was corrupted.");
                    }
                }

                host.DisconnectClient(senderPeerId, NetError.ModMismatch);
            }

            return;
        }

        MethodInfo disconnect = service!.GetType().GetMethod("Disconnect", new[] { typeof(NetError), typeof(bool) })
            ?? service.GetType().GetMethod("Disconnect", new[] { typeof(NetError) })
            ?? throw new MissingMethodException(service.GetType().FullName, "Disconnect(NetError)");

        object?[] arguments = disconnect.GetParameters().Length == 2
            ? new object?[] { NetError.ModMismatch, false }
            : new object?[] { NetError.ModMismatch };
        disconnect.Invoke(service, arguments);
    }

    private static object? GetOrCreateRejectionStack(object runtime)
    {
        Shape shape = _shape
            ?? throw new InvalidOperationException("tail-error-paths: the shape was not captured.");

        FieldInfo field = shape.OutgoingRejections;

        // Upstream declares it "[ThreadStatic] private static Stack<LanConnectProtocolFailure>?" and the probe
        // verified Push/Pop exist and that it is static. GetValue/SetValue ignore the instance argument for static
        // fields, so pass null: an instance argument would suggest a per-binding field and hide that the stack is
        // per-thread.
        //
        // A real check, not Debug.Assert: Debug.Assert is [Conditional("DEBUG")] and this ships as Release, so the
        // assertion produced no code at all and an instance field would have thrown TargetException right here.
        if (!field.IsStatic)
        {
            Entry.Warn(
                "tail-error-paths: _outgoingRejections is no longer static; the per-thread rejection stack " +
                "cannot be used. " +
                "Tail rejections will lose their detail, the session stays usable.");
            return null;
        }

        if (field.GetValue(null) is { } existing)
        {
            return existing;
        }

        object created = Activator.CreateInstance(field.FieldType)
            ?? throw new InvalidOperationException($"Could not create {field.FieldType.FullName}.");
        field.SetValue(null, created);
        return created;
    }

    /// <summary>
    /// Port of <c>LanConnectTailMessageRuntime.AbortActiveBinding</c>.
    /// The only change from upstream is that <c>ConnectedPeers</c> is now an
    /// <c>IReadOnlyList</c> and <c>DisconnectClient</c> takes a fourth argument.
    /// </summary>
    public static void AbortActiveBinding(object runtime, object binding, string reason, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(exception);

        Shape shape = _shape
            ?? throw new InvalidOperationException(
                "tail-error-paths: the shape was not captured, so the redirect cannot run.");

        object sync = shape.Sync.GetValue(runtime)
            ?? throw new InvalidOperationException("Tail runtime _sync is null.");

        ulong[] peerIds;

        lock (sync)
        {
            object? terminated = shape.TryMarkTerminated.Invoke(binding, null);
            if (terminated is false)
            {
                return;
            }

            RemoveBindingIfCurrent(shape.Bindings, shape.BindingMessageBus, runtime, binding);
            RemoveBindingIfCurrent(shape.WriterBindings, shape.BindingWriter, runtime, binding);

            object writer = shape.BindingWriter.GetValue(binding)
                ?? throw new InvalidOperationException("Tail binding has no writer.");
            shape.ClearPendingOutgoing.Invoke(runtime, new[] { writer });

            var peers = new List<ulong>();
            if (shape.BindingBoundPeerIds.GetValue(binding) is System.Collections.IEnumerable bound)
            {
                foreach (object? peer in bound)
                {
                    if (peer is ulong id)
                    {
                        peers.Add(id);
                    }
                }
            }

            if (shape.BindingService.GetValue(binding) is NetHostGameService host)
            {
                foreach (NetClientData peer in host.ConnectedPeers)
                {
                    peers.Add(peer.peerId);
                }
            }

            peerIds = peers.Distinct().ToArray();

            shape.ClearNative.Invoke(binding, null);
        }

        Entry.Error(
            $"sts2_lan_connect tail: terminating active Native Tail binding reason={reason} " +
            $"exception={exception.GetType().FullName} hresult={exception.HResult}.");

        if (shape.BindingService.GetValue(binding) is NetHostGameService netHost)
        {
            foreach (ulong peerId in peerIds)
            {
                try
                {
                    netHost.DisconnectClient(peerId, NetError.ModMismatch);
                }
                catch (Exception disconnectException)
                {
                    Entry.Warn(
                        "sts2_lan_connect tail: host peer disconnect failed during binding termination: " +
                        $"{disconnectException.GetType().Name}: {disconnectException.Message}");
                }
            }

            return;
        }

        // Client side: upstream disconnects the service itself. Without this the binding is marked
        // terminated while the connection stays up, and the client hangs on a dead session.
        try
        {
            object service = shape.BindingService.GetValue(binding)
                ?? throw new InvalidOperationException("Tail binding has no service.");
            MethodInfo disconnect = service.GetType()
                .GetMethod("Disconnect", new[] { typeof(NetError), typeof(bool) })
                ?? service.GetType().GetMethod("Disconnect", new[] { typeof(NetError) })
                ?? throw new MissingMethodException(service.GetType().FullName, "Disconnect(NetError)");

            object?[] arguments = disconnect.GetParameters().Length == 2
                ? new object?[] { NetError.ModMismatch, false }
                : new object?[] { NetError.ModMismatch };
            disconnect.Invoke(service, arguments);
        }
        catch (Exception disconnectException)
        {
            Entry.Warn(
                "sts2_lan_connect tail: client disconnect failed during binding termination: " +
                $"{disconnectException.GetType().Name}: {disconnectException.Message}");
        }
    }

    /// <summary>
    /// The host's kick flow waits 1.5s on a Godot timer and then disconnects the peer from the timer
    /// callback. That callback is a closure whose IL references the removed three-parameter
    /// <c>DisconnectClient</c>, so it can never be JITted: kicking a player would throw instead of
    /// disconnecting them, and the ENet peer would stay connected. The scheduling method itself is
    /// readable, so we replace it with the same logic written against the 0.113 signature.
    /// </summary>
    private static void InstallKickDisconnectRedirect(Harmony harmony, Shape shape)
    {
        if (shape.ScheduleDelayedDisconnect == null)
        {
            if (shape.OptionalNote != null)
            {
                Entry.Warn("tail-error-paths: " + shape.OptionalNote);
            }

            return;
        }

        harmony.Patch(
            shape.ScheduleDelayedDisconnect,
            prefix: new HarmonyMethod(typeof(TailErrorPathFix), nameof(ScheduleDelayedDisconnectPrefix))
            {
                priority = Priority.First
            });

        Entry.Info("tail-error-paths: kick disconnect redirected to the 0.113 DisconnectClient signature.");
    }

    // ReSharper disable once UnusedParameter.Local — Harmony binds by position.
    public static bool ScheduleDelayedDisconnectPrefix(object runtime, ulong targetNetId, object target)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(target);

        Shape shape = _shape
            ?? throw new InvalidOperationException("tail-error-paths: the shape was not captured.");

        object? service = shape.GetHostNetService!.Invoke(runtime, null);
        if (service is not NetHostGameService hostService)
        {
            return false;
        }

        if (Godot.Engine.GetMainLoop() is not Godot.SceneTree tree)
        {
            return false;
        }

        object? action = shape.CreateKickDisconnectAction!.Invoke(runtime, new[] { target });
        if (action is not Func<Action, bool> disconnectIfCurrent)
        {
            throw new InvalidOperationException(
                "LanConnectLobbyRuntime.CreateKickDisconnectAction no longer returns Func<Action, bool>; " +
                "the kick flow cannot be redirected safely.");
        }

        Godot.SceneTreeTimer timer = tree.CreateTimer(1.5);
        timer.Timeout += () =>
        {
            try
            {
                bool disconnected = disconnectIfCurrent(
                    () => hostService.DisconnectClient(targetNetId, NetError.Quit, now: false));

                if (!disconnected)
                {
                    // Upstream wording (dec-official LanConnectRemoteLobbyPlayerPatches.cs:286) carries the
                    // binding id as well; without it a cancelled kick cannot be matched to a lobby binding.
                    Entry.Info(
                        $"sts2_lan_connect kick: delayed ENet disconnect cancelled for netId={targetNetId} " +
                        $"bindingId={DescribeKickBindingId(target)}");
                    return;
                }

                // Upstream logs the success branch without a binding id (dec-official:290); kept as is.
                Entry.Info($"sts2_lan_connect kick: delayed ENet disconnect for netId={targetNetId}");
            }
            catch (Exception ex)
            {
                Entry.Warn($"sts2_lan_connect kick: delayed ENet disconnect failed: {ex.Message}");
            }
        };

        return false;
    }

    /// <summary>
    /// Mirrors upstream's <c>target.BindingId ?? "&lt;legacy&gt;"</c> (dec-official
    /// LanConnectRemoteLobbyPlayerPatches.cs:286). Read reflectively because this patch has no compile-time
    /// reference to the host assembly; a missing property degrades to the same "&lt;legacy&gt;" upstream prints
    /// for a null binding id, never to an exception in a timer callback.
    /// </summary>
    private static string DescribeKickBindingId(object target) =>
        target.GetType().GetProperty("BindingId", AnyMember)?.GetValue(target) as string ?? "<legacy>";

    private static void RemoveBindingIfCurrent(
        FieldInfo mapField,
        PropertyInfo keyProperty,
        object runtime,
        object binding)
    {
        if (mapField.GetValue(runtime) is not System.Collections.IDictionary map)
        {
            return;
        }

        object key = keyProperty.GetValue(binding)
            ?? throw new InvalidOperationException($"Tail binding has no {keyProperty.Name}.");

        if (map.Contains(key) && ReferenceEquals(map[key], binding))
        {
            map.Remove(key);
        }
    }
}
