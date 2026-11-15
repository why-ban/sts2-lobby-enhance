using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

    /// <summary>
    /// Leaves only 「新协议」 in the create-room dialog and preselects it. The legacy option is hidden, not removed:
    /// the upstream button, its description, its protocol id and every path handling 兼容旧版 Mod stay untouched,
    /// and joining a legacy room is unaffected because that path reads the room's advertised profile
    ///
    /// RitsuLib is a dependency of many mods, and upstream refuses to host it on the legacy profile at all
    /// (LanConnectCreateRoomIntent.cs:17-19 throws RitsuLibNotAllowedInCompat), so a host who picks 兼容旧版 Mod
    /// with RitsuLib installed gets an error and one without RitsuLib silently creates a room that cannot carry
    /// it, while the two profiles are otherwise identical in play
    ///
    /// The postfix does nothing unless upstream still offers both protocol buttons and considers 新协议
    /// selectable, so any other shape leaves the dialog exactly as upstream built it
    /// </summary>
internal sealed class NewProtocolOnlyFix : ICompatFix
{
    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyOverlay";
    private const string RefreshMethodName = "RefreshProtocolProfileOptionAvailability";
    private const string TailButtonFieldName = "_protocolTailButton";
    private const string CompatButtonFieldName = "_protocolCompatButton";
    private const string SelectedIdFieldName = "_selectedCreateProtocolId";
    private const string SelectableMethodName = "IsCreateProtocolSelectable";
    private const string TailRuntimeMethodName = "IsStandaloneTailRuntimeAvailable";
    private const string StylesMethodName = "UpdateProtocolChoiceStyles";
    private const string HintMethodName = "UpdateProtocolProfileHint";

    /// <summary>Upstream's protocol id for 新协议 (<c>LanConnectLobbyOverlay.cs:7301</c>, id <c>301</c>).</summary>
    private const int TailProtocolId = 301;

    private static Shape? _shape;

    public string Id => "new-protocol-only";

    /// <summary>
    /// The only repair that edits the create-room dialog, so it owns the <see cref="FixGroup.Modes"/> instance:
    /// a shape change in that dialog rolls back this repair and nothing else
    /// </summary>
    public FixGroup Group => FixGroup.Modes;

