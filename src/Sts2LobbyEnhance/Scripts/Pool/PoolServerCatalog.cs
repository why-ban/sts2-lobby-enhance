using System.Collections;
using System.Diagnostics;
using System.Reflection;

namespace Sts2LobbyEnhance;

/// <summary>
/// Every host-mod member the server pool touches, resolved exactly once in Applies() and never again.
///
/// One table for all three pool repairs on purpose: a captured anchor and a logged miss have to name the same
/// member, otherwise "missing: X" from the refresh repair and "missing: X" from the join repair would describe
/// two different lookups. Metadata only - nothing here is invoked while probing, and no host type is named with
/// typeof.
///
/// Required members are looked up with ReflectScope so every miss is reported at once; the cosmetic ones (status
/// text, theme colours, the label factory) are optional, because losing them must cost a log line and not the
/// feature. Which of the two sets a given repair demands depends on PoolAnchorScope: the join repair never
/// touches _Process and must not be skipped because of it.
/// </summary>
internal sealed class PoolAnchors
{
    internal const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyOverlay";
    internal const string RuntimeTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyRuntime";
    internal const string ApiClientTypeName = "Sts2LanConnect.Scripts.LobbyApiClient";
    internal const string RoomTypeName = "Sts2LanConnect.Scripts.LobbyRoomSummary";
    internal const string BootstrapTypeName = "Sts2LanConnect.Scripts.LanConnectServerListBootstrap";
    internal const string EntryTypeName = "Sts2LanConnect.Scripts.ServerListEntry";
    internal const string ConfigTypeName = "Sts2LanConnect.Scripts.LanConnectConfig";
    internal const string BuildInfoTypeName = "Sts2LanConnect.Scripts.LanConnectBuildInfo";
    internal const string MetricsClientTypeName = "Sts2LanConnect.Scripts.LanConnectPeerMetricsClient";
    internal const string LeaseTypeName = "Sts2LanConnect.Scripts.LanConnectServerContextLease";
    internal const string ConstantsTypeName = "Sts2LanConnect.Scripts.LanConnectConstants";

    internal Type Overlay = null!;

    internal Type RoomType = null!;

    /// <summary>LanConnectLobbyOverlay.RefreshRoomsAsync(bool, bool, CancellationToken), LanConnectLobbyOverlay.cs:3362</summary>
    internal MethodInfo RefreshRooms = null!;

    /// <summary>LanConnectLobbyOverlay._Process(double), LanConnectLobbyOverlay.cs:725 - the only guaranteed main-thread tick</summary>
    internal MethodInfo Process = null!;

    /// <summary>LanConnectLobbyOverlay.BuildRoomMetaRow(LobbyRoomSummary), LanConnectLobbyOverlay.cs:3943</summary>
    internal MethodInfo BuildRoomMetaRow = null!;

    /// <summary>LanConnectLobbyOverlay.CreateRoomAsync(), LanConnectLobbyOverlay.cs:4401</summary>
    internal MethodInfo CreateRoom = null!;

    /// <summary>
    /// LanConnectLobbyOverlay.BeginJoinRoomAsync(LobbyRoomSummary, string, CancellationToken,
    /// LanConnectServerContextLease, string), LanConnectLobbyOverlay.cs:4731
    /// </summary>
    internal MethodInfo BeginJoin = null!;

    /// <summary>List of LobbyRoomSummary, field _rooms, LanConnectLobbyOverlay.cs:101</summary>
    internal FieldInfo Rooms = null!;

    /// <summary>LanConnectLobbyOverlay.RebuildRoomStage(), LanConnectLobbyOverlay.cs:3543</summary>
    internal MethodInfo RebuildRoomStage = null!;

    // ---- the pieces of RefreshRoomsAsync this repair now reproduces (:3362-3481) ----

    /// <summary>LanConnectLobbyOverlay.PersistSettings() (:3373)</summary>
    internal MethodInfo PersistSettings = null!;

    /// <summary>LanConnectLobbyOverlay.UpdateHealthIndicator() (:3427)</summary>
    internal MethodInfo UpdateHealthIndicator = null!;

    /// <summary>LanConnectLobbyOverlay.UpdatePageControls(int filteredCount) (:3428)</summary>
    internal MethodInfo UpdatePageControls = null!;

    /// <summary>LanConnectLobbyOverlay.RefreshSelectedRoomDetails() (:3429)</summary>
    internal MethodInfo RefreshSelectedRoomDetails = null!;

    /// <summary>LanConnectLobbyOverlay.GetFilteredRooms() (:3562)</summary>
    internal MethodInfo GetFilteredRooms = null!;

    /// <summary>LanConnectLobbyOverlay.GetSelectedRoom() (:3387)</summary>
    internal MethodInfo GetSelectedRoom = null!;

    /// <summary>LanConnectLobbyOverlay.AreRoomListsVisuallyEquivalent(IReadOnlyList, IReadOnlyList), static (:3419)</summary>
    internal MethodInfo AreRoomListsVisuallyEquivalent = null!;

    /// <summary>LanConnectLobbyOverlay.ApplyAnnouncements(IReadOnlyList of LobbyAnnouncementItem) (:3503)</summary>
    internal MethodInfo ApplyAnnouncements = null!;

    /// <summary>LanConnectLobbyOverlay.EnsureAnnouncementFallback() (:3406)</summary>
    internal MethodInfo EnsureAnnouncementFallback = null!;

    /// <summary>LobbyApiClient.CreateConfigured(), LanConnectLobbyApiClient.cs:52</summary>
    internal MethodInfo CreateConfiguredClient = null!;

    /// <summary>LobbyApiClient.GetAnnouncementsAsync(CancellationToken), LanConnectLobbyApiClient.cs:115</summary>
    internal MethodInfo GetAnnouncements = null!;

    /// <summary>LobbyApiClient.MeasureProbeRttAsync(CancellationToken), LanConnectLobbyApiClient.cs:121</summary>
    internal MethodInfo MeasureProbeRtt = null!;

    /// <summary>LanConnectLobbyOverlay._serverPickerOpen (:229 area), upstream's refresh short circuit (:3368)</summary>
    internal FieldInfo ServerPickerOpen = null!;

    /// <summary>LanConnectLobbyOverlay._actionInFlight, upstream's refresh short circuit (:3368)</summary>
    internal FieldInfo ActionInFlight = null!;

    /// <summary>LanConnectLobbyOverlay._timeUntilAutoRefresh (:3376)</summary>
    internal FieldInfo TimeUntilAutoRefresh = null!;

    /// <summary>LanConnectLobbyOverlay._lastLobbyRttMs (:3396, :3444)</summary>
    internal FieldInfo LastLobbyRttMs = null!;

    /// <summary>LanConnectLobbyOverlay._consecutiveRefreshFailures (:3397, :3443)</summary>
    internal FieldInfo ConsecutiveRefreshFailures = null!;

    /// <summary>LanConnectLobbyOverlay.RefreshFailureSwitchHint, appended to the failure status (:3454)</summary>
    internal FieldInfo RefreshFailureHint = null!;

    /// <summary>LanConnectConstants.LobbyRefreshIntervalSeconds (:3376), read as a constant so a bump follows upstream</summary>
    internal double RefreshIntervalSeconds;

    /// <summary>LanConnectConstants.LobbyRoomsPerPage (:50 area) - the page size ClampCurrentPage divides by</summary>
    internal int RoomsPerPage;

    /// <summary>LanConnectLobbyOverlay._currentPageIndex (:236) - saved and clamped around a rebuild</summary>
    internal FieldInfo? CurrentPageIndex;

