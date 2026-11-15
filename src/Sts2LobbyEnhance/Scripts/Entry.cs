using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace Sts2LobbyEnhance;

    /// <summary>
    /// Mod entry point
    ///
    /// A missing host mod is the only condition that disables everything; it is not a compatibility judgement,
    /// the assembly is simply not loaded. Everything else fails per group, because each group patches under its
    /// own Harmony id and <c>UnpatchAll</c> takes an id rather than a patch set, so a rollback cannot reach
    /// another group's patches
    ///
    /// Removing a group's patches is not the same as undoing them: Harmony hooks disappear, but state a repair
    /// already wrote into this process survives until restart
    /// </summary>
[ModInitializer(nameof(Init))]
public static class Entry
{
    internal const string HostModAssemblyName = "sts2_lan_connect";

    /// <summary>
    /// Prefix of the per-group Harmony ids (<c>"sts2_lobby_enhance." + group name, lower case</c>). Per-group
    /// ids are what make a rollback scoped, because <c>UnpatchAll</c> takes an id rather than a patch set
    /// </summary>
    internal const string HarmonyId = "sts2_lobby_enhance";

    private static readonly object Gate = new();
    private static bool _initialized;

    /// <summary>
    /// Set while <see cref="Execute"/> runs under an out-of-process harness. MegaCrit's logger resolves
    /// <c>Godot.OS</c> in its own static constructor, which outside a real Godot process is an access violation
    /// (0xC0000005) rather than a managed exception, so it cannot be caught and the only safe option is never to
    /// touch the logger
    /// </summary>
    private static bool _suppressLogging;

    internal static string HarmonyIdFor(FixGroup group) =>
        HarmonyId + "." + group.ToString().ToLowerInvariant();

    /// <summary>
    /// Every Harmony id this mod can install under. <c>Harmony.GetPatchedMethods()</c> only reports the id of the
    /// instance it is called on, so a harness enumerating installed patches needs all of them
    /// </summary>
    internal static IEnumerable<string> AllHarmonyIds() =>
        Enum.GetValues<FixGroup>().Select(HarmonyIdFor);

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

    internal static StartupReport RunSilently() => Execute(silent: true);

    private static StartupReport Execute(bool silent)
    {
        _suppressLogging = silent;
        try
        {
    // Capture() guards each probe internally, but not its OWN type initialiser: a throwing static initialiser
    // surfaces as TypeInitializationException at the first static member access, which is this line and outside
    // Capture()'s try. Losing one snapshot is acceptable, losing the whole startup report is not
            CompatSnapshot snapshot = default;
            string? captureFailure = null;
            try
            {
                CompatProbe.Logger ??= Info;
                snapshot = CompatProbe.Capture();
            }
            catch (Exception exception)
            {
    // Keep going with an all-false snapshot: repairs that do not depend on a flag still install, and the ones
    // that do skip with a "missing: ..." reason. Aborting here would be the old global switch in disguise
                captureFailure = "CompatProbe.Capture(): " + exception.GetType().Name + ": " + exception.Message +
                                 " (repairs that read a flag will skip; those that do not still install)";
            }

            var report = new StartupReport(snapshot);
            if (captureFailure != null)
            {
                report.Failed.Add(captureFailure);
            }

            Assembly? host = Reflect.FindAssembly(HostModAssemblyName);
            if (host == null)
            {
                report.HostMissing = true;
                return report;
            }

    // BUILDING the list is itself fallible and is not a per-repair concern: Build() is an iterator, so
    // "new NewRunFromGameOverFix()" runs during MoveNext(), here and not inside Install()'s try, and a throwing
    // constructor (or static initialiser) would escape with the groups installed so far left live and no
    // summary line
            List<ICompatFix> fixes;
            try
            {
                fixes = CompatFixes.Build().ToList();
            }
            catch (Exception exception)
            {
                report.Failed.Add("CompatFixes.Build(): " + exception.GetType().Name + ": " + exception.Message);
                report.Inert = report.Applied.Count == 0 && report.Failed.Count == 0;
                return report;
            }

            foreach (ICompatFix fix in fixes)
            {
                Install(fix, snapshot, report);
            }

            report.Inert = report.Applied.Count == 0 && report.Failed.Count == 0;
            return report;
        }
        finally
        {
            _suppressLogging = false;
        }
    }

