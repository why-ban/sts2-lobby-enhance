using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

    /// <summary>
    /// Opens the lobby on the room list directly, without the "选择服务器" picker in front of it.
    ///
    /// A prefix on `LanConnectServerSelectionStartup.Show` rather than on the lobby button itself: upstream's button
    /// resolves the overlay and inspects the clipboard for an invite code and a LAN resume code before it reaches
    /// Show, and replacing the whole handler would drop that precheck. Skipping only the dialog keeps it - the button
    /// still runs its own logic, and when it reaches the picker we invoke the callback upstream would have invoked
    /// after a pick, then return false so no dialog appears.
    ///
    /// The same Show overload also serves the in-overlay SERVER toolbar button, which must keep its picker. The two
    /// callers are told apart by `_serverPickerOpen`: opening the picker raises it before calling Show, and the lobby
    /// entry path does not.
    ///
    /// This is a drop-in mod: the player configures nothing, so pressing 游戏大厅 goes straight to the room list. An
    /// address that turns out to be unusable is the overlay's own status line to report - its room refresh writes the
    /// failure there - not a reason to interrupt the player with a dialog they never asked for.
    ///
    /// The conditions that fall back to upstream's picker are structural, not configurational: no overlay in the tree
    /// yet, no pick callback to invoke, or this repair throwing, in which case upstream's own behaviour is the safer
    /// of the two. Each of those logs one line, because the install log has already reported the seam as applied.
    /// </summary>
internal sealed class LobbyEntryDirectFix : ICompatFix
{
    private const string StartupTypeName = "Sts2LanConnect.Scripts.LanConnectServerSelectionStartup";

    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyOverlay";

    private const string ShowMethodName = "Show";

    private const string PickerOpenFieldName = "_serverPickerOpen";

    private const string ConfigTypeName = "Sts2LanConnect.Scripts.LanConnectConfig";

    private const string ConfigBaseUrlPropertyName = "LobbyServerBaseUrl";

    /// <summary>The overlay's node name (`LanConnectConstants.LobbyOverlayName`, const value).</summary>
    private const string OverlayNodeName = "LanConnectLobbyOverlay";

    private const string SceneTreeTypeName = "Godot.SceneTree";

    private static Shape? _shape;

    private static int _warned;

    /// <summary>Guards the one-time "went straight to the room list" line.</summary>
    private static int _directLogged;

    public string Id => "lobby-entry-direct";

    /// <summary>
    /// Grouped with the pool: the picker is the entry point shared by the lobby button and the SERVER button, and the
    /// server-list handling lives on the same seam. A rollback returns the picker, which is upstream's behaviour.
    /// </summary>
    public FixGroup Group => FixGroup.Pool;

    public bool Applies(out string reason)
    {
        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = "host mod '" + Entry.HostModAssemblyName + "' is not loaded";
            return false;
        }

        var problems = new List<string>();
        Shape? shape = null;
        try
        {
            shape = Shape.TryCapture(host, problems);
        }
        catch (Exception exception)
        {
            problems.Add(exception.GetType().Name + ": " + exception.Message);
        }

        if (shape == null || problems.Count > 0)
        {
            reason = "missing: " + string.Join(", ", problems);
            return false;
        }

