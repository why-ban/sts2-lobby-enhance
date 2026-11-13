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

    /// <summary>Same member filter as <see cref="Reflect"/> uses, for the reflective lookups below.</summary>
    private const BindingFlags AnyMember =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

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

    public bool Applies(in CompatSnapshot snapshot) =>
        snapshot.DisconnectClientGainedParameter || snapshot.ConnectedPeersChangedType;

    public void Apply(Harmony harmony, in CompatSnapshot snapshot)
    {
        Assembly host = Reflect.FindAssembly(Entry.HostModAssemblyName)
            ?? throw new InvalidOperationException("Host mod assembly is not loaded.");

        Type runtimeType = Reflect.RequireType(host, RuntimeTypeName);

        MethodInfo abort = Reflect.RequireMethodByName(runtimeType, "AbortActiveBinding", 3);
        MethodInfo reject = Reflect.RequireMethodByName(runtimeType, "RejectAndDisconnect", 3);

        MethodInfo abortReplacement = typeof(TailErrorPathFix)
            .GetMethod(nameof(AbortActiveBinding), BindingFlags.Public | BindingFlags.Static)!;
        MethodInfo rejectReplacement = typeof(TailErrorPathFix)
            .GetMethod(nameof(RejectAndDisconnect), BindingFlags.Public | BindingFlags.Static)!;

        // Hand the two call targets to the transpiler. Both are compared by token+module there, exactly
        // like References() above, so a same-named method on an unrelated type can never be rewritten.
        _abortTarget = abort;
        _rejectTarget = reject;

        var patched = new List<string>();
        var unreadable = new List<string>();

        foreach (MethodBase caller in EnumerateMethods(runtimeType))
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

            bool referencesAbort = References(original, abort);
            bool referencesReject = References(original, reject);
            if (!referencesAbort && !referencesReject)
            {
                continue;
            }

            int rewrittenBefore = _rewrittenCallInstructions;
            harmony.Patch(
                caller,
                transpiler: new HarmonyMethod(typeof(TailErrorPathFix), nameof(RedirectTranspiler))
                {
                    priority = Priority.First
                });

            // Harmony reports the patch as installed even when the transpiler matched nothing. Verify the
            // rewrite actually happened instead of trusting the installation, so "applied" in the log can
            // never mean "attached but still calling the broken original".
            if (_rewrittenCallInstructions == rewrittenBefore)
            {
                throw new InvalidOperationException(
                    $"{caller.DeclaringType?.Name}::{caller.Name} was patched, but the transpiler rewrote no " +
                    "call instruction: the call target is no longer the AbortActiveBinding/RejectAndDisconnect " +
                    "this patch replaces.");
            }

            patched.Add(caller.DeclaringType?.Name + "::" + caller.Name);
        }

        Entry.Info(
            $"tail-error-paths: redirected {patched.Count} call site method(s) " +
            $"[{string.Join(", ", patched)}] (skipped {unreadable.Count} unreadable/generic, " +
            $"rewrote {_rewrittenCallInstructions} call instruction(s)).");

        InstallKickDisconnectRedirect(host, harmony);

        // Both originals must be reached through the redirects; if the scan found nothing the contract
        // changed underneath us and the patch would silently do nothing.
        if (patched.Count == 0)
        {
            throw new InvalidOperationException(
                "No readable caller of AbortActiveBinding/RejectAndDisconnect was found. " +
                "The upstream tail runtime has changed shape; this repair cannot be applied safely.");
        }
    }

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

    private static bool References(IEnumerable<CodeInstruction> instructions, MethodInfo target)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo called && SameMethod(called, target))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameMethod(MethodInfo left, MethodInfo right) =>
        left.MetadataToken == right.MetadataToken && left.Module == right.Module;

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

        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo called)
            {
                // Identity, not name: the same predicate References() used to decide this caller needs
                // the patch, so nothing but the two upstream methods is ever rewritten.
                if (SameMethod(called, abort))
                {
                    _rewrittenCallInstructions++;
                    yield return new CodeInstruction(
                        OpCodes.Call,
                        typeof(TailErrorPathFix).GetMethod(nameof(AbortActiveBinding))!);
                    continue;
                }

                if (SameMethod(called, reject))
                {
                    _rewrittenCallInstructions++;
                    yield return new CodeInstruction(
                        OpCodes.Call,
                        typeof(TailErrorPathFix).GetMethod(nameof(RejectAndDisconnect))!);
                    continue;
                }
            }

            yield return instruction;
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

        Type runtimeType = runtime.GetType();
        object? service = Reflect.GetPropertyValue(binding, "Service");

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
            object rejections = GetOrCreateRejectionStack(runtime);
            MethodInfo push = rejections.GetType().GetMethod("Push")
                ?? throw new MissingMethodException(rejections.GetType().FullName, "Push");
            MethodInfo pop = rejections.GetType().GetMethod("Pop")
                ?? throw new MissingMethodException(rejections.GetType().FullName, "Pop");

            push.Invoke(rejections, new[] { failure });
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
                object? popped = pop.Invoke(rejections, null);
                if (!ReferenceEquals(popped, failure))
                {
                    throw new InvalidOperationException("Tail rejection call context was corrupted.");
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

        _ = runtimeType;
    }

    private static object GetOrCreateRejectionStack(object runtime)
    {
        FieldInfo field = Reflect.RequireField(runtime.GetType(), "_outgoingRejections");

        // Upstream declares it "[ThreadStatic] private static Stack<LanConnectProtocolFailure>?"
        // (dec-official LanConnectTailMessageRuntime.cs:445-446). GetValue/SetValue ignore the instance
        // argument for static fields, so pass null: an instance argument would suggest a per-binding
        // field and hide that the stack is per-thread.
        Debug.Assert(field.IsStatic);
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

        Type runtimeType = runtime.GetType();
        object sync = Reflect.RequireField(runtimeType, "_sync").GetValue(runtime)
            ?? throw new InvalidOperationException("Tail runtime _sync is null.");

        ulong[] peerIds;

        lock (sync)
        {
            object? terminated = Reflect.RequireMethodByName(binding.GetType(), "TryMarkTerminated", 0)
                .Invoke(binding, null);
            if (terminated is false)
            {
                return;
            }

            RemoveBindingIfCurrent(runtime, "_bindings", "MessageBus", binding);
            RemoveBindingIfCurrent(runtime, "_writerBindings", "Writer", binding);

            object writer = Reflect.GetPropertyValue(binding, "Writer")
                ?? throw new InvalidOperationException("Tail binding has no writer.");
            Reflect.RequireMethodByName(runtimeType, "ClearPendingOutgoing", 1).Invoke(runtime, new[] { writer });

            var peers = new List<ulong>();
            if (Reflect.GetPropertyValue(binding, "BoundPeerIds") is System.Collections.IEnumerable bound)
            {
                foreach (object? peer in bound)
                {
                    if (peer is ulong id)
                    {
                        peers.Add(id);
                    }
                }
            }

            if (Reflect.GetPropertyValue(binding, "Service") is NetHostGameService host)
            {
                foreach (NetClientData peer in host.ConnectedPeers)
                {
                    peers.Add(peer.peerId);
                }
            }

            peerIds = peers.Distinct().ToArray();

            Reflect.RequireMethodByName(binding.GetType(), "ClearNative", 0).Invoke(binding, null);
        }

        Entry.Error(
            $"sts2_lan_connect tail: terminating active Native Tail binding reason={reason} " +
            $"exception={exception.GetType().FullName} hresult={exception.HResult}.");

        if (Reflect.GetPropertyValue(binding, "Service") is NetHostGameService netHost)
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
            object service = Reflect.GetPropertyValue(binding, "Service")
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
    private static void InstallKickDisconnectRedirect(Assembly host, Harmony harmony)
    {
        Type patchesType = Reflect.RequireType(
            host, "Sts2LanConnect.Scripts.LanConnectRemoteLobbyPlayerPatches");
        MethodInfo schedule = Reflect.RequireMethodByName(patchesType, "ScheduleDelayedDisconnect", 3);

        harmony.Patch(
            schedule,
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

        object? service = Reflect.RequireMethodByName(runtime.GetType(), "GetHostNetService", 0)
            .Invoke(runtime, null);
        if (service is not NetHostGameService hostService)
        {
            return false;
        }

        if (Godot.Engine.GetMainLoop() is not Godot.SceneTree tree)
        {
            return false;
        }

        object? action = Reflect.RequireMethodByName(runtime.GetType(), "CreateKickDisconnectAction", 1)
            .Invoke(runtime, new[] { target });
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

    private static void RemoveBindingIfCurrent(object runtime, string dictionaryField, string keyProperty, object binding)    {
        if (Reflect.RequireField(runtime.GetType(), dictionaryField).GetValue(runtime) is not System.Collections.IDictionary map)
        {
            return;
        }

        object key = Reflect.GetPropertyValue(binding, keyProperty)
            ?? throw new InvalidOperationException($"Tail binding has no {keyProperty}.");

        if (map.Contains(key) && ReferenceEquals(map[key], binding))
        {
            map.Remove(key);
        }
    }
}
