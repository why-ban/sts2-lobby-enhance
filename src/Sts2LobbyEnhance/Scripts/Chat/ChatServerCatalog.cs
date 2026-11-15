using System.Collections;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Sts2LobbyEnhance;

    /// <summary>
    /// One lobby server as this mod sees it: where to connect, what to call it, how far away it is, and whether
    /// its <c>/probe</c> advertises the public channel protocol this client speaks.
    /// </summary>
    /// <param name="Address">Normalized base URL, no trailing slash.</param>
    /// <param name="DisplayName">Operator name from the server list, empty when nobody reported one.</param>
    /// <param name="PingMs">Measured <c>/probe</c> round trip, -1 when the probe never answered.</param>
    /// <param name="ChatSupported">The probe advertised <c>capabilities.serverChatVersion == 1</c>.</param>
    /// <param name="IsCurrent">This is the lobby server the client is configured against.</param>
    /// <param name="IsFeatured">This is upstream's pinned server (<c>FeaturedServerAddress</c>).</param>
    internal sealed record CatalogServer(
        string Address,
        string DisplayName,
        int PingMs,
        bool ChatSupported,
        bool IsCurrent,
        bool IsFeatured);

    /// <summary>An immutable candidate list, rebuilt at most every <see cref="ChatServerCatalog.SnapshotLifetime"/>.</summary>
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
    /// Why the preflight exists at all: connecting to a lobby that does not advertise server chat
    /// <c>v1</c> makes the client call <c>MarkPermanentStop(Unsupported)</c> (<c>LanConnectServerChatClient.cs:181-189</c>)
    /// and the reconnect loop treats that exception as fatal and returns (<c>:1048-1059</c>), so one wrong
    /// target kills the public channel for the rest of the process. A two-second GET is cheap next to that.
    ///
    /// Everything here is plain .NET: no Godot call, no node. It runs on a worker thread while the panel keeps
    /// painting, which is why it may not touch anything the scene tree owns.
    /// </summary>
    internal static class ChatServerCatalog
    {
        internal const string BootstrapTypeName = "Sts2LanConnect.Scripts.LanConnectServerListBootstrap";

        internal const string EntryTypeName = "Sts2LanConnect.Scripts.ServerListEntry";

        private const string FeaturedFieldName = "FeaturedServerAddress";

        private const string GatherInitialName = "GatherInitialCandidates";

        private const string GatherCloudflareName = "GatherCloudflareCandidatesAsync";

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2d);

        private static readonly TimeSpan CloudflareTimeout = TimeSpan.FromSeconds(3d);

        /// <summary>
        /// How long a resolved display name is kept. <c>GatherInitialCandidates</c> rewrites the known-peers cache
        /// file (<c>LanConnectServerListBootstrap.cs:47-49</c>), so the name is asked for at most this often, not
        /// once per frame.
        /// </summary>
        private static readonly TimeSpan NameLifetime = TimeSpan.FromMinutes(5d);

        /// <summary>Probe answers and the built snapshot are both kept this long (spec: five minutes).</summary>
        internal static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(5d);

        private static readonly object Gate = new();

        private static readonly Dictionary<string, ProbeOutcome> Probes = new(StringComparer.OrdinalIgnoreCase);

        private static Handles? _handles;

        private static CatalogSnapshot? _snapshot;

        private static Task<CatalogSnapshot>? _building;

        /// <summary>Bumped by <see cref="Invalidate"/> so a build that started earlier cannot repopulate the cache.</summary>
        private static long _generation;

        /// <summary>True once <see cref="TryCapture"/> resolved every anchor this class reads.</summary>
        internal static bool Captured => _handles != null;

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
                    handles.GatherCloudflare = ReflectScope.MethodOf(bootstrap, GatherCloudflareName, 1, missing)!;

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
        /// <c>GatherInitialCandidates</c> reads <em>and rewrites</em> the known-peers cache file
        /// (<c>LanConnectServerListBootstrap.cs:45-85</c>), so it is called from the main thread only: the pool
        /// repair and the server picker both run it there, and a worker-thread call would race them on a file
        /// neither of us owns. The network half of the build stays off-thread.
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
        /// </summary>
        /// <param name="currentBaseUrl">The lobby server the client is configured against; always entry 0.</param>
        /// <param name="seeds">Main-thread output of <see cref="ReadInitialCandidates"/>.</param>
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
        /// One server's <c>/probe</c> answer, cached for <see cref="SnapshotLifetime"/>. Never throws: an
        /// unreachable server is an answer, not an error.
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
        /// The name shown in the panel and under an announcement: the operator name, else the address host.
        /// Longer than <paramref name="maxChars"/> is truncated with an ellipsis (one of the dots counts).
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
        /// candidate gatherer. Used by the announcement source line, which has no candidate walk of its own.
        /// </summary>
        /// <remarks>
        /// The gatherer reads and rewrites the known-peers cache, so the answer is memoised for a minute rather
        /// than being asked for on every announcement tick.
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
                using var client = new HttpClient { Timeout = ProbeTimeout };
                using HttpResponseMessage response = await client
                    .GetAsync(address + "/probe", HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
                stopwatch.Stop();

                if (!response.IsSuccessStatusCode)
                {
                    return new ProbeOutcome(true, false, (int)stopwatch.ElapsedMilliseconds, DateTimeOffset.UtcNow);
                }

                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
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
        /// Decides "does this server speak public channel v1" with exactly the test the client itself applies:
        /// <c>probe.Ok &amp;&amp; probe.Capabilities != null &amp;&amp; probe.Capabilities.SupportsTextServerChat</c>
        /// (<c>LanConnectServerChatClient.cs:181</c>, where <c>SupportsTextServerChat</c> is
        /// <c>ServerChatVersion == 1</c>, <c>LanConnectLobbyModels.cs:179</c>).
        ///
        /// The strictness is not pedantry. <c>LobbyProbeResponse.Ok</c> is a plain <c>bool</c> that defaults to
        /// false (<c>LanConnectLobbyModels.cs:127-132</c>), so a body without an <c>"ok"</c> field deserialises to
        /// "not ok" on the client. A lenient check here would let such a body through, the client would then call
        /// <c>MarkPermanentStop(Unsupported)</c> (<c>:183-188</c>), and the reconnect loop returns instead of
        /// retrying (<c>:1048-1059</c>) - one wrong switch kills the public channel for the rest of the process.
        /// Anything short of all three conditions is therefore "not supported".
        ///
        /// The body shape is <c>lobby-service/src/app.ts:484-512</c>.
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
