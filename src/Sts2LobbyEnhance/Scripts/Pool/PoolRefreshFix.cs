using System.Collections;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Turns the room list into a pool: every known server's rooms, on one stage, with the owning server printed on
/// each card.
///
/// RefreshRoomsAsync is taken over completely instead of wrapped: the overlay's own refresh republishes the current
/// server's list, and two writers on _rooms flip the stage between the pool and that one server every eight seconds.
/// The prefix answers false and reproduces the whole method, guards and error texts included.
///
/// The _Process postfix only keeps the cold-start loading text honest (a round is painted once, when it settles), and
/// the BuildRoomMetaRow postfix appends the source badge.
///
/// Nothing in Applies or Apply touches Godot, the config, the network or the engine: this repair is installed from a
/// headless harness that only knows reflection.
/// </summary>
internal sealed class PoolRefreshFix : ICompatFix
{
    internal const string FixId = "server-pool-refresh";

    private const string Tag = "[server-pool] ";

    /// <summary>
    /// Ceiling for the current server's /probe and /announcements: the same 2 s one pooled fetch gets, instead of the
    /// 10 s HttpClient timeout upstream leaves them on. It has to stay below <see cref="RoundBudgetMs"/>: a budget the
    /// round ceiling always expires first is a number nobody can observe.
    /// </summary>
    private const int CurrentServerQueryBudgetMs = 2000;

    /// <summary>
    /// Hard ceiling for one pooled round, so the paint waits at most this long. It has to stay above both the
    /// per-server fetch and the current server's query budget above, or the round would cut them off.
    /// </summary>
    private const int RoundBudgetMs = 2500;

    private static PoolAnchors? _anchors;

    /// <summary>
    /// The anchors this repair resolved once, for callers that need the same handles (never rebuilt). A rolled-back
    /// group can leave this non-null while the pool snapshot is empty; a consumer must read that as "unknown", not as
    /// "the room is gone"
    /// </summary>
    internal static PoolAnchors? LiveAnchors => _anchors;

    /// <summary>Warnings already printed, keyed by their call signature: one kind of trouble must not silence the rest</summary>
    private static readonly object WarnGate = new();

    private static readonly HashSet<string> WarnedSignatures = new(StringComparer.Ordinal);

    private const int MaxWarnedSignatures = 32;

