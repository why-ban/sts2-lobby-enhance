using System.Collections.Generic;

namespace Sts2LobbyEnhance;

/// <summary>Which server a pooled room came from, and the short name to print for it.</summary>
internal sealed class PoolOrigin
{
    internal PoolOrigin(string baseUrl, string shortName, bool isStale)
    {
        BaseUrl = baseUrl;
        ShortName = shortName;
        IsStale = isStale;
    }

    /// <summary>
    /// True when these rooms are the last answer of a server that is currently in its failure cooldown, i.e. nobody
    /// has confirmed them this round. The card says so instead of pretending the list is fresh.
    /// </summary>
    internal bool IsStale { get; }

    /// <summary>Normalized authority of the owning server.</summary>
    internal string BaseUrl { get; }

    internal string ShortName { get; }
}

/// <summary>
/// One refresh round: every server's answer as it arrives, keyed by (server, roomId).
///
/// The cycle is deliberately separate from what is on screen. A round that dies half way through must not empty
/// the list the player is looking at, so nothing is published until the round is committed, and a round with no
/// successful server at all publishes nothing and leaves the previous result standing.
/// </summary>
internal sealed class PoolCycle
{
    private readonly object _gate = new();

    private readonly List<Row> _rows = new();

    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    private int _succeeded;

    private int _failed;

    private int _version;

    /// <summary>First few failure reasons, so "failed=2" never has to be taken on faith.</summary>
    private readonly List<string> _errors = new();

    internal int Succeeded
    {
        get
        {
            lock (_gate)
            {
                return _succeeded;
            }
        }
    }

    internal int Failed
    {
        get
        {
            lock (_gate)
            {
                return _failed;
            }
        }
    }

    /// <summary>
    /// Bumped every time rows are added. The refresh uses it to tell "the stage already shows this round's final
    /// list" (the pump painted after the last answer) from "it shows a half filled one".
    /// </summary>
    internal int Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    internal bool HasRows
    {
        get
        {
            lock (_gate)
            {
                return _rows.Count > 0;
            }
        }
    }

    /// <summary>
    /// Folds one server's answer in. Later servers keep their own rows even when a room id repeats, because the
    /// identity of a pooled room is (baseUrl, roomId) and two servers really can advertise the same id.
    /// </summary>
    internal void Accept(PoolAnchors anchors, PoolServerFetch fetch)
    {
        lock (_gate)
        {
            if (!fetch.Succeeded)
            {
                _failed++;
                if (_errors.Count < 3)
                {
                    _errors.Add(fetch.Server.BaseUrl + ": " + (fetch.Error ?? "unknown failure"));
                }

                return;
            }

            _succeeded++;
            AddRows(anchors, fetch.Server, fetch.Rooms, fetch.LatencyMs, isStale: false);
        }
    }

    /// <summary>
    /// Rows of a server that is cooling down: kept so one dead node cannot empty its part of the stage, flagged so the
    /// card can say they are not confirmed.
    /// </summary>
    internal void AcceptStale(PoolAnchors anchors, PoolServer server, IReadOnlyList<object> rooms)
    {
        lock (_gate)
        {
            AddRows(anchors, server, rooms, server.LatencyMs ?? PoolServerFetch.UnknownLatency, isStale: true);
        }
    }

    private void AddRows(
        PoolAnchors anchors,
        PoolServer server,
        IReadOnlyList<object> rooms,
        int latencyMs,
        bool isStale)
    {
        PoolOrigin origin = new(
            server.BaseUrl,
            server.ShortName(PoolText.SourceNameMaxLength),
            isStale);

        foreach (object room in rooms)
        {
            string roomId = PoolRoomClient.RoomIdOf(anchors, room);
            string key = server.BaseUrl + "\u001f" + roomId;
            if (!_keys.Add(key))
            {
                continue;
            }

            _rows.Add(new Row(room, origin, latencyMs));
            _version++;
        }
    }

    /// <summary>
    /// The pooled list in display order: same game version first, more players first, faster server first, then the
    /// server's own address. Nothing here depends on who answered first, so the stage does not reshuffle between two
    /// rounds that produced the same rooms; rooms of one server keep their /rooms order through LINQ's stable sort.
    /// </summary>
    internal IReadOnlyList<object> Snapshot(PoolAnchors anchors, string localVersion, out Dictionary<object, PoolOrigin> origins)
    {
        Row[] rows;
        lock (_gate)
        {
            rows = _rows.ToArray();
        }

        origins = new Dictionary<object, PoolOrigin>(ReferenceEqualityComparer.Instance);
        foreach (Row row in rows)
        {
            origins[row.Room] = row.Origin;
        }

        return rows
            .OrderByDescending(row => PoolVersion.Same(localVersion, VersionOf(anchors, row.Room)))
            .ThenByDescending(row => PlayersOf(anchors, row.Room))
            .ThenBy(row => row.LatencyMs)
            // Address, never arrival order: an arrival key reshuffles the stage every time a different server happens
            // to answer first, which is the churn the audit measured (15-17 repaints of different orderings per round).
            .ThenBy(row => row.Origin.BaseUrl, StringComparer.OrdinalIgnoreCase)
            .Select(row => row.Room)
            .ToList();
    }

