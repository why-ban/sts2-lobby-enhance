using System.Reflection;
using Godot;

namespace Sts2LobbyEnhance;

/// <summary>
/// The click side of an invite capsule: asks once, then joins through upstream's own steps - switch the lobby server,
/// read that server's rooms, find the room, hand it to the overlay's join. Nothing here writes upstream's
/// <c>_actionInFlight</c> or its pending clipboard invite payload, and the server-context lease is disposed on every
/// path that does not hand it to the join.
/// </summary>
internal static class ChatInviteJoin
{
    private const string ConfigTypeName = "Sts2LanConnect.Scripts.LanConnectConfig";

    private const string RuntimeTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyRuntime";

    private const string ApiClientTypeName = "Sts2LanConnect.Scripts.LobbyApiClient";

    private const string PopupSimpleName = "LanConnectPopupUtil";

    private const string AddressSimpleName = "LanConnectLobbyServerAddress";

    private const string RoomIdPropertyName = "RoomId";

    private const string BeginJoinMethodName = "BeginJoinRoomAsync";

    private const string JoinMeta = "sts2le-join";

    private static Shape? _shape;

    private static int _warned;

    private static bool _busy;

    internal static string MetaName => JoinMeta;

    /// <summary>False when the join anchors could not be resolved; the capsule then stays plain text.</summary>
    internal static bool Available => _shape != null;

    /// <summary>Resolved once by the caller's <see cref="ChatInviteCardFix.Applies"/>; nothing here touches Godot.</summary>
    internal static bool TryCapture(Assembly host, List<string> problems)
    {
        Type? overlay = ReflectScope.HostType(host, PoolAnchors.OverlayTypeName, problems);
        Type? config = ReflectScope.HostType(host, ConfigTypeName, problems);
        Type? runtime = ReflectScope.HostType(host, RuntimeTypeName, problems);
        Type? api = ReflectScope.HostType(host, ApiClientTypeName, problems);
        Type? popup = BySimpleName(host, PopupSimpleName);
        Type? address = BySimpleName(host, AddressSimpleName);

        if (popup == null)
        {
            problems.Add(PopupSimpleName + " (by simple name)");
        }

        if (address == null)
        {
            problems.Add(AddressSimpleName + " (by simple name)");
        }

        MethodInfo? beginJoin = overlay?.GetMethods(AnyMember)
            .FirstOrDefault(method => method.Name == BeginJoinMethodName
                                      && method.GetParameters().Length == 5
                                      && typeof(Task).IsAssignableFrom(method.ReturnType));

        PropertyInfo? baseUrl = config?.GetProperty("LobbyServerBaseUrl", StaticAny)
                               ?? config?.GetProperty("LobbyServerBaseUrl", InstanceAny);

        PropertyInfo? instance = runtime?.GetProperty("Instance", StaticAny)
                                 ?? runtime?.GetProperty("Instance", InstanceAny);

        MethodInfo? switchServer = runtime?.GetMethods(AnyMember)
            .FirstOrDefault(method => method.Name == "SwitchLobbyServerWithContextAsync"
                                      && method.GetParameters().Length == 2
                                      && typeof(Task).IsAssignableFrom(method.ReturnType));

        MethodInfo? acquire = runtime?.GetMethods(AnyMember)
            .FirstOrDefault(method => method.Name == "AcquireCurrentServerContext"
                                      && method.GetParameters().Length == 0);

        MethodInfo? createClient = api?.GetMethods(AnyMember)
            .FirstOrDefault(method => method.Name == "CreateConfigured"
                                      && method.GetParameters().Length == 0);

        MethodInfo? getRooms = api?.GetMethods(AnyMember)
            .FirstOrDefault(method => method.Name == "GetRoomsAsync"
                                      && method.GetParameters().Length == 1
                                      && typeof(Task).IsAssignableFrom(method.ReturnType));

        MethodInfo? showInfo = popup?.GetMethods(AnyMember)
            .FirstOrDefault(method => method.Name == "ShowInfo"
                                      && method.GetParameters().Length == 1
                                      && method.GetParameters()[0].ParameterType == typeof(string));

        MethodInfo? normalize = address?.GetMethods(AnyMember)
            .FirstOrDefault(method => method.Name == "NormalizeAuthority"
                                      && method.GetParameters().Length == 2
                                      && method.ReturnType == typeof(string));

        if (beginJoin == null)
        {
            problems.Add(PoolAnchors.OverlayTypeName + "." + BeginJoinMethodName + "/5");
        }

        if (baseUrl == null)
        {
            problems.Add(ConfigTypeName + ".LobbyServerBaseUrl");
        }

        if (instance == null)
        {
            problems.Add(RuntimeTypeName + ".Instance");
        }

        if (switchServer == null)
        {
            problems.Add(RuntimeTypeName + ".SwitchLobbyServerWithContextAsync/2");
        }

        if (acquire == null)
        {
            problems.Add(RuntimeTypeName + ".AcquireCurrentServerContext/0");
        }

        if (createClient == null)
        {
            problems.Add(ApiClientTypeName + ".CreateConfigured/0");
        }

        if (getRooms == null)
        {
            problems.Add(ApiClientTypeName + ".GetRoomsAsync/1");
        }

        if (showInfo == null)
        {
            problems.Add(PopupSimpleName + ".ShowInfo/1");
        }

        if (normalize == null)
        {
            problems.Add(AddressSimpleName + ".NormalizeAuthority/2");
        }

        if (problems.Count > 0)
        {
            return false;
        }

        _shape = new Shape(
            beginJoin!,
            baseUrl!,
            instance!,
            switchServer!,
            acquire!,
            createClient!,
            getRooms!,
            showInfo!,
            normalize!);
        return true;
    }

