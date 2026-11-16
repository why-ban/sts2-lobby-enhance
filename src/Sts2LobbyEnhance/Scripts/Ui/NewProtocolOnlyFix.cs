using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Removes the whole 联机协议 section from the create-room dialog and creates every room on 新协议 (id 301).
///
/// The layer is redundant once the legacy profile is gone: upstream still renders a title, two choice buttons and a
/// hint, while this mod offers only 新协议. Removing the section also gives the server picker the space back.
///
/// Upstream resets the selection on every open (ShowCreateRoomDialog calls SelectDefaultCreateProtocol and then
/// RefreshProtocolProfileOptionAvailability), and its default is 兼容旧版 Mod whenever RitsuLib is absent, so the
/// choice has to be overridden where it is read (GetSelectedCreateProtocolId) rather than once at build time.
///
/// The section is detached, not freed: ShowCreateRoomDialog still calls RefreshProtocolProfileOptionAvailability,
/// which writes the buttons' Disabled state and repaints their styles. Detached nodes stay valid, so those writes
/// keep working; freeing them would make every later open throw.
/// </summary>
internal sealed class NewProtocolOnlyFix : ICompatFix
{
    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyOverlay";
    private const string BuildDialogMethodName = "BuildCreateDialog";
    private const string SelectedIdMethodName = "GetSelectedCreateProtocolId";
    private const string CompatButtonFieldName = "_protocolCompatButton";
    private const string SelectableMethodName = "IsCreateProtocolSelectable";
    private const string TailRuntimeMethodName = "IsStandaloneTailRuntimeAvailable";

    /// <summary>Upstream's id for 新协议 (`CreateProtocolTailId`)</summary>
    private const int TailProtocolId = 301;

    private static Shape? _shape;

    private static int _warned;

    /// <summary>-1 unknown, 0 legacy stays because 新协议 is unusable, 1 新协议 forced</summary>
    private static int _decision = -1;

    public string Id => "new-protocol-only";

    public FixGroup Group => FixGroup.Modes;

    public bool Applies(out string reason)
    {
        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = "host mod '" + Entry.HostModAssemblyName + "' is not loaded";
            return false;
        }

        Shape? shape = Shape.TryCapture(host, out reason);
        if (shape == null)
        {
            return false;
        }

