using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

    /// <summary>
    /// Adds a red <c>来源：{服务器}</c> line under the lobby announcement body and makes the carousel page with the
    /// mouse wheel.
    ///
    /// Where the line goes and why it is a new node: <c>LobbyAnnouncementCarousel.SetAnnouncements</c> copies
    /// exactly six fields out of each DTO (<c>LobbyAnnouncementCarousel.cs:87-96</c>), so a source added to
    /// <c>LobbyAnnouncementItem</c> would be dropped before it ever reached a label - and the DTO belongs to
    /// upstream anyway. A label inserted after <c>_bodyLabel</c> inside the carousel's own <c>root</c>
    /// VBoxContainer (<c>:147-153</c>, body added at <c>:241-250</c>) is the one place that survives that copy and
    /// still sits in the layout instead of floating over it.
    ///
    /// Why the prefix on <c>ApplyAnnouncements</c> does not rewrite the list: <c>ApplyAnnouncements</c>
    /// (<c>LanConnectLobbyOverlay.cs:3503-3520</c>) copies the incoming items into the overlay's own list and hands
    /// that to the carousel, so replacing the argument would only change what is copied - the per-announcement
    /// origin still has nowhere to live. The prefix therefore marks the batch boundary (the seam a future
    /// multi-server fetch would register origins through) and the source line is rendered from the carousel's own
    /// copy, matched by announcement id.
    ///
    /// Why the wheel handler goes on <c>_frame</c>: the carousel itself is <c>MouseFilter = Stop</c>
    /// (<c>:43</c>), but its child <c>_frame</c> is a full-rect <c>PanelContainer</c> that is also <c>Stop</c>
    /// (<c>:128-133</c>), so Godot delivers the wheel to the frame and never to the carousel. The frame is where
    /// the handler has to be.
    /// </summary>
internal sealed class AnnouncementSourceFix : ICompatFix
{
    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyOverlay";

    private const string CarouselTypeName = "Sts2LanConnect.Scripts.LobbyAnnouncementCarousel";

    private const string ThemeTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyThemes";

    private const string ConfigTypeName = "Sts2LanConnect.Scripts.LanConnectConfig";

    private const string ApplyAnnouncementsMethodName = "ApplyAnnouncements";

    private const string RefreshAnnouncementMethodName = "RefreshCurrentAnnouncement";

    private const string AdvanceMethodName = "Advance";

    private const string ConfigBaseUrlPropertyName = "LobbyServerBaseUrl";

    private const string SourceLabelNodeName = "LobbyEnhanceAnnouncementSource";

    private const string WheelMetaKey = "sts2_lobby_enhance_announcement_wheel";

    private const string SourcePrefix = "来源：";

    /// <summary>Body text is 13 px (<c>LobbyAnnouncementCarousel.cs:249</c>), so the source line is two less.</summary>
    private const int SourceFontSize = 11;

    /// <summary>Same short-name rule as the room cards and the channel row, one step wider for a full-width line.</summary>
    private const int MaxNameChars = 12;

    /// <summary>Upstream's <c>Palette.Danger</c> of the current theme, only used if that lookup moved.</summary>
    private static readonly Color FallbackDanger = new(0.97f, 0.44f, 0.44f, 1f);

    private static Shape? _shape;

    private static int _warned;

    public string Id => "announcement-source";