    /// <summary>
    /// One click on a capsule: asks first, and only the confirmation starts the join. Everything the join needs is read
    /// from the capsule's own record, so no global state is involved.
    /// </summary>
    internal static bool Ask(Node view, string server, string roomId, string? password, string roomText)
    {
        try
        {
            if (_shape == null || view is not Node node || !GodotObject.IsInstanceValid(node) || _busy)
            {
                return false;
            }

            Node? overlay = FindOverlay(node);
            if (overlay == null)
            {
                WarnOnce("no lobby overlay was found, so the invite cannot be joined from here.");
                return false;
            }

            var dialog = new ConfirmationDialog
            {
                Title = "加入房间",
                DialogText = "加入这个房间吗？\n\n" + roomText + ServerLine(server) +
                             (string.IsNullOrWhiteSpace(password) ? string.Empty : "\n邀请码里已经包含密码，会自动使用。"),
                OkButtonText = "加入",
                CancelButtonText = "取消",
                Exclusive = true,
                MinSize = new Vector2I(760, 420)
            };

            // The dialog keeps the engine's default type sizes, which read as tiny inside a 760x420 box, so the body and
            // both buttons are scaled to the window that was just enlarged
            dialog.GetLabel().AddThemeFontSizeOverride("font_size", 26);
            dialog.GetOkButton().AddThemeFontSizeOverride("font_size", 24);
            dialog.GetCancelButton()?.AddThemeFontSizeOverride("font_size", 24);

            dialog.Confirmed += () =>
            {
                dialog.QueueFree();
                _ = JoinAsync(overlay, server, roomId, password);
            };
            dialog.Canceled += () => dialog.QueueFree();

            overlay.AddChild(dialog);
            dialog.PopupCentered();
            return true;
        }
        catch (Exception exception)
        {
            WarnOnce("showing the invite confirmation failed: " + exception.GetType().Name + ": " + exception.Message);
            return false;
        }
    }