    /// <summary>LobbyServiceException - the refresh failure branch is chosen by this type (:3441)</summary>
    internal Type LobbyServiceExceptionType = null!;

    internal ConstructorInfo ApiClientCtor = null!;

    /// <summary>LobbyApiClient.GetRoomsAsync(CancellationToken), LanConnectLobbyApiClient.cs:110</summary>
    internal MethodInfo GetRooms = null!;

    internal PropertyInfo RoomId = null!;

    internal PropertyInfo RoomVersion = null!;

    internal PropertyInfo RoomPlayers = null!;

    /// <summary>
    /// LanConnectServerListBootstrap.GatherInitialCandidates(), LanConnectServerListBootstrap.cs:45 - known-peers
    /// cache + bundled seeds + the pinned featured server, synchronous and network free
    /// </summary>
    internal MethodInfo GatherInitialCandidates = null!;

    internal PropertyInfo EntryAddress = null!;

    /// <summary>LanConnectConfig.LobbyServerBaseUrl, LanConnectConfig.cs:135</summary>
    internal PropertyInfo ConfigBaseUrl = null!;

    internal PropertyInfo? EntryDisplayName;

    internal PropertyInfo? EntryPinned;

    internal PropertyInfo? EntryRooms;

    internal PropertyInfo? EntryGuardStatus;

    /// <summary>LanConnectServerListBootstrap.GatherCloudflareCandidatesAsync(CancellationToken), LanConnectServerListBootstrap.cs:87</summary>
    internal MethodInfo? GatherCloudflareCandidates;

    /// <summary>LanConnectServerListBootstrap.MergeDiscoveredEntries(List, IEnumerable), LanConnectServerListBootstrap.cs:112</summary>
    internal MethodInfo? MergeDiscovered;

    /// <summary>LanConnectPeerMetricsClient.FetchSnapshotAsync(string, CancellationToken), LanConnectPeerMetricsClient.cs:19</summary>
    internal MethodInfo? FetchSnapshot;

    internal PropertyInfo? SnapshotMetrics;

    internal PropertyInfo? MetricsGuardStatus;

    internal PropertyInfo? MetricsDisplayName;

    internal PropertyInfo? MetricsRooms;

    internal PropertyInfo? ConfigReadToken;

    internal PropertyInfo? ConfigCreateToken;

    internal PropertyInfo? RuntimeInstance;

    internal PropertyInfo? RuntimeActiveRoomSession;

    /// <summary>LanConnectLobbyRuntime.HasActiveHostedRoom (:267) - upstream's create refusal at :4450</summary>
    internal PropertyInfo? RuntimeHasActiveHostedRoom;

    internal PropertyInfo? RuntimeSwitchInProgress;

    /// <summary>LanConnectLobbyRuntime.SwitchLobbyServerWithContextAsync(string, CancellationToken), LanConnectLobbyRuntime.cs:1876</summary>
    internal MethodInfo? SwitchWithContext;

    /// <summary>LanConnectLobbyRuntime.SwitchLobbyServerAsync(string, CancellationToken), LanConnectLobbyRuntime.cs:1862</summary>
    internal MethodInfo? SwitchPlain;

    /// <summary>LanConnectServerContextLease.Token - the per-switch cancellation token the join flow honours</summary>
    internal PropertyInfo? LeaseToken;

    /// <summary>LanConnectBuildInfo.GetGameVersion(), LanConnectBuildInfo.cs:25 - the local version the pool sorts by</summary>
    internal MethodInfo? GetGameVersion;

    // ---- protocol capability anchors, used to refuse a server that cannot host our profile ----

    /// <summary>LobbyApiClient.GetProbeAsync(CancellationToken), LanConnectLobbyApiClient.cs:142</summary>
    internal MethodInfo? GetProbe;

    /// <summary>LobbyProbeResponse.Capabilities, LanConnectLobbyModels.cs:127-132</summary>
    internal PropertyInfo? ProbeCapabilities;

    /// <summary>LobbyProbeCapabilities.SupportedProtocolProfiles, LanConnectLobbyModels.cs:138</summary>
    internal PropertyInfo? CapabilitiesProfiles;

    /// <summary>LobbyProbeCapabilities.LanProtocolMin, LanConnectLobbyModels.cs:140</summary>
    internal PropertyInfo? CapabilitiesLanMin;

    /// <summary>LobbyProbeCapabilities.LanProtocolMax, LanConnectLobbyModels.cs:142</summary>
    internal PropertyInfo? CapabilitiesLanMax;

    /// <summary>LobbyProbeCapabilities.TailV1MinimumClientVersion, LanConnectLobbyModels.cs:146</summary>
    internal PropertyInfo? CapabilitiesTailMinimumClient;

    /// <summary>LanConnectLobbyOverlay.GetSelectedCreateProtocolId() (:6664)</summary>
    internal MethodInfo? GetSelectedCreateProtocolId;

    /// <summary>LanConnectLobbyOverlay.ProtocolProfileFromCreateId(int), static (:6780 area)</summary>
    internal MethodInfo? ProtocolProfileFromCreateId;

    /// <summary>LanConnectProtocolProfileExtensions.ToCanonical(LanConnectProtocolProfile), static</summary>
    internal MethodInfo? ProfileToCanonical;

    /// <summary>LanConnectTailRuntimeSupport.IsAvailable, static - the ceiling of our own LAN protocol offer</summary>
    internal PropertyInfo? TailRuntimeAvailable;

    /// <summary>LanConnectConstants.TailLanProtocolVersion, the value LanConnectProtocolOffer.CreateCurrent() puts in our offer</summary>
    internal int TailLanProtocolVersion;

    /// <summary>LanConnectLobbyOverlay._refreshInFlight - upstream's own in-flight guard, read before we start a round.</summary>
    internal FieldInfo? RefreshInFlight;

    /// <summary>LanConnectLobbyOverlay._selectedRoomId (:228) - restored when upstream's single-server refresh drops it.</summary>
    internal FieldInfo? SelectedRoomId;

    internal MethodInfo? SetStatus;

    internal MethodInfo? UpdateActionButtons;

    internal MethodInfo? UpdateNetworkSummary;

    internal MethodInfo? CreateBodyLabel;

    internal PropertyInfo? TextMutedColor;

    /// <summary>LanConnectLobbyOverlay._serverBaseUrlInput (:141) - repainted after a switch, like upstream does</summary>
    internal FieldInfo? ServerBaseUrlInput;

    /// <summary>LanConnectLobbyOverlay._roomNameInput - the create dialog's name box, read for the cheap pre-check</summary>
    internal FieldInfo? RoomNameInput;

    /// <summary>LanConnectConfig.LobbyServerBaseUrlOverride (:155) - what the "HTTP override" box mirrors</summary>
    internal PropertyInfo? ConfigBaseUrlOverride;

    /// <summary>LobbyRoomSummary.SavedRun (:41) - a saved-run room may open a slot dialog and never join</summary>
    internal PropertyInfo? RoomSavedRun;

    /// <summary>MegaCrit.Sts2.Core.Saves.SaveManager.Instance (game type, decompiled 0.113:72)</summary>
    internal PropertyInfo? SaveManagerInstance;

    /// <summary>SaveManager.HasMultiplayerRunSave (game type, decompiled 0.113:114)</summary>
    internal PropertyInfo? SaveManagerHasMultiplayerRun;

