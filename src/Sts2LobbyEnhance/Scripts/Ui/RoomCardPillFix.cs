using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// The mode pill drawn on every lobby room card (and in the selected-room summary).
///
/// This used to live inside <see cref="ExtraGameModesFix"/> and that was a mistake: the pill needs three
/// private <c>Color</c> properties of the host overlay, and a rename of any one of them made the whole
/// <c>extra-game-modes</c> repair fail — which, through <c>Entry</c>'s rollback, took every other repair
/// down with it. A cosmetic label must never be able to disable networking fixes, so it is its own fix
/// and it is allowed to fail on its own.
///
/// Upstream's <c>GetRoomGameModePill</c> maps the mode string to exactly three labels: <c>"daily"</c> →
/// 挑战, <c>"custom"</c> → 自定义, and everything else → <c>"STD"</c>
/// (<c>dec-official/LanConnectLobbyOverlay.cs:7943-7950</c>). The two strings this mod feeds into the
/// lobby (<c>blitz</c>/<c>dread</c>) are "everything else", which is why those rooms showed STD.
///
/// Self-healing by construction: the postfix only rewrites a result whose text is still the catch-all
/// <c>"STD"</c>. If any build returns anything else for these two modes, that value is left untouched and
/// this fix steps aside — no version check, no probe, no way to overwrite a label it did not produce.
/// </summary>
internal sealed class RoomCardPillFix : ICompatFix
{
    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyOverlay";
    private const string PillMethodName = "GetRoomGameModePill";

    /// <summary>Upstream's catch-all pill text (<c>dec-official/LanConnectLobbyOverlay.cs:7949</c>).</summary>
    private const string StandardPillText = "STD";

    private static Shape? _shape;
    private static int _warned;

    public string Id => "room-card-pill";

    public FixGroup Group => FixGroup.Cosmetic;