    internal string Describe()
    {
        lock (_gate)
        {
            string summary = $"rooms={_rows.Count} servers={_succeeded} failed={_failed}";
            return _errors.Count == 0 ? summary : summary + " [" + string.Join("; ", _errors) + "]";
        }
    }

    private static string VersionOf(PoolAnchors anchors, object room)
    {
        try
        {
            return anchors.RoomVersion.GetValue(room) as string ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static int PlayersOf(PoolAnchors anchors, object room)
    {
        try
        {
            return anchors.RoomPlayers.GetValue(room) as int? ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private sealed record Row(object Room, PoolOrigin Origin, int LatencyMs);
}

/// <summary>
/// What the pool currently shows: the flattened room list plus the room to server map the source badge reads.
///
/// The map is keyed by object identity, not by room id, because the same room id can exist on two servers and the
/// card being drawn is built from one specific instance. A round that fails completely leaves both the list and
/// the map untouched, which is what "keep the last result" has to mean for the badges as well.
/// </summary>
internal sealed class PoolRoomStore
{
    private readonly object _gate = new();

    private Dictionary<object, PoolOrigin> _origins = new(ReferenceEqualityComparer.Instance);

    private IReadOnlyList<object> _rooms = Array.Empty<object>();

    private string _localVersion = string.Empty;

    private int _localVersionAt;

    internal static PoolRoomStore Instance { get; } = new();

    internal IReadOnlyList<object> Rooms
    {
        get
        {
            lock (_gate)
            {
                return _rooms;
            }
        }
    }

    /// <summary>The last committed round's server breakdown, for the one-line log.</summary>
    internal string LastSummary { get; private set; } = "no round yet";

    /// <summary>A fresh round. Separate from the published list so a half-finished round never empties the stage.</summary>
    internal PoolCycle BeginCycle() => new();

    /// <summary>
    /// Publishes a finished round. A round where at least one server answered publishes its result even when that
    /// result is empty (an empty hall must be able to empty the stage); only a round where nobody answered returns
    /// false and leaves the previous list and origin map untouched.
    /// </summary>
    internal bool Commit(PoolCycle cycle, PoolAnchors anchors)
    {
        if (!cycle.HasRows)
        {
            LastSummary = cycle.Describe();

            // Two very different "no rows" cases. If at least one server answered, the hall really is empty (or only
            // its empty servers answered) and publishing the empty list is the truth; keeping the previous rooms
            // would leave ghost cards that 404 on click. Only "nobody answered at all" keeps the previous list.
            if (cycle.Succeeded > 0)
            {
                lock (_gate)
                {
                    _rooms = Array.Empty<object>();
                    _origins = new Dictionary<object, PoolOrigin>(ReferenceEqualityComparer.Instance);
                }

                return true;
            }

            return false;
        }

        IReadOnlyList<object> rooms = cycle.Snapshot(anchors, LocalVersion(anchors), out Dictionary<object, PoolOrigin> origins);
        if (rooms.Count == 0)
        {
            LastSummary = cycle.Describe();
            return false;
        }

        lock (_gate)
        {
            _rooms = rooms;
            _origins = origins;
        }

        LastSummary = cycle.Describe();
        return true;
    }

    /// <summary>The server a card was drawn for, or null when the room did not come from the pool.</summary>
    internal PoolOrigin? OriginOf(object room)
    {
        lock (_gate)
        {
            return _origins.TryGetValue(room, out PoolOrigin? origin) ? origin : null;
        }
    }

    /// <summary>The local game version, the pool's first sort key. Cached for a minute: it cannot change mid session.</summary>
    internal string LocalVersion(PoolAnchors anchors)
    {
        int now = Environment.TickCount;
        lock (_gate)
        {
            if (_localVersion.Length > 0 && unchecked(now - _localVersionAt) < 60_000)
            {
                return _localVersion;
            }
        }

        string version = string.Empty;
        try
        {
            version = anchors.GetGameVersion?.Invoke(null, null) as string ?? string.Empty;
        }
        catch (Exception exception)
        {
            PoolRefreshFix.WarnOnce(
                "game-version",
                "the local game version is unreadable, so version-equal rooms cannot be ranked first: " +
                PoolRoomClient.Describe(exception));
        }

        lock (_gate)
        {
            _localVersion = PoolVersion.Normalize(version);
            _localVersionAt = now;
            return _localVersion;
        }
    }
}

/// <summary>Text limits the pool shares with the rest of the mod.</summary>
internal static class PoolText
{
    /// <summary>Source names are cut at twelve characters so the meta row keeps fitting on one card line.</summary>
    internal const int SourceNameMaxLength = 12;

    internal const string SourceLabelPrefix = "来源：";

    /// <summary>Set on the source label so a rebuilt row can be recognised instead of appended to twice.</summary>
    internal const string SourceMetaKey = "lobby_enhance_pool_source";

    /// <summary>Appended to the source name while the server is in its failure cooldown.</summary>
    internal const string StaleSuffix = "（旧）";
}