    /// <summary>
    /// <see cref="CompatSnapshot"/> is not read: no game-version breakpoint has anything to say about which
    /// protocol buttons exist
    /// </summary>
    public bool Applies(in CompatSnapshot snapshot, out string reason)
    {
        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = "host mod 'sts2_lan_connect' is not loaded";
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

    public void Apply(Harmony harmony, in CompatSnapshot snapshot)
    {
        Shape shape = _shape ?? throw new InvalidOperationException(
            "new-protocol-only: Applies() did not capture the dialog shape; refusing to patch blind.");

        harmony.Patch(
            shape.Refresh,
            postfix: new HarmonyMethod(typeof(NewProtocolOnlyFix), nameof(RefreshPostfix))
            {
                priority = Priority.Last
            });

        Entry.Info(
            "new-protocol-only: patched " + OverlayTypeName + "." + RefreshMethodName + "; the create-room dialog " +
            "now offers 新协议 only (the legacy button is hidden, not removed).");
    }

    // ReSharper disable once UnusedParameter.Local — Harmony binds by position.
    private static void RefreshPostfix(object __instance)
    {
        if (_shape is not { } shape || __instance == null)
        {
            return;
        }

        try
        {
            if (shape.TailButton.GetValue(__instance) is not Button tail ||
                shape.CompatButton.GetValue(__instance) is not Button compat)
            {
                // Dialog not built yet (upstream returns early in the same situation), or the buttons were
                // removed. Either way there is nothing to hide and forcing a selection would be wrong.
                return;
            }

            // Only act when upstream agrees 新协议 is actually usable right now. If the message registry is not
            // ready, the legacy button is the only way to host at all, and hiding it would strand the player.
            if (!IsTailSelectable(shape))
            {
                return;
            }

            compat.Visible = false;

            if (shape.SelectedId.GetValue(__instance) is int selected && selected == TailProtocolId)
            {
                return;
            }

            shape.SelectedId.SetValue(__instance, TailProtocolId);
            shape.UpdateStyles.Invoke(__instance, null);
            shape.UpdateHint.Invoke(__instance, null);
        }
        catch (Exception exception)
        {
            // A UI preference must never break room creation. Leave upstream's dialog as it is and say so once.
            WarnOnce("could not hide the legacy protocol entry: " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>
    /// Uses upstream's own predicate so this fix cannot disagree with the button's disabled state
    /// </summary>
    private static bool IsTailSelectable(Shape shape)
    {
        object? offer = shape.CreateOffer.Invoke(null, null);
        object? tailAvailable = shape.TailRuntimeAvailable.Invoke(null, null);
        if (offer == null || tailAvailable is not bool available)
        {
            return false;
        }

        return shape.IsSelectable.Invoke(null, new[] { (object)TailProtocolId, offer, available }) is true;
    }

    private static int _warned;

    private static void WarnOnce(string message)
    {
        if (Interlocked.Exchange(ref _warned, 1) != 0)
        {
            return;
        }

        Entry.Warn("new-protocol-only: " + message);
    }

    /// <summary>
    /// Every member this fix touches, resolved once in <see cref="Applies"/> and never again. Metadata only: no
    /// <c>typeof</c> on a host type, nothing invoked while probing
    /// </summary>
    internal sealed class Shape
    {
        private Shape(
            MethodInfo refresh,
            FieldInfo tailButton,
            FieldInfo compatButton,
            FieldInfo selectedId,
            MethodInfo isSelectable,
            MethodInfo createOffer,
            MethodInfo tailRuntimeAvailable,
            MethodInfo updateStyles,
            MethodInfo updateHint)
        {
            Refresh = refresh;
            TailButton = tailButton;
            CompatButton = compatButton;
            SelectedId = selectedId;
            IsSelectable = isSelectable;
            CreateOffer = createOffer;
            TailRuntimeAvailable = tailRuntimeAvailable;
            UpdateStyles = updateStyles;
            UpdateHint = updateHint;
        }

        internal MethodInfo Refresh { get; }

        internal FieldInfo TailButton { get; }

        internal FieldInfo CompatButton { get; }

        internal FieldInfo SelectedId { get; }

        internal MethodInfo IsSelectable { get; }

        internal MethodInfo CreateOffer { get; }

        internal MethodInfo TailRuntimeAvailable { get; }

        internal MethodInfo UpdateStyles { get; }

        internal MethodInfo UpdateHint { get; }

        internal static Shape? TryCapture(Assembly host, out string reason)
        {
            var missing = new List<string>();

            Type? overlay = host.GetType(OverlayTypeName, throwOnError: false, ignoreCase: false);
            if (overlay == null)
            {
                reason = "missing: " + OverlayTypeName;
                return null;
            }

            MethodInfo? refresh = Instance(overlay, RefreshMethodName, 0, missing);
            FieldInfo? tail = Field(overlay, TailButtonFieldName, missing);
            FieldInfo? compat = Field(overlay, CompatButtonFieldName, missing);
            FieldInfo? selected = Field(overlay, SelectedIdFieldName, missing);
            MethodInfo? selectable = Static(overlay, SelectableMethodName, 3, missing);
            MethodInfo? styles = Instance(overlay, StylesMethodName, 0, missing);
            MethodInfo? hint = Instance(overlay, HintMethodName, 0, missing);

            // LanConnectProtocolOffer is a host type, so it is resolved by name rather than named here.
            Type? offerType = host.GetType("Sts2LanConnect.Scripts.LanConnectProtocolOffer", throwOnError: false, ignoreCase: false);
            MethodInfo? createOffer = offerType == null
                ? null
                : offerType.GetMethod("CreateCurrent", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (createOffer == null)
            {
                missing.Add("LanConnectProtocolOffer.CreateCurrent");
            }

            // Static, not instance: dec-official LanConnectLobbyOverlay.cs:10667 is
            // "private static bool IsStandaloneTailRuntimeAvailable()". Getting that shape wrong is exactly the
            // failure mode that once left a repair installed-but-inert, so a mismatch is reported as a missing
            // anchor instead of patching blind.
            MethodInfo? tailRuntimeAvailable = Static(overlay, TailRuntimeMethodName, 0, missing);

            if (missing.Count > 0)
            {
                reason = "missing: " + string.Join(", ", missing);
                return null;
            }

            reason = string.Empty;
            return new Shape(refresh!, tail!, compat!, selected!, selectable!, createOffer!, tailRuntimeAvailable!, styles!, hint!);
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
