using System.Collections;
using System.Reflection;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace Sts2LobbyEnhance;

/// <summary>
/// Drops the accepted Tail roster epoch of one live session, so the next roster-bearing message from the
/// host is validated as a bootstrap instead of as a "current state" repeat
///
/// Used by <see cref="NewRunFromGameOverFix"/>, which keeps the <c>NetService</c> alive across a room rebuild
/// and re-handshakes on that same session
///
/// The trap, in the host mod's own <c>AcceptRoster</c>:
/// <c>LanConnectTailMessageRuntime.AcceptRoster</c> picks the snapshot use with
/// <c>authority.Current == null ? firstUse : CurrentState</c>, so the <c>Bootstrap</c> hint
/// <c>RestoreStartRunResponse</c> passes is ignored once the authority holds a snapshot. <c>CurrentState</c>
/// then demands the snapshot repeat the accepted revision byte for byte, and throws when it does not.
/// Official joins never reach that branch because they arrive on a fresh binding with an empty roster
/// authority, but a kept-alive session still holds the <c>StartRunLobbyPlayer</c> epoch from the room it
/// left while the host publishes a <c>LoadRunLobbyPlayer</c> epoch, and the length check fails before any
/// field is compared
///
/// Resetting only the client is deliberate: the host keeps publishing monotonically increasing revisions,
/// and a client that resets loses only its own stale epoch
///
/// Best effort throughout: a missing anchor or a throwing target logs one warning and returns <c>false</c>,
/// because both call sites sit inside a handshake that must not die for a diagnostic
/// </summary>
internal static class TailRoster
{
    private const string TailRuntimeTypeName = "Sts2LanConnect.Scripts.LanConnectTailMessageRuntime";
    private const string RosterStateTypeName = "Sts2LanConnect.Scripts.LanConnectRosterAuthorityState";

    private const string Tag = "tail-roster: ";

    private static readonly object Gate = new();

    private static Anchors? _anchors;

    private static string? _lastResolutionFailure;

    /// <summary>
    /// Forgets the accepted roster epoch of <paramref name="service"/>'s Tail binding
    ///
    /// <c>true</c> once the reset ran. A binding that never established an authority gets an empty one created
    /// by <c>RequireRoster()</c> and resetting that is a no-op, so <c>true</c> does not mean an epoch existed.
    /// <paramref name="when"/> names the call site and only reaches the log
    /// </summary>
    internal static bool Reset(INetGameService service, string when)
    {
        // A caller bug, not a missing anchor: worth one line, never an exception.
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
    /// A failed resolution is deliberately not cached as a permanent verdict, only its log line is deduplicated
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
    /// binding would be worse than a loud failure: the reset would "succeed" and change nothing
    ///
    /// In the host mod these are
    /// <c>LanConnectTailMessageRuntime.Shared</c>, <c>GetMessageBus(INetGameService)</c> and the private
    /// <c>_bindings</c> dictionary (keyed by <c>NetMessageBus</c>, valued by the nested private <c>Binding</c>),
    /// plus <c>Binding.RequireRoster()</c> and <c>LanConnectRosterAuthorityState.Reset()</c>
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

        internal PropertyInfo Shared { get; }

        internal MethodInfo GetMessageBus { get; }

        internal FieldInfo Bindings { get; }

        internal MethodInfo RequireRoster { get; }

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
