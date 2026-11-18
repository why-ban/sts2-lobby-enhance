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

    /// <summary>First few failure reasons, so "failed=2" never has to be taken on faith.</summary>
    private readonly List<string> _errors = new();

    /// <summary>
    /// Base URLs of the servers that answered successfully in this round, including the ones that answered with zero
    /// rooms: "answered and has no such room" and "never answered" are different facts and callers need both
    /// </summary>
    private readonly HashSet<string> _answered = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>The servers that answered successfully this round, copied so a caller cannot see a half-built round.</summary>
    internal IReadOnlyCollection<string> AnsweredServers
    {
        get
        {
            lock (_gate)
            {
                return _answered.ToArray();
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
            _answered.Add(fetch.Server.BaseUrl);
            AddRows(anchors, fetch.Server, fetch.Rooms, fetch.LatencyMs);
        }
    }

    private void AddRows(
        PoolAnchors anchors,
        PoolServer server,
        IReadOnlyList<object> rooms,
        int latencyMs)
    {
        PoolOrigin origin = new(
            server.BaseUrl,
            server.ShortName(PoolText.SourceNameMaxLength));

        foreach (object room in rooms)
        {
            string roomId = PoolRoomClient.RoomIdOf(anchors, room);
            string key = server.BaseUrl + "\u001f" + roomId;
            if (!_keys.Add(key))
            {
                continue;
            }

            _rows.Add(new Row(room, origin, latencyMs));
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

    /// <summary>
    /// Second index, keyed by room id. A steady-state round replaces the room instances without rebuilding the cards
    /// (that is the point of "unchanged means no repaint"), so the card closures still hold last round's objects; the
    /// identity lookup misses for those and this one answers instead. Ambiguous only when two servers advertise the same
    /// room id, and then the last server to answer this round wins.
    /// </summary>
    private Dictionary<string, PoolOrigin> _originByRoomId = new(StringComparer.Ordinal);

    private IReadOnlyList<object> _rooms = Array.Empty<object>();

    /// <summary>The servers the last committed round heard from, kept beside the rooms so "no such room" can be told
    /// apart from "that server never answered".</summary>
    private IReadOnlyCollection<string> _answeredServers = Array.Empty<string>();

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
                    _originByRoomId = new Dictionary<string, PoolOrigin>(StringComparer.Ordinal);
                    _answeredServers = cycle.AnsweredServers;
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

        var byRoomId = new Dictionary<string, PoolOrigin>(StringComparer.Ordinal);
        foreach (KeyValuePair<object, PoolOrigin> entry in origins)
        {
            string roomId = PoolRoomClient.RoomIdOf(anchors, entry.Key);
            if (roomId.Length > 0)
            {
                byRoomId[roomId] = entry.Value;
            }
        }

        lock (_gate)
        {
            _rooms = rooms;
            _origins = origins;
            _originByRoomId = byRoomId;
            _answeredServers = cycle.AnsweredServers;
        }

        LastSummary = cycle.Describe();
        return true;
    }

    /// <summary>
    /// True when the last committed round heard from this server - regardless of how many rooms it had. Callers use it to
    /// tell "this server has no such room any more" from "this server has not answered"
    /// </summary>
    internal bool ServerAnswered(string baseUrl)
    {
        string wanted = PoolAddress.ToBaseUrl(baseUrl);
        lock (_gate)
        {
            foreach (string answered in _answeredServers)
            {
                if (string.Equals(PoolAddress.ToBaseUrl(answered), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The server a card was drawn for, or null when the room did not come from the pool.</summary>
    internal PoolOrigin? OriginOf(PoolAnchors anchors, object room)
    {
        lock (_gate)
        {
            if (_origins.TryGetValue(room, out PoolOrigin? exact))
            {
                return exact;
            }

            string roomId = PoolRoomClient.RoomIdOf(anchors, room);
            if (roomId.Length == 0)
            {
                return null;
            }

            return _originByRoomId.TryGetValue(roomId, out PoolOrigin? byId) ? byId : null;
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
    /// <summary>
    /// Source names are cut at fifteen characters, the longest an IPv4 address can be: a shorter limit turned
    /// 112.74.164.10 into the same badge as 112.74.164.11. Anything longer is cut with an ellipsis.
    /// </summary>
    internal const int SourceNameMaxLength = 15;

    internal const string SourceLabelPrefix = "来源：";

    /// <summary>Set on the source label so a rebuilt row can be recognised instead of appended to twice.</summary>
    internal const string SourceMetaKey = "lobby_enhance_pool_source";

}
