using System.Collections;
using System.Reflection;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace Sts2LobbyEnhance;

/// <summary>
/// Drops the accepted Tail roster epoch of one live session, so the next roster-bearing message from the
/// host is validated as a bootstrap instead of as a "current state" repeat.
///
/// Why this is a shared component: two repairs keep the <c>NetService</c> alive across a room rebuild and
/// then re-handshake on that very same session — the mid-run restart (<see cref="MidRunRestartFix"/>) and
/// the game-over "new run" button (<see cref="NewRunFromGameOverFix"/>). Both walk into the same trap, so
/// the reflection lives here once instead of twice.
///
/// The trap, quoted from the shipped 0.6.3 binary (the one that is actually loaded, see
/// <c>work/lobby/dec-official</c>): <c>LanConnectTailMessageRuntime.AcceptRoster</c> chooses the snapshot
/// use with <c>authority.Current == null ? firstUse : CurrentState</c>
/// (LanConnectTailMessageRuntime.cs:1350-1354), so the <c>Bootstrap</c> hint that
/// <c>RestoreStartRunResponse</c> passes at :1169 is *ignored* once the authority already holds a snapshot.
/// <c>CurrentState</c> then demands that the snapshot repeat the accepted revision byte for byte
/// (LanConnectRosterAuthorityState.cs:83-88) and throws at :86.
///
/// The official flows never hit that branch: a join always arrives on a brand-new binding whose roster
/// authority is still empty. A repair that keeps the session alive keeps the binding alive too, so the
/// client still holds the <c>StartRunLobbyPlayer</c> epoch from the room it just left while the host is
/// about to publish a <c>LoadRunLobbyPlayer</c> epoch — a different carrier type, hence a different length,
/// hence a guaranteed failure at the length check before any field is compared. The field log says exactly
/// that: <c>lan_protocol_version_mismatch: Current-state snapshots must repeat the accepted revision
/// byte-for-byte</c>.
///
/// Resetting only the client is deliberate: the host keeps publishing monotonically increasing revisions,
/// which is what an unpatched client expects, so a mixed room degrades to the unpatched behaviour instead
/// of to a new failure mode ("Roster revision moved backwards", LanConnectRosterAuthorityState.cs:77).
///
/// Failure policy: every step is best effort. A missing anchor or a throwing target logs one warning and
/// returns <c>false</c>; nothing is ever thrown at the caller, because both call sites are inside a
/// handshake that must not die for a diagnostic.
/// </summary>
internal static class TailRoster
{
    private const string TailRuntimeTypeName = "Sts2LanConnect.Scripts.LanConnectTailMessageRuntime";
    private const string RosterStateTypeName = "Sts2LanConnect.Scripts.LanConnectRosterAuthorityState";

    /// <summary>Log prefix. The caller adds its own success line, so this tag only marks shared failures.</summary>
    private const string Tag = "tail-roster: ";

    private static readonly object Gate = new();

    /// <summary>Resolved anchors. Written once, on the first successful resolution, then never re-resolved.</summary>
    private static Anchors? _anchors;

    /// <summary>Last resolution failure, only so a repeated failure does not flood the log every handshake.</summary>
    private static string? _lastResolutionFailure;

