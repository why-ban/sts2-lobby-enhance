using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Sidebar chat only: a message that carries an upstream invite code shows a coloured capsule instead of the raw
/// <c>STS2INV:</c> blob, followed by an independently clickable "复制" that copies the raw code.
///
/// The work happens before the row is built. <c>BuildMessageContent</c> raises a flag, and the message view's
/// constructor postfix rewrites the span list it was handed - splitting the one text span that holds the invite into
/// prefix / capsule / "  | 复制" / suffix - and rebuilds the view's reference table for the new indices. Rewriting the
/// list before upstream's <c>_Ready</c> renders it is what keeps <c>ref-{i}</c> and <c>_references</c> in step; doing it
/// afterwards would shift every later reference.
///
/// The view's own <c>CopyText</c> is computed in the constructor from the original spans and is not touched, so the
/// whole-row copy still yields the untouched code. The capsule is plain coloured text and is deliberately not clickable
/// yet; only "复制" carries a reference and lands here, where the meta is consumed and the raw code goes to the clipboard.
/// </summary>
internal sealed class ChatInviteCardFix : ICompatFix
{
    private const string PanelTypeName = "Sts2LanConnect.Scripts.LanConnectBasicChatPanel";

    private const string BuildContentMethodName = "BuildMessageContent";

    private const string ViewTypeName = "Sts2LanConnect.Scripts.LanConnectRichMessageView";

    private const string MetaClickedMethodName = "OnMetaClicked";

    private const string SpansFieldName = "_spans";

    private const string ReferencesFieldName = "_references";

    private const string InviteCodeTypeName = "Sts2LanConnect.Scripts.LanConnectInviteCode";

    private const string TextSegmentTypeName = "Sts2LanConnect.Scripts.LanConnectTextSegment";

    private const string ControlTypeName = "Godot.Control";

    private const string VariantTypeName = "Godot.Variant";

    private const string VoidTypeName = "System.Void";

    private const string StringTypeName = "System.String";

    private const string InvitePrefix = "STS2INV:";

    private const string CopyLabel = "  ｜ 复制";

    private static readonly Color AvailableColor = new(0.55f, 0.92f, 1f);

    private static readonly Color StaleColor = new(0.74f, 0.44f, 0.44f);

    private static readonly Color UnknownColor = new(0.95f, 0.8f, 0.45f);

    private static readonly Color CopyColor = new(0.75f, 0.78f, 0.85f);

    /// <summary>Per view: the metas that mean "copy the raw code", and the code itself.</summary>
    private static readonly ConditionalWeakTable<object, CopyHook> CopyHooks = new();

    private static Shape? _shape;

    private static int _warned;

    /// <summary>Set only while the sidebar builder runs, so the HUD's flat rows are never touched.</summary>
    private static bool _sidebarBuilding;

    public string Id => "chat-invite-card";

    public FixGroup Group => FixGroup.Chat;

    public bool Applies(out string reason)
    {
        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = $"host mod '{Entry.HostModAssemblyName}' is not loaded";
            return false;
        }