        _shape = shape;
        reason = string.Empty;
        return true;
    }

    public void Apply(Harmony harmony)
    {
        Shape shape = _shape ?? throw new InvalidOperationException(
            "lobby-entry-direct: Applies() did not capture the picker shape; refusing to patch blind.");

        harmony.Patch(
            shape.Show,
            prefix: new HarmonyMethod(typeof(LobbyEntryDirectFix), nameof(ShowPrefix))
            {
                priority = Priority.First
            });

        Entry.Info(
            "lobby-entry-direct: 游戏大厅 opens the room list directly instead of the server picker, with no " +
            "configuration check; the SERVER toolbar button keeps its picker.");
    }

    /// <summary>
    /// Runs instead of the picker.
    ///
    /// The parameters are named exactly like upstream's so Harmony binds by name; every type here is Godot or BCL, so
    /// the signature does not depend on a host type.
    /// </summary>
    private static bool ShowPrefix(
        SceneTree tree,
        Func<string, Task>? onPicked,
        Action? onCancelled,
        Action? onSettled)
    {
        _ = onCancelled;

        try
        {
            Shape? shape = _shape;
            if (shape == null)
            {
                // Nothing to call instead of the dialog: let upstream show it.
                WarnSkipped("this repair never captured its anchors");
                return true;
            }

            if (onPicked == null)
            {
                WarnSkipped("Show was called without a pick callback, so there is nothing to invoke instead of the dialog");
                return true;
            }

            object? overlay = FindOverlay(tree, shape);
            if (overlay == null)
            {
                WarnSkipped("no '" + OverlayNodeName + "' node in the tree yet");
                return true;
            }

            if (shape.PickerOpen.GetValue(overlay) is bool open && open)
            {
                // The SERVER toolbar button opened this call: OpenServerPicker raises the flag before it calls Show.
                // That entry point keeps the picker; this repair only answers for the lobby button.
                return true;
            }

            // No configuration check on purpose: the mod is drop-in, so the room list is always what the player
            // asked for. An address that does not answer is the overlay's status line to report.
            string address = shape.ConfigBaseUrl.GetValue(null) as string ?? string.Empty;
            Task? picked = onPicked.Invoke(address);
            if (picked != null && !picked.IsCompleted)
            {
                // The callback is fire-and-forget by contract; observing the exception keeps it from surfacing
                // as an unobserved task fault without blocking this frame.
                _ = picked.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }

            onSettled?.Invoke();

            // One line, because the failure this guards against is silent: if upstream ever raises
            // _serverPickerOpen after Show instead of before it, the SERVER button would lose its picker and nothing
            // else in the log would say so.
            if (Interlocked.Exchange(ref _directLogged, 1) == 0)
            {
                Entry.Info(
                    "lobby-entry-direct: 游戏大厅 went straight to the room list (no server picker). The SERVER " +
                    "toolbar button still opens the picker; if it stops doing so, _serverPickerOpen is being " +
                    "raised after Show now.");
            }

            return false;
        }
        catch (Exception exception)
        {
            // Any surprise here must not cost the player the lobby: fall back to upstream's picker.
            WarnOnce("could not skip the server picker: " + exception.GetType().Name + ": " + exception.Message);
            return true;
        }
    }

    /// <summary>
    /// The overlay is a child of the multiplayer submenu, so a recursive search from the tree root finds the one this
    /// press targets. When it finds none, upstream's own path reports "大厅页面尚未准备好" and this repair stays out
    /// of the way.
    /// </summary>
    private static object? FindOverlay(SceneTree tree, Shape shape)
    {
        Node? root = tree.Root;
        if (root == null)
        {
            return null;
        }

        Node? node = root.FindChild(OverlayNodeName, recursive: true, owned: false);
        return node != null && shape.Overlay.IsInstanceOfType(node) ? node : null;
    }

    private static void WarnOnce(string message)
    {
        if (Interlocked.Exchange(ref _warned, 1) != 0)
        {
            return;
        }

        Entry.Warn("lobby-entry-direct: " + message);
    }

    /// <summary>Reasons already logged, keyed by text so a second distinct miss stays visible.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal);

    /// <summary>
    /// One line per distinct reason the picker was not skipped. A silent fallback here is invisible otherwise: the
    /// install log has already reported this seam as applied.
    /// </summary>
    private static void WarnSkipped(string reason)
    {
        lock (Skipped)
        {
            if (!Skipped.Add(reason))
            {
                return;
            }
        }

        Entry.Warn("lobby-entry-direct: skipped, upstream's server picker is shown instead - " + reason + ".");
    }

    /// <summary>
    /// Every member this repair touches, resolved once in `Applies` and never again. Metadata only:
    /// no `typeof` on a host type, nothing invoked while probing.
    /// </summary>
    internal sealed class Shape
    {
        internal Type Overlay = null!;

        internal MethodInfo Show = null!;

        internal FieldInfo PickerOpen = null!;

        internal PropertyInfo ConfigBaseUrl = null!;

        internal static Shape? TryCapture(Assembly host, List<string> problems)
        {
            Type? startup = ReflectScope.HostType(host, StartupTypeName, problems);
            Type? overlay = ReflectScope.HostType(host, OverlayTypeName, problems);
            Type? config = ReflectScope.HostType(host, ConfigTypeName, problems);
            if (startup == null || overlay == null || config == null)
            {
                return null;
            }

            var shape = new Shape
            {
                Overlay = overlay,
                ConfigBaseUrl = ReflectScope.StaticProperty(problems, config, ConfigBaseUrlPropertyName)!
            };

            // Show has two four-parameter overloads that differ only in the callback type
            // (Action<string> at :17-30, Func<string,Task> at :43-47). The lobby entry button uses the second one,
            // so the callback type is what the anchor pins; an arity lookup alone would be ambiguous.
            MethodInfo[] matches = startup
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => string.Equals(method.Name, ShowMethodName, StringComparison.Ordinal))
                .Where(method => method.GetParameters().Length == 4)
                .Where(method => string.Equals(
                    method.GetParameters()[0].ParameterType.FullName, SceneTreeTypeName, StringComparison.Ordinal))
                .Where(method => IsAsyncPickCallback(method.GetParameters()[1].ParameterType))
                .ToArray();

            if (matches.Length == 1)
            {
                // Harmony binds this patch by parameter name. Without this check a rename would turn into a
                // HarmonyException inside Apply(), and Entry.Install answers that by rolling back the whole
                // Group.Pool (refresh/join/create together) instead of skipping this repair.
                ReflectScope.RequireParameterNames(problems, matches[0], "tree", "onPicked", "onCancelled", "onSettled");
                shape.Show = matches[0];
            }
            else
            {
                problems.Add(matches.Length == 0
                    ? StartupTypeName + "." + ShowMethodName +
                      "(SceneTree, Func<string,Task>, Action, Action) (not found)"
                    : StartupTypeName + "." + ShowMethodName + " (ambiguous: " + matches.Length + " overloads)");
            }

            FieldInfo? pickerOpen = ReflectScope.Field(problems, overlay, PickerOpenFieldName);
            if (pickerOpen != null)
            {
                if (pickerOpen.FieldType != typeof(bool))
                {
                    problems.Add(PickerOpenFieldName + " is " + pickerOpen.FieldType.FullName +
                                 ", expected System.Boolean");
                }
                else
                {
                    shape.PickerOpen = pickerOpen;
                }
            }

            return shape;
        }

        /// <summary>`Func&lt;string, Task&gt;`, matched by shape so no host type is named here.</summary>
        private static bool IsAsyncPickCallback(Type type)
        {
            if (!type.IsGenericType ||
                !string.Equals(type.GetGenericTypeDefinition().FullName, "System.Func`2", StringComparison.Ordinal))
            {
                return false;
            }

            Type[] arguments = type.GetGenericArguments();
            return arguments[0] == typeof(string) && arguments[1] == typeof(Task);
        }
    }
}