    internal static PoolAnchors? TryCapture(Assembly host, PoolAnchorScope scope, out string reason)
    {
        var missing = new List<string>();
        var anchors = new PoolAnchors();

        anchors.Overlay = ReflectScope.HostType(host, OverlayTypeName, missing)!;
        anchors.RoomType = ReflectScope.HostType(host, RoomTypeName, missing)!;

        // Shared by all three repairs: a room list has to be readable per server and the room key has to be
        // extractable, otherwise there is no pool to speak of.
        anchors.ApiClientCtor = CaptureApiClientCtor(host, missing)!;
        anchors.GetRooms = ReflectScope.InstanceMethod(
            missing,
            ReflectScope.HostType(host, ApiClientTypeName, missing),
            "GetRoomsAsync",
            "System.Threading.CancellationToken")!;
        anchors.RoomId = ReflectScope.PropertyOf(anchors.RoomType, "RoomId", missing)!;
        anchors.RoomVersion = ReflectScope.PropertyOf(anchors.RoomType, "Version", missing)!;
        anchors.RoomPlayers = ReflectScope.PropertyOf(anchors.RoomType, "CurrentPlayers", missing)!;
        anchors.ConfigBaseUrl = ReflectScope.StaticProperty(
            missing,
            ReflectScope.HostType(host, ConfigTypeName, missing),
            "LobbyServerBaseUrl")!;
        anchors.GatherInitialCandidates = ReflectScope.StaticMethod(
            missing,
            ReflectScope.HostType(host, BootstrapTypeName, missing),
            "GatherInitialCandidates")!;
        anchors.EntryAddress = ReflectScope.PropertyOf(
            ReflectScope.HostType(host, EntryTypeName, missing) ?? typeof(object),
            "Address",
            missing)!;

        switch (scope)
        {
            case PoolAnchorScope.Refresh:
                anchors.RefreshRooms = ReflectScope.InstanceMethod(
                    missing,
                    anchors.Overlay,
                    "RefreshRoomsAsync",
                    "System.Boolean",
                    "System.Boolean",
                    "System.Threading.CancellationToken")!;
                anchors.Process = ReflectScope.InstanceMethod(missing, anchors.Overlay, "_Process", "System.Double")!;
                anchors.BuildRoomMetaRow = ReflectScope.InstanceMethod(
                    missing,
                    anchors.Overlay,
                    "BuildRoomMetaRow",
                    RoomTypeName)!;
                anchors.Rooms = ReflectScope.Field(missing, anchors.Overlay, "_rooms")!;
                anchors.RebuildRoomStage = ReflectScope.InstanceMethod(missing, anchors.Overlay, "RebuildRoomStage")!;
                anchors.CaptureRefreshPrologue(missing, host);
                break;

            case PoolAnchorScope.Join:
                anchors.BeginJoin = ReflectScope.InstanceMethod(
                    missing,
                    anchors.Overlay,
                    "BeginJoinRoomAsync",
                    RoomTypeName,
                    "System.String",
                    "System.Threading.CancellationToken",
                    LeaseTypeName,
                    "System.String")!;
                anchors.RequireSwitch(missing, host);
                break;

            case PoolAnchorScope.Create:
                anchors.CreateRoom = ReflectScope.InstanceMethod(missing, anchors.Overlay, "CreateRoomAsync")!;
                anchors.RequireSwitch(missing, host);
                anchors.CaptureProtocolAnchors(missing, host);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(scope));
        }

        // Everything below is allowed to be absent: every use is guarded at the call site, so a host-mod rename
        // costs the message text or the source-name column, not the pool.
        Type? bootstrap = ReflectScope.HostType(host, BootstrapTypeName, new List<string>());
        anchors.GatherCloudflareCandidates = ReflectScope.StaticMethod(
            new List<string>(),
            bootstrap,
            "GatherCloudflareCandidatesAsync",
            "System.Threading.CancellationToken");
        anchors.MergeDiscovered = FindMergeDiscovered(bootstrap);

        Type? entry = ReflectScope.HostType(host, EntryTypeName, new List<string>());
        anchors.EntryDisplayName = OptionalProperty(entry, "DisplayName");
        anchors.EntryPinned = OptionalProperty(entry, "IsPinned");
        anchors.EntryRooms = OptionalProperty(entry, "Rooms");
        anchors.EntryGuardStatus = OptionalProperty(entry, "CreateRoomGuardStatus");

        Type? metricsClient = ReflectScope.HostType(host, MetricsClientTypeName, new List<string>());
        anchors.FetchSnapshot = ReflectScope.StaticMethod(
            new List<string>(),
            metricsClient,
            "FetchSnapshotAsync",
            "System.String",
            "System.Threading.CancellationToken");
        Type? snapshot = metricsClient?.GetNestedType("PeerDiscoverySnapshot", BindingFlags.Public | BindingFlags.NonPublic);
        anchors.SnapshotMetrics = OptionalProperty(snapshot, "Metrics");
        Type? metrics = anchors.SnapshotMetrics?.PropertyType;
        anchors.MetricsGuardStatus = OptionalProperty(metrics, "CreateRoomGuardStatus");
        anchors.MetricsDisplayName = OptionalProperty(metrics, "DisplayName");
        anchors.MetricsRooms = OptionalProperty(metrics, "Rooms");

        Type? config = ReflectScope.HostType(host, ConfigTypeName, new List<string>());
        anchors.ConfigReadToken = ReflectScope.StaticProperty(new List<string>(), config, "LobbyReadAccessToken");
        anchors.ConfigCreateToken = ReflectScope.StaticProperty(new List<string>(), config, "LobbyCreateRoomToken");

        Type? runtime = ReflectScope.HostType(host, RuntimeTypeName, new List<string>());
        anchors.RuntimeInstance = ReflectScope.StaticProperty(new List<string>(), runtime, "Instance");
        anchors.RuntimeSwitchInProgress = OptionalProperty(runtime, "IsLobbyServerSwitchInProgress");
        anchors.RuntimeActiveRoomSession = OptionalProperty(runtime, "HasActiveRoomSession");
        anchors.RuntimeHasActiveHostedRoom = OptionalProperty(runtime, "HasActiveHostedRoom");
        anchors.LeaseToken = OptionalProperty(
            host.GetType(LeaseTypeName, throwOnError: false, ignoreCase: false),
            "Token");

        anchors.GetGameVersion = ReflectScope.StaticMethod(
            new List<string>(),
            ReflectScope.HostType(host, BuildInfoTypeName, new List<string>()),
            "GetGameVersion");