        try
        {
            if (!Shape.TryCapture(host, out Shape shape, out string missing))
            {
                reason = "missing: " + missing;
                return false;
            }

            _shape = shape;

            // The join side is optional: without those anchors the capsule simply stays unclickable while 复制 keeps
            // working, so a missing join anchor must not take the whole repair down
            ChatInviteJoin.TryCapture(host, new List<string>());

            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            reason = $"missing: anchor capture threw {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    public void Apply(Harmony harmony)
    {
        Shape shape = _shape ?? throw new InvalidOperationException(
            "chat-invite-card: Apply ran without a captured shape; Applies must be consulted first.");

        harmony.Patch(
            shape.BuildContent,
            prefix: new HarmonyMethod(typeof(ChatInviteCardFix), nameof(SidebarBegin)),
            postfix: new HarmonyMethod(typeof(ChatInviteCardFix), nameof(SidebarEnd)));

        harmony.Patch(
            shape.ViewCtor,
            postfix: new HarmonyMethod(typeof(ChatInviteCardFix), nameof(ViewCtorPostfix)));

        harmony.Patch(
            shape.MetaClicked,
            prefix: new HarmonyMethod(typeof(ChatInviteCardFix), nameof(MetaClickedPrefix))
            {
                priority = Priority.First
            });

        Entry.Info(
            "chat-invite-card: patched " + PanelTypeName + "." + BuildContentMethodName + ", the " + ViewTypeName +
            " constructor and " + ViewTypeName + "." + MetaClickedMethodName + ". On the lobby sidebar a message that " +
            "carries a STS2INV code renders as a capsule plus an independent " + CopyLabel.Trim() + " that copies the raw " +
            "code; the capsule's text is read from the pool snapshot, so nothing here sends a request.");
    }

    // ReSharper disable once InconsistentNaming
    private static void SidebarBegin() => _sidebarBuilding = true;

    // ReSharper disable once InconsistentNaming
    private static void SidebarEnd() => _sidebarBuilding = false;

    /// <summary>
    /// Runs after upstream's constructor stored the span list and built the reference table, and before the view enters
    /// the tree, so the render in <c>_Ready</c> uses the rewritten list with matching indices
    /// </summary>
    // ReSharper disable once InconsistentNaming
    private static void ViewCtorPostfix(object? __instance)
    {
        try
        {
            Shape? shape = _shape;
            if (!_sidebarBuilding || shape == null || __instance is not Node view || !GodotObject.IsInstanceValid(view))
            {
                return;
            }

            if (shape.Spans.GetValue(__instance) is not System.Collections.IEnumerable spans)
            {
                return;
            }

            var original = new List<object>();
            foreach (object? span in spans)
            {
                if (span != null)
                {
                    original.Add(span);
                }
            }

            if (original.Count == 0)
            {
                return;
            }

            int hit = -1;
            string text = string.Empty;
            for (int index = 0; index < original.Count; index++)
            {
                if (shape.DisplayText.GetValue(original[index]) is string display
                    && display.Contains(InvitePrefix, StringComparison.Ordinal))
                {
                    hit = index;
                    text = display;
                    break;
                }
            }

            if (hit < 0 || !shape.TryReadInvite(text, out string code, out string server, out string room, out string? password))
            {
                return;
            }

            (string capsule, Color color) = shape.Describe(server, room);

            int at = text.IndexOf(InvitePrefix, StringComparison.Ordinal);
            int end = at + InvitePrefix.Length;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            var pieces = new List<object>();
            if (at > 0)
            {
                pieces.Add(shape.MakeText(original[hit], text[..at], text[..at], string.Empty));
            }

            object joinReference = shape.MakeReference(shape.MakeSegment(code));
            pieces.Add(shape.MakeSpan(capsule, code, capsule, color, null, joinReference));

            object reference = shape.MakeReference(shape.MakeSegment(code));
            pieces.Add(shape.MakeSpan(CopyLabel, code, CopyLabel, CopyColor, null, reference));

            if (end < text.Length)
            {
                pieces.Add(shape.MakeText(original[hit], text[end..], text[end..], string.Empty));
            }

            var rebuilt = new List<object>();
            for (int index = 0; index < original.Count; index++)
            {
                if (index == hit)
                {
                    rebuilt.AddRange(pieces);
                }
                else
                {
                    rebuilt.Add(original[index]);
                }
            }

            Array array = Array.CreateInstance(shape.SpanType, rebuilt.Count);
            var hooks = new CopyHook(code, server, room, password, capsule);
            for (int index = 0; index < rebuilt.Count; index++)
            {
                array.SetValue(rebuilt[index], index);
                object? entry = shape.Reference.GetValue(rebuilt[index]);
                if (ReferenceEquals(entry, reference))
                {
                    hooks.Metas.Add("ref-" + index);
                }
                else if (ReferenceEquals(entry, joinReference))
                {
                    hooks.JoinMetas.Add("ref-" + index);
                }
            }

            object table = Activator.CreateInstance(shape.ReferenceTableType)!;
            MethodInfo indexer = shape.ReferenceTableType.GetProperty("Item")!.GetSetMethod()!;
            for (int index = 0; index < rebuilt.Count; index++)
            {
                object? entry = shape.Reference.GetValue(rebuilt[index]);
                if (entry != null)
                {
                    indexer.Invoke(table, new[] { "ref-" + index, entry });
                }
            }

            indexer.Invoke(table, new[] { ChatInviteJoin.MetaName, joinReference });
            indexer.Invoke(table, new[] { "sts2le-copy", reference });

            shape.Spans.SetValue(__instance, array);
            shape.References.SetValue(__instance, table);
            CopyHooks.Remove(view);
            CopyHooks.Add(view, hooks);
        }
        catch (Exception exception)
        {
            WarnOnce("a chat invite capsule could not be built: " + exception.GetType().Name + ": " + exception.Message);
        }
    }

    /// <summary>Consumes the copy hot zone's meta so upstream never treats it as a reference</summary>
    // ReSharper disable once InconsistentNaming
    private static bool MetaClickedPrefix(object? __instance, Variant meta)
    {
        try
        {
            if (__instance is not Node view || !GodotObject.IsInstanceValid(view)
                || !CopyHooks.TryGetValue(view, out CopyHook? hook))
            {
                return true;
            }

            string name = meta.ToString();
            if (hook.JoinMetas.Contains(name))
            {
                return !ChatInviteJoin.Ask(view, hook.Server, hook.RoomId, hook.Password, hook.Capsule);
            }

            if (!hook.Metas.Contains(name))
            {
                return true;
            }

            DisplayServer.ClipboardSet(hook.Raw);
            return false;
        }
        catch (Exception exception)
        {
            WarnOnce("copying a chat invite failed: " + exception.GetType().Name + ": " + exception.Message);
            return true;
        }
    }

    private static void WarnOnce(string message) => Entry.WarnOnce(ref _warned, "chat-invite-card", message);

    private sealed class CopyHook
    {
        internal CopyHook(string raw, string server, string roomId, string? password, string capsule)
        {
            Raw = raw;
            Server = server;
            RoomId = roomId;
            Password = password;
            Capsule = capsule;
        }

        internal string Raw { get; }

        internal string Server { get; }

        internal string RoomId { get; }

        internal string? Password { get; }

        internal string Capsule { get; }

        internal List<string> Metas { get; } = new();

        internal List<string> JoinMetas { get; } = new();
    }

    /// <summary>Every anchor this repair needs, resolved once in <see cref="Applies"/>.</summary>
    internal sealed class Shape
    {
        private Shape(
            MethodInfo buildContent,
            ConstructorInfo viewCtor,
            FieldInfo spans,
            FieldInfo references,
            MethodInfo metaClicked,
            Type spanType,
            Type referenceType,
            Type textSegmentType,
            MethodInfo tryDecode)
        {
            BuildContent = buildContent;
            ViewCtor = viewCtor;
            Spans = spans;
            References = references;
            MetaClicked = metaClicked;
            SpanType = spanType;
            ReferenceType = referenceType;
            ReferenceTableType = references.FieldType;
            TextSegmentType = textSegmentType;
            TryDecode = tryDecode;
            DisplayText = spanType.GetProperty("DisplayText")!;
            CopyText = spanType.GetProperty("CopyText")!;
            Color = spanType.GetProperty("Color")!;
            Texture = spanType.GetProperty("Texture")!;
            Reference = spanType.GetProperty("Reference")!;
            SpanCtor = spanType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
            ReferenceCtor = referenceType.GetConstructors().First();
        }

        internal MethodInfo BuildContent { get; }

        internal ConstructorInfo ViewCtor { get; }

        internal FieldInfo Spans { get; }

        internal FieldInfo References { get; }

        internal MethodInfo MetaClicked { get; }

        internal Type SpanType { get; }

        internal Type ReferenceType { get; }

        internal Type ReferenceTableType { get; }

        internal Type TextSegmentType { get; }

        internal MethodInfo TryDecode { get; }

        internal PropertyInfo DisplayText { get; }

        internal PropertyInfo CopyText { get; }

        internal PropertyInfo Color { get; }

        internal PropertyInfo Texture { get; }

        internal PropertyInfo Reference { get; }

        internal ConstructorInfo SpanCtor { get; }

        internal ConstructorInfo ReferenceCtor { get; }

        internal static bool TryCapture(Assembly host, out Shape shape, out string missing)
        {
            shape = null!;
            var problems = new List<string>();
            Type? panel = ReflectScope.HostType(host, PanelTypeName, problems);
            Type? view = ReflectScope.HostType(host, ViewTypeName, problems);
            Type? inviteCode = ReflectScope.HostType(host, InviteCodeTypeName, problems);
            Type? textSegment = ReflectScope.HostType(host, TextSegmentTypeName, problems);

            MethodInfo? buildContent = panel?.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == BuildContentMethodName
                                     && m.GetParameters().Length == 1
                                     && m.ReturnType.FullName == ControlTypeName);

            MethodInfo? metaClicked = view?.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == MetaClickedMethodName
                                     && m.GetParameters().Length == 1
                                     && m.GetParameters()[0].ParameterType.FullName == VariantTypeName
                                     && m.ReturnType.FullName == VoidTypeName);

            FieldInfo? spans = ReflectScope.Field(problems, view, SpansFieldName);
            FieldInfo? references = ReflectScope.Field(problems, view, ReferencesFieldName);

            ConstructorInfo? ctor = view?.GetConstructors(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(c => c.GetParameters().Length == 3);

            MethodInfo? tryDecode = inviteCode?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "TryDecode"
                                     && m.GetParameters().Length == 2
                                     && m.GetParameters()[0].ParameterType.FullName == StringTypeName);

            if (buildContent == null)
            {
                problems.Add(PanelTypeName + "." + BuildContentMethodName + "(…): Godot.Control not found");
            }

            if (metaClicked == null)
            {
                problems.Add(ViewTypeName + "." + MetaClickedMethodName + "(Variant) not found");
            }

            if (ctor == null)
            {
                problems.Add(ViewTypeName + " constructor with 3 parameters not found");
            }

            if (tryDecode == null)
            {
                problems.Add(InviteCodeTypeName + ".TryDecode(string, out …) not found");
            }

            if (spans == null || spans.FieldType.GetGenericArguments().Length != 1)
            {
                problems.Add(ViewTypeName + "." + SpansFieldName + " is not a list of spans");
            }

            if (references == null || references.FieldType.GetGenericArguments().Length != 2)
            {
                problems.Add(ViewTypeName + "." + ReferencesFieldName + " is not a reference table");
            }

            if (textSegment == null)
            {
                problems.Add(TextSegmentTypeName + " not found");
            }

            if (problems.Count > 0)
            {
                missing = string.Join(", ", problems);
                return false;
            }

            shape = new Shape(
                buildContent!,
                ctor!,
                spans!,
                references!,
                metaClicked!,
                spans!.FieldType.GetGenericArguments()[0],
                references!.FieldType.GetGenericArguments()[1],
                textSegment!,
                tryDecode!);
            missing = string.Empty;
            return true;
        }

