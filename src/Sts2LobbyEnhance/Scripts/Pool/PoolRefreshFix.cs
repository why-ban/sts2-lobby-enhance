using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Godot;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Turns the room list into a pool: every known server's rooms, on one stage, with the owning server printed on
/// each card.
///
/// Three patches, one seam each:
///
/// - RefreshRoomsAsync, LanConnectLobbyOverlay.cs:3362. A prefix that takes the call over, starts the fan-out
///   first, and then runs the untouched original with a re-entry gate so the overlay keeps every piece of
///   behaviour it already had: status text, announcements (AnnouncementSourceFix rides on that call), the health
///   indicator, selection repair, the refresh button state and the eight second auto-refresh timer
///   (LanConnectConstants.cs:50). What we add is the room source: when the original finishes, _rooms is replaced
///   by the committed pool and the stage is rebuilt.
///   Taking the method over instead of re-implementing it is deliberate. The alternative is fifteen private
///   anchors for the UI steps above, and every one of them is a way to break a lobby that upstream gets right.
/// - _Process, LanConnectLobbyOverlay.cs:725. The repaint pump: the only place the pool touches the scene tree
///   besides commit, because a Godot tick is the one main-thread guarantee this mod has. Per-server results only
///   write data; the next frame paints them.
/// - BuildRoomMetaRow, LanConnectLobbyOverlay.cs:3943. Appends the "来源：{server}" label to the meta row that
///   already wraps (HFlowContainer), sized and coloured like upstream's own CreateMetaLabel (:3927-3933).
///
/// Nothing in Applies or Apply touches Godot, the config, the network or the engine: this repair is installed
/// from a headless harness that only knows reflection.
/// </summary>
internal sealed class PoolRefreshFix : ICompatFix
{
    internal const string FixId = "server-pool-refresh";

    private const string Tag = "[server-pool] ";

    /// <summary>
    /// Re-entry gate for the one path that calls the original RefreshRoomsAsync by reflection. Thread-local is
    /// enough: the gate only has to hold for the synchronous prologue of the invoke, which runs on this thread.
    /// </summary>
    [ThreadStatic]
    private static bool _reentering;

    private static PoolAnchors? _anchors;

