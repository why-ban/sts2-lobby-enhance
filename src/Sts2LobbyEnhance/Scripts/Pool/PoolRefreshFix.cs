using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Turns the room list into a pool: every known server's rooms, on one stage, with the owning server printed on
/// each card.
///
/// RefreshRoomsAsync is taken over completely (LanConnectLobbyOverlay.cs:3362-3481). It is not wrapped and the
/// original body never runs: the overlay's own refresh republishes the *current* server's room list, and two
/// writers on _rooms means the stage flips between the pool and one server on every eight second tick. So the
/// prefix answers false, hands back a task of its own, and reproduces the method's contract section by section:
///
/// <list type="bullet">
/// <item>the three guards and their false return (:3367-3371)</item>
/// <item>PersistSettings, the in-flight flag, the auto-refresh timer (:3373-3376)</item>
/// <item>the status line and the action buttons (:3377-3382)</item>
/// <item>the current server's /probe and /announcements, which the pool does not replace (:3388-3407)</item>
/// <item>the room list itself, which is where the pool goes (:3391-3399)</item>
/// <item>selection repair and the "did the stage change" decision (:3409-3430)</item>
/// <item>the lobby-error and generic-error status texts (:3441-3474)</item>
/// <item>the finally that clears the flag and re-enables the buttons (:3475-3480)</item>
/// </list>
///
/// Two other patches: _Process (:725) is the repaint pump, the one main-thread tick that can touch the scene tree
/// while the fan-out is streaming in; BuildRoomMetaRow (:3943) appends the source badge.
///
/// Nothing in Applies or Apply touches Godot, the config, the network or the engine: this repair is installed from
/// a headless harness that only knows reflection.
/// </summary>
internal sealed class PoolRefreshFix : ICompatFix
{
    internal const string FixId = "server-pool-refresh";

    private const string Tag = "[server-pool] ";

    /// <summary>
    /// Ceiling for the current server's /probe and /announcements. Upstream leaves them on the client's 10 s
    /// HttpClient timeout, which would let one dead server hold the status line; the failure path is the same.
    /// </summary>
    private const int CurrentServerQueryBudgetMs = 3000;

    private static PoolAnchors? _anchors;

    /// <summary>
    /// Warnings already printed, keyed by their call signature. This used to be a single global flag, which made the
    /// whole group go silent for the rest of the process after its first warning - every other repair in the group
    /// shares this one exit.
    /// </summary>
    private static readonly object WarnGate = new();

    private static readonly HashSet<string> WarnedSignatures = new(StringComparer.Ordinal);

    private const int MaxWarnedSignatures = 32;

    /// <summary>The round currently being painted. Read by the _Process postfix, set and cleared on the main thread.</summary>
    internal static PoolRefreshSession? ActiveSession;

    public string Id => FixId;

    public FixGroup Group => FixGroup.Pool;

    public bool Applies(out string reason)
    {
        _anchors = null;
        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = "host mod '" + Entry.HostModAssemblyName + "' is not loaded";
            return false;
        }