        internal bool TryReadInvite(string text, out string code, out string server, out string room, out string? password)
        {
            code = string.Empty;
            server = string.Empty;
            room = string.Empty;
            password = null;

            int at = text.IndexOf(InvitePrefix, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            int end = at + InvitePrefix.Length;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            code = text[at..end];
            object?[] arguments = { code, null };
            if (TryDecode.Invoke(null, arguments) is not true || arguments[1] is not object payload)
            {
                return false;
            }

            Type payloadType = payload.GetType();
            server = payloadType.GetProperty("S")?.GetValue(payload) as string ?? string.Empty;
            room = payloadType.GetProperty("R")?.GetValue(payload) as string ?? string.Empty;
            password = payloadType.GetProperty("P")?.GetValue(payload) as string;
            return server.Length > 0 && room.Length > 0;
        }

        internal (string Text, Color Color) Describe(string server, string roomId)
        {
            PoolAnchors? anchors = PoolRefreshFix.LiveAnchors;
            if (anchors == null)
            {
                return ("? 暂时无法确认 · 点击重试", UnknownColor);
            }

            try
            {
                bool serverSeen = false;
                string wanted = PoolAddress.ToBaseUrl(server);
                foreach (object candidate in PoolRoomStore.Instance.Rooms)
                {
                    PoolOrigin? origin = PoolRoomStore.Instance.OriginOf(anchors, candidate);
                    if (origin == null
                        || !string.Equals(PoolAddress.ToBaseUrl(origin.BaseUrl), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    serverSeen = true;
                    if (!string.Equals(PoolRoomClient.RoomIdOf(anchors, candidate), roomId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string name = Field(candidate, "RoomName") ?? "房间";
                    string mode = Field(candidate, "GameMode") ?? "STD";
                    int players = Int(candidate, "CurrentPlayers");
                    int max = Int(candidate, "MaxPlayers");
                    return ($"▶ {name} · {mode} · {players}/{max}", AvailableColor);
                }

                return serverSeen
                    ? ("✕ 过时的房间（无法加入）", StaleColor)
                    : ("? 暂时无法确认 · 点击重试", UnknownColor);
            }
            catch (Exception)
            {
                return ("? 暂时无法确认 · 点击重试", UnknownColor);
            }
        }

        internal object MakeText(object source, string display, string copy, string accessible)
        {
            object? texture = Texture.GetValue(source);
            object? reference = null;
            _ = reference;
            return Activator.CreateInstance(
                SpanType,
                display,
                copy,
                accessible,
                Color.GetValue(source),
                texture,
                null)!;
        }

        internal object MakeSpan(string display, string copy, string accessible, Color color, Texture2D? texture, object? reference) =>
            Activator.CreateInstance(SpanType, display, copy, accessible, color, texture, reference)!;

        internal object MakeSegment(string text) => Activator.CreateInstance(TextSegmentType, text)!;

        internal object MakeReference(object segment) => ReferenceCtor.Invoke(new object?[] { segment, null, null });

        private static string? Field(object room, string name) =>
            room.GetType().GetProperty(name)?.GetValue(room) as string;

        private static int Int(object room, string name) =>
            room.GetType().GetProperty(name)?.GetValue(room) is int value ? value : 0;
    }
}
