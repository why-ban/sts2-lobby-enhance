using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2LobbyEnhance;

/// <summary>
/// Mod entry point.
///
/// Startup contract:
///   1. Probe the game build. If none of the 0.113 signature changes are present, do nothing at all.
///   2. Locate the host mod (sts2_lan_connect). If it is absent, log and stay inert.
///   3. Apply every repair that reports itself as needed, each isolated, then summarise.
///
/// A partial application is treated as a startup failure: a half-patched networking stack is worse
/// than an unpatched one, because it makes the failure look like a game bug. Concretely, if any repair
/// throws, every patch this mod installed during this start is removed again
/// (<see cref="Harmony.UnpatchAll(string)"/>, 0Harmony's only overload) and the log says so. Removing the
/// patches is not the same as undoing them — see the wording in <see cref="Announce"/> — but it does stop
/// a half-installed set from running.
/// </summary>
[ModInitializer(nameof(Init))]
public static class Entry
{
    internal const string HostModAssemblyName = "sts2_lan_connect";
    internal const string HarmonyId = "sts2_lobby_enhance";

    private static readonly object Gate = new();
    private static bool _initialized;

    /// <summary>
    /// Set while <see cref="Execute"/> runs under an out-of-process harness.
    ///
    /// MegaCrit's logger resolves <c>Godot.OS</c> in its own static constructor, which reaches native
    /// interop. Outside a real Godot process that is not a managed exception but an access violation
    /// (0xC0000005), so it cannot be caught — the only safe option is to never touch the logger at all.
    /// </summary>
    private static bool _suppressLogging;

