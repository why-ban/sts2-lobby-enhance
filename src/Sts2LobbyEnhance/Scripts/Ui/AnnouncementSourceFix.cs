using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace Sts2LobbyEnhance;

/// <summary>
/// Makes the announcement bar follow the server the public channel is attached to. The bar keeps upstream's
/// shape, refresh cadence and request count; only the destination changes.
/// </summary>
internal sealed class AnnouncementSourceFix : ICompatFix
{
    private const string ApiClientTypeName = "Sts2LanConnect.Scripts.LobbyApiClient";

    private const string ConfigTypeName = "Sts2LanConnect.Scripts.LanConnectConfig";

    private const string GetAnnouncementsMethodName = "GetAnnouncementsAsync";

    private const string ConfigBaseUrlPropertyName = "LobbyServerBaseUrl";

    /// <summary>Retired clients wait this long before disposal, so an in-flight request can finish first.</summary>
    private static readonly TimeSpan ClientGrace = TimeSpan.FromSeconds(60d);

    private static readonly object Gate = new();

    private static readonly List<(object Client, DateTimeOffset RetiredAt)> Graveyard = new();

    private static Shape? _shape;

    private static int _warned;

    private static object? _client;

    private static string _clientAddress = string.Empty;

    /// <summary>
    /// Non-zero while this prefix is rerunning the method on our own client. Per thread on purpose: the nested call is
    /// made on this thread and re-enters synchronously, and a process-wide flag would swallow a parallel request.
    /// </summary>
    [ThreadStatic]
    private static int _selfCall;

    public string Id => "announcement-source";