    /// <summary>The round in flight. Read by the _Process postfix, set and cleared on the main thread.</summary>
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
            WarnOnce("refresh", "the pooled refresh could not start: " + PoolRoomClient.Describe(exception));
            __result = Task.FromResult(false);
            return false;
        }
    }

    /// <summary>The host mod's whole refresh, with the pool as the room source instead of one server's list.</summary>
    private static async Task<bool> RefreshAsync(
        PoolAnchors anchors,
        object overlay,
        bool userInitiated,
        bool allowActionInFlight,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The same three short circuits, with the same false.
        if (ReadBool(anchors.RefreshInFlight!, overlay)
            || ReadBool(anchors.ServerPickerOpen, overlay)
            || (ReadBool(anchors.ActionInFlight, overlay) && !allowActionInFlight))
        {
            return false;
        }

        anchors.PersistSettings.Invoke(overlay, null);
        anchors.RefreshInFlight!.SetValue(overlay, true);
        anchors.TimeUntilAutoRefresh.SetValue(overlay, anchors.RefreshIntervalSeconds);
        if (userInitiated || RoomCount(anchors, overlay) == 0)
        {
            SetStatus(overlay, "正在刷新大厅列表...");
        }

        if (RoomCount(anchors, overlay) == 0)
        {
            // Until the round settles the stage would keep claiming "大厅当前没有房间。".
            ShowLoadingPlaceholder(anchors, overlay);
        }

        anchors.UpdateActionButtons!.Invoke(overlay, null);

        PoolRefreshSession? session = null;
        object? apiClient = null;
        try
        {
            string? selectedRoomId = CurrentSelection(anchors, overlay);

            // The current server's probe and announcements, which the pool does not replace: the announcements belong
            // to the server the player is on, and AnnouncementSourceFix rides on the same call.
            apiClient = TryCreateConfiguredClient(anchors);
            Task<double?> probeTask = MeasureRttAsync(anchors, apiClient, cancellationToken);
            Task<object?> announcementTask = FetchAnnouncementsAsync(anchors, apiClient, cancellationToken);

            // The room source, which is the pool instead of one server's list.
            session = new PoolRefreshSession(anchors, overlay, cancellationToken);
            ActiveSession = session;
            session.Start();

            // One ceiling for the whole round: probe + announcements + fan-out. Whatever has arrived when it expires is
            // committed below; a still-running fetch is cancelled by the session's own token in the finally.
            Task round = Task.WhenAll(probeTask, announcementTask, session.Fetch);
            // When the ceiling wins, nobody awaits the aggregate task: observe it so a fault cannot surface later as an
            // anonymous unobserved-exception line.
            _ = round.ContinueWith(
                static finished => _ = finished.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            Task budget = Task.Delay(RoundBudgetMs);
            if (ReferenceEquals(await Task.WhenAny(round, budget).ConfigureAwait(true), budget))
            {
                NoteRoundBudgetSpent(session);
            }

            // A caller that withdrew the round leaves it cancelled, before the commit and before any failure is
            // counted. Upstream rethrows at the same point and the switch flow answers "服务器切换已取消".
            cancellationToken.ThrowIfCancellationRequested();

            double? measuredProbeRtt = probeTask.IsCompletedSuccessfully ? probeTask.Result : null;
            object? announcements = announcementTask.IsCompletedSuccessfully ? announcementTask.Result : null;

            anchors.LastLobbyRttMs.SetValue(overlay, measuredProbeRtt ?? -1d);
            anchors.ConsecutiveRefreshFailures.SetValue(overlay, 0);

            if (announcements != null)
            {
                anchors.ApplyAnnouncements.Invoke(overlay, new[] { announcements });
            }
            else
            {
                anchors.EnsureAnnouncementFallback.Invoke(overlay, null);
            }

            // What is on screen right now - the previous round's list. This is the baseline the round has to beat
            // before it is worth touching the scene tree.
            IReadOnlyList<object> previousRooms = Snapshot(anchors, overlay);
            string? selectionBefore = CurrentSelection(anchors, overlay) ?? selectedRoomId;

            // The round is committed exactly once, here, after it settled or after its ceiling expired. Writing into
            // _rooms while servers are still answering would clamp the page against a short list, repaint the stage once
            // per answer and print a room count that is true only for the servers that replied so far.
            IReadOnlyList<object> rooms = session.CommitRound();
            if (rooms.Count > 0)
            {
                session.AnnounceFirstRound();
            }

            int pageBefore = ReadCurrentPage(anchors, overlay);
            WriteRooms(anchors, overlay, rooms);

            string? nextSelection = ApplySelection(anchors, overlay, rooms, selectionBefore);

            // Nothing about painting here: "unchanged" has to mean "do not rebuild".
            bool roomStageChanged = userInitiated
                                    || !AreVisuallyEquivalent(anchors, previousRooms, rooms)
                                    || !string.Equals(selectionBefore, nextSelection, StringComparison.Ordinal);
            if (roomStageChanged)
            {
                // Put the page back first, clamped to the new length: RebuildRoomStage clamps too, but it would clamp
                // whatever a previous partial write left behind, and a page the player chose must survive a refresh
                // that did not make it invalid.
                RestoreCurrentPage(anchors, overlay, pageBefore);
                anchors.RebuildRoomStage.Invoke(overlay, null);
            }
            else
            {
                anchors.UpdateHealthIndicator.Invoke(overlay, null);
                anchors.UpdatePageControls.Invoke(overlay, new object[] { FilteredCount(anchors, overlay) });
                anchors.RefreshSelectedRoomDetails.Invoke(overlay, null);
            }

            // The pool's failure mode is per server, so upstream's catch blocks cannot fire for it. "Every server
            // was silent" is still a failed refresh, and it is reported with upstream's own wording.
            if (session.Cycle.Succeeded == 0 && session.Cycle.Failed > 0)
            {
                WarnOnce("all-silent", "no lobby server answered: " + session.Cycle.Describe());
                MarkRefreshFailed(anchors, overlay);
                if (RoomCount(anchors, overlay) == 0)
                {
                    // The loading line is only honest while a round is actually running: put the host mod's own empty
                    // state back so the stage and the failure status agree.
                    anchors.RebuildRoomStage.Invoke(overlay, null);
                }
                SetStatus(overlay, "大厅刷新失败，已保留上次成功列表：所有大厅服务器均未响应\n" + FailureHint(anchors));
                return false;
            }

            if (session.NoServersKnown)
            {
                // An empty hall and an empty catalogue look the same on screen but are different failures: with no
                // server to ask, clearing the status line reports a success nobody performed.
                WarnOnce("no-servers", "no lobby server is known: neither the built-in list nor discovery produced one");
                MarkRefreshFailed(anchors, overlay);
                if (RoomCount(anchors, overlay) == 0)
                {
                    anchors.RebuildRoomStage.Invoke(overlay, null);
                }

                SetStatus(overlay, "大厅服务不可用：没有可用的内建或发现的大厅服务器");
                return false;
            }

            if (rooms.Count == 0)
            {
                ShowEmptyText(anchors, overlay);
            }

            SetStatus(overlay, string.Empty);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (anchors.LobbyServiceExceptionType.IsInstanceOfType(exception))
        {
            MarkRefreshFailed(anchors, overlay);
            string message = ExceptionMessage(exception);
            if (userInitiated || RoomCount(anchors, overlay) == 0)
            {
                SetStatus(overlay, "大厅服务不可用：" + message);
            }
            else
            {
                SetStatus(overlay, "大厅刷新失败，已保留上次成功列表：" + message + "\n" + FailureHint(anchors));
            }

            return false;
        }
        catch (Exception exception)
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
            // Release the round before running host code: UpdateActionButtons touches nodes and can throw on an
            // overlay that a theme change freed mid-round, and that must not skip the fan-out stop or the dispose.
            session?.Finish();
            (apiClient as IDisposable)?.Dispose();
            anchors.RefreshInFlight!.SetValue(overlay, false);
            anchors.UpdateActionButtons!.Invoke(overlay, null);
        }
    }

    /// <summary>
    /// Main-thread pump. A round is drawn once, when it settles, so the only work left is keeping the cold-start text
    /// honest while the first round is still loading.
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

            session.KeepLoadingPlaceholder();
        }
        catch (Exception exception)
        {
            WarnOnce("pump", "a partly filled pool could not be drawn: " + PoolRoomClient.Describe(exception));
        }
    }

    /// <summary>
    /// Appends the owning server to the card's meta row. The row is an HFlowContainer, so a long server name wraps
    /// instead of squeezing the player count.
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

            PoolOrigin? origin = PoolRoomStore.Instance.OriginOf(anchors, room);
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
            WarnOnce("source-label", "a room card was drawn without its server name: " + PoolRoomClient.Describe(exception));
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
            WarnOnce("client", "the current server's client could not be built: " + PoolRoomClient.Describe(exception));
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller withdrew the round. Swallowing this is what let a cancelled refresh look like a failed one.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own budget expired: no measurement, which is a null latency and not a failure.
            return null;
        }
        catch (Exception exception)
        {
            WarnOnce("probe", "the current server's /probe failed: " + PoolRoomClient.Describe(exception));
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            WarnOnce("announcements", "the current server's announcements could not be read: " + PoolRoomClient.Describe(exception));
            return null;
        }
    }

    /// <summary>_rooms.Clear() then AddRange(pool), through the non-generic list interface.</summary>
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

    /// <summary>Keep the selection while it is still in the list, otherwise fall back to the first room.</summary>
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
            // The stage rebuild clamps the selection anyway.
        }

        return next;
    }

    /// <summary>
    /// AreRoomListsVisuallyEquivalent takes IReadOnlyList of LobbyRoomSummary, so both sides are rebuilt into that
    /// exact type. Matched through reflection because the element type only exists at runtime.
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
            // Falls through to the raw field, which is what upstream falls back to as well.
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

    /// <summary>Upstream's own empty-pool texts. Matching them is what keeps this rewrite to the one state it is for.</summary>
    private const string EmptySummaryPrefix = "大厅当前没有房间";

    private const string EmptyHintPrefix = "刷新大厅后，你也可以在右侧直接创建一个新的公开房间";

    private const string LoadingTitle = "正在加载房间列表…";

    private const string LoadingHint = "正在向所有大厅服务器并发拉取房间，首次打开通常需要 1–2 秒。";

    /// <summary>What an empty hall says once a round has confirmed it: the pool spans every server, so none of them has a room.</summary>
    private const string EmptyPoolTitle = "所有服务器都没有房间";

    private static int _budgetLogs;

    /// <summary>
    /// While the first round is loading the stage has no rooms, and the host mod renders that as "大厅当前没有房间。" -
    /// a claim, not a loading state. This rewrites exactly that text (and only that text). Who replaces it depends on
    /// the round: a round that brings rooms rebuilds the stage, a round that confirms the hall is empty calls
    /// <see cref="ShowEmptyText"/>.
    /// </summary>
    internal static void ShowLoadingPlaceholder(PoolAnchors anchors, object overlay)
    {
        try
        {
            Label? summary = anchors.RoomListSummaryLabel?.GetValue(overlay) as Label;

            // A round that already confirmed the hall is empty has said so on the stage. Rewriting the hint to "loading"
            // and back on every eight-second tick is a flash that carries no information.
            if (summary != null && summary.Text.StartsWith(EmptyPoolTitle, StringComparison.Ordinal))
            {
                return;
            }

            if (summary != null && summary.Text.StartsWith(EmptySummaryPrefix, StringComparison.Ordinal))
            {
                summary.Text = LoadingTitle;
            }

            if (anchors.RoomListContainer?.GetValue(overlay) is not Node container)
            {
                return;
            }

            foreach (Node node in container.FindChildren("*", "Label", recursive: true, owned: false))
            {
                if (node is not Label label)
                {
                    continue;
                }

                if (label.Text.StartsWith(EmptySummaryPrefix, StringComparison.Ordinal))
                {
                    label.Text = LoadingTitle;
                }
                else if (label.Text.StartsWith(EmptyHintPrefix, StringComparison.Ordinal))
                {
                    label.Text = LoadingHint;
                }
            }
        }
        catch (Exception exception)
        {
            WarnOnce("loading-text", "the loading placeholder could not be written: " + PoolRoomClient.Describe(exception));
        }
    }

    /// <summary>
    /// The empty hall, in the pool's words. The host mod's own empty state is "大厅当前没有房间。", which is true for one
    /// server and a claim for fifteen; a round that started from an empty stage may also still be showing the loading
    /// text, because a round with zero rooms has nothing for the stage-change check to compare and nothing rebuilds it.
    /// Only labels that still say one of those two things are rewritten.
    /// </summary>
    internal static void ShowEmptyText(PoolAnchors anchors, object overlay)
    {
        try
        {
            if (anchors.RoomListSummaryLabel?.GetValue(overlay) is Label summary)
            {
                summary.Text = EmptyPoolText(summary.Text);
            }

            if (anchors.RoomListContainer?.GetValue(overlay) is not Node container)
            {
                return;
            }

            foreach (Node node in container.FindChildren("*", "Label", recursive: true, owned: false))
            {
                if (node is Label label)
                {
                    label.Text = EmptyPoolText(label.Text);
                }
            }
        }
        catch (Exception exception)
        {
            WarnOnce("empty-text", "the empty-hall text could not be written: " + PoolRoomClient.Describe(exception));
        }
    }

    private static string EmptyPoolText(string text)
    {
        if (text.StartsWith(EmptySummaryPrefix, StringComparison.Ordinal)
            || text.StartsWith(LoadingTitle, StringComparison.Ordinal))
        {
            return EmptyPoolTitle;
        }

        return text.StartsWith(LoadingHint, StringComparison.Ordinal) ? EmptyHintPrefix + "。" : text;
    }

    private static void NoteRoundBudgetSpent(PoolRefreshSession session)
    {
        if (Interlocked.Increment(ref _budgetLogs) > 3)
        {
            return;
        }

        Entry.Info(
            "[server-pool] the round hit its " + RoundBudgetMs + " ms ceiling; the paint uses the servers that " +
            "answered until then (" + session.Cycle.Describe() + ").");
    }

    /// <summary>Clamps the page to the new list length and writes it back, leaving it alone while it is still valid.</summary>
    private static void RestoreCurrentPage(PoolAnchors anchors, object overlay, int page)
    {
        if (anchors.CurrentPageIndex == null || anchors.RoomsPerPage <= 0)
        {
            return;
        }

        try
        {
            int filtered = FilteredCount(anchors, overlay);
            int totalPages = filtered <= 0 ? 0 : (filtered + anchors.RoomsPerPage - 1) / anchors.RoomsPerPage;
            int clamped = totalPages <= 0 ? 0 : Math.Clamp(page, 0, totalPages - 1);
            if (anchors.CurrentPageIndex.GetValue(overlay) is int current && current == clamped)
            {
                return;
            }

            anchors.CurrentPageIndex.SetValue(overlay, clamped);
        }
        catch (Exception exception)
        {
            WarnOnce("page", "the current page could not be preserved: " + PoolRoomClient.Describe(exception));
        }
    }

    private static int ReadCurrentPage(PoolAnchors anchors, object overlay)
    {
        try
        {
            return anchors.CurrentPageIndex?.GetValue(overlay) as int? ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
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

    private static Label CreateSourceLabel(PoolAnchors anchors, PoolOrigin origin)
    {
        string text = PoolText.SourceLabelPrefix + origin.ShortName;
        Label label = anchors.CreateBodyLabel?.Invoke(null, new object?[] { text }) as Label
                      ?? new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", 14);
        label.AddThemeColorOverride("font_color", MutedColor(anchors));
        label.TooltipText = origin.BaseUrl;
        label.SetMeta(PoolText.SourceMetaKey, origin.BaseUrl);
        return label;
    }

    internal static Color MutedColor(PoolAnchors anchors)
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
/// One pooled refresh round: the fan-out, and the single commit that becomes the stage. Nothing is drawn from a
/// partial round; the write and the stage decision belong to RefreshAsync, which also owns the status line, the
/// selection and the page controls.
/// </summary>
internal sealed class PoolRefreshSession
{
    private readonly PoolAnchors _anchors;

    private readonly PoolCycle _cycle;

    private readonly CancellationTokenSource _cancellation;

    private readonly CancellationToken _callerToken;

    /// <summary>Set once per process, so the first pooled round is logged exactly once.</summary>
    private static int _announced;

    private int _finished;

    internal PoolRefreshSession(PoolAnchors anchors, object overlay, CancellationToken cancellationToken)
    {
        _anchors = anchors;
        Overlay = overlay;
        _callerToken = cancellationToken;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cycle = PoolRoomStore.Instance.BeginCycle();
    }

    internal object Overlay { get; }

    internal Task Fetch { get; private set; } = Task.CompletedTask;

    /// <summary>How the round went, for the refresh's "every server was silent" decision.</summary>
    internal PoolCycle Cycle => _cycle;

    /// <summary>Set when the catalogue has no server at all, so the epilogue reports it instead of clearing the status line.</summary>
    internal bool NoServersKnown { get; private set; }

    /// <summary>
    /// Commits the finished round and returns the list the overlay should hold. Called once per round, from the
    /// refresh's epilogue; a round that produced nothing keeps the previously published list.
    /// </summary>
    internal IReadOnlyList<object> CommitRound()
    {
        _ = PoolRoomStore.Instance.Commit(_cycle, _anchors);
        return PoolRoomStore.Instance.Rooms;
    }

    /// <summary>Starts the fan-out. Runs on the caller's thread and returns immediately.</summary>
    internal void Start()
    {
        Fetch = RunAsync();
    }

    internal bool StageIsEmpty() =>
        _anchors.Rooms.GetValue(Overlay) is not ICollection rooms || rooms.Count == 0;

    /// <summary>
    /// Keeps the loading text where the host mod would print "大厅当前没有房间。". Only labels that still say one of the
    /// two are touched, so a round that already painted its result is left alone.
    /// </summary>
    internal void KeepLoadingPlaceholder()
    {
        if (Volatile.Read(ref _finished) != 0 || !StageIsEmpty())
        {
            return;
        }

        PoolRefreshFix.ShowLoadingPlaceholder(_anchors, Overlay);
    }

    /// <summary>The one-line summary of the first pooled round.</summary>
    internal void AnnounceFirstRound()
    {
        if (Interlocked.Exchange(ref _announced, 1) == 0)
        {
            Entry.Info("[server-pool] first pooled round: " + PoolRoomStore.Instance.LastSummary + ".");
        }
    }

    /// <summary>Clears the pump's session and stops a superseded fan-out.</summary>
    internal void Finish()
    {
        Volatile.Write(ref _finished, 1);
        Interlocked.CompareExchange(ref PoolRefreshFix.ActiveSession, null, this);
        try
        {
            // Cancel first: the fan-out holds this token, and a disposed source cannot cancel.
            _cancellation.Cancel();
            _cancellation.Dispose();
        }
        catch (Exception)
        {
            // Already cancelled or disposed.
        }
    }

    private async Task RunAsync()
    {
        try
        {
            // The list first, discovery second: discovery merges into the catalogue GetServers just built, so asking
            // while the catalogue is still empty was a round that returned before doing anything.
            IReadOnlyList<PoolServer> servers = PoolServerCatalog.GetServers(_anchors);

            if (PoolServerCatalog.NeedsDiscovery())
            {
                // Fire and forget on purpose: the extra servers can only help the *next* round, and the discovery
                // round has its own ceiling. It is not tied to this session's token, which is cancelled as soon as
                // the refresh ends.
                _ = PoolServerCatalog.RefreshDiscoveryAsync(_anchors, CancellationToken.None);
            }

            if (servers.Count == 0)
            {
                // Nothing to ask. The epilogue reports it; returning quietly here used to read as a successful round.
                NoServersKnown = true;
                return;
            }

            var pending = new List<Task<PoolServerFetch>>(servers.Count);
            foreach (PoolServer server in servers)
            {
                if (PoolServerCatalog.IsCoolingDown(server))
                {
                    // A dead node would cost the round its full 2 s ceiling on every tick, so it sits out until its
                    // slot expires. Its rooms are not shown in the meantime: an unconfirmed room is a room nobody can
                    // join, and the next round that reaches the server brings them back.
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
                if (finished.Status != TaskStatus.RanToCompletion)
                {
                    // FetchAsync is written not to throw; a fault here is a bug in this mod, and an unobserved task
                    // exception would surface as noise in the game log with no repair name on it.
                    _ = finished.Exception;
                    continue;
                }

                PoolServerFetch fetch = finished.Result;
                if (fetch.Cancelled)
                {
                    // The round was superseded, so this server did not fail: writing it down as a failure would put
                    // it on the cooldown ladder and close its create guard for a request nobody waited for.
                    continue;
                }

                PoolServerCatalog.RecordRoomsFetch(fetch);
                _cycle.Accept(_anchors, fetch);
            }
        }
        catch (OperationCanceledException) when (_callerToken.IsCancellationRequested)
        {
            // The caller withdrew the refresh: the round stays cancelled instead of turning into a failure.
            throw;
        }
        catch (OperationCanceledException)
        {
            // A superseded round publishes nothing: our own budget expired and the session was finished under it.
        }
        catch (Exception exception)
        {
            PoolRefreshFix.WarnOnce(
                "fan-out",
                "the pool round failed and the previous room list was kept: " + PoolRoomClient.Describe(exception));
        }
    }
}