    /// <summary>
    /// Ask, then patch, then record. Never lets an exception escape, because one repair is not allowed to decide
    /// whether the other repairs run
    /// </summary>
    private static void Install(ICompatFix fix, CompatSnapshot snapshot, StartupReport report)
    {
        GroupReport group = report.GroupFor(fix.Group);

        string reason;
        bool applies;
        try
        {
            applies = fix.Applies(snapshot, out reason);
        }
        catch (Exception exception)
        {
            Fail(fix, group, report, $"Applies() threw {DescribeException(exception)}");
            RollBackGroup(group, report);
            return;
        }

        if (!applies)
        {
            string why = string.IsNullOrWhiteSpace(reason) ? "not applicable to this build" : reason.Trim();
            group.Skipped.Add($"{fix.Id}: {why}");
            report.Skipped.Add(fix.Id);
            return;
        }

        try
        {
            fix.Apply(group.Harmony, snapshot);
            group.Applied.Add(fix.Id);
            report.Applied.Add(fix.Id);
        }
        catch (Exception exception)
        {
            Fail(fix, group, report, DescribeException(exception));
            RollBackGroup(group, report);
        }
    }

    private static void Fail(ICompatFix fix, GroupReport group, StartupReport report, string detail)
    {
        group.Failed.Add($"{fix.Id}: {detail}");
        report.Failed.Add($"{fix.Id}: {detail}");
    }

    private static string DescribeException(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message}";

    /// <summary>
    /// Removes this group's patches and only this group's. 0Harmony 2.4.2 exposes exactly one
    /// unpatch-everything entry point, <c>void HarmonyLib.Harmony.UnpatchAll(string harmonyID)</c>, with no
    /// parameterless overload, and the id argument is what scopes the removal
    ///
    /// A failed removal leaves the patches live, so its ids stay in <see cref="StartupReport.Applied"/>: the
    /// report must describe what is running, not what was attempted
    /// </summary>
    private static void RollBackGroup(GroupReport group, StartupReport report)
    {
        try
        {
            group.Harmony.UnpatchAll(group.HarmonyId);
            group.RolledBack = true;
            report.RolledBack = true;
        }
        catch (Exception exception)
        {
            group.RollbackFailure = DescribeException(exception);
            report.RollbackFailure = report.RollbackFailure == null
                ? $"{GroupName(group.Group)}: {group.RollbackFailure}"
                : $"{report.RollbackFailure}; {GroupName(group.Group)}: {group.RollbackFailure}";
            return;
        }

        foreach (string id in group.Applied.ToArray())
        {
            report.Applied.Remove(id);
            report.Failed.Add(
                $"{id}: installed, then removed with its group '{GroupName(group.Group)}' because another " +
                "repair in the same group failed");
        }
    }

    private static string GroupName(FixGroup group) => group.ToString().ToLowerInvariant();

    private static void Announce(StartupReport report)
    {
        Info($"game probe: {report.Snapshot.Describe()}");

        if (report.HostMissing)
        {
            Warn(
                $"host mod '{HostModAssemblyName}' is not loaded; nothing to patch. " +
                "Install LAN Connect 0.6.4 or newer and restart.");
            return;
        }

        Assembly? host = Reflect.FindAssembly(HostModAssemblyName);
        Info($"host mod: {host?.GetName().Name} {host?.GetName().Version} @ {host?.Location}");

        foreach (GroupReport group in report.Groups)
        {
            Info(
                $"group {GroupName(group.Group)} [{group.HarmonyId}]: " +
                $"applied={group.Applied.Count}[{string.Join(",", group.Applied)}] " +
                $"skipped={group.Skipped.Count} failed={group.Failed.Count}" +
                (group.RolledBack ? " rolledBack=true" : string.Empty));

            foreach (string skip in group.Skipped)
            {
                Info($"  skip {skip}");
            }

            foreach (string failure in group.Failed)
            {
                Error($"  FAILED {failure}");
            }
        }

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

        if (report.Inert)
        {
            Info(
                "no repair reported itself as needed for this build, so nothing was installed. That is a " +
                "normal outcome after a host-mod or game update, not an error.");
        }

        foreach (GroupReport group in report.Groups)
        {
            AnnounceRollback(group, report);
        }
    }