    /// <summary>
    /// Applicable when the host mod is present and the pill seam resolves: <c>GetRoomGameModePill(string)</c>
    /// returning <c>(string, Color, Color)</c>, plus the three private static <c>Color</c> properties.
    ///
    /// <see cref="CompatSnapshot"/> is not read. Nothing about a game-version breakpoint decides whether a
    /// room card can carry a different label; the anchors themselves are the whole question.
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
            "room-card-pill: Applies() did not capture the overlay shape; refusing to patch blind.");

        harmony.Patch(
            shape.Pill,
            postfix: new HarmonyMethod(typeof(RoomCardPillFix), nameof(PillPostfix))
            {
                priority = Priority.Last
            });

        Entry.Info(
            "room-card-pill: patched " + OverlayTypeName + "." + PillMethodName +
            " (blitz/dread labels; only rewrites upstream's \"" + StandardPillText + "\" catch-all).");
    }

    // ReSharper disable once UnusedParameter.Local — names are Harmony's binding contract.
    private static void PillPostfix(string? gameMode, ref (string Text, Color Border, Color Background) __result)
    {
        try
        {
            // Self-healing: anything upstream already labelled is left untouched.
            if (!string.Equals(__result.Text, StandardPillText, StringComparison.Ordinal))
            {
                return;
            }

            Shape shape = _shape ?? throw new InvalidOperationException("room-card-pill: shape was not captured.");

            switch (gameMode?.Trim().ToLowerInvariant())
            {
                case "blitz":
                    __result = (
                        "极速",
                        shape.ReadColor(shape.Success, "SuccessColor"),
                        shape.ReadColor(shape.SurfaceMuted, "SurfaceMutedColor"));
                    break;
                case "dread":
                    __result = (
                        "恐惧",
                        shape.ReadColor(shape.Danger, "DangerColor"),
                        shape.ReadColor(shape.SurfaceMuted, "SurfaceMutedColor"));
                    break;
            }
        }
        catch (Exception exception)
        {
            // Runs once per room card; a broken theme read must not spam the log or blank the list.
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                Entry.Warn(
                    "room-card-pill: could not build the pill, keeping the original text (" +
                    exception.GetType().Name + ": " + exception.Message + ").");
            }
        }
    }

    /// <summary>
    /// Everything this fix needs, resolved without <c>typeof</c> on any version-specific type: a probe
    /// that named <c>NBlitzScreen</c> or the overlay directly would itself fail to JIT on a build that
    /// dropped it, which is exactly the failure this probe exists to avoid.
    /// </summary>
    private sealed class Shape
    {
        private Shape(MethodInfo pill, PropertyInfo success, PropertyInfo danger, PropertyInfo surfaceMuted)
        {
            Pill = pill;
            Success = success;
            Danger = danger;
            SurfaceMuted = surfaceMuted;
        }

        internal MethodInfo Pill { get; }

        internal PropertyInfo Success { get; }

        internal PropertyInfo Danger { get; }

        internal PropertyInfo SurfaceMuted { get; }

        /// <summary>Reads one of the overlay's private static <c>Color</c> properties on every call.</summary>
        internal Color ReadColor(PropertyInfo property, string name) =>
            property.GetValue(null) is Color color
                ? color
                : throw new InvalidOperationException($"{OverlayTypeName}.{name} did not return a Color.");

        internal static Shape? TryCapture(Assembly host, out string reason)
        {
            List<string> missing = new();

            Type? overlay = host.GetType(OverlayTypeName, throwOnError: false, ignoreCase: false);
            if (overlay == null)
            {
                reason = "missing host type " + OverlayTypeName;
                return null;
            }

            MethodInfo? pill = Unique(overlay, PillMethodName, 1, missing);
            if (pill != null)
            {
                if (!pill.IsStatic)
                {
                    missing.Add("LanConnectLobbyOverlay." + PillMethodName + " (instance, expected static)");
                    pill = null;
                }
                else if (!ReturnsStringColorColor(pill.ReturnType))
                {
                    missing.Add("LanConnectLobbyOverlay." + PillMethodName + "(string) -> (string, Color, Color)");
                    pill = null;
                }
            }

            PropertyInfo? success = StaticColorProperty(overlay, "SuccessColor", missing);
            PropertyInfo? danger = StaticColorProperty(overlay, "DangerColor", missing);
            PropertyInfo? surfaceMuted = StaticColorProperty(overlay, "SurfaceMutedColor", missing);

            if (pill == null || success == null || danger == null || surfaceMuted == null)
            {
                reason = "missing: " + string.Join(", ", missing);
                return null;
            }

            reason = string.Empty;
            return new Shape(pill, success, danger, surfaceMuted);
        }

        // All three element types are checked, not just the first: the postfix binds
        // "ref (string, Color, Color) __result", so a tuple whose other two elements are not Color would be
        // accepted here and then fail at bind time. Color is a Godot type we already reference, so naming it is
        // not a version-dependent game type.
        private static bool ReturnsStringColorColor(Type returnType) =>
            returnType.IsGenericType
            && returnType.GetGenericArguments().Length == 3
            && returnType.GetGenericArguments()[0] == typeof(string)
            && returnType.GetGenericArguments()[1] == typeof(Color)
            && returnType.GetGenericArguments()[2] == typeof(Color);

        private static PropertyInfo? StaticColorProperty(Type overlay, string name, List<string> missing)
        {
            PropertyInfo? property = overlay.GetProperty(
                name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            if (property == null)
            {
                missing.Add("LanConnectLobbyOverlay." + name + " (private static Color)");
                return null;
            }

            if (property.PropertyType != typeof(Color))
            {
                missing.Add("LanConnectLobbyOverlay." + name + " (Color) (found: " + property.PropertyType.Name + ")");
                return null;
            }

            MethodInfo? getter = property.GetGetMethod(nonPublic: true);
            if (getter == null || !getter.IsStatic)
            {
                missing.Add("LanConnectLobbyOverlay." + name + " (static property) (found: instance)");
                return null;
            }

            return property;
        }

        private static MethodInfo? Unique(Type type, string name, int parameterCount, List<string> missing)
        {
            MethodInfo[] matches = type
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal))
                .Where(method => method.GetParameters().Length == parameterCount)
                .ToArray();

            if (matches.Length == 1)
            {
                return matches[0];
            }

            missing.Add(matches.Length == 0
                ? type.Name + "." + name + "/" + parameterCount + " param(s)"
                : type.Name + "." + name + "/" + parameterCount + " param(s) (ambiguous: " + matches.Length + " overloads)");
            return null;
        }
    }
}