        _shape = shape;
        reason = string.Empty;
        return true;
    }

    public void Apply(Harmony harmony)
    {
        Shape shape = _shape ?? throw new InvalidOperationException(
            "new-protocol-only: Applies() did not capture the dialog shape; refusing to patch blind.");

        harmony.Patch(shape.BuildDialog, postfix: new HarmonyMethod(typeof(NewProtocolOnlyFix), nameof(DropProtocolSectionPostfix)));
        harmony.Patch(shape.SelectedId, postfix: new HarmonyMethod(typeof(NewProtocolOnlyFix), nameof(ForceTailPostfix)));

        Entry.Info(
            "new-protocol-only: patched " + OverlayTypeName + ".BuildCreateDialog and .GetSelectedCreateProtocolId; " +
            "the create dialog no longer shows the 联机协议 section and rooms are published on 新协议.");
    }

    private static void DropProtocolSectionPostfix(object? __instance)
    {
        try
        {
            Shape? shape = _shape;
            if (shape == null || __instance == null)
            {
                return;
            }

            if (shape.CompatButton.GetValue(__instance) is not Button compat || !GodotObject.IsInstanceValid(compat))
            {
                return;
            }

            // compat -> choices VBox -> the section VBox that BuildCreateDialog added to the dialog body
            Node? section = compat.GetParent()?.GetParent();
            Node? body = section?.GetParent();
            if (section == null || body == null)
            {
                return;
            }

            body.RemoveChild(section);
        }
        catch (Exception exception)
        {
            WarnOnce("could not remove the protocol section: " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static void ForceTailPostfix(ref int __result)
    {
        try
        {
            Shape? shape = _shape;
            if (shape == null)
            {
                return;
            }

            // Upstream's own predicate: when 新协议 cannot be hosted right now (no standalone tail runtime), forcing
            // it would turn a working create into a hard failure.
            bool tailUsable = shape.IsSelectable.Invoke(null, new object?[]
            {
                TailProtocolId,
                shape.CreateOffer.Invoke(null, null),
                shape.TailRuntimeAvailable.Invoke(null, null),
            }) is true;

            if (!tailUsable)
            {
                RecordDecision(0, "[lobby-enhance] new-protocol-only: 新协议 is not selectable right now, so the room " +
                                  "keeps upstream's legacy profile even though the section is hidden.");
                return;
            }

            __result = TailProtocolId;
            RecordDecision(1, "[lobby-enhance] new-protocol-only: rooms are published on 新协议 (301).");
        }
        catch (Exception exception)
        {
            WarnOnce("could not force the protocol choice: " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static void RecordDecision(int value, string message)
    {
        if (Interlocked.Exchange(ref _decision, value) == value)
        {
            return;
        }

        Entry.Info(message);
    }

    private static void WarnOnce(string message)
    {
        if (Interlocked.Exchange(ref _warned, 1) != 0)
        {
            return;
        }

        Entry.Warn("new-protocol-only: " + message);
    }

    internal sealed class Shape
    {
        internal MethodInfo BuildDialog { get; init; } = null!;

        internal MethodInfo SelectedId { get; init; } = null!;

        internal FieldInfo CompatButton { get; init; } = null!;

        internal MethodInfo IsSelectable { get; init; } = null!;

        internal MethodInfo CreateOffer { get; init; } = null!;

        internal MethodInfo TailRuntimeAvailable { get; init; } = null!;

        internal static Shape? TryCapture(Assembly host, out string reason)
        {
            var missing = new List<string>();

            Type? overlay = host.GetType(OverlayTypeName, throwOnError: false, ignoreCase: false);
            if (overlay == null)
            {
                reason = "missing: " + OverlayTypeName;
                return null;
            }

            MethodInfo? build = Instance(overlay, BuildDialogMethodName, 0, missing);
            MethodInfo? selected = Instance(overlay, SelectedIdMethodName, 0, missing);
            MethodInfo? selectable = Static(overlay, SelectableMethodName, 3, missing);
            MethodInfo? tailRuntime = Static(overlay, TailRuntimeMethodName, 0, missing);
            FieldInfo? compat = Field(overlay, CompatButtonFieldName, missing);

            Type? offerType = host.GetType("Sts2LanConnect.Scripts.LanConnectProtocolOffer", throwOnError: false, ignoreCase: false);
            MethodInfo? createOffer = offerType?.GetMethod("CreateCurrent", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (createOffer == null)
            {
                missing.Add("LanConnectProtocolOffer.CreateCurrent");
            }

            if (missing.Count > 0)
            {
                reason = "missing: " + string.Join(", ", missing);
                return null;
            }

            reason = string.Empty;
            return new Shape
            {
                BuildDialog = build!,
                SelectedId = selected!,
                CompatButton = compat!,
                IsSelectable = selectable!,
                CreateOffer = createOffer!,
                TailRuntimeAvailable = tailRuntime!,
            };
        }

        private static MethodInfo? Instance(Type owner, string name, int arity, List<string> missing)
        {
            MethodInfo? method = owner
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == arity);
            if (method == null)
            {
                missing.Add(OverlayTypeName + "." + name + "/" + arity);
            }

            return method;
        }

        private static MethodInfo? Static(Type owner, string name, int arity, List<string> missing)
        {
            MethodInfo? method = owner
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == arity);
            if (method == null)
            {
                missing.Add(OverlayTypeName + "." + name + "/" + arity + " (static)");
            }

            return method;
        }

        private static FieldInfo? Field(Type owner, string name, List<string> missing)
        {
            FieldInfo? field = owner.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
            {
                missing.Add(OverlayTypeName + "." + name);
            }

            return field;
        }
    }
}