        anchors.RefreshInFlight = anchors.Overlay
            .GetField("_refreshInFlight", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        anchors.SelectedRoomId = anchors.Overlay
            .GetField("_selectedRoomId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        anchors.SetStatus = ReflectScope.InstanceMethod(new List<string>(), anchors.Overlay, "SetStatus", "System.String");
        anchors.UpdateActionButtons = ReflectScope.InstanceMethod(new List<string>(), anchors.Overlay, "UpdateActionButtons");
        anchors.UpdateNetworkSummary = ReflectScope.InstanceMethod(new List<string>(), anchors.Overlay, "UpdateNetworkSummary");
        anchors.CreateBodyLabel = ReflectScope.StaticMethod(new List<string>(), anchors.Overlay, "CreateBodyLabel", "System.String");
        anchors.TextMutedColor = ReflectScope.StaticProperty(new List<string>(), anchors.Overlay, "TextMutedColor");
        anchors.ServerBaseUrlInput = anchors.Overlay
            .GetField("_serverBaseUrlInput", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        anchors.RoomNameInput = anchors.Overlay
            .GetField("_roomNameInput", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        anchors.ConfigBaseUrlOverride = ReflectScope.StaticProperty(new List<string>(), config, "LobbyServerBaseUrlOverride");
        anchors.RoomSavedRun = OptionalProperty(anchors.RoomType, "SavedRun");

        // Game-side types are looked up by full name, so a game update costs this pre-check and not the repair.
        Type? saveManager = ReflectScope.FindType("MegaCrit.Sts2.Core.Saves.SaveManager");
        anchors.SaveManagerInstance = OptionalProperty(saveManager, "Instance");
        anchors.SaveManagerHasMultiplayerRun = saveManager?
            .GetProperty("HasMultiplayerRunSave", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // The return type is part of the contract, not a detail: the refresh prefix hands its own Task back through
        // Harmony's __result, so a Task the host mod no longer produces would silently start the original twice.
        if (anchors.RefreshRooms is { } refreshRooms && refreshRooms.ReturnType != typeof(Task<bool>))
        {
            missing.Add(OverlayTypeName + ".RefreshRoomsAsync -> " + refreshRooms.ReturnType.Name + " (expected Task of bool)");
        }

        if (anchors.BeginJoin is { } beginJoin && beginJoin.ReturnType != typeof(Task))
        {
            missing.Add(OverlayTypeName + ".BeginJoinRoomAsync -> " + beginJoin.ReturnType.Name + " (expected Task)");
        }

        if (anchors.CreateRoom is { } createRoom && createRoom.ReturnType != typeof(Task))
        {
            missing.Add(OverlayTypeName + ".CreateRoomAsync -> " + createRoom.ReturnType.Name + " (expected Task)");
        }

        // A room session is what makes a switch destructive (:1160-1163), so the create repair cannot decide
        // anything without it. The join repair only reads it as a "did the join land" probe.
        if (scope == PoolAnchorScope.Create && anchors.RuntimeActiveRoomSession == null)
        {
            missing.Add(RuntimeTypeName + ".HasActiveRoomSession");
        }

        // The refresh takeover reproduces the whole method, so the four members it shares with the other scopes
        // (kept optional there on purpose) are load bearing here.
        if (scope == PoolAnchorScope.Refresh)
        {
            if (anchors.RefreshInFlight == null)
            {
                missing.Add(OverlayTypeName + "._refreshInFlight");
            }

            if (anchors.SelectedRoomId == null)
            {
                missing.Add(OverlayTypeName + "._selectedRoomId");
            }

            if (anchors.SetStatus == null)
            {
                missing.Add(OverlayTypeName + ".SetStatus(string)");
            }

            if (anchors.UpdateActionButtons == null)
            {
                missing.Add(OverlayTypeName + ".UpdateActionButtons()");
            }
        }

        if (missing.Count > 0)
        {
            reason = "missing: " + string.Join(", ", missing);
            return null;
        }

        reason = string.Empty;
        return anchors;
    }

    /// <summary>Whether this capture can switch the lobby server at all.</summary>
    internal bool CanSwitch => (SwitchWithContext ?? SwitchPlain) != null && RuntimeInstance != null;

    /// <summary>
    /// LobbyApiClient(string baseUrl, string readAccessToken, string createRoomToken, HttpMessageHandler
    /// httpMessageHandler, Action of string diagnosticSink, bool disposeHttpMessageHandler), matched by parameter
    /// type rather than arity. The sink matters: the default is GD.Print (LanConnectLobbyApiClient.cs:37) and it
    /// is called on every request (:49, :124, :132), so a client built without a sink cannot be used from a
    /// headless harness and would flood the game log.
    /// </summary>
    /// <summary>
    /// The rest of RefreshRoomsAsync that this repair reproduces: the guard fields, the status/health updates and the
    /// current server's probe + announcement calls. Every one of them is required for the refresh scope, because a
    /// half-reproduced refresh is worse than no takeover at all.
    /// </summary>
    private void CaptureRefreshPrologue(List<string> missing, Assembly host)
    {
        PersistSettings = ReflectScope.InstanceMethod(missing, Overlay, "PersistSettings")!;
        UpdateHealthIndicator = ReflectScope.InstanceMethod(missing, Overlay, "UpdateHealthIndicator")!;
        UpdatePageControls = ReflectScope.InstanceMethod(missing, Overlay, "UpdatePageControls", "System.Int32")!;
        RefreshSelectedRoomDetails = ReflectScope.InstanceMethod(missing, Overlay, "RefreshSelectedRoomDetails")!;
        GetFilteredRooms = ReflectScope.InstanceMethod(missing, Overlay, "GetFilteredRooms")!;
        GetSelectedRoom = ReflectScope.InstanceMethod(missing, Overlay, "GetSelectedRoom")!;
        EnsureAnnouncementFallback = ReflectScope.InstanceMethod(missing, Overlay, "EnsureAnnouncementFallback")!;
        AreRoomListsVisuallyEquivalent = FindReadOnlyListMethod(Overlay, "AreRoomListsVisuallyEquivalent", requireStatic: true, "LobbyRoomSummary", missing)!;
        ApplyAnnouncements = FindReadOnlyListMethod(Overlay, "ApplyAnnouncements", requireStatic: false, "LobbyAnnouncementItem", missing)!;

        Type? apiClient = ReflectScope.HostType(host, ApiClientTypeName, missing);
        CreateConfiguredClient = ReflectScope.StaticMethod(missing, apiClient, "CreateConfigured")!;
        GetAnnouncements = ReflectScope.InstanceMethod(missing, apiClient, "GetAnnouncementsAsync", "System.Threading.CancellationToken")!;
        MeasureProbeRtt = ReflectScope.InstanceMethod(missing, apiClient, "MeasureProbeRttAsync", "System.Threading.CancellationToken")!;

        ServerPickerOpen = ReflectScope.Field(missing, Overlay, "_serverPickerOpen")!;
        ActionInFlight = ReflectScope.Field(missing, Overlay, "_actionInFlight")!;
        TimeUntilAutoRefresh = ReflectScope.Field(missing, Overlay, "_timeUntilAutoRefresh")!;
        LastLobbyRttMs = ReflectScope.Field(missing, Overlay, "_lastLobbyRttMs")!;
        ConsecutiveRefreshFailures = ReflectScope.Field(missing, Overlay, "_consecutiveRefreshFailures")!;
        RefreshFailureHint = ReflectScope.StaticField(missing, Overlay, "RefreshFailureSwitchHint")!;

        RefreshIntervalSeconds = ConstantDouble(host, ConstantsTypeName, "LobbyRefreshIntervalSeconds", missing);
        RoomsPerPage = ConstantInt(host, ConstantsTypeName, "LobbyRoomsPerPage", missing);
        CurrentPageIndex = OptionalField(Overlay, "_currentPageIndex");
        LobbyServiceExceptionType = ReflectScope.HostType(host, "Sts2LanConnect.Scripts.LobbyServiceException", missing)!;
    }

    /// <summary>
    /// What the create repair needs to answer "can this server host the profile we are about to create":
    /// the probe endpoint, the capability fields inside it, and the two local helpers that turn the dialog's
    /// selected protocol id into the canonical profile string the service will be asked for.
    /// </summary>
    private void CaptureProtocolAnchors(List<string> missing, Assembly host)
    {
        Type? apiClient = ReflectScope.HostType(host, ApiClientTypeName, missing);
        GetProbe = ReflectScope.InstanceMethod(missing, apiClient, "GetProbeAsync", "System.Threading.CancellationToken");

        Type? probeResponse = ReflectScope.HostType(host, "Sts2LanConnect.Scripts.LobbyProbeResponse", missing);
        ProbeCapabilities = ReflectScope.PropertyOf(probeResponse ?? typeof(object), "Capabilities", missing);
        Type? capabilities = ProbeCapabilities?.PropertyType;
        CapabilitiesProfiles = ReflectScope.PropertyOf(capabilities ?? typeof(object), "SupportedProtocolProfiles", missing);
        CapabilitiesLanMin = ReflectScope.PropertyOf(capabilities ?? typeof(object), "LanProtocolMin", missing);
        CapabilitiesLanMax = ReflectScope.PropertyOf(capabilities ?? typeof(object), "LanProtocolMax", missing);
        CapabilitiesTailMinimumClient = ReflectScope.PropertyOf(capabilities ?? typeof(object), "TailV1MinimumClientVersion", missing);

        GetSelectedCreateProtocolId = ReflectScope.InstanceMethod(missing, Overlay, "GetSelectedCreateProtocolId");
        ProtocolProfileFromCreateId = ReflectScope.StaticMethod(missing, Overlay, "ProtocolProfileFromCreateId", "System.Int32");

        Type? extensions = ReflectScope.HostType(host, "Sts2LanConnect.Scripts.LanConnectProtocolProfileExtensions", missing);
        ProfileToCanonical = extensions?
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => string.Equals(method.Name, "ToCanonical", StringComparison.Ordinal)
                                      && method.GetParameters().Length == 1);
        if (ProfileToCanonical == null)
        {
            missing.Add("LanConnectProtocolProfileExtensions.ToCanonical(LanConnectProtocolProfile)");
        }

        TailRuntimeAvailable = ReflectScope.StaticProperty(
            missing,
            ReflectScope.HostType(host, "Sts2LanConnect.Scripts.LanConnectTailRuntimeSupport", missing),
            "IsAvailable");
        TailLanProtocolVersion = ConstantInt(host, ConstantsTypeName, "TailLanProtocolVersion", missing);
    }

    /// <summary>
    /// Finds a method whose single/single-pair parameter is IReadOnlyList&lt;LobbyRoomSummary&gt; (or the announcement
    /// equivalent). Matched structurally rather than by a spelled-out type name: a generic parameter's FullName embeds
    /// the assembly identity, which would make the anchor depend on the host assembly's version.
    /// </summary>
    private static MethodInfo? FindReadOnlyListMethod(
        Type? owner,
        string name,
        bool requireStatic,
        string elementTypeName,
        List<string> missing)
    {
        MethodInfo? method = owner?
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
            .Where(candidate => candidate.IsStatic == requireStatic)
            .FirstOrDefault(candidate => candidate.GetParameters().Length is 1 or 2
                                         && candidate.GetParameters().All(parameter =>
                                             IsReadOnlyListOf(parameter.ParameterType, elementTypeName)));
        if (method == null)
        {
            missing.Add(OverlayTypeName + "." + name + "(" + (requireStatic ? "static, " : string.Empty) +
                        "IReadOnlyList of " + elementTypeName + ")");
        }

        return method;
    }

    private static bool IsReadOnlyListOf(Type parameterType, string elementTypeName) =>
        parameterType.IsGenericType
        && parameterType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
        && string.Equals(parameterType.GetGenericArguments()[0].Name, elementTypeName, StringComparison.Ordinal);

    private static double ConstantDouble(Assembly host, string typeName, string fieldName, List<string> missing)
    {
        object? value = Constant(host, typeName, fieldName, missing);
        return value is double number ? number : 0d;
    }

    private static int ConstantInt(Assembly host, string typeName, string fieldName, List<string> missing)
    {
        object? value = Constant(host, typeName, fieldName, missing);
        return value is int number ? number : 0;
    }

    private static object? Constant(Assembly host, string typeName, string fieldName, List<string> missing)
    {
        Type? type = ReflectScope.HostType(host, typeName, missing);
        FieldInfo? field = type?.GetField(fieldName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null)
        {
            missing.Add(typeName + "." + fieldName);
            return null;
        }

        return field.GetRawConstantValue();
    }

    private static ConstructorInfo? CaptureApiClientCtor(Assembly host, List<string> missing)
    {
        Type? client = ReflectScope.HostType(host, ApiClientTypeName, missing);
        if (client == null)
        {
            return null;
        }

        ConstructorInfo? ctor = client
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
            {
                ParameterInfo[] parameters = candidate.GetParameters();
                return parameters.Length == 6
                       && parameters[0].ParameterType == typeof(string)
                       && parameters[1].ParameterType == typeof(string)
                       && parameters[2].ParameterType == typeof(string)
                       && parameters[3].ParameterType == typeof(System.Net.Http.HttpMessageHandler)
                       && parameters[4].ParameterType == typeof(Action<string>)
                       && parameters[5].ParameterType == typeof(bool);
            });

        if (ctor == null)
        {
            missing.Add(ApiClientTypeName + " ctor(string, string, string, HttpMessageHandler, Action of string, bool)");
        }

        return ctor;
    }

    /// <summary>
    /// MergeDiscoveredEntries(List of ServerListEntry, IEnumerable of ServerListEntry). Matched by name and arity
    /// only: the generic arguments are host types, so spelling them out would cost the anchor on a namespace move
    /// for no gain.
    /// </summary>
    private static MethodInfo? FindMergeDiscovered(Type? bootstrap)
    {
        if (bootstrap == null)
        {
            return null;
        }

        MethodInfo[] matches = bootstrap
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => string.Equals(method.Name, "MergeDiscoveredEntries", StringComparison.Ordinal))
            .Where(method => method.GetParameters().Length == 2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static FieldInfo? OptionalField(Type? owner, string name) =>
        owner?.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static PropertyInfo? OptionalProperty(Type? owner, string name) =>
        owner?.GetProperty(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private void RequireSwitch(List<string> missing, Assembly host)
    {
        Type? runtime = ReflectScope.HostType(host, RuntimeTypeName, missing);
        if (runtime == null)
        {
            return;
        }

        RuntimeInstance = ReflectScope.StaticProperty(missing, runtime, "Instance");
        SwitchWithContext = ReflectScope.InstanceMethod(
            missing,
            runtime,
            "SwitchLobbyServerWithContextAsync",
            "System.String",
            "System.Threading.CancellationToken");
        SwitchPlain = ReflectScope.InstanceMethod(
            new List<string>(),
            runtime,
            "SwitchLobbyServerAsync",
            "System.String",
            "System.Threading.CancellationToken");
    }
}

internal enum PoolAnchorScope
{
    Refresh,

    Join,

    Create
}

/// <summary>One lobby server as the pool sees it: address, display name, measured latency, create guard.</summary>
internal sealed class PoolServer
{
    internal PoolServer(string baseUrl)
    {
        BaseUrl = baseUrl;
    }

    /// <summary>Normalized authority, no trailing slash. The pool's server key.</summary>
    internal string BaseUrl { get; }

    internal string? DisplayName { get; set; }

    internal bool IsPinned { get; set; }

    /// <summary>Round trip of the last GET /rooms (or /peers/metrics during a create probe), in milliseconds.</summary>
    internal int? LatencyMs { get; set; }

    internal string GuardStatus { get; set; } = "allow";

    internal int? RoomCount { get; set; }

    /// <summary>
    /// When /peers/metrics last answered. Deliberately NOT written by a /rooms fetch: the two probes answer
    /// different questions, and sharing one stamp is what let a failed room fetch masquerade as "probed a moment
    /// ago" and keep GuardStatus at the seed default "allow".
    /// </summary>
    internal DateTimeOffset ProbedAt { get; set; }

    /// <summary>Consecutive /rooms failures, for the cooldown backoff. Reset by the first success.</summary>
    internal int ConsecutiveFailures { get; set; }

    /// <summary>While this is in the future the server is skipped entirely: no request, no wait.</summary>
    internal DateTimeOffset CooldownUntil { get; set; }

    /// <summary>
    /// The rooms this server returned last time it answered. While it is cooling down those rows are still shown,
    /// marked stale, so one dead node cannot make its rooms vanish from the stage for minutes.
    /// </summary>
    internal IReadOnlyList<object>? LastRooms { get; set; }

    /// <summary>
    /// capabilities.supportedProtocolProfiles from GET /probe (LanConnectLobbyModels.cs:138), or null when the
    /// server did not answer. Null is not "supports everything": it means the server could not be asked, and the
    /// create repair treats it as unusable so a silent node can never be the one a room is published to.
    /// </summary>
    internal IReadOnlyList<string>? SupportedProfiles { get; set; }

    internal int? LanProtocolMin { get; set; }

    internal int? LanProtocolMax { get; set; }

    /// <summary>capabilities.tailV1MinimumClientVersion: the server knows the tail v1 generation exists.</summary>
    internal string? TailMinimumClientVersion { get; set; }

    internal DateTimeOffset CapabilitiesAt { get; set; }

    internal string ShortName(int max) => PoolAddress.ShortName(DisplayName, BaseUrl, max);
}

/// <summary>
/// The server directory: known-peers cache + bundled seeds + the Cloudflare discovery list, pinned entry
/// included, with latency and create-guard figures cached for ProbeTtlSeconds.
///
/// Everything here runs on the game's main thread inside a patch body. Discovery is never started from
/// Applies()/Apply() and never from a worker thread: the host mod's discovery helpers read its config and its
/// bundled defaults file, and this repair does not get to assume that is thread safe.
///
/// The latency cache exists for the reason the brief gives - the create-room choice must not make the player
/// wait on a dozen servers - so the catalog hands out what it already knows and only re-probes stale entries.
/// </summary>
internal static class PoolServerCatalog
{
    /// <summary>How long the discovered server list is trusted before discovery runs again.</summary>
    internal const int DiscoveryTtlSeconds = 300;

    /// <summary>How long a measured latency / create guard is trusted.</summary>
    internal const int ProbeTtlSeconds = 180;

    /// <summary>How long a server's protocol capabilities are trusted before /probe is asked again.</summary>
    internal const int CapabilityTtlSeconds = 300;

    /// <summary>The canonical tail profile (LanConnectProtocolProfileExtensions.TailCanonical).</summary>
    internal const string TailProfileCanonical = "tail_v1";

    /// <summary>Longest one discovery round may take before it gives up and tries again next tick.</summary>
    internal const int DiscoveryBudgetMs = 1500;

    private static readonly object Gate = new();

    private static List<PoolServer> _servers = new();

    /// <summary>When the seed list was last rebuilt. Separate from the discovery stamp on purpose: rebuilding the
    /// seeds must not make the catalog believe the Cloudflare round already happened, which would starve it forever.</summary>
    private static DateTimeOffset _builtAt = DateTimeOffset.MinValue;

    private static DateTimeOffset _discoveredAt = DateTimeOffset.MinValue;

    private static int _discoveryInFlight;

    /// <summary>
    /// The current server list, cheapest possible: the cache when it is fresh, otherwise the synchronous host-mod
    /// seeds. Never throws and never waits on the network, so the first paint can never be gated on discovery.
    /// </summary>
    internal static IReadOnlyList<PoolServer> GetServers(PoolAnchors anchors)
    {
        bool stale;
        lock (Gate)
        {
            stale = DateTimeOffset.UtcNow - _builtAt > TimeSpan.FromSeconds(DiscoveryTtlSeconds);
        }

        if (stale)
        {
            List<PoolServer>? seeded = BuildFromSeeds(anchors);
            if (seeded is { Count: > 0 })
            {
                // Merge, never replace: the seed list knows nothing about the Cloudflare-discovered servers, and a
                // wholesale swap dropped every one of them once per DiscoveryTtlSeconds.
                List<PoolServer> merged = seeded;
                lock (Gate)
                {
                    foreach (PoolServer known in _servers)
                    {
                        if (!merged.Exists(candidate => PoolAddress.Same(candidate.BaseUrl, known.BaseUrl)))
                        {
                            merged.Add(known);
                        }
                    }
                }

                Replace(merged, carryProbe: true, discovered: false);
            }
        }

        lock (Gate)
        {
            return _servers.ToArray();
        }
    }

    internal static bool NeedsDiscovery()
    {
        lock (Gate)
        {
            return DateTimeOffset.UtcNow - _discoveredAt > TimeSpan.FromSeconds(DiscoveryTtlSeconds);
        }
    }

    /// <summary>
    /// One Cloudflare aggregation round, merged into the cache for the following refreshes. Cancellation, a dead
    /// discovery endpoint and a missing anchor are all the same thing here: the cache keeps what it has.
    /// </summary>
    internal static async Task RefreshDiscoveryAsync(PoolAnchors anchors, CancellationToken cancellationToken)
    {
        if (anchors.GatherCloudflareCandidates == null || anchors.MergeDiscovered == null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _discoveryInFlight, 1) != 0)
        {
            return;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(DiscoveryBudgetMs);
        try
        {
            List<PoolServer> current;
            lock (Gate)
            {
                current = new List<PoolServer>(_servers);
            }

            if (current.Count == 0)
            {
                return;
            }

            object? invocation = anchors.GatherCloudflareCandidates.Invoke(null, new object?[] { budget.Token });
            if (invocation is not Task gather)
            {
                return;
            }

            await gather.ConfigureAwait(true);
            if (gather.GetType().GetProperty("Result")?.GetValue(gather) is not IEnumerable discovered)
            {
                return;
            }

            var additions = new List<PoolServer>();
            foreach (object? entry in discovered)
            {
                if (entry == null)
                {
                    continue;
                }

                string? normalized = PoolAddress.TryNormalize(anchors.EntryAddress.GetValue(entry) as string);
                if (normalized == null)
                {
                    continue;
                }

                additions.Add(new PoolServer(normalized)
                {
                    DisplayName = anchors.EntryDisplayName?.GetValue(entry) as string,
                    IsPinned = anchors.EntryPinned?.GetValue(entry) is true
                });
            }

            if (additions.Count == 0)
            {
                return;
            }

            var merged = new List<PoolServer>(current);
            foreach (PoolServer addition in additions)
            {
                if (!merged.Exists(existing => PoolAddress.Same(existing.BaseUrl, addition.BaseUrl)))
                {
                    merged.Add(addition);
                }
            }

            Replace(merged, carryProbe: true, discovered: true);
        }
        catch (OperationCanceledException)
        {
            // Leaving the cache alone is the whole recovery path.
        }
        catch (Exception exception)
        {
            PoolRefreshFix.WarnOnce(
                "discovery",
                "server discovery failed, the pool keeps the servers it already knew: " +
                exception.GetType().Name + ": " + exception.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _discoveryInFlight, 0);
        }
    }

    /// <summary>
    /// A /peers/metrics answer (or the absence of one). This is the create side's cache, and it is allowed to cache a
    /// failure: a node that does not publish metrics cannot be verified for hosting, so re-asking it on every click
    /// would only cost the dialog two seconds per try.
    /// </summary>
    internal static void RecordMetricsProbe(PoolServer server, int? latencyMs, string? displayName, string? guardStatus, int? rooms)
    {
        if (latencyMs is > 0)
        {
            server.LatencyMs = latencyMs;
        }

        if (!string.IsNullOrWhiteSpace(displayName))
        {
            server.DisplayName = displayName;
        }

        if (!string.IsNullOrWhiteSpace(guardStatus))
        {
            server.GuardStatus = guardStatus;
        }

        if (rooms is >= 0)
        {
            server.RoomCount = rooms;
        }

        server.ProbedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// The backoff ladder for a node that keeps failing, in seconds. A dead node used to cost the whole round its
    /// 2 s ceiling; the measured difference (lead research-pool-latency.md) is 2,013-2,110 ms versus 247-333 ms once
    /// the dead node is out of the round. The refresh cadence stays at the host mod's 8 s; only the nodes in cooldown
    /// are skipped, and they are retried as soon as their slot expires.
    /// </summary>
    private static readonly int[] CooldownSeconds = { 10, 30, 90, 300 };

    internal static bool IsCoolingDown(PoolServer server) => server.CooldownUntil > DateTimeOffset.UtcNow;

    internal static bool IsProbeFresh(PoolServer server) =>
        DateTimeOffset.UtcNow - server.ProbedAt <= TimeSpan.FromSeconds(ProbeTtlSeconds);

    /// <summary>
    /// A /rooms answer. This is the measurement the pool's sort uses and the only signal allowed to move the
    /// cooldown ladder: it is a real round trip to the endpoint the pool actually needs.
    /// </summary>
    internal static void RecordRoomsFetch(PoolServerFetch fetch)
    {
        PoolServer server = fetch.Server;
        if (fetch.Succeeded)
        {
            RegisterSuccess(server);
            if (fetch.LatencyMs is > 0 and < PoolServerFetch.UnknownLatency)
            {
                server.LatencyMs = fetch.LatencyMs;
            }

            server.LastRooms = fetch.Rooms;
            return;
        }

        // A failed fetch is not a measurement, and it is not evidence that the node can host: both stamps stay where
        // they were, the guard becomes unknown, and the node starts (or continues) its cooldown.
        server.GuardStatus = "unknown";
        RegisterFailure(server);
    }

    private static void RegisterSuccess(PoolServer server)
    {
        server.ConsecutiveFailures = 0;
        server.CooldownUntil = default;
    }

    private static void RegisterFailure(PoolServer server)
    {
        int failures = Math.Min(server.ConsecutiveFailures + 1, CooldownSeconds.Length);
        server.ConsecutiveFailures = failures;
        int seconds = CooldownSeconds[Math.Min(failures, CooldownSeconds.Length) - 1];
        server.CooldownUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
    }

    internal static bool IsCapabilityFresh(PoolServer server) =>
        server.CapabilitiesAt != default
        && DateTimeOffset.UtcNow - server.CapabilitiesAt <= TimeSpan.FromSeconds(CapabilityTtlSeconds);

    /// <summary>Anything the create decision reads is stale: latency, guard, or the protocol capabilities.</summary>
    internal static bool NeedsProbe(PoolServer server) => !IsProbeFresh(server) || !IsCapabilityFresh(server);

    internal static void RecordCapabilities(
        PoolServer server,
        IReadOnlyList<string>? profiles,
        int? lanProtocolMin,
        int? lanProtocolMax,
        string? tailMinimumClientVersion)
    {
        server.SupportedProfiles = profiles;
        server.LanProtocolMin = lanProtocolMin;
        server.LanProtocolMax = lanProtocolMax;
        server.TailMinimumClientVersion = tailMinimumClientVersion;
        server.CapabilitiesAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Whether this server can host the profile the create dialog is about to ask for.
    ///
    /// The service freezes the room's protocol from the client's offer and its own hardcoded range
    /// (lobby-service/src/store.ts:1315-1322, protocol-capabilities.ts:71-79: selected is
    /// min(offerMax, serverMax) and must reach max(offerMin, serverMin)), and the client then validates the frozen
    /// selection (LanConnectProtocolSelection.cs:54-100). A server whose advertised range cannot meet our offer
    /// therefore ends as "LAN 协议版本不匹配" after a successful create, which is exactly the failure this check
    /// exists to avoid. Unknown (never answered / stale) is treated as "cannot host", never as "probably fine".
    /// </summary>
    internal static bool SupportsProfile(PoolServer server, string canonicalProfile, int ourVersion)
    {
        if (!IsCapabilityFresh(server) || server.SupportedProfiles == null)
        {
            return false;
        }

        bool listed = server.SupportedProfiles.Any(profile =>
            string.Equals(profile, canonicalProfile, StringComparison.OrdinalIgnoreCase));
        if (!listed)
        {
            return false;
        }

        if (!string.Equals(canonicalProfile, TailProfileCanonical, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // tail v1 also has to survive the version handshake, and has to be a generation the server knows about.
        if (string.IsNullOrWhiteSpace(server.TailMinimumClientVersion))
        {
            return false;
        }

        if (ourVersion <= 0)
        {
            return true;
        }

        if (server.LanProtocolMin is not { } serverMin || server.LanProtocolMax is not { } serverMax)
        {
            return false;
        }

        int selected = Math.Min(ourVersion, serverMax);
        int commonMinimum = Math.Max(ourVersion, serverMin);
        return selected >= commonMinimum;
    }

    /// <summary>
    /// One server's live snapshot: latency, display name, room count and the create-room guard
    /// (LanConnectPeerMetricsClient.cs:19-23, which reads /peers/metrics and falls back to /probe). Never throws:
    /// a server that does not answer records its round trip and drops out of the create-room candidates because
    /// its guard status becomes unknown.
    /// </summary>
    internal static async Task ProbeAsync(
        PoolAnchors anchors,
        PoolServer server,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        // Both requests are independent and each has its own ceiling, so they run at the same time: the create
        // dialog should never wait for 4 s on a stale server just because two endpoints had to be asked in sequence.
        Task metrics = ProbeMetricsAsync(anchors, server, timeoutMs, cancellationToken);
        Task capabilities = ProbeCapabilitiesAsync(anchors, server, timeoutMs, cancellationToken);
        await Task.WhenAll(metrics, capabilities).ConfigureAwait(true);
    }

    /// <summary>
    /// capabilities from GET /probe: which canonical protocol profiles the server can host, the LAN protocol range it
    /// freezes rooms at, and whether it knows the tail v1 generation at all (LanConnectLobbyModels.cs:134-186,
    /// lobby-service/src/app.ts:484-512). Cached for CapabilityTtlSeconds; a server that does not answer is recorded
    /// as unusable rather than merely unknown.
    /// </summary>
    private static async Task ProbeCapabilitiesAsync(
        PoolAnchors anchors,
        PoolServer server,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (anchors.GetProbe == null || anchors.ProbeCapabilities == null)
        {
            return;
        }

        object? client = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);
            client = PoolRoomClient.CreateClient(anchors, server.BaseUrl);
            object? invocation = anchors.GetProbe.Invoke(client, new object?[] { cts.Token });
            if (invocation is not Task task)
            {
                RecordCapabilities(server, null, null, null, null);
                return;
            }

            await task.ConfigureAwait(true);
            object? response = task.GetType().GetProperty("Result")?.GetValue(task);
            object? capabilities = response == null ? null : anchors.ProbeCapabilities.GetValue(response);
            if (capabilities == null)
            {
                RecordCapabilities(server, null, null, null, null);
                return;
            }

            var profiles = new List<string>();
            if (anchors.CapabilitiesProfiles?.GetValue(capabilities) is IEnumerable declared)
            {
                foreach (object? profile in declared)
                {
                    if (profile is string text && !string.IsNullOrWhiteSpace(text))
                    {
                        profiles.Add(text.Trim());
                    }
                }
            }

            RecordCapabilities(
                server,
                profiles,
                anchors.CapabilitiesLanMin?.GetValue(capabilities) as int?,
                anchors.CapabilitiesLanMax?.GetValue(capabilities) as int?,
                anchors.CapabilitiesTailMinimumClient?.GetValue(capabilities) as string);
        }
        catch (OperationCanceledException)
        {
            RecordCapabilities(server, null, null, null, null);
        }
        catch (Exception exception)
        {
            RecordCapabilities(server, null, null, null, null);
            PoolRefreshFix.WarnOnce(
                "probe-capabilities",
                "the protocol capabilities of " + server.BaseUrl + " could not be read: " +
                PoolRoomClient.Describe(exception));
        }
        finally
        {
            (client as IDisposable)?.Dispose();
        }
    }

    private static async Task ProbeMetricsAsync(
        PoolAnchors anchors,
        PoolServer server,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (anchors.FetchSnapshot == null)
        {
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeoutMs);
            object? invocation = anchors.FetchSnapshot.Invoke(null, new object?[]
            {
                PoolAddress.ToBaseUrl(server.BaseUrl),
                cts.Token
            });
            if (invocation is not Task task)
            {
                return;
            }

            await task.ConfigureAwait(true);
            stopwatch.Stop();
            int latency = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue - 1);

            object? metrics = task.GetType().GetProperty("Result")?.GetValue(task) is { } snapshot
                ? anchors.SnapshotMetrics?.GetValue(snapshot)
                : null;
            if (metrics == null)
            {
                // /peers/metrics is optional upstream and its timeout is a normal answer, not an exception
                // (LanConnectPeerMetricsClient.cs:62-126 never throws). "No metrics" therefore means "cannot verify
                // the create guard", which is recorded as unknown rather than left at the seed default "allow".
                RecordMetricsProbe(server, latency, null, "unknown", null);
                PoolRefreshFix.WarnOnce(
                    "probe-metrics",
                    "at least one server does not publish /peers/metrics (" + server.BaseUrl +
                    "), so it cannot be verified for hosting");
                return;
            }

            RecordMetricsProbe(
                server,
                latency,
                anchors.MetricsDisplayName?.GetValue(metrics) as string,
                anchors.MetricsGuardStatus?.GetValue(metrics) as string,
                anchors.MetricsRooms?.GetValue(metrics) as int?);
        }
        catch (Exception exception)
        {
            // No OperationCanceledException branch: the snapshot call is a no-throw contract, so a cancellation and a
            // timeout both arrive as a null metrics payload above. Anything reaching here is a real defect.
            RecordMetricsProbe(server, null, null, "unknown", null);
            PoolRefreshFix.WarnOnce(
                "probe-metrics-exception",
                "probing " + server.BaseUrl + " failed: " + PoolRoomClient.Describe(exception));
        }
    }

    /// <summary>The server the game is currently pointed at, as the config reports it.</summary>
    internal static string CurrentBaseUrl(PoolAnchors anchors)
    {
        try
        {
            return anchors.ConfigBaseUrl.GetValue(null) as string ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Builds the pool's server list from the host mod's synchronous seeds. Never throws.</summary>
    private static List<PoolServer>? BuildFromSeeds(PoolAnchors anchors)
    {
        try
        {
            if (anchors.GatherInitialCandidates.Invoke(null, null) is not IEnumerable entries)
            {
                return null;
            }

            List<PoolServer> servers = ToServers(anchors, entries);
            _builtAt = DateTimeOffset.UtcNow;
            return servers;
        }
        catch (Exception exception)
        {
            PoolRefreshFix.WarnOnce(
                "seeds",
                "the host mod's server list could not be read, so the pool stays empty: " +
                exception.GetType().Name + ": " + exception.Message);
            return null;
        }
    }

    /// <summary>Copies the host mod's entries, carrying a previously measured latency across by address.</summary>
    private static List<PoolServer> ToServers(PoolAnchors anchors, IEnumerable entries)
    {
        Dictionary<string, PoolServer> known = new(StringComparer.OrdinalIgnoreCase);
        lock (Gate)
        {
            foreach (PoolServer existing in _servers)
            {
                known[existing.BaseUrl] = existing;
            }
        }

        var servers = new List<PoolServer>();
        foreach (object? entry in entries)
        {
            if (entry == null)
            {
                continue;
            }

            string? normalized = PoolAddress.TryNormalize(anchors.EntryAddress.GetValue(entry) as string);
            if (normalized == null || servers.Exists(server => PoolAddress.Same(server.BaseUrl, normalized)))
            {
                continue;
            }

            var server = new PoolServer(normalized)
            {
                DisplayName = anchors.EntryDisplayName?.GetValue(entry) as string,
                IsPinned = anchors.EntryPinned?.GetValue(entry) is true,
                RoomCount = anchors.EntryRooms?.GetValue(entry) as int?,
                GuardStatus = anchors.EntryGuardStatus?.GetValue(entry) as string ?? "allow"
            };

            if (known.TryGetValue(normalized, out PoolServer? previous))
            {
                server.DisplayName ??= previous.DisplayName;
                server.LatencyMs = previous.LatencyMs;
                server.ProbedAt = previous.ProbedAt;
                server.GuardStatus = previous.GuardStatus;
            }

            servers.Add(server);
        }

        return servers;
    }

    private static void Replace(List<PoolServer> servers, bool carryProbe, bool discovered)
    {
        if (carryProbe)
        {
            Dictionary<string, PoolServer> known = new(StringComparer.OrdinalIgnoreCase);
            lock (Gate)
            {
                foreach (PoolServer existing in _servers)
                {
                    known[existing.BaseUrl] = existing;
                }
            }

            foreach (PoolServer server in servers)
            {
                if (!known.TryGetValue(server.BaseUrl, out PoolServer? previous))
                {
                    continue;
                }

                server.LatencyMs ??= previous.LatencyMs;
                server.DisplayName ??= previous.DisplayName;
                if (server.ProbedAt == default)
                {
                    server.ProbedAt = previous.ProbedAt;
                    server.GuardStatus = previous.GuardStatus;
                }
            }
        }

        lock (Gate)
        {
            _servers = servers;
            _builtAt = DateTimeOffset.UtcNow;
            if (discovered)
            {
                _discoveredAt = DateTimeOffset.UtcNow;
            }
        }
    }
}

/// <summary>Address normalization, mirroring the host mod's LanConnectLobbyServerAddress rules by hand.</summary>
internal static class PoolAddress
{
    /// <summary>http(s)://host[:port] without a trailing slash, or null when the value is unusable.</summary>
    internal static string? TryNormalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    internal static bool Same(string? left, string? right) =>
        string.Equals(TryNormalize(left), TryNormalize(right), StringComparison.OrdinalIgnoreCase);

    internal static string ToBaseUrl(string authority) => authority.TrimEnd('/') + "/";

    /// <summary>
    /// The short name the pool prints for a room: the operator's display name when there is one, otherwise the
    /// host part of the address, truncated to max characters.
    /// </summary>
    internal static string ShortName(string? displayName, string authority, int max)
    {
        string candidate = displayName?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
        {
            candidate = Uri.TryCreate(authority, UriKind.Absolute, out Uri? uri) ? uri.Host : authority;
        }

        return candidate.Length <= max ? candidate : candidate[..max];
    }
}

/// <summary>
/// Game-version comparison for the pool's first sort key: leading v/V and surrounding whitespace are ignored, and
/// an unknown on either side is never a match.
/// </summary>
internal static class PoolVersion
{
    internal static string Normalize(string? version)
    {
        string trimmed = version?.Trim() ?? string.Empty;
        if (trimmed.Length > 0 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
        {
            trimmed = trimmed[1..].TrimStart();
        }

        return trimmed;
    }

    internal static bool Same(string? left, string? right)
    {
        string a = Normalize(left);
        string b = Normalize(right);
        return a.Length > 0 && b.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
