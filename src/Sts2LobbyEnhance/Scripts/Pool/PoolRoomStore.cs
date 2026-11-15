using System.Collections.Generic;

namespace Sts2LobbyEnhance;

/// <summary>Which server a pooled room came from, and the short name to print for it.</summary>
internal sealed class PoolOrigin
{
    internal PoolOrigin(string baseUrl, string shortName)
    {
        BaseUrl = baseUrl;
        ShortName = shortName;
    }

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
        PoolOrigin origin = new(
            fetch.Server.BaseUrl,
            fetch.Server.ShortName(PoolText.SourceNameMaxLength));

        lock (_gate)
        {
            if (!fetch.Succeeded)
            {
                _failed++;
                return;
            }

            _succeeded++;
            foreach (object room in fetch.Rooms)
            {
                string roomId = PoolRoomClient.RoomIdOf(anchors, room);
                string key = fetch.Server.BaseUrl + "\u001f" + roomId;
                if (!_keys.Add(key))
                {
                    continue;
                }

                _rows.Add(new Row(room, origin, fetch.LatencyMs, _rows.Count));
            }
        }
    }

    /// <summary>
    /// The pooled list in display order: same game version first, then more players first, then the faster server
    /// first, then the order the servers and their room lists arrived in, which is the host mod's own order.
    /// LINQ ordering is stable, so the last key needs no explicit index.
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
            .ThenBy(row => row.Order)
            .Select(row => row.Room)
            .ToList();
    }

    internal string Describe()
    {
        lock (_gate)
        {
            return $"rooms={_rows.Count} servers={_succeeded} failed={_failed}";
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

    private sealed record Row(object Room, PoolOrigin Origin, int LatencyMs, int Order);
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
    /// Publishes a finished round. Returns false when the round produced nothing, in which case the previous list
    /// and the previous origin map stay exactly as they are.
    /// </summary>
    internal bool Commit(PoolCycle cycle, PoolAnchors anchors)
    {
        if (!cycle.HasRows)
        {
            LastSummary = cycle.Describe();
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
}