    private static int _warned;

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
            Tag + "the room list is now a pool over every known server; patched " +
            PoolAnchors.OverlayTypeName + ".RefreshRoomsAsync (LanConnectLobbyOverlay.cs:3362), ._Process (:725) and " +
            ".BuildRoomMetaRow (:3943). Cross-server join and create-room server choice are separate repairs.");
    }

    /// <summary>
    /// Takes RefreshRoomsAsync over: start the fan-out, run the original unchanged, then republish _rooms from the
    /// pool once the original is done. Every failure path returns true so the unmodified refresh still runs.
    /// </summary>
    // ReSharper disable once InconsistentNaming - Harmony binds these by their exact names.
    public static bool RefreshRoomsPrefix(object? __instance, object?[]? __args, ref Task<bool> __result)
    {
        PoolRefreshSession? session = null;
        try
        {
            PoolAnchors? anchors = _anchors;
            if (anchors == null || __instance == null || __args is not { Length: >= 3 } || _reentering)
            {
                return true;
            }

            // Upstream's own guard would return false without touching anything, so starting a fan-out for a
            // refresh that cannot land is pure cost.
            if (anchors.RefreshInFlight?.GetValue(__instance) is true)
            {
                return true;
            }

            CancellationToken cancellationToken = __args[2] is CancellationToken token ? token : CancellationToken.None;
            session = new PoolRefreshSession(anchors, __instance, cancellationToken);
            session.Start();
            ActiveSession = session;

            Task<bool>? original = InvokeRefresh(anchors, __instance, __args);
            if (original == null)
            {
                Abandon(session);
                return true;
            }

            __result = PoolRefreshSession.FollowAsync(original, session);
            return false;
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            // Upstream threw out of its own prologue (a cancelled token, most often). Reproduce that exactly:
            // swallowing it here would change what the caller sees, and running the body again would double it.
            Abandon(session);
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            return true;
        }
        catch (Exception exception)
        {
            Abandon(session);
            WarnOnce(
                "refresh",
                "the pooled refresh could not start, so the unmodified refresh ran instead: " +
                PoolRoomClient.Describe(exception));
            return true;
        }
    }

    /// <summary>
    /// Repaint pump. Godot only calls this on the main thread, which is why the scene-tree write lives here and not
    /// in the fan-out's continuations.
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

            if (!session.TakeDirty())
            {
                return;
            }

            session.Publish();
        }
        catch (Exception exception)
        {
            WarnOnce("pump", "a partly filled pool could not be drawn: " + PoolRoomClient.Describe(exception));
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
            WarnOnce("source-label", "a room card was drawn without its server name: " + PoolRoomClient.Describe(exception));
        }
    }

    internal static void WarnOnce(string signature, string message)
    {
        if (Interlocked.Exchange(ref _warned, 1) != 0)
        {
            return;
        }

        Entry.Warn(Tag + signature + ": " + message);
    }

    /// <summary>Sets the overlay's status line when the host mod still has one. Cosmetic, hence optional.</summary>
    internal static void SetStatus(object overlay, string message)
    {
        try
        {
            _anchors?.SetStatus?.Invoke(overlay, new object?[] { message });
        }
        catch (Exception)
        {
            // A status line is not worth a second warning.
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

    private static Task<bool>? InvokeRefresh(PoolAnchors anchors, object overlay, object?[] args)
    {
        bool previous = _reentering;
        _reentering = true;
        try
        {
            return anchors.RefreshRooms.Invoke(overlay, args) as Task<bool>;
        }
        finally
        {
            _reentering = previous;
        }
    }

    private static void Abandon(PoolRefreshSession? session)
    {
        if (session == null)
        {
            return;
        }

        Interlocked.CompareExchange(ref ActiveSession, null, session);
        session.Cancel();
    }
}

/// <summary>
/// One pooled refresh round, from the fan-out to the scene tree.
///
/// The fan-out starts before the original refresh runs and never blocks it, so the pool costs the overlay nothing
/// but the extra request per server. Painting is incremental: every server that answers marks the round dirty and
/// the _Process pump repaints, which is what keeps a single slow node from leaving the stage empty.
/// </summary>
internal sealed class PoolRefreshSession
{
    private readonly PoolAnchors _anchors;

    private readonly PoolCycle _cycle;

    private readonly CancellationTokenSource _cancellation;

    private int _dirty;

    /// <summary>One line in the log for the first round of the session, and nothing after it: at eight seconds a
    /// turn anything chattier is noise.</summary>
    private static int _announced;

    internal PoolRefreshSession(PoolAnchors anchors, object overlay, CancellationToken cancellationToken)
    {
        _anchors = anchors;
        Overlay = overlay;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cycle = PoolRoomStore.Instance.BeginCycle();
    }

    internal object Overlay { get; }

    internal Task Fetch { get; private set; } = Task.CompletedTask;

    private string? _selection;

    /// <summary>Starts the fan-out. Runs on the caller's thread and returns immediately.</summary>
    internal void Start()
    {
        CaptureSelection();
        Fetch = RunAsync();
    }

    /// <summary>
    /// Remembers what was selected before the original refresh runs. Upstream keeps the selection only while the
    /// selected room is in the list it fetched (:3409-3416), so a room that lives on another server would be dropped
    /// on every eight second tick; the pool puts it back.
    /// </summary>
    private void CaptureSelection()
    {
        try
        {
            _selection = _anchors.SelectedRoomId?.GetValue(Overlay) as string;
        }
        catch (Exception)
        {
            _selection = null;
        }
    }

    /// <summary>
    /// Restores the remembered selection only when the current one is not in the pool. A selection the player made
    /// after the refresh started is always in the pool and is therefore never overwritten.
    /// </summary>
    private void RestoreSelection(IReadOnlyList<object> rooms)
    {
        System.Reflection.FieldInfo? field = _anchors.SelectedRoomId;
        if (field == null || string.IsNullOrWhiteSpace(_selection) || Contains(rooms, _selection))
        {
            return;
        }

        string? current;
        try
        {
            current = field.GetValue(Overlay) as string;
        }
        catch (Exception)
        {
            return;
        }

        if (Contains(rooms, current))
        {
            return;
        }

        try
        {
            field.SetValue(Overlay, _selection);
        }
        catch (Exception)
        {
            // A lost selection is cosmetic; the stage rebuild clamps it anyway (:3618-3621).
        }
    }

    private bool Contains(IReadOnlyList<object> rooms, string? roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return false;
        }

        foreach (object room in rooms)
        {
            if (string.Equals(PoolRoomClient.RoomIdOf(_anchors, room), roomId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal void Cancel()
    {
        try
        {
            _cancellation.Cancel();
        }
        catch (Exception)
        {
            // Already disposed or already cancelled.
        }
    }

    internal void MarkDirty() => Volatile.Write(ref _dirty, 1);

    internal bool TakeDirty() => Interlocked.Exchange(ref _dirty, 0) != 0;

    /// <summary>
    /// Reproduces the original refresh's result while the pool takes over the room list. The original's task is
    /// awaited first and its exception is rethrown unchanged: a cancelled refresh still cancels, and the pool only
    /// publishes when the round upstream reported actually finished.
    /// </summary>
    internal static async Task<bool> FollowAsync(Task<bool> original, PoolRefreshSession session)
    {
        try
        {
            bool refreshed = await original.ConfigureAwait(true);
            session.MarkDirty();
            session.Publish();
            if (Interlocked.Exchange(ref _announced, 1) == 0)
            {
                Entry.Info("[server-pool] first pooled round: " + PoolRoomStore.Instance.LastSummary +
                           "; a room list on each card now carries its server.");
            }

            return refreshed;
        }
        finally
        {
            Interlocked.CompareExchange(ref PoolRefreshFix.ActiveSession, null, session);
            session.Cancel();
        }
    }

    /// <summary>Publishes what the round has so far and redraws the stage.</summary>
    internal void Publish()
    {
        if (!PoolRoomStore.Instance.Commit(_cycle, _anchors))
        {
            return;
        }

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

        RestoreSelection(rooms);
        _anchors.RebuildRoomStage.Invoke(Overlay, null);
    }

    private async Task RunAsync()
    {
        try
        {
            if (PoolServerCatalog.NeedsDiscovery())
            {
                // Fire and forget on purpose: the extra servers can only help the *next* round, and the discovery
                // round has its own ceiling. It is not tied to this session's token, which is cancelled the moment
                // the overlay's refresh finishes.
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
                    PoolServerCatalog.RecordProbe(
                        fetch.Server,
                        fetch.Succeeded ? fetch.LatencyMs : PoolServerFetch.UnreachablePenaltyMs,
                        null,
                        null,
                        null);
                    _cycle.Accept(_anchors, fetch);
                }
                else
                {
                    // FetchAsync is written not to throw; a fault here is a bug in this mod, and an unobserved
                    // task exception would surface as noise in the game log with no repair name on it.
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