    /// <summary>
    /// The announcement fetch. A shape change there costs the follow-along only; the room list and the channel
    /// switch keep their own groups.
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
            "announcement-source: Applies() did not capture the lobby client shape; refusing to patch blind.");

        // Not the helper inside RefreshRoomsAsync: this build replaces that method with the pool refresh, which
        // fetches through the API client directly, so the client method is the one both paths call.
        harmony.Patch(
            shape.GetAnnouncements,
            prefix: new HarmonyMethod(typeof(AnnouncementSourceFix), nameof(GetAnnouncementsPrefix))
            {
                priority = Priority.First
            });

        Entry.Info(
            "announcement-source: the announcement fetch now asks the server the public channel is attached " +
            "to; the bar, its refresh cadence and the number of requests are unchanged.");
    }

    /// <summary>
    /// Runs the same fetch against the channel's server. Synchronous: it hands back a task and does not wait for it.
    /// </summary>
    private static bool GetAnnouncementsPrefix(object[] __args, ref object __result)
    {
        try
        {
            if (_selfCall != 0)
            {
                // The call we started below re-entered this prefix on this thread: let it reach the real method.
                return true;
            }

            Shape? shape = _shape;
            if (shape == null)
            {
                return true;
            }

            string lobbyUrl = ReadConfigBaseUrl(shape);
            string? target = ChatServerSwitchFix.CurrentChatTargetAddress;
            if (string.IsNullOrWhiteSpace(target) || SameAddress(target, lobbyUrl))
            {
                // The channel is on the lobby's own server (or nothing was switched yet): upstream's client.
                RetireClient();
                return true;
            }

            object? client = EnsureClient(shape, target);
            if (client == null)
            {
                return true;
            }

            object? invocationArgument = __args.Length > 0 ? __args[0] : default(CancellationToken);
            object? forwarded;
            _selfCall++;
            try
            {
                forwarded = shape.GetAnnouncements.Invoke(client, new[] { invocationArgument });
            }
            finally
            {
                _selfCall--;
            }

            if (forwarded == null)
            {
                return true;
            }

            __result = forwarded;
            return false;
        }
        catch (Exception exception)
        {
            // Announcements are cosmetic: on any surprise the lobby's own client stays in place.
            WarnOnce("could not point the announcement request at the channel server: " +
                     exception.GetType().Name + ": " + exception.Message);
            return true;
        }
    }

    /// <summary>
    /// One client per selected server, reused across refreshes.
    ///
    /// Reuse is not an optimisation: the caller owns the client it passes in but not the one this repair substitutes,
    /// so a fresh client every refresh tick would leak a socket pool per tick. A replaced client is retired rather
    /// than disposed immediately, because the previous request may still be in flight.
    /// </summary>
    private static object? EnsureClient(Shape shape, string address)
    {
        lock (Gate)
        {
            if (_client != null && SameAddress(_clientAddress, address))
            {
                return _client;
            }

            RetireClientLocked();
            DisposeExpiredLocked();

            // No tokens: the configured client's credentials belong to the lobby server this player is on, and this
            // client only ever talks to a server the channel was switched to. /announcements needs no auth anyway.
            // A non-null diagnostic sink is mandatory: with null, LobbyApiClient's constructor falls back to GD.Print
            // and resolves its configuration banner through the engine, which a patch body must not trigger.
            object? created = shape.ApiClientConstructor.Invoke(new object?[]
            {
                address,
                null,
                null,
                null,
                (Action<string>)(_ => { }),
                true
            });

            if (created == null)
            {
                return null;
            }

            _client = created;
            _clientAddress = address;
            return created;
        }
    }

    private static void RetireClient()
    {
        lock (Gate)
        {
            RetireClientLocked();
            // Otherwise the last retired client waits for a switch to a remote server that may never come.
            DisposeExpiredLocked();
        }
    }

    private static void RetireClientLocked()
    {
        if (_client == null)
        {
            return;
        }

        Graveyard.Add((_client, DateTimeOffset.UtcNow));
        _client = null;
        _clientAddress = string.Empty;
    }

    private static void DisposeExpiredLocked()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int index = Graveyard.Count - 1; index >= 0; index--)
        {
            (object client, DateTimeOffset retiredAt) = Graveyard[index];
            if (now - retiredAt < ClientGrace)
            {
                continue;
            }

            Graveyard.RemoveAt(index);
            try
            {
                (client as IDisposable)?.Dispose();
            }
            catch (Exception)
            {
                // A client that declines to dispose is still collected; nothing to report.
            }
        }
    }

    private static string ReadConfigBaseUrl(Shape shape)
    {
        try
        {
            return shape.ConfigBaseUrl.GetValue(null) as string ?? string.Empty;
        }
        catch (Exception exception)
        {
            WarnOnce("could not read the configured lobby server: " +
                     exception.GetType().Name + ": " + exception.Message);
            return string.Empty;
        }
    }

    private static bool SameAddress(string? left, string? right) =>
        string.Equals(
            (left ?? string.Empty).Trim().TrimEnd('/'),
            (right ?? string.Empty).Trim().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    private static void WarnOnce(string message) => Entry.WarnOnce(ref _warned, "announcement-source", message);

    /// <summary>
    /// Every member this repair touches, resolved once in <see cref="Applies"/>. Metadata only: nothing is invoked
    /// while probing, and no Godot call is made anywhere in this file, because the smoke test runs without an engine.
    /// </summary>
    internal sealed class Shape
    {
        internal MethodInfo GetAnnouncements = null!;

        internal ConstructorInfo ApiClientConstructor = null!;

        internal PropertyInfo ConfigBaseUrl = null!;

        internal static Shape? TryCapture(Assembly host, List<string> problems)
        {
            Type? client = ReflectScope.HostType(host, ApiClientTypeName, problems);
            Type? config = ReflectScope.HostType(host, ConfigTypeName, problems);
            if (client == null || config == null)
            {
                return null;
            }

            var shape = new Shape
            {
                ConfigBaseUrl = RequireStringStatic(problems, config, ConfigBaseUrlPropertyName)!,
                GetAnnouncements = ReflectScope.Exact(
                    client,
                    GetAnnouncementsMethodName,
                    requireStatic: false,
                    new[] { "System.Threading.CancellationToken" },
                    null,
                    problems)!
            };

            ConstructorInfo? constructor = FindConstructor(client);
            if (constructor == null)
            {
                problems.Add(client.FullName +
                             "((string, string, string, HttpMessageHandler, Action<string>, bool)) (not found)");
            }
            else
            {
                shape.ApiClientConstructor = constructor;
            }

            return shape;
        }

        /// <summary>
        /// <c>LobbyApiClient(string, string?, string?, HttpMessageHandler?, Action&lt;string&gt;?, bool)</c>, matched by
        /// shape so parameter renames cannot drift the anchor.
        /// </summary>
        private static ConstructorInfo? FindConstructor(Type client)
        {
            return client
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.GetParameters().Length == 6)
                .FirstOrDefault(candidate =>
                {
                    ParameterInfo[] parameters = candidate.GetParameters();
                    return parameters[0].ParameterType == typeof(string)
                           && parameters[1].ParameterType == typeof(string)
                           && parameters[2].ParameterType == typeof(string)
                           && parameters[4].ParameterType == typeof(Action<string>)
                           && parameters[5].ParameterType == typeof(bool);
                });
        }

        private static PropertyInfo? RequireStringStatic(List<string> problems, Type owner, string name)
        {
            PropertyInfo? property = ReflectScope.StaticProperty(problems, owner, name);
            if (property != null && property.PropertyType != typeof(string))
            {
                problems.Add(name + " is " + property.PropertyType.FullName + ", expected System.String");
                return null;
            }

            return property;
        }
    }
}