    /// <summary>
    /// Forgets the accepted roster epoch of <paramref name="service"/>'s Tail binding.
    /// </summary>
    /// <param name="service">The session that is about to re-handshake. Must be the live one.</param>
    /// <param name="when">
    /// Short clause naming the call site, e.g. <c>before the reload join request</c>. Only used in logs.
    /// </param>
    /// <returns>
    /// <c>true</c> only when an accepted epoch was really dropped. <c>false</c> means "nothing was reset"
    /// (no binding, no anchor, or a throwing target) and the caller must not claim success in its own log.
    /// </returns>
    internal static bool Reset(INetGameService service, string when)
    {
        // "Never throws" is literal: even a caller bug must come back as false plus a warning, because both
        // call sites sit inside a message handler whose job is to keep the handshake alive.
        if (service is null)
        {
            Entry.Warn($"{Tag}no session to reset the roster epoch on ({when}).");
            return false;
        }

        Anchors? anchors = Resolve();
        if (anchors == null)
        {
            // Resolve() already logged why; do not repeat it here.
            return false;
        }

        try
        {
            object shared = anchors.Shared.GetValue(null)
                ?? throw new InvalidOperationException($"{TailRuntimeTypeName}.Shared returned null.");
            object bus = anchors.GetMessageBus.Invoke(null, new object?[] { service })
                ?? throw new InvalidOperationException("the tail runtime could not resolve the message bus.");

            if (anchors.Bindings.GetValue(shared) is not IDictionary bindings || !bindings.Contains(bus))
            {
                Entry.Warn(
                    $"{Tag}no tail binding to reset the roster epoch on ({when}); the handshake will be " +
                    "rejected as a stale current-state snapshot.");
                return false;
            }

            object binding = bindings[bus]!;
            object authority = anchors.RequireRoster.Invoke(binding, null)
                ?? throw new InvalidOperationException("RequireRoster() returned null.");
            anchors.RosterReset.Invoke(authority, null);
            return true;
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            // MethodInfo.Invoke wraps whatever the target threw; report the real cause, not the wrapper.
            Entry.Warn(
                $"{Tag}could not reset the roster epoch ({when}): " +
                $"{exception.InnerException.GetType().Name}: {exception.InnerException.Message}");
            return false;
        }
        catch (Exception exception)
        {
            Entry.Warn(
                $"{Tag}could not reset the roster epoch ({when}): " +
                $"{exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Resolves the reflection anchors once and caches them. A failed resolution is deliberately *not*
    /// cached as a permanent verdict — the host assembly or the tail runtime could still be settling — but
    /// the identical failure is logged only once so a retry on every handshake stays quiet.
    /// </summary>
    private static Anchors? Resolve()
    {
        // The warning is composed under the lock but emitted outside it: the game's logger is not ours to
        // call while holding a lock this component's other call sites also take.
        string? warnAbout = null;

        lock (Gate)
        {
            if (_anchors != null)
            {
                return _anchors;
            }

            try
            {
                _anchors = Anchors.Capture();
                _lastResolutionFailure = null;
                return _anchors;
            }
            catch (Exception exception)
            {
                string failure = $"{exception.GetType().Name}: {exception.Message}";
                if (!string.Equals(failure, _lastResolutionFailure, StringComparison.Ordinal))
                {
                    _lastResolutionFailure = failure;
                    warnAbout = failure;
                }
            }
        }

        if (warnAbout != null)
        {
            Entry.Warn(
                $"{Tag}the roster epoch reset is unavailable on this build, so a re-handshake on a " +
                $"kept session may be rejected: {warnAbout}");
        }

        return null;
    }

    /// <summary>
    /// Every anchor the reset needs, captured once. Each one is shape-checked, because a silently wrong
    /// binding would be worse than a loud failure: the reset would "succeed" and change nothing.
    ///
    /// Anchors (shipped 0.6.3 binary, <c>work/lobby/dec-official/Sts2LanConnect.Scripts/</c>):
    /// <list type="bullet">
    /// <item><c>LanConnectTailMessageRuntime.Shared</c> — LanConnectTailMessageRuntime.cs:459</item>
    /// <item><c>LanConnectTailMessageRuntime.GetMessageBus(INetGameService)</c> — :1670</item>
    /// <item><c>LanConnectTailMessageRuntime._bindings</c> (<c>Dictionary&lt;NetMessageBus, Binding&gt;</c>) — :439</item>
    /// <item><c>Binding.RequireRoster()</c> — :116 (Binding is a nested private type at :27)</item>
    /// <item><c>LanConnectRosterAuthorityState.Reset()</c> — LanConnectRosterAuthorityState.cs:128</item>
    /// </list>
    /// </summary>
    private sealed class Anchors
    {
        private Anchors(
            PropertyInfo shared,
            MethodInfo getMessageBus,
            FieldInfo bindings,
            MethodInfo requireRoster,
            MethodInfo rosterReset)
        {
            Shared = shared;
            GetMessageBus = getMessageBus;
            Bindings = bindings;
            RequireRoster = requireRoster;
            RosterReset = rosterReset;
        }

        /// <summary><c>LanConnectTailMessageRuntime.Shared</c>.</summary>
        internal PropertyInfo Shared { get; }

        /// <summary><c>LanConnectTailMessageRuntime.GetMessageBus(INetGameService)</c>.</summary>
        internal MethodInfo GetMessageBus { get; }

        /// <summary><c>LanConnectTailMessageRuntime._bindings</c> (bus to session binding).</summary>
        internal FieldInfo Bindings { get; }

        /// <summary><c>Binding.RequireRoster()</c>.</summary>
        internal MethodInfo RequireRoster { get; }

        /// <summary><c>LanConnectRosterAuthorityState.Reset()</c>.</summary>
        internal MethodInfo RosterReset { get; }

        internal static Anchors Capture()
        {
            Assembly host = Reflect.FindAssembly(Entry.HostModAssemblyName)
                ?? throw new InvalidOperationException(
                    $"host mod assembly '{Entry.HostModAssemblyName}' is not loaded.");

            Type tailRuntime = Reflect.RequireType(host, TailRuntimeTypeName);

            PropertyInfo shared = Reflect.RequireProperty(tailRuntime, "Shared");
            if (shared.GetGetMethod(nonPublic: true) is not { IsStatic: true } getter
                || getter.ReturnType != tailRuntime)
            {
                throw new MissingMemberException(
                    tailRuntime.FullName,
                    $"Shared is not a static {tailRuntime.Name} property.");
            }

            // GetMessageBus is a *static* helper; resolve it by name and arity, then verify the shape we
            // depend on instead of guessing the binding flags.
            MethodInfo getMessageBus = Reflect.RequireMethodByName(tailRuntime, "GetMessageBus", 1);
            if (!getMessageBus.IsStatic
                || getMessageBus.ReturnType != typeof(NetMessageBus)
                || getMessageBus.GetParameters()[0].ParameterType != typeof(INetGameService))
            {
                throw new MissingMemberException(
                    tailRuntime.FullName,
                    $"GetMessageBus(INetGameService) is static={getMessageBus.IsStatic} " +
                    $"and returns {getMessageBus.ReturnType.Name}");
            }

            FieldInfo bindings = Reflect.RequireField(tailRuntime, "_bindings");

            // _bindings is Dictionary<NetMessageBus, Binding> and Binding is a nested private type, so the
            // only stable way to reach it is through the field's own generic arguments.
            Type[] bindingArguments = bindings.FieldType.GetGenericArguments();
            if (bindingArguments.Length != 2 || bindingArguments[0] != typeof(NetMessageBus))
            {
                throw new MissingMemberException(
                    tailRuntime.FullName,
                    $"_bindings is not a NetMessageBus-keyed dictionary (found {bindings.FieldType.Name})");
            }

            Type bindingType = bindingArguments[1];
            Type rosterState = Reflect.RequireType(host, RosterStateTypeName);
            MethodInfo requireRoster = Reflect.RequireMethodByName(bindingType, "RequireRoster", 0);
            if (requireRoster.ReturnType != rosterState)
            {
                throw new MissingMemberException(
                    bindingType.FullName,
                    $"RequireRoster() returns {requireRoster.ReturnType.Name}, not {rosterState.Name}");
            }

            MethodInfo rosterReset = Reflect.RequireInstanceMethod(rosterState, "Reset");
            if (rosterReset.ReturnType != typeof(void) || rosterReset.GetParameters().Length != 0)
            {
                throw new MissingMemberException(
                    rosterState.FullName,
                    $"Reset() is {rosterReset.ReturnType.Name}({rosterReset.GetParameters().Length} parameter(s))");
            }

            return new Anchors(shared, getMessageBus, bindings, requireRoster, rosterReset);
        }
    }
}