    /// <summary>
    /// The carousel's own seam. It is deliberately not <see cref="FixGroup.Pool"/> or <see cref="FixGroup.Chat"/>:
    /// a carousel shape change must cost the carousel only, not the room list or the channel switch.
    /// </summary>
    public FixGroup Group => FixGroup.Announce;

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
            "announcement-source: Applies() did not capture the carousel shape; refusing to patch blind.");

        harmony.Patch(
            shape.ApplyAnnouncements,
            prefix: new HarmonyMethod(typeof(AnnouncementSourceFix), nameof(ApplyAnnouncementsPrefix))
            {
                priority = Priority.First
            });

        harmony.Patch(
            shape.RefreshAnnouncement,
            postfix: new HarmonyMethod(typeof(AnnouncementSourceFix), nameof(RefreshAnnouncementPostfix))
            {
                priority = Priority.Last
            });

        Entry.Info(
            "announcement-source: every announcement now carries a red 来源 line under its body, and the " +
            "carousel pages with the mouse wheel.");
    }

    /// <summary>
    /// Marks a fresh batch. Nothing is rewritten here on purpose: the origin of an announcement is not part of
    /// the DTO, so the line is rendered per item from the carousel's own copy.
    /// </summary>
    private static void ApplyAnnouncementsPrefix()
    {
        try
        {
            Origins.BeginBatch();
        }
        catch (Exception exception)
        {
            WarnOnce("could not mark the announcement batch: " +
                     exception.GetType().Name + ": " + exception.Message);
        }
    }

    // ReSharper disable once UnusedParameter.Local — Harmony binds by name.
    private static void RefreshAnnouncementPostfix(object __instance)
    {
        Shape? shape = _shape;
        if (shape == null || __instance is not Control carousel)
        {
            return;
        }

        if (!GodotObject.IsInstanceValid(carousel) || !carousel.IsInsideTree())
        {
            return;
        }

        try
        {
            Sync(shape, carousel);
        }
        catch (Exception exception)
        {
            // A cosmetic line must never break the announcement panel: leave the carousel as upstream built it.
            WarnOnce("could not add the announcement source line: " +
                     exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static void Sync(Shape shape, Control carousel)
    {
        if (shape.BodyLabel.GetValue(carousel) is not Label body || !GodotObject.IsInstanceValid(body))
        {
            return;
        }

        // Upstream builds the body as a child of its root VBoxContainer (<c>LobbyAnnouncementCarousel.cs:241-250</c>).
        if (body.GetParent() is not VBoxContainer root)
        {
            return;
        }

        Label? source = EnsureLabel(shape, root, body);
        if (source == null)
        {
            return;
        }

        source.AddThemeColorOverride("font_color", ResolveDangerColor(shape));

        IList? items = shape.Announcements.GetValue(carousel) as IList;
        int index = shape.CurrentIndex.GetValue(carousel) is int value ? value : 0;
        string id = ReadId(shape, items, index);
        string fallback = ResolveLocalName(shape);
        string name = id.Length > 0 && Origins.TryGet(id, out string registered) ? registered : fallback;
        string text = BuildText(name, shape, items, fallback, index);
        if (!string.Equals(source.Text, text, StringComparison.Ordinal))
        {
            source.Text = text;
        }

        // The body hides itself in compact mode and when the announcement has no body text; the source line has
        // nowhere to sit then, so it follows and the tooltip carries the same information.
        source.Visible = body.Visible;
        ApplyTooltip(shape, carousel, text);
        ConnectWheel(shape, carousel);
    }

    private static Label? EnsureLabel(Shape shape, VBoxContainer root, Label body)
    {
        Node? existing = root.FindChild(SourceLabelNodeName, recursive: false, owned: false);
        if (existing is Label label)
        {
            return GodotObject.IsInstanceValid(label) ? label : null;
        }

        if (existing != null)
        {
            // A foreign node already holds our name; do not fight over it.
            return null;
        }

        var created = new Label
        {
            Name = SourceLabelNodeName,
            ClipText = true,
            AutowrapMode = TextServer.AutowrapMode.Off,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        created.AddThemeFontSizeOverride("font_size", SourceFontSize);
        created.AddThemeColorOverride("font_color", ResolveDangerColor(shape));

        root.AddChild(created);
        root.MoveChild(created, body.GetIndex() + 1);
        return created;
    }

    /// <summary>
    /// <c>LanConnectLobbyThemes.Current.Palette.Danger</c> (<c>LanConnectLobbyTheme.cs:308/121/58</c>): the same red
    /// upstream already uses for destructive text, so a theme switch recolours the line with it.
    /// </summary>
    private static Color ResolveDangerColor(Shape shape)
    {
        if (shape.ThemeCurrent == null || shape.ThemePalette == null || shape.ThemeDanger == null)
        {
            return FallbackDanger;
        }

        try
        {
            object? theme = shape.ThemeCurrent.GetValue(null);
            object? palette = theme == null ? null : shape.ThemePalette.GetValue(theme);
            object? danger = palette == null ? null : shape.ThemeDanger.GetValue(palette);
            if (danger is Color color)
            {
                return color;
            }
        }
        catch (Exception)
        {
            // Falls through to the literal: a moved palette must not cost the line.
        }

        return FallbackDanger;
    }

    private static string BuildText(string name, Shape shape, IList? items, string fallback, int index)
    {
        string text = SourcePrefix + name;
        if (items == null || items.Count <= 1)
        {
            return text;
        }

        // At most one grouping hint in the whole carousel: upstream builds one dot per item
        // (<c>LobbyAnnouncementCarousel.cs:381-394</c>), so inserting a hint item per group would bloat the dot row.
        // The hint rides on the first slide's line instead of adding an item.
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        for (int position = 0; position < items.Count; position++)
        {
            string id = ReadId(shape, items, position);
            distinct.Add(id.Length > 0 && Origins.TryGet(id, out string registered) ? registered : fallback);
        }

        if (distinct.Count > 1 && index == 0)
        {
            text += "（共 " + items.Count + " 条，来自 " + distinct.Count + " 台服务器）";
        }

        return text;
    }

    private static void ApplyTooltip(Shape shape, Control carousel, string text)
    {
        if (shape.Frame.GetValue(carousel) is not PanelContainer frame || !GodotObject.IsInstanceValid(frame))
        {
            return;
        }

        string tooltip = frame.TooltipText;
        if (tooltip.Contains(SourcePrefix, StringComparison.Ordinal))
        {
            return;
        }

        frame.TooltipText = tooltip.Length == 0 ? text : tooltip + "\n" + text;
    }

    /// <summary>
    /// Godot delivers the wheel to the deepest <c>Stop</c> control under the pointer, which is the carousel's own
    /// full-rect <c>_frame</c> (<c>LobbyAnnouncementCarousel.cs:128-133</c>), not the carousel. Both are wired so a
    /// future layout that drops the frame still pages; the meta key keeps the wiring idempotent per node.
    /// </summary>
    private static void ConnectWheel(Shape shape, Control carousel)
    {
        if (shape.Frame.GetValue(carousel) is Control frame && GodotObject.IsInstanceValid(frame))
        {
            Wire(frame, shape, carousel);
        }

        Wire(carousel, shape, carousel);
    }

    private static void Wire(Control target, Shape shape, Control carousel)
    {
        if (target.HasMeta(WheelMetaKey))
        {
            return;
        }

        target.SetMeta(WheelMetaKey, true);
        target.Connect(
            Control.SignalName.GuiInput,
            Callable.From<InputEvent>(@event => OnWheel(shape, carousel, @event)));
    }

    private static void OnWheel(Shape shape, Control carousel, InputEvent input)
    {
        try
        {
            if (input is not InputEventMouseButton { Pressed: true } button)
            {
                return;
            }

            int direction;
            if (button.ButtonIndex == MouseButton.WheelUp)
            {
                direction = -1;
            }
            else if (button.ButtonIndex == MouseButton.WheelDown)
            {
                direction = 1;
            }
            else
            {
                return;
            }

            // Advance() resets the auto-advance timer itself (<c>LobbyAnnouncementCarousel.cs:275-285</c>), so a
            // wheel turn is never overridden by a timer that was already nine-tenths done.
            shape.Advance.Invoke(carousel, new object?[] { direction });
            carousel.AcceptEvent();
        }
        catch (Exception exception)
        {
            WarnOnce("could not page the announcement carousel: " +
                     exception.GetType().Name + ": " + exception.Message);
        }
    }

    private static string ReadId(Shape shape, IList? items, int index)
    {
        if (items == null || items.Count == 0)
        {
            return string.Empty;
        }

        object? item = items[Math.Clamp(index, 0, items.Count - 1)];
        return item == null ? string.Empty : shape.ItemId.GetValue(item) as string ?? string.Empty;
    }

    /// <summary>The server the lobby itself is pointed at, which is where upstream fetches announcements from.</summary>
    private static string ResolveLocalName(Shape shape)
    {
        string baseUrl = shape.ConfigBaseUrl?.GetValue(null) as string ?? string.Empty;
        if (baseUrl.Length == 0)
        {
            return "本服";
        }

        try
        {
            return ChatServerCatalog.TryResolveDisplayName(baseUrl, out string displayName)
                ? ChatServerCatalog.ShortName(displayName, baseUrl, MaxNameChars)
                : ChatServerCatalog.ShortName(null, baseUrl, MaxNameChars);
        }
        catch (Exception)
        {
            return ChatServerCatalog.ShortName(null, baseUrl, MaxNameChars);
        }
    }

    private static void WarnOnce(string message)
    {
        if (Interlocked.Exchange(ref _warned, 1) != 0)
        {
            return;
        }

        Entry.Warn("announcement-source: " + message);
    }

    /// <summary>
    /// Which server each announcement came from, keyed by announcement id.
    ///
    /// Upstream fetches announcements from one client bound to one server
    /// (<c>LanConnectLobbyOverlay.cs:3483-3501</c>), so today every id resolves through the fallback and the line
    /// reads "来源：{current server}" - honest, if not yet interesting. A pooled fetch that mixes servers has to
    /// register here before it hands items over; unknown ids keep the fallback, never a guessed name.
    /// </summary>
    internal static class Origins
    {
        private static readonly object Gate = new();

        private static readonly Dictionary<string, string> ById = new(StringComparer.Ordinal);

        private static long _batch;

        internal static long Batch => Interlocked.Read(ref _batch);

        internal static void BeginBatch() => Interlocked.Increment(ref _batch);

        internal static void Register(string announcementId, string serverName)
        {
            if (string.IsNullOrWhiteSpace(announcementId) || string.IsNullOrWhiteSpace(serverName))
            {
                return;
            }

            lock (Gate)
            {
                ById[announcementId.Trim()] = serverName.Trim();
            }
        }

        internal static bool TryGet(string announcementId, out string serverName)
        {
            lock (Gate)
            {
                return ById.TryGetValue(announcementId, out serverName!);
            }
        }

        internal static void Forget(string announcementId)
        {
            lock (Gate)
            {
                ById.Remove(announcementId);
            }
        }
    }

    /// <summary>
    /// Every member this repair touches, resolved once in <see cref="Applies"/> and never again. Metadata only:
    /// nothing is invoked while probing, and no Godot call happens outside a patch body.
    /// </summary>
    internal sealed class Shape
    {
        internal MethodInfo ApplyAnnouncements = null!;

        internal MethodInfo RefreshAnnouncement = null!;

        internal MethodInfo Advance = null!;

        internal FieldInfo BodyLabel = null!;

        internal FieldInfo Frame = null!;

        internal FieldInfo Announcements = null!;

        internal FieldInfo CurrentIndex = null!;

        internal PropertyInfo ItemId = null!;

        /// <summary>Optional: only the display name needs it, and a miss degrades the name, not the line.</summary>
        internal PropertyInfo? ConfigBaseUrl;

        /// <summary>Optional: theme colour chain, degrading to <see cref="FallbackDanger"/>.</summary>
        internal PropertyInfo? ThemeCurrent;

        internal PropertyInfo? ThemePalette;

        internal PropertyInfo? ThemeDanger;

        internal static Shape? TryCapture(Assembly host, List<string> problems)
        {
            Type? overlay = ReflectScope.HostType(host, OverlayTypeName, problems);
            Type? carousel = ReflectScope.HostType(host, CarouselTypeName, problems);
            if (overlay == null || carousel == null)
            {
                return null;
            }

            var shape = new Shape
            {
                RefreshAnnouncement = ReflectScope.Exact(
                    carousel,
                    RefreshAnnouncementMethodName,
                    requireStatic: false,
                    Array.Empty<string>(),
                    "System.Void",
                    problems)!,
                Advance = ReflectScope.MethodOf(carousel, AdvanceMethodName, 1, problems)!,
                BodyLabel = RequireField(problems, carousel, "_bodyLabel", "Label")!,
                Frame = RequireField(problems, carousel, "_frame", "PanelContainer")!,
                CurrentIndex = RequireField(problems, carousel, "_currentIndex", "Int32")!
            };

            FieldInfo? announcements = RequireField(problems, carousel, "_announcements", null);
            if (announcements != null)
            {
                shape.Announcements = announcements;
                Type? itemType = announcements.FieldType.IsGenericType
                    ? announcements.FieldType.GetGenericArguments()[0]
                    : null;
                if (itemType == null ||
                    !string.Equals(itemType.Name, "LobbyAnnouncementItem", StringComparison.Ordinal))
                {
                    problems.Add("_announcements is " + announcements.FieldType.FullName +
                                 ", expected List<LobbyAnnouncementItem>");
                }
                else
                {
                    shape.ItemId = ReflectScope.PropertyOf(itemType, "Id", problems)!;
                }
            }

            MethodInfo? apply = ReflectScope.MethodOf(overlay, ApplyAnnouncementsMethodName, 1, problems);
            if (apply != null)
            {
                Type parameterType = apply.GetParameters()[0].ParameterType;
                Type? element = parameterType.IsGenericType ? parameterType.GetGenericArguments()[0] : null;
                if (element == null ||
                    !string.Equals(element.Name, "LobbyAnnouncementItem", StringComparison.Ordinal))
                {
                    problems.Add(OverlayTypeName + "." + ApplyAnnouncementsMethodName +
                                 " no longer takes a list of LobbyAnnouncementItem");
                }
                else
                {
                    shape.ApplyAnnouncements = apply;
                }
            }

            CaptureOptional(shape, host);
            return shape;
        }

        /// <summary>
        /// Everything below changes only what the line says or how it is coloured. Reporting it as a missing anchor
        /// would trade a whole feature for a shade of red, so misses are collected and dropped.
        /// </summary>
        private static void CaptureOptional(Shape shape, Assembly host)
        {
            var scratch = new List<string>();

            Type? config = host.GetType(ConfigTypeName, throwOnError: false, ignoreCase: false);
            shape.ConfigBaseUrl = config == null
                ? null
                : ReflectScope.StaticProperty(scratch, config, ConfigBaseUrlPropertyName);

            Type? themes = host.GetType(ThemeTypeName, throwOnError: false, ignoreCase: false);
            PropertyInfo? current = themes == null ? null : ReflectScope.StaticProperty(scratch, themes, "Current");
            PropertyInfo? palette = current == null
                ? null
                : ReflectScope.PropertyOf(current.PropertyType, "Palette", scratch);
            PropertyInfo? danger = palette == null
                ? null
                : ReflectScope.PropertyOf(palette.PropertyType, "Danger", scratch);

            if (current != null && palette != null && danger != null)
            {
                shape.ThemeCurrent = current;
                shape.ThemePalette = palette;
                shape.ThemeDanger = danger;
            }

            // The name resolver, best effort for the same reason.
            try
            {
                ChatServerCatalog.TryCapture(host, new List<string>());
            }
            catch (Exception)
            {
                // The line falls back to the address host.
            }
        }

        private static FieldInfo? RequireField(
            List<string> problems,
            Type owner,
            string name,
            string? expectedTypeName)
        {
            FieldInfo? field = ReflectScope.Field(problems, owner, name);
            if (field == null)
            {
                return null;
            }

            if (expectedTypeName != null &&
                !string.Equals(field.FieldType.Name, expectedTypeName, StringComparison.Ordinal))
            {
                problems.Add(name + " is " + field.FieldType.FullName + ", expected " + expectedTypeName);
                return null;
            }

            return field;
        }
    }
}
