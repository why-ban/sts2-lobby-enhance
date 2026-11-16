using System.Collections;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Sts2LobbyEnhance;

    /// <summary>
    /// One lobby server as this mod sees it. <c>PingMs</c> is the <c>/probe</c> round trip in milliseconds, or -1
    /// when the probe never answered; <c>ChatSupported</c> means the probe advertised
    /// <c>capabilities.serverChatVersion == 1</c>.
    /// </summary>
    internal sealed record CatalogServer(
        string Address,
        string DisplayName,
        int PingMs,
        bool ChatSupported,
        bool IsCurrent,
        bool IsFeatured);

    /// <summary>A candidate list that is never mutated once built.</summary>
    internal sealed class CatalogSnapshot
    {
        internal CatalogSnapshot(List<CatalogServer> servers)
        {
            Servers = servers;
            BuiltAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Index 0 is always the current server; the rest are chat-capable, featured first, then by ping.</summary>
        internal IReadOnlyList<CatalogServer> Servers { get; }

        internal DateTimeOffset BuiltAt { get; }
    }

    /// <summary>
    /// The candidate server list the chat switch walks, plus the <c>/probe</c> answer it refuses to switch
    /// without.
    ///
    /// Why the preflight exists: connecting to a lobby that does not advertise server chat <c>v1</c> makes the client
    /// call <c>MarkPermanentStop(Unsupported)</c>, and the reconnect loop treats that as fatal and returns, so one
    /// wrong target kills the public channel for the rest of the process. A two-second GET is cheap next to that.
    ///
    /// Everything here is plain .NET, no Godot call and no node: it runs on a worker thread while the panel keeps
    /// painting, so it may not touch anything the scene tree owns.
    /// </summary>
    internal static class ChatServerCatalog
    {
        internal const string BootstrapTypeName = "Sts2LanConnect.Scripts.LanConnectServerListBootstrap";

        internal const string EntryTypeName = "Sts2LanConnect.Scripts.ServerListEntry";

        private const string FeaturedFieldName = "FeaturedServerAddress";

        private const string GatherInitialName = "GatherInitialCandidates";

        private const string GatherCloudflareName = "GatherCloudflareCandidatesAsync";

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2d);

        /// <summary>
        /// One connection pool for every probe. A client per call builds a fresh handler, throws its pool away and
        /// leaves the TCP connections in TIME_WAIT; the per-request ceiling is applied with a token instead.
        /// </summary>
        private static readonly HttpClient ProbeClient = new() { Timeout = Timeout.InfiniteTimeSpan };

        private static readonly TimeSpan CloudflareTimeout = TimeSpan.FromSeconds(3d);

        /// <summary>
        /// How long a resolved display name is kept. <c>GatherInitialCandidates</c> rewrites the known-peers cache
        /// file, so the name is asked for at most this often instead of on every frame.
        /// </summary>
        private static readonly TimeSpan NameLifetime = TimeSpan.FromMinutes(5d);

        /// <summary>Probe answers and the built snapshot are both kept this long.</summary>
        internal static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(5d);

        private static readonly object Gate = new();

        private static readonly Dictionary<string, ProbeOutcome> Probes = new(StringComparer.OrdinalIgnoreCase);

        private static Handles? _handles;

        private static CatalogSnapshot? _snapshot;

        private static Task<CatalogSnapshot>? _building;

        /// <summary>Bumped by <see cref="Invalidate"/> so a build that started earlier cannot repopulate the cache.</summary>
        private static long _generation;

        /// <summary>Every anchor this class reads, resolved once and never again.</summary>
        internal sealed class Handles
        {
            internal MethodInfo GatherInitial = null!;

            internal MethodInfo GatherCloudflare = null!;

            internal FieldInfo Featured = null!;

            internal PropertyInfo EntryAddress = null!;

            internal PropertyInfo EntryDisplayName = null!;
        }

        /// <summary>
        /// Resolves the anchors and reports every miss in <paramref name="problems"/>. Safe to call from more
        /// than one repair: the first successful capture wins and later calls are no-ops.
        /// </summary>
        internal static bool TryCapture(Assembly host, List<string> problems)
        {
            lock (Gate)
            {
                if (_handles != null)
                {
                    return true;
                }

                var missing = new List<string>();
                var handles = new Handles();

                Type? bootstrap = ReflectScope.HostType(host, BootstrapTypeName, missing);
                Type? entry = ReflectScope.HostType(host, EntryTypeName, missing);

                if (bootstrap != null)
                {
                    handles.GatherInitial = ReflectScope.MethodOf(bootstrap, GatherInitialName, 0, missing)!;
                    // Bound by parameter type, not arity: a same-arity overload with a different parameter would
                    // otherwise be accepted and only throw when the candidate list is built.
                    handles.GatherCloudflare = ReflectScope.Exact(
                        bootstrap,
                        GatherCloudflareName,
                        requireStatic: true,
                        ["System.Threading.CancellationToken"],
                        null,
                        missing)!;

                    // public const string: a literal field, so GetRawConstantValue is the safe read.
                    FieldInfo? featured = bootstrap.GetField(
                        FeaturedFieldName,
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (featured == null)
                    {
                        missing.Add(BootstrapTypeName + "." + FeaturedFieldName);
                    }
                    else
                    {
                        handles.Featured = featured;
                    }
                }

                if (entry != null)
                {
                    handles.EntryAddress = ReflectScope.PropertyOf(entry, "Address", missing)!;
                    handles.EntryDisplayName = ReflectScope.PropertyOf(entry, "DisplayName", missing)!;
                }

                if (missing.Count > 0)
                {
                    problems.AddRange(missing);
                    return false;
                }

                _handles = handles;
                return true;
            }
        }

        /// <summary>
        /// The display name the directory has for one address, if it has one.
        ///
        /// This is the label's first source: the catalogue is where the operator names arrive from (cache, CF
        /// aggregation, <c>/peers/metrics</c>), so asking it beats trusting the name a walk happened to carry.
        /// When the address is not in the current snapshot the snapshot is rebuilt - once per
        /// <see cref="RebuildThrottle"/>, never per frame - because a missing address usually means the snapshot
        /// predates the server appearing in the CF list.
        /// </summary>
        /// <param name="address">Any form of the address; it is normalised before comparing.</param>
        /// <param name="currentBaseUrl">Only needed to rebuild; pass the configured lobby url.</param>
        /// <param name="displayName">The name, or empty when the directory has none (or does not know it yet).</param>
        internal static bool TryLookupDisplayName(string address, string currentBaseUrl, out string displayName)
        {
            displayName = string.Empty;
            string key = Normalize(address);
            if (key.Length == 0 || _handles == null)
            {
                return false;
            }

            CatalogSnapshot? snapshot;
            lock (Gate)
            {
                snapshot = _snapshot;
            }

            if (snapshot != null)
            {
                foreach (CatalogServer server in snapshot.Servers)
                {
                    if (!string.Equals(Normalize(server.Address), key, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    displayName = (server.DisplayName ?? string.Empty).Trim();
                    return displayName.Length > 0;
                }
            }

            // Not in the directory we have: ask for a fresh one (throttled), and let the caller fall back for now.
            RequestRebuild(currentBaseUrl);
            return false;
        }

        /// <summary>At most one snapshot rebuild per this window, however often a label asks.</summary>
        internal static readonly TimeSpan RebuildThrottle = TimeSpan.FromSeconds(30d);

        private static DateTimeOffset _lastRebuildRequest = DateTimeOffset.MinValue;

        /// <summary>
        /// Drops the cached snapshot and rebuilds it in the background. Throttled so a per-frame label cannot turn
        /// a missing name into a request storm.
        /// </summary>
        internal static void RequestRebuild(string currentBaseUrl)
        {
            lock (Gate)
            {
                if (DateTimeOffset.UtcNow - _lastRebuildRequest < RebuildThrottle)
                {
                    return;
                }

                _lastRebuildRequest = DateTimeOffset.UtcNow;
                _snapshot = null;
            }

            List<(string Address, string Name)> seeds = ReadInitialCandidates();
            _ = Task.Run(async () =>
            {
                try
                {
                    await GetSnapshotAsync(currentBaseUrl, seeds).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The next walk retries; a label that stays on the host name is not worth an error line.
                }
            });
        }

        /// <summary>
        /// Upstream's pinned server (<c>LanConnectServerListBootstrap.FeaturedServerAddress</c>), which the chat row's
        /// reset button returns to. Empty when the anchor was never captured.
        /// </summary>
        internal static string FeaturedAddress
        {
            get
            {
                Handles? handles = _handles;
                return handles == null ? string.Empty : ReadFeatured(handles);
            }
        }

        /// <summary>Drops the cached candidate list so the next call rebuilds it (the configured server changed).</summary>
        internal static void Invalidate()
        {
            lock (Gate)
            {
                _generation++;
                _snapshot = null;
                _building = null;
                _nameCache = null;
                Probes.Clear();
            }
        }

        /// <summary>
        /// The cached/seed half of the candidate list, read on the calling thread.
        /// </summary>
        /// <remarks>
        /// <c>GatherInitialCandidates</c> reads <em>and rewrites</em> the known-peers cache file, so it is called
        /// from the main thread only: the pool repair and the server picker both run it there, and a worker-thread
        /// call would race them on a file neither of us owns. The network half of the build stays off-thread.
        /// </remarks>
        internal static List<(string Address, string Name)> ReadInitialCandidates()
        {
            Handles? handles = _handles;
            if (handles == null)
            {
                return new List<(string Address, string Name)>();
            }

            try
            {
                return ReadEntries(handles, handles.GatherInitial.Invoke(null, null));
            }
            catch (Exception exception)
            {
                Entry.Warn(
                    "chat-server-switch: reading the cached/seed server list failed, falling back to the " +
                    $"configured server only: {exception.GetType().Name}: {exception.Message}");
                return new List<(string Address, string Name)>();
            }
        }

        /// <summary>
        /// Returns the candidate list, building it on a worker thread when the cache is cold or older than
        /// <see cref="SnapshotLifetime"/>. Returns null when the anchors were never captured.
        /// <paramref name="currentBaseUrl"/> is the configured lobby server and always ends up as entry 0;
        /// <paramref name="seeds"/> has to come from <see cref="ReadInitialCandidates"/> on the main thread.
        /// </summary>
        internal static async Task<CatalogSnapshot?> GetSnapshotAsync(
            string currentBaseUrl,
            List<(string Address, string Name)> seeds)
        {
            Handles? handles = _handles;
            if (handles == null)
            {
                return null;
            }

            Task<CatalogSnapshot> task;
            long generation;
            lock (Gate)
            {
                if (_snapshot is { } cached && DateTimeOffset.UtcNow - cached.BuiltAt < SnapshotLifetime)
                {
                    return cached;
                }

                generation = _generation;
                _building ??= Task.Run(() => BuildAsync(handles, currentBaseUrl, seeds));
                task = _building;
            }

            CatalogSnapshot? built = null;
            try
            {
                built = await task.ConfigureAwait(false);
            }
            finally
            {
                lock (Gate)
                {
                    if (ReferenceEquals(_building, task))
                    {
                        _building = null;
                    }

                    // A build started before Invalidate() describes a server list we no longer trust.
                    if (built != null && generation == _generation)
                    {
                        _snapshot = built;
                    }
                }
            }

            return built;
        }

        /// <summary>
        /// One server's <c>/probe</c> answer, cached for <see cref="SnapshotLifetime"/>. An unreachable server is an
        /// answer, not an error.
        /// </summary>
        internal static async Task<ProbeOutcome> ProbeAsync(string address)
        {
            string key = Normalize(address);
            if (key.Length == 0)
            {
                return ProbeOutcome.Unreachable;
            }

            lock (Gate)
            {
                if (Probes.TryGetValue(key, out ProbeOutcome cached) &&
                    DateTimeOffset.UtcNow - cached.At < SnapshotLifetime)
                {
                    return cached;
                }
            }

            ProbeOutcome outcome = await ProbeCoreAsync(key).ConfigureAwait(false);
            lock (Gate)
            {
                Probes[key] = outcome;
            }

            return outcome;
        }

        /// <summary>
        /// Remembers that a server does not speak server chat, so the next walk skips it instead of paying for
        /// the same preflight again.
        /// </summary>
        internal static void MarkUnsupported(string address)
        {
            string key = Normalize(address);
            if (key.Length == 0)
            {
                return;
            }

            lock (Gate)
            {
                Probes[key] = new ProbeOutcome(Reachable: true, Supported: false, Milliseconds: -1, At: DateTimeOffset.UtcNow);
            }
        }

        /// <summary>
        /// The name shown in the panel: the operator name, else the address host. Longer than
        /// <paramref name="maxChars"/> is truncated with an ellipsis (one of the dots counts).
        /// </summary>
        internal static string ShortName(string? displayName, string? address, int maxChars)
        {
            string candidate = (displayName ?? string.Empty).Trim();
            if (candidate.Length == 0)
            {
                candidate = HostOf(address);
            }

            if (candidate.Length == 0)
            {
                candidate = "未知服务器";
            }

            if (maxChars <= 1 || candidate.Length <= maxChars)
            {
                return candidate;
            }

            return candidate[..(maxChars - 1)] + "…";
        }

        /// <summary>Host part of an address that may or may not carry a scheme; empty when unparseable.</summary>
        internal static string HostOf(string? address)
        {
            string trimmed = (address ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            if (!trimmed.Contains("://", StringComparison.Ordinal))
            {
                trimmed = "http://" + trimmed;
            }

            return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) ? uri.Host : string.Empty;
        }

        /// <summary>Trimmed, trailing-slash-free form used as the dictionary key and the identity of a server.</summary>
        internal static string Normalize(string? address) => (address ?? string.Empty).Trim().TrimEnd('/');

        /// <summary>
        /// Best-effort display name for the server the client is configured against, read from upstream's own
        /// candidate gatherer. Used for the chat panel's title.
        /// </summary>
        /// <remarks>
        /// The gatherer reads and rewrites the known-peers cache, so the answer is memoised for
        /// <see cref="NameLifetime"/> instead of being asked for on every frame.
        /// </remarks>
        internal static bool TryResolveDisplayName(string baseUrl, out string displayName)
        {
            displayName = string.Empty;
            string current = Normalize(baseUrl);
            if (current.Length == 0)
            {
                return false;
            }

            lock (Gate)
            {
                if (_nameCache is { } cached && string.Equals(cached.Address, current, StringComparison.OrdinalIgnoreCase) &&
                    DateTimeOffset.UtcNow - cached.At < NameLifetime)
                {
                    displayName = cached.Name;
                    return displayName.Length > 0;
                }
            }

            string resolved = string.Empty;
            foreach ((string address, string name) in ReadInitialCandidates())
            {
                if (!string.Equals(address, current, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                resolved = name;
                break;
            }

            lock (Gate)
            {
                _nameCache = new NameCache(current, resolved, DateTimeOffset.UtcNow);
            }

            displayName = resolved;
            return displayName.Length > 0;
        }

        private sealed record NameCache(string Address, string Name, DateTimeOffset At);

        private static NameCache? _nameCache;

        private static async Task<CatalogSnapshot> BuildAsync(
            Handles handles,
            string currentBaseUrl,
            List<(string Address, string Name)> seeds)
        {
            string current = Normalize(currentBaseUrl);
            string featured = ReadFeatured(handles);
            var candidates = new List<(string Address, string Name)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Take(string? address, string? name)
            {
                string normalized = Normalize(address);
                if (normalized.Length == 0 || !seen.Add(normalized))
                {
                    return;
                }

                candidates.Add((normalized, (name ?? string.Empty).Trim()));
            }

            foreach ((string address, string name) in seeds)
            {
                Take(address, name);
            }

            foreach ((string address, string name) in await ReadCloudflareCandidatesAsync(handles).ConfigureAwait(false))
            {
                Take(address, name);
            }

            if (featured.Length > 0)
            {
                Take(featured, null);
            }

            Dictionary<string, ProbeOutcome> probes = await ProbeManyAsync(candidates.Select(entry => entry.Address))
                .ConfigureAwait(false);

            var servers = new List<CatalogServer>(candidates.Count);
            foreach ((string address, string name) in candidates)
            {
                probes.TryGetValue(address, out ProbeOutcome probe);
                servers.Add(new CatalogServer(
                    address,
                    name,
                    probe.Milliseconds,
                    probe.Supported,
                    string.Equals(address, current, StringComparison.OrdinalIgnoreCase),
                    string.Equals(address, Normalize(featured), StringComparison.OrdinalIgnoreCase)));
            }

            // Index 0 is the current server even when it does not advertise chat: that is the "本服" entry the
            // fallback returns to, and dropping it would leave the panel unable to describe where it points.
            var ordered = new List<CatalogServer>(servers.Count);
            CatalogServer? currentEntry = servers.FirstOrDefault(server => server.IsCurrent);
            if (currentEntry != null)
            {
                ordered.Add(currentEntry);
            }
            else if (current.Length > 0)
            {
                ordered.Add(new CatalogServer(current, string.Empty, -1, false, true, false));
            }

            ordered.AddRange(servers
                .Where(server => !server.IsCurrent && server.ChatSupported)
                .OrderByDescending(server => server.IsFeatured)
                .ThenBy(server => server.PingMs < 0 ? int.MaxValue : server.PingMs)
                .ThenBy(server => server.Address, StringComparer.OrdinalIgnoreCase));

            return new CatalogSnapshot(ordered);
        }

        private static async Task<Dictionary<string, ProbeOutcome>> ProbeManyAsync(IEnumerable<string> addresses)
        {
            var results = new Dictionary<string, ProbeOutcome>(StringComparer.OrdinalIgnoreCase);
            List<string> list = addresses.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0)
            {
                return results;
            }

            using var gate = new SemaphoreSlim(8, 8);
            Task<(string Address, ProbeOutcome Outcome)>[] tasks = list
                .Select(async address =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        return (address, await ProbeAsync(address).ConfigureAwait(false));
                    }
                    finally
                    {
                        gate.Release();
                    }
                })
                .ToArray();

            foreach ((string address, ProbeOutcome outcome) in await Task.WhenAll(tasks).ConfigureAwait(false))
            {
                results[address] = outcome;
            }

            return results;
        }

        private static List<(string Address, string Name)> ReadEntries(Handles handles, object? entries)
        {
            var sink = new List<(string Address, string Name)>();
            if (entries is not IEnumerable enumerable)
            {
                return sink;
            }

            foreach (object? entry in enumerable)
            {
                if (entry == null)
                {
                    continue;
                }

                string address = handles.EntryAddress.GetValue(entry) as string ?? string.Empty;
                string name = (handles.EntryDisplayName.GetValue(entry) as string ?? string.Empty).Trim();
                sink.Add((address, name));
            }

            return sink;
        }

        private static async Task<List<(string Address, string Name)>> ReadCloudflareCandidatesAsync(Handles handles)
        {
            var sink = new List<(string Address, string Name)>();
            try
            {
                object? returned = handles.GatherCloudflare.Invoke(null, new object?[] { CancellationToken.None });
                if (returned is not Task task)
                {
                    return sink;
                }

                // The discovery worker is a convenience, never a gate: when it is slow the cached and seed
                // entries are already enough to offer a switch, so this wait stays short.
                Task finished = await Task.WhenAny(task, Task.Delay(CloudflareTimeout)).ConfigureAwait(false);
                if (!ReferenceEquals(finished, task))
                {
                    return sink;
                }

                object? entries = task.GetType().GetProperty("Result")?.GetValue(task);
                return ReadEntries(handles, entries);
            }
            catch (Exception)
            {
                // A failing discovery round is not an error the user needs to hear about; the cached/seed list
                // is what the switch uses anyway.
                return sink;
            }
        }

        private static string ReadFeatured(Handles handles)
        {
            try
            {
                object? value = handles.Featured.IsLiteral
                    ? handles.Featured.GetRawConstantValue()
                    : handles.Featured.GetValue(null);
                return Normalize(value as string);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static async Task<ProbeOutcome> ProbeCoreAsync(string address)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var budget = new CancellationTokenSource(ProbeTimeout);
                using HttpResponseMessage response = await ProbeClient
                    .GetAsync(address + "/probe", HttpCompletionOption.ResponseHeadersRead, budget.Token)
                    .ConfigureAwait(false);
                stopwatch.Stop();

                if (!response.IsSuccessStatusCode)
                {
                    return new ProbeOutcome(true, false, (int)stopwatch.ElapsedMilliseconds, DateTimeOffset.UtcNow);
                }

                string body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
                return new ProbeOutcome(
                    Reachable: true,
                    Supported: ReadServerChatSupported(body),
                    Milliseconds: (int)stopwatch.ElapsedMilliseconds,
                    At: DateTimeOffset.UtcNow);
            }
            catch (Exception)
            {
                return ProbeOutcome.Unreachable;
            }
        }

        /// <summary>
        /// Decides "does this server speak public channel v1" by the same three conditions the client applies:
        /// <c>probe.Ok &amp;&amp; probe.Capabilities != null &amp;&amp; probe.Capabilities.SupportsTextServerChat</c>,
        /// where the last one is <c>ServerChatVersion == 1</c>.
        ///
        /// The strictness is not pedantry. <c>LobbyProbeResponse.Ok</c> is a plain <c>bool</c> that defaults to
        /// false, so a body without an <c>"ok"</c> field deserialises to "not ok" on the client; a lenient check here
        /// would let such a body through, the client would then call <c>MarkPermanentStop(Unsupported)</c>, and the
        /// reconnect loop returns instead of retrying. Anything short of all three conditions is therefore "not
        /// supported".
        ///
        /// Property names are matched case-sensitively against the camelCase body the service sends, with a Pascal
        /// spelling accepted for the version field.
        /// </summary>
        private static bool ReadServerChatSupported(string body)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                // A missing "ok" reads as false on the client, so it must read as false here.
                if (!root.TryGetProperty("ok", out JsonElement ok) || ok.ValueKind != JsonValueKind.True)
                {
                    return false;
                }

                if (!root.TryGetProperty("capabilities", out JsonElement capabilities) ||
                    capabilities.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                foreach (string name in new[] { "serverChatVersion", "ServerChatVersion" })
                {
                    if (capabilities.TryGetProperty(name, out JsonElement value) &&
                        value.ValueKind == JsonValueKind.Number &&
                        value.TryGetInt32(out int version))
                    {
                        return version == 1;
                    }
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>What one <c>/probe</c> said, as the switch needs it.</summary>
    internal readonly record struct ProbeOutcome(bool Reachable, bool Supported, int Milliseconds, DateTimeOffset At)
    {
        internal static ProbeOutcome Unreachable =>
            new(Reachable: false, Supported: false, Milliseconds: -1, At: DateTimeOffset.UtcNow);
    }