    private static void AnnounceRollback(GroupReport group, StartupReport report)
    {
        if (group.RollbackFailure != null)
        {
            Error(
                $"group '{GroupName(group.Group)}': a repair failed AND removing that group's patches failed " +
                $"({group.RollbackFailure}). Other groups are unaffected, but this group may still be partly " +
                "patched. Restart the game and report this log to the patch author.");
            return;
        }

        if (!group.RolledBack)
        {
            return;
        }

        Error(
            $"group '{GroupName(group.Group)}': a repair could not be installed, so every patch of this group " +
            $"was removed again (UnpatchAll(\"{group.HarmonyId}\")). The other groups keep their patches " +
            $"(still applied: [{string.Join(",", report.Applied)}]). The feature this group provides is " +
            "inactive; the rest of the mod is not.");
        Error(
            "removing a group's patches is NOT the same as undoing them. Harmony hooks are gone, but state a " +
            "repair had already written into this process stays until the process restarts — the reflection " +
            "handles every remaining repair captures at startup (NewRunFromGameOverFix, NewRunSaveBindingFix, " +
            "RosterRebaselineFix, LoadJoinOrderFix, NewProtocolOnlyFix), the game-over nodes " +
            "NewRunFromGameOverFix injects, and the reassemble / disconnect-suppression flags it raises are the " +
            "known cases. Restart the game for a clean state.");
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

    /// <summary>
    /// Outcome of one startup. The first five properties are read by out-of-process harnesses,
    /// <c>tools/smoke-test</c> by reflection over their names and <c>tools/wire-verify</c> by compiling against
    /// them, so their names and types are part of the contract
    /// </summary>
internal sealed class StartupReport
{
    private readonly List<GroupReport> _groups = new();

    internal StartupReport(CompatSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    internal CompatSnapshot Snapshot { get; }

    /// <summary>
    /// True when this startup installed nothing and nothing failed. It describes the run, it gates nothing
    /// </summary>
    internal bool Inert { get; set; }

    internal bool HostMissing { get; set; }

    internal List<string> Applied { get; } = new();

    internal List<string> Skipped { get; } = new();

    /// <summary>
    /// Repairs that failed to install plus repairs removed by their group's rollback. A group whose removal
    /// failed keeps its ids in <see cref="Applied"/>, because they are still patched
    /// </summary>
    internal List<string> Failed { get; } = new();

    internal IReadOnlyList<GroupReport> Groups => _groups;

    internal bool RolledBack { get; set; }

    internal string? RollbackFailure { get; set; }

    internal GroupReport GroupFor(FixGroup group)
    {
        foreach (GroupReport existing in _groups)
        {
            if (existing.Group == group)
            {
                return existing;
            }
        }

        var created = new GroupReport(group);
        _groups.Add(created);
        return created;
    }
}

    /// <summary>One <see cref="FixGroup"/>: its own Harmony instance, patch set and rollback</summary>
internal sealed class GroupReport
{
    internal GroupReport(FixGroup group)
    {
        Group = group;
        HarmonyId = Entry.HarmonyIdFor(group);
        Harmony = new Harmony(HarmonyId);
    }

    internal FixGroup Group { get; }

    internal string HarmonyId { get; }

    /// <summary>
    /// Shared by every repair in the group, so one <c>UnpatchAll(HarmonyId)</c> covers all of them and nothing
    /// outside the group
    /// </summary>
    internal Harmony Harmony { get; }

    /// <summary>Repairs of this group that installed their patches. History: kept after a rollback.</summary>
    internal List<string> Applied { get; } = new();

    internal List<string> Skipped { get; } = new();

    internal List<string> Failed { get; } = new();

    internal bool RolledBack { get; set; }

    internal string? RollbackFailure { get; set; }
}

internal static class CompatFixes
{
    internal static IEnumerable<ICompatFix> Build()
    {
        yield return new RosterRebaselineFix();
        yield return new LoadJoinOrderFix();
        yield return new NewRunFromGameOverFix();
        yield return new NewRunSaveBindingFix();
        yield return new NewProtocolOnlyFix();
    }
}
