using System.Collections;
using System.Diagnostics;
using System.Reflection;

namespace Sts2LobbyEnhance;

/// <summary>
/// Every host-mod member the server pool touches, resolved once in Applies() and never again.
///
/// One table for all three pool repairs on purpose: a captured anchor and a logged miss have to name the same
/// member, otherwise "missing: X" from the refresh repair and "missing: X" from the join repair describe two
/// different lookups. Metadata only: nothing here is invoked while capturing, and no host type is named with
/// typeof.
///
/// A required miss skips the repair. An optional one costs a log line and the part of the pool that reads it,
/// which is why those are captured into a list instead of a throwaway: a member that stopped resolving and a
/// member nobody needs look identical from the outside. PoolAnchorScope decides which set a repair demands, so
/// the join repair does not fail because the refresh repair's fields are gone.
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
    internal const string UiTextTypeName = "Sts2LanConnect.Scripts.LanConnectUiText";

    internal Type Overlay = null!;

    internal Type RoomType = null!;

    internal MethodInfo RefreshRooms = null!;

    /// <summary>The only guaranteed main-thread tick</summary>
    internal MethodInfo Process = null!;

    internal MethodInfo BuildRoomMetaRow = null!;

    internal MethodInfo CreateRoom = null!;

    internal MethodInfo BeginJoin = null!;

    internal FieldInfo Rooms = null!;

    internal MethodInfo RebuildRoomStage = null!;

    internal MethodInfo PersistSettings = null!;

    internal MethodInfo UpdateHealthIndicator = null!;

    internal MethodInfo UpdatePageControls = null!;

    internal MethodInfo RefreshSelectedRoomDetails = null!;

    internal MethodInfo GetFilteredRooms = null!;

    internal MethodInfo GetSelectedRoom = null!;

    internal MethodInfo AreRoomListsVisuallyEquivalent = null!;

    internal MethodInfo ApplyAnnouncements = null!;

    internal MethodInfo EnsureAnnouncementFallback = null!;

    internal MethodInfo CreateConfiguredClient = null!;

    internal MethodInfo GetAnnouncements = null!;

    internal MethodInfo MeasureProbeRtt = null!;

    /// <summary>Set while the server picker is open, which short-circuits the refresh</summary>
    internal FieldInfo ServerPickerOpen = null!;

    /// <summary>Set while a create/join is running, which short-circuits the refresh</summary>
    internal FieldInfo ActionInFlight = null!;

    internal FieldInfo TimeUntilAutoRefresh = null!;

    internal FieldInfo LastLobbyRttMs = null!;

    internal FieldInfo ConsecutiveRefreshFailures = null!;

    /// <summary>Appended to the failure status: it tells the player a switch is the way out</summary>
    internal FieldInfo RefreshFailureHint = null!;

    /// <summary>Read from the host mod's own constant, so an upstream cadence change follows</summary>
    internal double RefreshIntervalSeconds;

    /// <summary>Read from the host mod's own constant: the page size the clamp divides by</summary>
    internal int RoomsPerPage;

    /// <summary>Saved and clamped around a rebuild</summary>
    internal FieldInfo? CurrentPageIndex;

    /// <summary>Holds the empty-state card during a cold start</summary>
    internal FieldInfo? RoomListContainer;

    /// <summary>The "房间 N → M" line above the list</summary>
    internal FieldInfo? RoomListSummaryLabel;

    /// <summary>Chooses the refresh failure branch</summary>
    internal Type LobbyServiceExceptionType = null!;

    internal ConstructorInfo ApiClientCtor = null!;

    internal MethodInfo GetRooms = null!;

    internal PropertyInfo RoomId = null!;

    internal PropertyInfo RoomVersion = null!;

    internal PropertyInfo RoomPlayers = null!;

    /// <summary>Known-peers cache + bundled seeds + the pinned featured server: synchronous, no network</summary>
    internal MethodInfo GatherInitialCandidates = null!;

    internal PropertyInfo EntryAddress = null!;

    internal PropertyInfo ConfigBaseUrl = null!;

    internal PropertyInfo? EntryDisplayName;

    internal PropertyInfo? EntryPinned;

    internal PropertyInfo? EntryRooms;

    internal PropertyInfo? EntryGuardStatus;

    internal MethodInfo? GatherCloudflareCandidates;

    /// <summary>/peers/metrics, which is where the create guard comes from</summary>
    internal MethodInfo? FetchSnapshot;

    internal PropertyInfo? SnapshotMetrics;

    internal PropertyInfo? MetricsGuardStatus;

    internal PropertyInfo? MetricsDisplayName;

    internal PropertyInfo? MetricsRooms;

    internal PropertyInfo? ConfigReadToken;

    internal PropertyInfo? ConfigCreateToken;

    internal PropertyInfo? RuntimeInstance;

    internal PropertyInfo? RuntimeActiveRoomSession;

    /// <summary>The hosted half of "a room session is live", which is upstream's own create refusal</summary>
    internal PropertyInfo? RuntimeHasActiveHostedRoom;

    internal PropertyInfo? RuntimeSwitchInProgress;

    internal MethodInfo? SwitchWithContext;

    internal MethodInfo? SwitchPlain;

    /// <summary>The per-switch cancellation token the join flow honours</summary>
    internal PropertyInfo? LeaseToken;

    /// <summary>The local game version, the pool's first sort key</summary>
    internal MethodInfo? GetGameVersion;

    internal MethodInfo? GetProbe;

    internal PropertyInfo? ProbeCapabilities;

    internal PropertyInfo? CapabilitiesProfiles;

    internal PropertyInfo? CapabilitiesLanMin;

    internal PropertyInfo? CapabilitiesLanMax;

    internal PropertyInfo? CapabilitiesTailMinimumClient;

    internal MethodInfo? GetSelectedCreateProtocolId;

    internal MethodInfo? ProtocolProfileFromCreateId;

    internal MethodInfo? ProfileToCanonical;

    /// <summary>The ceiling of our own LAN protocol offer</summary>
    internal PropertyInfo? TailRuntimeAvailable;

    /// <summary>What LanConnectProtocolOffer.CreateCurrent puts in our offer</summary>
    internal int TailLanProtocolVersion;

    /// <summary>Upstream's own in-flight guard, read before a round starts</summary>
    internal FieldInfo? RefreshInFlight;

    /// <summary>Restored when upstream's single-server refresh drops it</summary>
    internal FieldInfo? SelectedRoomId;

    internal MethodInfo? SetStatus;

    internal MethodInfo? UpdateActionButtons;

    internal MethodInfo? UpdateNetworkSummary;

    internal MethodInfo? CreateBodyLabel;

    internal PropertyInfo? TextMutedColor;

    /// <summary>Repainted after a switch, like upstream does</summary>
    internal FieldInfo? ServerBaseUrlInput;

    /// <summary>Where the create dialog is assembled, once per overlay</summary>
    internal MethodInfo BuildCreateDialog = null!;

    /// <summary>The one path every open of the create dialog takes</summary>
    internal MethodInfo OpenCreateDialog = null!;

    /// <summary>Assigned by CreateDialogShell, for this dialog only</summary>
    internal FieldInfo? CreateDialogBody;

    internal FieldInfo? CreateDialogErrorLabel;

    internal MethodInfo? CreateActionButton;

    internal MethodInfo? CreateSectionLabel;

    internal MethodInfo? ApplyButtonStyle;

    internal MethodInfo? ShowCreateDialogError;

    internal PropertyInfo? DangerColor;

    /// <summary>The dialog's name box, read by the cheap create pre-check</summary>
    internal FieldInfo? RoomNameInput;

    /// <summary>What the "HTTP override" box mirrors; the resolved address is a different value</summary>
    internal PropertyInfo? ConfigBaseUrlOverride;

    /// <summary>A saved-run room can stop at the slot dialog instead of joining</summary>
    internal PropertyInfo? RoomSavedRun;

    /// <summary>The room the resume-slot dialog waits for, null when it is closed</summary>
    internal FieldInfo? PendingResumeJoinRoom;

    /// <summary>The host mod's Android glyph fallback for printed text</summary>
    internal MethodInfo? NormalizeForDisplay;

    /// <summary>Game type: the game's save-manager singleton</summary>
    internal PropertyInfo? SaveManagerInstance;

    /// <summary>Game type: a multiplayer run save blocks a new room</summary>
    internal PropertyInfo? SaveManagerHasMultiplayerRun;

    internal static PoolAnchors? TryCapture(Assembly host, PoolAnchorScope scope, out string reason)
    {
        var missing = new List<string>();
        var optional = new List<string>();
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
        anchors.RoomId = ReflectScope.PropertyOf(anchors.RoomType ?? typeof(object), "RoomId", missing)!;
        anchors.RoomVersion = ReflectScope.PropertyOf(anchors.RoomType ?? typeof(object), "Version", missing)!;
        anchors.RoomPlayers = ReflectScope.PropertyOf(anchors.RoomType ?? typeof(object), "CurrentPlayers", missing)!;
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
                anchors.CaptureRefreshPrologue(missing, optional, host);
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

            case PoolAnchorScope.CreatePicker:
                anchors.BuildCreateDialog = ReflectScope.InstanceMethod(missing, anchors.Overlay, "BuildCreateDialog")!;
                anchors.OpenCreateDialog = ReflectScope.InstanceMethod(missing, anchors.Overlay, "OpenCreateDialogInternal")!;
                // The picker draws latency and a protocol hint, so it needs the same probe anchors the create repair has.
                anchors.CaptureProtocolAnchors(missing, host);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(scope));
        }

        // Everything below is allowed to be absent: every use is guarded at the call site, so a host-mod rename
        // costs the message text or the source-name column, not the pool. The misses go to one list rather than to
        // the bin: a member that silently stopped resolving looks exactly like a member that is working.
        Type? bootstrap = OptionalType(host, BootstrapTypeName, optional);
        anchors.GatherCloudflareCandidates = ReflectScope.StaticMethod(
            optional,
            bootstrap,
            "GatherCloudflareCandidatesAsync",
            "System.Threading.CancellationToken");

        Type? entry = OptionalType(host, EntryTypeName, optional);
        anchors.EntryDisplayName = OptionalProperty(entry, "DisplayName", optional);
        anchors.EntryPinned = OptionalProperty(entry, "IsPinned", optional);
        anchors.EntryRooms = OptionalProperty(entry, "Rooms", optional);
        anchors.EntryGuardStatus = OptionalProperty(entry, "CreateRoomGuardStatus", optional);

        Type? metricsClient = OptionalType(host, MetricsClientTypeName, optional);
        anchors.FetchSnapshot = ReflectScope.StaticMethod(
            optional,
            metricsClient,
            "FetchSnapshotAsync",
            "System.String",
            "System.Threading.CancellationToken");
        Type? snapshot = metricsClient?.GetNestedType("PeerDiscoverySnapshot", BindingFlags.Public | BindingFlags.NonPublic);
        anchors.SnapshotMetrics = OptionalProperty(snapshot, "Metrics", optional);
        Type? metrics = anchors.SnapshotMetrics?.PropertyType;
        anchors.MetricsGuardStatus = OptionalProperty(metrics, "CreateRoomGuardStatus", optional);
        anchors.MetricsDisplayName = OptionalProperty(metrics, "DisplayName", optional);
        anchors.MetricsRooms = OptionalProperty(metrics, "Rooms", optional);

        Type? config = OptionalType(host, ConfigTypeName, optional);
        anchors.ConfigReadToken = ReflectScope.StaticProperty(optional, config, "LobbyReadAccessToken");
        anchors.ConfigCreateToken = ReflectScope.StaticProperty(optional, config, "LobbyCreateRoomToken");

        Type? runtime = OptionalType(host, RuntimeTypeName, optional);
        anchors.RuntimeInstance = ReflectScope.StaticProperty(optional, runtime, "Instance");
        anchors.RuntimeSwitchInProgress = OptionalProperty(runtime, "IsLobbyServerSwitchInProgress", optional);
        anchors.RuntimeActiveRoomSession = OptionalProperty(runtime, "HasActiveRoomSession", optional);
        anchors.RuntimeHasActiveHostedRoom = OptionalProperty(runtime, "HasActiveHostedRoom", optional);
        anchors.LeaseToken = OptionalProperty(OptionalType(host, LeaseTypeName, optional), "Token", optional);

        anchors.GetGameVersion = ReflectScope.StaticMethod(
            optional,
            OptionalType(host, BuildInfoTypeName, optional),
            "GetGameVersion");

        anchors.RefreshInFlight = OptionalField(anchors.Overlay, "_refreshInFlight", optional);
        anchors.SelectedRoomId = OptionalField(anchors.Overlay, "_selectedRoomId", optional);
        anchors.SetStatus = ReflectScope.InstanceMethod(optional, anchors.Overlay, "SetStatus", "System.String");
        anchors.UpdateActionButtons = ReflectScope.InstanceMethod(optional, anchors.Overlay, "UpdateActionButtons");
        anchors.UpdateNetworkSummary = ReflectScope.InstanceMethod(optional, anchors.Overlay, "UpdateNetworkSummary");
        anchors.CreateBodyLabel = ReflectScope.StaticMethod(optional, anchors.Overlay, "CreateBodyLabel", "System.String");
        anchors.TextMutedColor = ReflectScope.StaticProperty(optional, anchors.Overlay, "TextMutedColor");
        anchors.ServerBaseUrlInput = OptionalField(anchors.Overlay, "_serverBaseUrlInput", optional);
        anchors.RoomNameInput = OptionalField(anchors.Overlay, "_roomNameInput", optional);
        anchors.CreateDialogBody = OptionalField(anchors.Overlay, "_createDialogBody", optional);
        anchors.CreateDialogErrorLabel = OptionalField(anchors.Overlay, "_createDialogErrorLabel", optional);
        anchors.CreateActionButton = FindSingleMethod(anchors.Overlay, "CreateActionButton", 6, optional);
        anchors.CreateSectionLabel = FindSingleMethod(anchors.Overlay, "CreateSectionLabel", 1, optional, requireStatic: true);
        anchors.ApplyButtonStyle = FindSingleMethod(anchors.Overlay, "ApplyButtonStyle", 3, optional, requireStatic: true);
        anchors.ShowCreateDialogError = FindSingleMethod(anchors.Overlay, "ShowCreateDialogError", 2, optional);
        anchors.DangerColor = ReflectScope.StaticProperty(optional, anchors.Overlay, "DangerColor");
        anchors.ConfigBaseUrlOverride = ReflectScope.StaticProperty(optional, config, "LobbyServerBaseUrlOverride");
        anchors.RoomSavedRun = OptionalProperty(anchors.RoomType, "SavedRun", optional);
        anchors.PendingResumeJoinRoom = OptionalField(anchors.Overlay, "_pendingResumeJoinRoom", optional);
        anchors.NormalizeForDisplay = ReflectScope.StaticMethod(
            optional,
            OptionalType(host, UiTextTypeName, optional),
            "NormalizeForDisplay",
            "System.String");

        // Game-side types are looked up by full name, so a game update costs this pre-check and not the repair.
        Type? saveManager = ReflectScope.FindType("MegaCrit.Sts2.Core.Saves.SaveManager");
        anchors.SaveManagerInstance = OptionalProperty(saveManager, "Instance", optional);
        anchors.SaveManagerHasMultiplayerRun = OptionalProperty(saveManager, "HasMultiplayerRunSave", optional);

        ReportOptionalMisses(optional);

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

        // A room session is what makes a switch destructive, so the create repair cannot decide anything without it.
        // The join repair only reads it as a "did the join land" probe.
        if (scope == PoolAnchorScope.Create && anchors.RuntimeActiveRoomSession == null)
        {
            missing.Add(RuntimeTypeName + ".HasActiveRoomSession");
        }

        // The picker inserts itself into the dialog body and writes its own error line into the host mod's label.
        if (scope == PoolAnchorScope.CreatePicker)
        {
            if (anchors.CreateDialogBody == null)
            {
                missing.Add(OverlayTypeName + "._createDialogBody");
            }

            if (anchors.CreateDialogErrorLabel == null)
            {
                missing.Add(OverlayTypeName + "._createDialogErrorLabel");
            }
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
    /// The rest of the refresh this repair reproduces: the guard fields, the status and health updates and the current
    /// server's probe and announcement calls. All of them are required for the refresh scope, because a half-reproduced
    /// refresh is worse than no takeover at all.
    /// </summary>
    private void CaptureRefreshPrologue(List<string> missing, List<string> optional, Assembly host)
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
        CurrentPageIndex = OptionalField(Overlay, "_currentPageIndex", optional);
        RoomListContainer = OptionalField(Overlay, "_roomListContainer", optional);
        RoomListSummaryLabel = OptionalField(Overlay, "_roomListSummaryLabel", optional);
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

    /// <summary>
    /// LobbyApiClient's six-parameter constructor, matched by parameter type rather than arity. The sink matters: the
    /// default is GD.Print and it is called on every request, so a client built without one cannot be used from a
    /// headless harness and would flood the game log.
    /// </summary>
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

    /// <summary>One method by name and arity, or null when the host mod has zero or several. Ambiguity is never
    /// resolved by picking one: binding to the wrong overload is silent.</summary>
    private static MethodInfo? FindSingleMethod(
        Type? owner,
        string name,
        int arity,
        List<string>? optional = null,
        bool requireStatic = false)
    {
        MethodInfo[] matches = owner?
            .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal))
            .Where(method => method.GetParameters().Length == arity)
            .Where(method => !requireStatic || method.IsStatic)
            .ToArray() ?? Array.Empty<MethodInfo>();
        if (matches.Length == 1)
        {
            return matches[0];
        }

        if (owner != null)
        {
            optional?.Add(owner.FullName + "." + name + "(" + arity + " parameter(s))");
        }
        return null;
    }

    private static FieldInfo? OptionalField(Type? owner, string name, List<string>? optional = null)
    {
        FieldInfo? field = owner?.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null && owner != null)
        {
            optional?.Add(owner.FullName + "." + name);
        }

        return field;
    }

    private static PropertyInfo? OptionalProperty(Type? owner, string name, List<string>? optional = null)
    {
        PropertyInfo? property = owner?.GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null && owner != null)
        {
            optional?.Add(owner.FullName + "." + name);
        }

        return property;
    }

    private static Type? OptionalType(Assembly host, string fullName, List<string> optional)
    {
        Type? type = host.GetType(fullName, throwOnError: false, ignoreCase: false);
        if (type == null)
        {
            optional.Add("type " + fullName);
        }

        return type;
    }

    /// <summary>
    /// One log line for the whole optional set. Keyed by the set itself so the four scope captures, which run one
    /// after another at startup, report it once instead of four times.
    /// </summary>
    private static void ReportOptionalMisses(List<string> optional)
    {
        if (optional.Count == 0)
        {
            return;
        }

        string[] names = optional
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        PoolRefreshFix.WarnOnce(
            "optional-anchors:" + string.Join("|", names),
            "the host mod no longer has " + string.Join(", ", names) +
            "; the pool keeps working, with the parts that read them switched off");
    }

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

    Create,

    /// <summary>The create dialog's server picker: a UI repair, so it needs the dialog anchors and nothing that patches.</summary>
    CreatePicker
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

    /// <summary>
    /// Round trip of the last request that actually answered: GET /rooms during a refresh, /peers/metrics during a
    /// create probe. A failed or timed-out probe leaves it alone, so null means "never measured", not "slow".
    /// </summary>
    internal int? LatencyMs { get; set; }

    internal string GuardStatus { get; set; } = "allow";

    internal int? RoomCount { get; set; }

    /// <summary>
    /// When /peers/metrics was last attempted, answered or not. Deliberately NOT written by a /rooms fetch: the two
    /// probes answer different questions, and sharing one stamp is what let a failed room fetch masquerade as "probed
    /// a moment ago" and keep GuardStatus at the seed default "allow".
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
    /// capabilities.supportedProtocolProfiles from GET /probe, or null when the server did not answer. Null is not
    /// "supports everything": it means the server could not be asked, and the create repair treats it as unusable so a
    /// silent node can never be the one a room is published to.
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
    /// The current server list, cheapest possible: the cache when it is fresh, otherwise the synchronous host-mod seeds.
    /// No network work happens here, so the first paint cannot be gated on discovery.
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
                // Merge, never replace: the seed list knows nothing about the Cloudflare-discovered servers, so a
                // wholesale swap loses every one of them.
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
        if (anchors.GatherCloudflareCandidates == null)
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
            // Leaving the cache alone is the whole recovery path, but a budget that expires on every tick is
            // invisible without a line: the next round simply asks again.
            PoolRefreshFix.WarnOnce(
                "discovery-budget",
                "server discovery used its whole " + DiscoveryBudgetMs + " ms budget; the pool keeps the servers it " +
                "already knew and asks again next refresh");
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
    /// The backoff ladder for a node that keeps failing, in seconds. An unreachable node otherwise costs the whole round
    /// its 2 s ceiling; the measured difference (lead research-pool-latency.md) is 2,013-2,110 ms versus 247-333 ms once
    /// it is out of the round. The refresh cadence stays at the host mod's 8 s; only the nodes in cooldown are skipped,
    /// and they are retried as soon as their slot expires.
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
    /// The service freezes the room's protocol from the client's offer and its own hardcoded range: the selected
    /// version is min(offerMax, serverMax) and has to reach max(offerMin, serverMin). The client then validates that
    /// frozen selection, so a server whose advertised range cannot meet our offer ends as "LAN 协议版本不匹配" after a
    /// successful create - exactly the failure this check exists to avoid. Unknown (never answered, or stale) counts as
    /// "cannot host", not as "probably fine".
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
    /// One server's live snapshot: latency, display name, room count and the create-room guard, read from
    /// /peers/metrics with /probe as the fallback. A server that does not answer keeps its previous latency and drops
    /// out of the create-room candidates because its guard status becomes unknown.
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
    /// freezes rooms at, and whether it knows the tail v1 generation at all. Cached for CapabilityTtlSeconds; a server
    /// that does not answer is recorded as unusable rather than merely unknown.
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
                "probe-capabilities:" + server.BaseUrl,
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
                // Upstream answers a cancelled or timed-out /peers/metrics request with an empty snapshot instead of
                // throwing, so "no metrics" cannot tell "too slow" from "does not publish it". Either way this round trip measured nothing: it must not become the latency the
                // dialog caches for ProbeTtlSeconds, or a node that never answered is shown as a fast one.
                RecordMetricsProbe(server, null, null, "unknown", null);
                PoolRefreshFix.WarnOnce(
                    "probe-metrics:" + server.BaseUrl,
                    "no /peers/metrics answer from " + server.BaseUrl + " within " + timeoutMs +
                    " ms, so it cannot be verified for hosting");
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
                "probe-metrics-exception:" + server.BaseUrl,
                "probing " + server.BaseUrl + " failed: " + PoolRoomClient.Describe(exception));
        }
    }

    /// <summary>
    /// The address the game is on, as a server the create choice can compare against, or null when it is not a lobby
    /// address at all. Not cached: one decision needs it, and the room list is better off without an extra node.
    /// </summary>
    internal static PoolServer? AdoptCurrent(string? baseUrl)
    {
        string? normalized = PoolAddress.TryNormalize(baseUrl);
        return normalized == null ? null : new PoolServer(normalized);
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

    /// <summary>Builds the pool's server list from the host mod's synchronous seeds</summary>
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

    /// <summary>
    /// Everything the pool has learned about a server has to survive a rebuild of the list it lives in. A seed rebuild
    /// runs every DiscoveryTtlSeconds and builds fresh PoolServer objects; carrying only the probe figures would reset
    /// the cooldown ladder to its first step, drop the stale rooms of a dead node and throw away the create-side
    /// capability cache.
    /// </summary>
    private static void CarryState(PoolServer server, PoolServer previous)
    {
        server.DisplayName ??= previous.DisplayName;
        server.LatencyMs = previous.LatencyMs;
        server.GuardStatus = previous.GuardStatus;
        server.ProbedAt = previous.ProbedAt;
        server.ConsecutiveFailures = previous.ConsecutiveFailures;
        server.CooldownUntil = previous.CooldownUntil;
        server.LastRooms = previous.LastRooms;
        server.SupportedProfiles = previous.SupportedProfiles;
        server.LanProtocolMin = previous.LanProtocolMin;
        server.LanProtocolMax = previous.LanProtocolMax;
        server.TailMinimumClientVersion = previous.TailMinimumClientVersion;
        server.CapabilitiesAt = previous.CapabilitiesAt;
    }

    /// <summary>Copies the host mod's entries, carrying everything already known about each address across.</summary>
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
                CarryState(server, previous);
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

                CarryState(server, previous);
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
