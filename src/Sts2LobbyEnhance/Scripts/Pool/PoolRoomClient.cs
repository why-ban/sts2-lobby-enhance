using System.Collections;
using System.Diagnostics;

namespace Sts2LobbyEnhance;

/// <summary>Outcome of one server's GET /rooms.</summary>
internal sealed class PoolServerFetch
{
    internal PoolServerFetch(PoolServer server)
    {
        Server = server;
    }

    internal PoolServer Server { get; }

    internal List<object> Rooms { get; } = new();

    /// <summary>Round trip of the request, or <see cref="UnknownLatency"/> when nothing came back.</summary>
    internal int LatencyMs { get; set; } = UnknownLatency;

    internal string? Error { get; set; }

    /// <summary>Set when the caller's token stopped the request. Cancellation is not a failed measurement.</summary>
    internal bool Cancelled { get; set; }

    internal bool Succeeded => Error == null;

    internal const int UnknownLatency = int.MaxValue;

    /// <summary>
    /// A failed fetch is not a measurement and must not be written into the latency cache as one: the node's previous
    /// latency stays, its guard becomes unknown, and the cooldown ladder (PoolServerCatalog) is what keeps it from
    /// costing the next rounds their ceiling.
    /// </summary>
}

/// <summary>
/// The per-server room fetch: one LobbyApiClient per server, every server in parallel, a hard 2 s ceiling per
/// server, and no dependency between them, so fourteen slow nodes cost one ceiling and not fourteen.
///
/// Two deliberate details:
///
/// - The client is built through the host mod's own constructor with an explicit no-op diagnostic sink. The
///   default sink is GD.Print and it is called on every request, so a client created without one cannot be used
///   from a headless harness and would flood the log in game.
/// - The ceiling is the CancellationToken and not the client's HttpClient.Timeout, which the constructor pins to
///   10 s. Cancelling the token aborts the request and surfaces as an OperationCanceledException that this class
///   turns into a failed fetch, which is exactly the "dead node is skipped silently" rule.
///
/// Latency is measured around this request rather than with a separate /probe round trip: one request per server
/// per refresh is the whole budget.
/// </summary>
internal static class PoolRoomClient
{
    /// <summary>Hard ceiling for one server's room list during a pool refresh.</summary>
    internal const int RefreshTimeoutMs = 2000;

    /// <summary>Ceiling for the room list that re-checks a room after a cross-server switch.</summary>
    internal const int VerifyTimeoutMs = 4000;

    /// <summary>Fetches one server's rooms. Never throws; a failure is reported through the result.</summary>
    internal static async Task<PoolServerFetch> FetchAsync(
        PoolAnchors anchors,
        PoolServer server,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var fetch = new PoolServerFetch(server);
        Stopwatch stopwatch = Stopwatch.StartNew();
        IDisposable? client = null;
        try
        {
            client = CreateClient(anchors, server.BaseUrl);
            List<object> rooms = await GetRoomsCoreAsync(anchors, client, timeoutMs, cancellationToken).ConfigureAwait(true);
            stopwatch.Stop();
            fetch.Rooms.AddRange(rooms);
            fetch.LatencyMs = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue - 1);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            fetch.Error = "timeout after " + timeoutMs + " ms";
        }
        catch (OperationCanceledException)
        {
            fetch.Cancelled = true;
            fetch.Error = "cancelled";
        }
        catch (Exception exception)
        {
            fetch.Error = Describe(exception);
        }
        finally
        {
            client?.Dispose();
        }

        return fetch;
    }

    /// <summary>Rooms of one server, throwing on failure. Used where the caller needs to know why.</summary>
    internal static async Task<List<object>> GetRoomsAsync(
        PoolAnchors anchors,
        string baseUrl,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using IDisposable client = CreateClient(anchors, baseUrl);
        return await GetRoomsCoreAsync(anchors, client, timeoutMs, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// LobbyApiClient(baseUrl, readToken, createToken, httpMessageHandler, diagnosticSink, disposeHttpMessageHandler).
    /// The read/create tokens are the single pair the config holds for the current server; a server that wants a
    /// different token answers 401 and is treated like any other dead node.
    /// </summary>
    internal static IDisposable CreateClient(PoolAnchors anchors, string baseUrl)
    {
        string readToken = anchors.ConfigReadToken?.GetValue(null) as string ?? string.Empty;
        string createToken = anchors.ConfigCreateToken?.GetValue(null) as string ?? string.Empty;
        object? client = anchors.ApiClientCtor.Invoke(new object?[]
        {
            PoolAddress.ToBaseUrl(baseUrl),
            readToken,
            createToken,
            null,
            new Action<string>(_ => { }),
            true
        });

        return client as IDisposable
               ?? throw new InvalidOperationException("LobbyApiClient is no longer disposable.");
    }

    private static async Task<List<object>> GetRoomsCoreAsync(
        PoolAnchors anchors,
        object client,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);

        object? invocation = anchors.GetRooms.Invoke(client, new object?[] { cts.Token });
        if (invocation is not Task task)
        {
            throw new InvalidOperationException("LobbyApiClient.GetRoomsAsync no longer returns a Task.");
        }

        await task.ConfigureAwait(true);

        var rooms = new List<object>();
        if (task.GetType().GetProperty("Result")?.GetValue(task) is not IEnumerable sequence)
        {
            return rooms;
        }

        foreach (object? room in sequence)
        {
            if (room != null)
            {
                rooms.Add(room);
            }
        }

        return rooms;
    }

    internal static string RoomIdOf(PoolAnchors anchors, object room)
    {
        try
        {
            return anchors.RoomId.GetValue(room) as string ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Reflection invoke that unwraps TargetInvocationException, so a caller sees the host mod's own exception
    /// type and message instead of "Exception has been thrown by the target of an invocation".
    /// </summary>
    internal static object? Invoke(System.Reflection.MethodInfo method, object? target, object?[]? arguments)
    {
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    internal static string Describe(Exception exception)
    {
        Exception root = exception;
        while (root is System.Reflection.TargetInvocationException { InnerException: not null } wrapped)
        {
            root = wrapped.InnerException!;
        }

        return root.GetType().Name + ": " + root.Message;
    }
}