    public static void Init()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
        }

        try
        {
            StartupReport report = Execute(silent: false);
            Announce(report);
        }
        catch (Exception exception)
        {
            Error($"startup failed: {exception}");
        }
    }

    /// <summary>
    /// Runs the real startup sequence with logging disabled, so a harness can exercise the reflection
    /// and patching paths without a Godot runtime. Returns the outcome instead of reporting it.
    /// </summary>
    internal static StartupReport RunSilently() => Execute(silent: true);

    private static StartupReport Execute(bool silent)
    {
        _suppressLogging = silent;
        try
        {
            CompatSnapshot snapshot = CompatProbe.Capture();
            var report = new StartupReport(snapshot);

            if (!snapshot.RequiresPatch)
            {
                report.Inert = true;
                return report;
            }

            Assembly? host = Reflect.FindAssembly(HostModAssemblyName);
            if (host == null)
            {
                report.HostMissing = true;
                return report;
            }

            var harmony = new Harmony(HarmonyId);
            foreach (ICompatFix fix in CompatFixes.Build())
            {
                if (!fix.Applies(snapshot))
                {
                    report.Skipped.Add(fix.Id);
                    continue;
                }

                try
                {
                    fix.Apply(harmony, snapshot);
                    report.Applied.Add(fix.Id);
                }
                catch (Exception exception)
                {
                    report.Failed.Add($"{fix.Id}: {exception.GetType().Name}: {exception.Message}");
                }
            }

            if (report.Failed.Count > 0)
            {
                RollBack(harmony, report);
            }

            return report;
        }
        finally
        {
            _suppressLogging = false;
        }
    }

    /// <summary>
    /// Removes every patch this mod installed in this start.
    ///
    /// 0Harmony 2.4.2 exposes exactly one unpatch-everything entry point —
    /// <c>void HarmonyLib.Harmony.UnpatchAll(string harmonyID)</c> (instance method; there is no
    /// parameterless overload in that assembly) — and passing our id scopes the removal to our own
    /// patches instead of wiping another mod's.
    /// </summary>
    private static void RollBack(Harmony harmony, StartupReport report)
    {
        try
        {
            harmony.UnpatchAll(HarmonyId);
            report.RolledBack = true;
        }
        catch (Exception exception)
        {
            report.RollbackFailure = $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    private static void Announce(StartupReport report)
    {
        Info($"game probe: {report.Snapshot.Describe()}");

        if (report.HostMissing)
        {
            Warn(
                $"host mod '{HostModAssemblyName}' is not loaded; nothing to patch. " +
                "Install LAN Connect 0.6.3 or newer and restart.");
            return;
        }

        if (report.Inert)
        {
            Info(
                "this game build does not show the 0.113 signature changes; LAN Connect needs no " +
                "compatibility patch here. Staying inert.");
            return;
        }

        Assembly? host = Reflect.FindAssembly(HostModAssemblyName);
        Info($"host mod: {host?.GetName().Name} {host?.GetName().Version} @ {host?.Location}");

        foreach (string applied in report.Applied)
        {
            Info($"applied: {applied}");
        }

        Info(
            $"summary: applied={report.Applied.Count}[{string.Join(",", report.Applied)}] " +
            $"skipped={report.Skipped.Count}[{string.Join(",", report.Skipped)}] " +
            $"failed={report.Failed.Count}");

        foreach (string failure in report.Failed)
        {
            Error($"FAILED: {failure}");
        }

        if (report.RolledBack)
        {
            // Never run in a half-patched state: say exactly what was done about it, and say what was not.
            Error(
                $"one or more repairs could not be installed, so every patch this mod installed this start " +
                $"was removed again (UnpatchAll(\"{HarmonyId}\") over applied=[{string.Join(",", report.Applied)}]). " +
                "No compatibility patch is active now: multiplayer behaves exactly as an unpatched LAN Connect " +
                "does, so a bug report should point at the failing repair above, not at the host mod.");
            Error(
                "removing the patches is NOT the same as undoing them. Harmony hooks are gone, but state a " +
                "repair had already written into this process stays until the process restarts — for example " +
                "ExtraGameModesFix's transpiler plan tables (Scripts/Ui/ExtraGameModesFix.cs:74,85,131,164,180), " +
                "TailErrorPathFix's LastRejection (Scripts/Tail/TailErrorPathFix.cs:40) and the Blitz/Dread " +
                "epoch ids UnlockExtraModesFix has already resolved and cached " +
                "(Scripts/Unlock/UnlockExtraModesFix.cs:183-187); the epoch ids it *injects* into the reported " +
                "unlock state (Scripts/Unlock/UnlockExtraModesFix.cs:78-114) run through the patched seam, so " +
                "those injections stop happening, but the cache does not. Restart the game for a clean state.");
        }
        else if (report.RollbackFailure != null)
        {
            Error(
                "one or more repairs could not be installed AND removing the installed ones failed " +
                $"({report.RollbackFailure}). This process may be running a half-patched networking stack; " +
                "restart the game and report this log to the patch author.");
        }
        else if (report.Failed.Count > 0)
        {
            // Unreachable while Execute keeps rolling back, but the message must not silently disappear if
            // that ever changes: never let a half-patched state look like a working installation.
            Error(
                "one or more repairs could not be installed. Multiplayer is NOT fully compatible; " +
                "please report this log to the patch author.");
        }
    }

    internal static void Info(string message)
    {
        if (!_suppressLogging)
        {
            Log.Info("[lobby-enhance] " + message);
        }
    }

    internal static void Warn(string message)
    {
        if (!_suppressLogging)
        {
            Log.Warn("[lobby-enhance] " + message);
        }
    }

    internal static void Error(string message)
    {
        if (!_suppressLogging)
        {
            Log.Error("[lobby-enhance] " + message);
        }
    }
}

internal sealed class StartupReport
{
    internal StartupReport(CompatSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    internal CompatSnapshot Snapshot { get; }

    internal bool Inert { get; set; }

    internal bool HostMissing { get; set; }

    internal List<string> Applied { get; } = new();

    internal List<string> Skipped { get; } = new();

    internal List<string> Failed { get; } = new();

    /// <summary>True when <c>Entry.RollBack</c> removed the patches after a failure.</summary>
    internal bool RolledBack { get; set; }

    /// <summary>Why the rollback itself failed, or <c>null</c> when it succeeded or was not needed.</summary>
    internal string? RollbackFailure { get; set; }
}

internal static class CompatFixes
{
    internal static IEnumerable<ICompatFix> Build()
    {
        yield return new JoinTakeoverFix();
        yield return new JoinScreenDispatchFix();
        yield return new HostWireFix();
        yield return new TailErrorPathFix();
        yield return new AbandonRunFix();
        yield return new NewRunFromGameOverFix();
        yield return new MidRunRestartFix();
        yield return new ExtraGameModesFix();
        yield return new UnlockExtraModesFix();
    }
}