        try
        {
            PoolAnchors? anchors = PoolAnchors.TryCapture(host, PoolAnchorScope.Refresh, out reason);
            if (anchors == null)
            {
                return false;
            }

            _anchors = anchors;
            reason = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            reason = "missing: anchor capture threw " + exception.GetType().Name + ": " + exception.Message;
            return false;
        }
    }

    public void Apply(Harmony harmony)
    {
        PoolAnchors anchors = _anchors
            ?? throw new InvalidOperationException(
                "server-pool: Apply ran without a captured anchor set; Applies must be consulted first.");

        harmony.Patch(
            anchors.RefreshRooms,
            prefix: new HarmonyMethod(typeof(PoolRefreshFix), nameof(RefreshRoomsPrefix))
            {
                priority = Priority.First
            });

        harmony.Patch(
            anchors.Process,
            postfix: new HarmonyMethod(typeof(PoolRefreshFix), nameof(ProcessPostfix))
            {
                priority = Priority.Last
            });

        harmony.Patch(
            anchors.BuildRoomMetaRow,
            postfix: new HarmonyMethod(typeof(PoolRefreshFix), nameof(BuildRoomMetaRowPostfix))
            {
                priority = Priority.Last
            });

        Entry.Info(
            Tag + "the room list is now a pool over every known server, and RefreshRoomsAsync is taken over " +
            "completely so the host mod can no longer republish its single server list; patched " +
            PoolAnchors.OverlayTypeName + ".RefreshRoomsAsync (LanConnectLobbyOverlay.cs:3362), ._Process (:725) and " +
            ".BuildRoomMetaRow (:3943). Cross-server join and create-room server choice are separate repairs.");
    }

    /// <summary>
    /// Answers false and supplies the whole refresh. Every failure path still answers false with a completed task:
    /// letting the original run would republish one server's rooms and reintroduce exactly the flip this repair
    /// exists to remove.
    /// </summary>
    // ReSharper disable once InconsistentNaming - Harmony binds these by their exact names.
    public static bool RefreshRoomsPrefix(object? __instance, object?[]? __args, ref Task<bool> __result)
    {
        try
        {
            PoolAnchors? anchors = _anchors;
            if (anchors == null || __instance == null || __args is not { Length: >= 3 })
            {
                return true;
            }

            bool userInitiated = __args[0] is bool initiated && initiated;
            bool allowActionInFlight = __args[1] is bool allow && allow;
            CancellationToken cancellationToken = __args[2] is CancellationToken token ? token : CancellationToken.None;
            __result = RefreshAsync(anchors, __instance, userInitiated, allowActionInFlight, cancellationToken);
            return false;
        }
        catch (Exception exception)
        {
            WarnOnce("refresh", "the pooled refresh could not start: " + Describe(exception));
            __result = Task.FromResult(false);
            return false;
        }
    }

    /// <summary>
    /// The body of LanConnectLobbyOverlay.RefreshRoomsAsync (:3362-3481) with the pool as the room source.
    /// Section comments carry the upstream line each block reproduces, so a diff against upstream stays possible.
    /// </summary>
    private static async Task<bool> RefreshAsync(
        PoolAnchors anchors,
        object overlay,
        bool userInitiated,
        bool allowActionInFlight,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // :3368-3371 - the same three short circuits, with the same false.
        if (ReadBool(anchors.RefreshInFlight!, overlay)
            || ReadBool(anchors.ServerPickerOpen, overlay)
            || (ReadBool(anchors.ActionInFlight, overlay) && !allowActionInFlight))
        {
            return false;
        }

        anchors.PersistSettings.Invoke(overlay, null);                                   // :3373
        anchors.RefreshInFlight!.SetValue(overlay, true);                                // :3374
        anchors.TimeUntilAutoRefresh.SetValue(overlay, anchors.RefreshIntervalSeconds);   // :3376
        if (userInitiated || RoomCount(anchors, overlay) == 0)                           // :3377
        {
            SetStatus(overlay, "正在刷新大厅列表...");                                     // :3379
        }

        anchors.UpdateActionButtons!.Invoke(overlay, null);                              // :3382

        PoolRefreshSession? session = null;
        object? apiClient = null;
        try
        {
            IReadOnlyList<object> previousRooms = Snapshot(anchors, overlay);            // :3386
            string? selectedRoomId = CurrentSelection(anchors, overlay);                 // :3387

            // :3388-3390 - the current server's probe and announcements. The pool does not replace these: the
            // announcements belong to the server the player is on, and AnnouncementSourceFix rides on the same call.
            apiClient = TryCreateConfiguredClient(anchors);
            Task<double?> probeTask = MeasureRttAsync(anchors, apiClient, cancellationToken);
            Task<object?> announcementTask = FetchAnnouncementsAsync(anchors, apiClient, cancellationToken);

            // :3391-3393 - the room source, which is the pool instead of one server's list.
            session = new PoolRefreshSession(anchors, overlay, cancellationToken);
            ActiveSession = session;
            session.Start();

            double? measuredProbeRtt = await probeTask.ConfigureAwait(true);
            object? announcements = await announcementTask.ConfigureAwait(true);
            await session.Fetch.ConfigureAwait(true);

            anchors.LastLobbyRttMs.SetValue(overlay, measuredProbeRtt ?? -1d);            // :3396
            anchors.ConsecutiveRefreshFailures.SetValue(overlay, 0);                     // :3397

            if (announcements != null)                                                   // :3400-3407
            {
                anchors.ApplyAnnouncements.Invoke(overlay, new[] { announcements });
            }
            else
            {
                anchors.EnsureAnnouncementFallback.Invoke(overlay, null);
            }

            IReadOnlyList<object> rooms = PoolRoomStore.Instance.Rooms;
            WriteRooms(anchors, overlay, rooms);                                         // :3398-3399

            string? nextSelection = ApplySelection(anchors, overlay, rooms, selectedRoomId); // :3409-3416

            // :3418-3420 exactly: nothing here asks whether the pump painted, so "unchanged means no rebuild" can
            // actually hold. The pump is responsible for what it painted; the one case it cannot cover - an answer that
            // arrived after its last frame - is settled below.
            bool roomStageChanged = userInitiated
                                    || !AreVisuallyEquivalent(anchors, previousRooms, rooms)
                                    || !string.Equals(selectedRoomId, nextSelection, StringComparison.Ordinal);
            if (roomStageChanged)
            {
                anchors.RebuildRoomStage.Invoke(overlay, null);                          // :3423
            }
            else
            {
                anchors.UpdateHealthIndicator.Invoke(overlay, null);                     // :3427
                anchors.UpdatePageControls.Invoke(overlay, new object[] { FilteredCount(anchors, overlay) }); // :3428
                anchors.RefreshSelectedRoomDetails.Invoke(overlay, null);                // :3429
            }

            // The pump paints on a coalescing window; if a server answered inside the last one, the stage is showing
            // the list as of the previous paint. This draws the final one, and does nothing at all when it already is.
            session.Settle();

            // The pool's failure mode is per server, so upstream's catch blocks cannot fire for it. "Every server
            // was silent" is still a failed refresh, and it is reported with upstream's own wording.
            if (session.Cycle.Succeeded == 0 && session.Cycle.Failed > 0)
            {
                WarnOnce("all-silent", "no lobby server answered: " + session.Cycle.Describe());
                MarkRefreshFailed(anchors, overlay);
                SetStatus(overlay, "大厅刷新失败，已保留上次成功列表：所有大厅服务器均未响应\n" + FailureHint(anchors));
                return false;
            }

            SetStatus(overlay, string.Empty);                                            // :3434
            return true;                                                                 // :3435
        }
        catch (OperationCanceledException)                                               // :3437-3440
        {
            throw;
        }
        catch (Exception exception) when (anchors.LobbyServiceExceptionType.IsInstanceOfType(exception))
        {
            MarkRefreshFailed(anchors, overlay);                                         // :3443-3447
            string message = ExceptionMessage(exception);
            if (userInitiated || RoomCount(anchors, overlay) == 0)                       // :3448-3451
            {
                SetStatus(overlay, "大厅服务不可用：" + message);
            }
            else
            {
                SetStatus(overlay, "大厅刷新失败，已保留上次成功列表：" + message + "\n" + FailureHint(anchors)); // :3454
            }

            return false;
        }
        catch (Exception exception)                                                      // :3458-3474
        {
            MarkRefreshFailed(anchors, overlay);
            string message = ExceptionMessage(exception);
            if (userInitiated || RoomCount(anchors, overlay) == 0)
            {
                SetStatus(overlay, "刷新大厅失败：" + message);
            }
            else
            {
                SetStatus(overlay, "大厅刷新失败，已保留上次成功列表：" + message + "\n" + FailureHint(anchors));
            }

            return false;
        }
        finally
        {
            anchors.RefreshInFlight!.SetValue(overlay, false);                           // :3477
            anchors.UpdateActionButtons!.Invoke(overlay, null);                          // :3479
            session?.Finish();
            (apiClient as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Repaint pump. Godot only calls this on the main thread, which is why the incremental scene-tree write lives
    /// here and not in the fan-out's continuations.
    /// </summary>
    public static void ProcessPostfix(object? __instance)
    {
        try
        {
            PoolRefreshSession? session = ActiveSession;
            if (session == null || __instance == null || !ReferenceEquals(session.Overlay, __instance))
            {
                return;
            }

            if (!session.TryBeginPaint())
            {
                return;
            }

            session.PublishPartial();
        }
        catch (Exception exception)
        {
            WarnOnce("pump", "a partly filled pool could not be drawn: " + Describe(exception));
        }
    }

    /// <summary>
    /// Appends the owning server to the card's meta row. The row is an HFlowContainer (LanConnectLobbyOverlay.cs:3945)
    /// so a long server name wraps instead of squeezing the player count.
    /// </summary>
    public static void BuildRoomMetaRowPostfix(object? __result, object? room)
    {
        try
        {
            PoolAnchors? anchors = _anchors;
            if (anchors == null || room == null || __result is not Control row)
            {
                return;
            }

            PoolOrigin? origin = PoolRoomStore.Instance.OriginOf(room);
            if (origin == null)
            {
                return;
            }

            foreach (Node child in row.GetChildren())
            {
                if (child.HasMeta(PoolText.SourceMetaKey))
                {
                    return;
                }
            }

            row.AddChild(CreateSourceLabel(anchors, origin));
        }
        catch (Exception exception)
        {
            WarnOnce("source-label", "a room card was drawn without its server name: " + Describe(exception));
        }
    }

    internal static void WarnOnce(string signature, string message)
    {
        lock (WarnGate)
        {
            if (!WarnedSignatures.Add(signature))
            {
                return;
            }

            if (WarnedSignatures.Count > MaxWarnedSignatures)
            {
                WarnedSignatures.Clear();
                WarnedSignatures.Add(signature);
            }
        }

        Entry.Warn(Tag + signature + ": " + message);
    }

    /// <summary>Sets the overlay's status line. Cosmetic, so a host-mod rename costs the line and not the repair.</summary>
    internal static void SetStatus(object overlay, string message)
    {
        try
        {
            _anchors?.SetStatus?.Invoke(overlay, new object?[] { message });
        }
        catch (Exception exception)
        {
            WarnOnce("status", "the status line could not be updated: " + PoolRoomClient.Describe(exception));
        }
    }

    private static void MarkRefreshFailed(PoolAnchors anchors, object overlay)
    {
        int failures = ReadInt(anchors.ConsecutiveRefreshFailures, overlay);
        anchors.ConsecutiveRefreshFailures.SetValue(overlay, failures + 1);
        anchors.LastLobbyRttMs.SetValue(overlay, -1d);
        anchors.EnsureAnnouncementFallback.Invoke(overlay, null);
        anchors.UpdateHealthIndicator.Invoke(overlay, null);
        WarnOnce("refresh-failed", "a pooled refresh failed; the previous room list is kept");
    }

    /// <summary>
    /// LobbyApiClient.CreateConfigured for the current server. A missing or unusable lobby base url throws inside the
    /// host mod, and that must cost the latency indicator and the announcements, not the whole refresh: the pool does
    /// not need this client and still runs without it.
    /// </summary>
    private static object? TryCreateConfiguredClient(PoolAnchors anchors)
    {
        try
        {
            return anchors.CreateConfiguredClient.Invoke(null, null);
        }
        catch (Exception exception)
        {
            WarnOnce("client", "the current server's client could not be built: " + Describe(exception));
            return null;
        }
    }

    /// <summary>
    /// LobbyApiClient.MeasureProbeRttAsync behind the same budget the pool uses. Null means "no measurement", which
    /// upstream writes as -1 into the latency indicator.
    /// </summary>
    private static async Task<double?> MeasureRttAsync(PoolAnchors anchors, object? client, CancellationToken cancellationToken)
    {
        if (client == null)
        {
            return null;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(CurrentServerQueryBudgetMs);
        try
        {
            object? invocation = anchors.MeasureProbeRtt.Invoke(client, new object?[] { budget.Token });
            if (invocation is not Task task)
            {
                return null;
            }

            await task.ConfigureAwait(true);
            return task.GetType().GetProperty("Result")?.GetValue(task) as double?;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            WarnOnce("probe", "the current server's /probe failed: " + Describe(exception));
            return null;
        }
    }

    /// <summary>LobbyApiClient.GetAnnouncementsAsync, with the null result standing for "use the fallback".</summary>
    private static async Task<object?> FetchAnnouncementsAsync(
        PoolAnchors anchors,
        object? client,
        CancellationToken cancellationToken)
    {
        if (client == null)
        {
            return null;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(CurrentServerQueryBudgetMs);
        try
        {
            object? invocation = anchors.GetAnnouncements.Invoke(client, new object?[] { budget.Token });
            if (invocation is not Task task)
            {
                return null;
            }

            await task.ConfigureAwait(true);
            return task.GetType().GetProperty("Result")?.GetValue(task);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            WarnOnce("announcements", "the current server's announcements could not be read: " + Describe(exception));
            return null;
        }
    }

    /// <summary>:3398-3399 - _rooms.Clear() then AddRange(pool), through the non-generic list interface.</summary>
    private static void WriteRooms(PoolAnchors anchors, object overlay, IReadOnlyList<object> rooms)
    {
        if (anchors.Rooms.GetValue(overlay) is not IList target)
        {
            return;
        }

        target.Clear();
        foreach (object room in rooms)
        {
            target.Add(room);
        }
    }

    /// <summary>:3409-3416 - keep the selection while it is still in the list, otherwise fall back to the first room.</summary>
    private static string? ApplySelection(PoolAnchors anchors, object overlay, IReadOnlyList<object> rooms, string? selectedRoomId)
    {
        string? next = null;
        if (!string.IsNullOrWhiteSpace(selectedRoomId) && Contains(anchors, rooms, selectedRoomId))
        {
            next = selectedRoomId;
        }
        else if (rooms.Count > 0)
        {
            next = PoolRoomClient.RoomIdOf(anchors, rooms[0]);
        }

        try
        {
            anchors.SelectedRoomId?.SetValue(overlay, next);
        }
        catch (Exception)
        {
            // The stage rebuild clamps the selection anyway (:3618-3621).
        }

        return next;
    }

    /// <summary>
    /// :3419 - AreRoomListsVisuallyEquivalent takes IReadOnlyList of LobbyRoomSummary, so both sides are rebuilt into
    /// that exact type. Matched through reflection because the element type only exists at runtime.
    /// </summary>
    private static bool AreVisuallyEquivalent(PoolAnchors anchors, IReadOnlyList<object> previous, IReadOnlyList<object> current)
    {
        object? previousList = TypedRoomList(anchors, previous);
        object? currentList = TypedRoomList(anchors, current);
        if (previousList == null || currentList == null)
        {
            return false;
        }

        return anchors.AreRoomListsVisuallyEquivalent.Invoke(null, new[] { previousList, currentList }) is true;
    }

    private static object? TypedRoomList(PoolAnchors anchors, IReadOnlyList<object> rooms)
    {
        if (Activator.CreateInstance(typeof(List<>).MakeGenericType(anchors.RoomType)) is not IList list)
        {
            return null;
        }

        foreach (object room in rooms)
        {
            list.Add(room);
        }

        return list;
    }

    private static IReadOnlyList<object> Snapshot(PoolAnchors anchors, object overlay)
    {
        var rooms = new List<object>();
        if (anchors.Rooms.GetValue(overlay) is IList current)
        {
            foreach (object? room in current)
            {
                if (room != null)
                {
                    rooms.Add(room);
                }
            }
        }

        return rooms;
    }

    private static int RoomCount(PoolAnchors anchors, object overlay) =>
        anchors.Rooms.GetValue(overlay) is ICollection rooms ? rooms.Count : 0;

    private static int FilteredCount(PoolAnchors anchors, object overlay)
    {
        try
        {
            return anchors.GetFilteredRooms.Invoke(overlay, null) is ICollection filtered ? filtered.Count : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string? CurrentSelection(PoolAnchors anchors, object overlay)
    {
        try
        {
            if (anchors.GetSelectedRoom.Invoke(overlay, null) is { } room)
            {
                return PoolRoomClient.RoomIdOf(anchors, room);
            }
        }
        catch (Exception)
        {
            // Falls through to the raw field, which is what upstream falls back to as well (:3387).
        }

        return anchors.SelectedRoomId?.GetValue(overlay) as string;
    }

    private static bool Contains(PoolAnchors anchors, IReadOnlyList<object> rooms, string roomId)
    {
        foreach (object room in rooms)
        {
            if (string.Equals(PoolRoomClient.RoomIdOf(anchors, room), roomId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ReadBool(FieldInfo field, object overlay) => field.GetValue(overlay) is true;

    private static int ReadInt(FieldInfo field, object overlay) => field.GetValue(overlay) as int? ?? 0;

    private static string FailureHint(PoolAnchors anchors) =>
        anchors.RefreshFailureHint.GetValue(null) as string ?? string.Empty;

    private static string ExceptionMessage(Exception exception)
    {
        try
        {
            return exception.GetType().GetProperty("Message")?.GetValue(exception) as string ?? exception.Message;
        }
        catch (Exception)
        {
            return exception.Message;
        }
    }

    private static string Describe(Exception exception)
    {
        Exception root = exception;
        while (root is TargetInvocationException { InnerException: not null } wrapped)
        {
            root = wrapped.InnerException!;
        }

        return root.GetType().Name + ": " + root.Message;
    }

    private static Label CreateSourceLabel(PoolAnchors anchors, PoolOrigin origin)
    {
        string text = PoolText.SourceLabelPrefix + origin.ShortName + (origin.IsStale ? PoolText.StaleSuffix : string.Empty);
        Label label = anchors.CreateBodyLabel?.Invoke(null, new object?[] { text }) as Label
                      ?? new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", 14);
        label.AddThemeColorOverride("font_color", MutedColor(anchors));
        label.TooltipText = origin.IsStale
            ? origin.BaseUrl + "\n该服务器当前无响应，这是它上次成功刷新时的房间。"
            : origin.BaseUrl;
        label.SetMeta(PoolText.SourceMetaKey, origin.BaseUrl);
        return label;
    }

    private static Color MutedColor(PoolAnchors anchors)
    {
        try
        {
            if (anchors.TextMutedColor?.GetValue(null) is Color color)
            {
                return color;
            }
        }
        catch (Exception)
        {
            // Fall through to the literal.
        }

        return new Color(0.66f, 0.68f, 0.72f);
    }
}

/// <summary>
/// One pooled refresh round, from the fan-out to the scene tree.
///
/// The fan-out is the room source of the refresh, and it is incremental: every server that answers marks the round
/// dirty and the _Process pump repaints, which is what keeps one slow node from leaving the stage empty. The final
/// write and the stage decision belong to RefreshAsync, because those are the parts of upstream's refresh that also
/// touch the status line, the selection and the page controls.
/// </summary>
internal sealed class PoolRefreshSession
{
    private readonly PoolAnchors _anchors;

    private readonly PoolCycle _cycle;

    private readonly CancellationTokenSource _cancellation;

    /// <summary>One line in the log for the first round of the session, and nothing after it.</summary>
    private static int _announced;

    private int _dirty;

    private int _finished;

    internal PoolRefreshSession(PoolAnchors anchors, object overlay, CancellationToken cancellationToken)
    {
        _anchors = anchors;
        Overlay = overlay;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cycle = PoolRoomStore.Instance.BeginCycle();
    }

    internal object Overlay { get; }

    internal Task Fetch { get; private set; } = Task.CompletedTask;

    /// <summary>How the round went, for the refresh's "every server was silent" decision.</summary>
    internal PoolCycle Cycle => _cycle;

    /// <summary>
    /// Draws the round's final list if the pump still owes a paint (a server answered inside the last coalescing
    /// window). When the pump already put everything on screen this is a no-op, which is what keeps an unchanged round
    /// from rebuilding the stage at all.
    /// </summary>
    internal void Settle()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return;
        }

        if (!PoolRoomStore.Instance.Commit(_cycle, _anchors))
        {
            return;
        }

        WriteRooms();
        _anchors.RebuildRoomStage.Invoke(Overlay, null);
    }

    /// <summary>Starts the fan-out. Runs on the caller's thread and returns immediately.</summary>
    internal void Start()
    {
        Fetch = RunAsync();
    }

    /// <summary>
    /// One frame is 16 ms and a full round can deliver fourteen answers, so painting on every arrival would rebuild
    /// the card grid up to fourteen times per eight second tick. The gate merges them: a dirty round is painted at most
    /// once per PaintCoalesceMs, and the dirty flag is left set so the very next frame past the gate paints.
    /// </summary>
    private const int PaintCoalesceMs = 120;

    private int _lastPaintTicks = System.Environment.TickCount - PaintCoalesceMs;

    internal void MarkDirty() => Volatile.Write(ref _dirty, 1);

    /// <summary>True when there is something to paint and the coalescing window has elapsed.</summary>
    internal bool TryBeginPaint()
    {
        if (Volatile.Read(ref _dirty) == 0)
        {
            return false;
        }

        int now = System.Environment.TickCount;
        if (unchecked(now - _lastPaintTicks) < PaintCoalesceMs)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return false;
        }

        _lastPaintTicks = now;
        return true;
    }

    /// <summary>Commits what the round has so far, writes _rooms and redraws. Only ever called from the _Process pump.</summary>
    internal void PublishPartial()
    {
        if (Volatile.Read(ref _finished) != 0 || !PoolRoomStore.Instance.Commit(_cycle, _anchors))
        {
            return;
        }

        WriteRooms();
        _anchors.RebuildRoomStage.Invoke(Overlay, null);

        if (Interlocked.Exchange(ref _announced, 1) == 0)
        {
            Entry.Info("[server-pool] first pooled round: " + PoolRoomStore.Instance.LastSummary +
                       "; every room card now carries the server it came from.");
        }
    }

    /// <summary>Clears the pump's session and stops a superseded fan-out.</summary>
    internal void Finish()
    {
        Volatile.Write(ref _finished, 1);
        Interlocked.CompareExchange(ref PoolRefreshFix.ActiveSession, null, this);
        try
        {
            _cancellation.Cancel();
        }
        catch (Exception)
        {
            // Already cancelled or disposed.
        }
    }

    private void WriteRooms()
    {
        if (_anchors.Rooms.GetValue(Overlay) is not IList target)
        {
            return;
        }

        IReadOnlyList<object> rooms = PoolRoomStore.Instance.Rooms;
        target.Clear();
        foreach (object room in rooms)
        {
            target.Add(room);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            if (PoolServerCatalog.NeedsDiscovery())
            {
                // Fire and forget on purpose: the extra servers can only help the *next* round, and the discovery
                // round has its own ceiling. It is not tied to this session's token, which is cancelled as soon as
                // the refresh ends.
                _ = PoolServerCatalog.RefreshDiscoveryAsync(_anchors, CancellationToken.None);
            }

            IReadOnlyList<PoolServer> servers = PoolServerCatalog.GetServers(_anchors);
            if (servers.Count == 0)
            {
                return;
            }

            var pending = new List<Task<PoolServerFetch>>(servers.Count);
            foreach (PoolServer server in servers)
            {
                if (PoolServerCatalog.IsCoolingDown(server))
                {
                    // A dead node used to cost the round its full 2 s ceiling on every tick. It is out of the round
                    // until its slot expires, and its last answer stays on the stage marked stale so its rooms do not
                    // blink out of existence in the meantime.
                    if (server.LastRooms is { Count: > 0 } staleRooms)
                    {
                        _cycle.AcceptStale(_anchors, server, staleRooms);
                        MarkDirty();
                    }

                    continue;
                }

                pending.Add(PoolRoomClient.FetchAsync(_anchors, server, PoolRoomClient.RefreshTimeoutMs, _cancellation.Token));
            }

            // First answer wins the first paint: awaiting them one at a time is the whole point, Task.WhenAll here
            // would reintroduce exactly the head-of-line wait this feature exists to remove.
            while (pending.Count > 0)
            {
                Task<PoolServerFetch> finished = await Task.WhenAny(pending).ConfigureAwait(true);
                pending.Remove(finished);
                if (finished.Status == TaskStatus.RanToCompletion)
                {
                    PoolServerFetch fetch = finished.Result;
                    PoolServerCatalog.RecordRoomsFetch(fetch);
                    _cycle.Accept(_anchors, fetch);
                }
                else
                {
                    // FetchAsync is written not to throw; a fault here is a bug in this mod, and an unobserved task
                    // exception would surface as noise in the game log with no repair name on it.
                    _ = finished.Exception;
                }

                MarkDirty();
            }
        }
        catch (OperationCanceledException)
        {
            // A superseded round publishes nothing.
        }
        catch (Exception exception)
        {
            PoolRefreshFix.WarnOnce(
                "fan-out",
                "the pool round failed and the previous room list was kept: " + PoolRoomClient.Describe(exception));
        }
    }
}