    private static string ServerLine(string server)
    {
        Shape shape = _shape!;
        try
        {
            string current = shape.BaseUrl.GetValue(null) as string ?? string.Empty;
            string invited = Normalize(shape, server);
            string here = Normalize(shape, current);
            return string.Equals(invited, here, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : "\n\n该邀请指向另一台服务器（" + server + "），确认后会先把大厅切到那台。";
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static async Task JoinAsync(Node overlay, string server, string roomId, string? password)
    {
        if (_busy)
        {
            return;
        }

        Shape shape = _shape!;
        object? lease = null;
        object? client = null;
        _busy = true;
        try
        {
            object? runtime = shape.Instance.GetValue(null);
            if (runtime == null)
            {
                shape.ShowInfo.Invoke(null, new object?[] { "加入失败：当前没有进行中的大厅。" });
                return;
            }

            string current = shape.BaseUrl.GetValue(null) as string ?? string.Empty;
            string invited = Normalize(shape, server);
            string here = Normalize(shape, current);

            if (string.Equals(invited, here, StringComparison.OrdinalIgnoreCase))
            {
                lease = shape.Acquire.Invoke(runtime, null);
            }
            else
            {
                object? switched = shape.Switch.Invoke(runtime, new object?[] { server, CancellationToken.None });
                lease = switched is Task task ? await AwaitObject(task).ConfigureAwait(true) : switched;
            }

            if (lease == null)
            {
                shape.ShowInfo.Invoke(null, new object?[] { "加入失败：切换到该服务器时没有得到会话。" });
                return;
            }

            client = shape.CreateClient.Invoke(null, null);
            if (client == null)
            {
                shape.ShowInfo.Invoke(null, new object?[] { "加入失败：无法创建大厅客户端。" });
                return;
            }

            object? roomsCall = shape.GetRooms.Invoke(client, new object?[] { CancellationToken.None });
            if (roomsCall is not Task roomsTask)
            {
                shape.ShowInfo.Invoke(null, new object?[] { "加入失败：读取房间列表没有返回结果。" });
                return;
            }

            if (await AwaitObject(roomsTask).ConfigureAwait(true) is not System.Collections.IEnumerable rooms)
            {
                shape.ShowInfo.Invoke(null, new object?[] { "加入失败：读取房间列表失败。" });
                return;
            }

            object? target = null;
            foreach (object? room in rooms)
            {
                if (room != null && string.Equals(RoomIdOf(room), roomId, StringComparison.Ordinal))
                {
                    target = room;
                    break;
                }
            }

            if (target == null)
            {
                shape.ShowInfo.Invoke(null, new object?[] { "邀请码对应的房间不存在或已关闭，可能房主已经关掉了它。" });
                return;
            }

            string expected = Normalize(shape, shape.BaseUrl.GetValue(null) as string ?? string.Empty);
            object? joinCall = shape.BeginJoin.Invoke(overlay, new object?[] { target, password, CancellationToken.None, lease, expected });
            lease = null;
            if (joinCall is Task joinTask)
            {
                await joinTask.ConfigureAwait(true);
            }
        }
        catch (Exception exception)
        {
            WarnOnce("joining an invited room failed: " + exception.GetType().Name + ": " + exception.Message);
            try
            {
                _shape?.ShowInfo.Invoke(null, new object?[] { "通过邀请码加入失败：" + ExceptionText(exception) });
            }
            catch (Exception)
            {
                // the popup is a courtesy; the warning above already recorded the failure
            }
        }
        finally
        {
            (lease as IDisposable)?.Dispose();
            (client as IDisposable)?.Dispose();
            _busy = false;
        }
    }

    private static async Task<object?> AwaitObject(Task task)
    {
        await task.ConfigureAwait(true);
        PropertyInfo? result = task.GetType().GetProperty("Result");
        return result?.GetValue(task);
    }

    private static string Normalize(Shape shape, string value)
    {
        try
        {
            return shape.Normalize.Invoke(null, new object?[] { value, "server" }) as string ?? value;
        }
        catch (Exception)
        {
            return value;
        }
    }

    private static string RoomIdOf(object room) =>
        room.GetType().GetProperty(RoomIdPropertyName)?.GetValue(room) as string ?? string.Empty;

    private static string ExceptionText(Exception exception) =>
        exception is TargetInvocationException { InnerException: { } inner } ? inner.Message : exception.Message;

    /// <summary>The overlay that owns this chat row, by walking up and then by looking for the live instance.</summary>
    private static Node? FindOverlay(Node node)
    {
        for (Node? parent = node; parent != null; parent = parent.GetParent())
        {
            if (parent.GetType().FullName == PoolAnchors.OverlayTypeName)
            {
                return parent;
            }
        }

        foreach (Node candidate in node.GetTree()?.Root?.FindChildren("*", "Control", true, false) ?? new Godot.Collections.Array<Node>())
        {
            if (candidate.GetType().FullName == PoolAnchors.OverlayTypeName)
            {
                return candidate;
            }
        }

        return null;
    }

    private static Type? BySimpleName(Assembly host, string name)
    {
        try
        {
            return host.GetTypes().FirstOrDefault(type => type.Name == name);
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.FirstOrDefault(type => type != null && type.Name == name);
        }
    }

    private static void WarnOnce(string message) => Entry.WarnOnce(ref _warned, "chat-invite-card", message);

    private const BindingFlags AnyMember =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private const BindingFlags StaticAny = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private const BindingFlags InstanceAny = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private sealed class Shape
    {
        internal Shape(
            MethodInfo beginJoin,
            PropertyInfo baseUrl,
            PropertyInfo instance,
            MethodInfo switchServer,
            MethodInfo acquire,
            MethodInfo createClient,
            MethodInfo getRooms,
            MethodInfo showInfo,
            MethodInfo normalize)
        {
            BeginJoin = beginJoin;
            BaseUrl = baseUrl;
            Instance = instance;
            Switch = switchServer;
            Acquire = acquire;
            CreateClient = createClient;
            GetRooms = getRooms;
            ShowInfo = showInfo;
            Normalize = normalize;
        }

        internal MethodInfo BeginJoin { get; }

        internal PropertyInfo BaseUrl { get; }

        internal PropertyInfo Instance { get; }

        internal MethodInfo Switch { get; }

        internal MethodInfo Acquire { get; }

        internal MethodInfo CreateClient { get; }

        internal MethodInfo GetRooms { get; }

        internal MethodInfo ShowInfo { get; }

        internal MethodInfo Normalize { get; }
    }
}
