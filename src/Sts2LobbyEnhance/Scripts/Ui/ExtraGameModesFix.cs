using System.Reflection;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.Blitz;
using MegaCrit.Sts2.Core.Nodes.Screens.Dread;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2LobbyEnhance;

/// <summary>
/// Adds 0.113's two extra game modes — Blitz (极速模式) and Dread (恐惧模式) — to the lobby's
/// create-room picker.
///
/// Why this is more delicate than it looks: the shipped 0.6.3 build was compiled against 0.111, where
/// <c>GameMode</c> was <c>{ None, Standard, Daily, Custom }</c>. Enum members are inlined as constants,
/// so its "Custom" is the literal <c>3</c>. In 0.113 the enum became
/// <c>{ None, Standard, Daily, Dread, Blitz }</c> — value <c>3</c> is now <b>Dread</b> and <c>4</c> is
/// <b>Blitz</b>. The host mod keeps working only because every one of its switches uses the same baked
/// constants, so <c>3</c> still means "custom" to it.
///
/// That leaves exactly one spare in-range value (<c>4</c>, the real Blitz) plus values above the enum's
/// range. We therefore use:
/// <list type="bullet">
/// <item><c>4</c> → Blitz, which is also the game's own value;</item>
/// <item><c>5</c> → Dread. A sentinel, because <c>3</c> is already taken by the host mod's Custom.
/// It never reaches the game engine: the only consumers of the mode value are the three methods this
/// fix patches, and the screen it routes to (<c>NDreadScreen</c>) supplies the real <c>GameMode.Dread</c>
/// itself.</item>
/// </list>
///
/// Every seam returns or accepts <c>GameMode</c>, a value type that cannot be bound through an
/// <c>object</c> Harmony argument, which is why the mapping seams are IL level rather than prefixes.
///
/// The room-card pill used to live here and no longer does (<see cref="RoomCardPillFix"/>): a cosmetic
/// colour property must not be able to fail the repair that makes the modes hostable at all.
///
/// Every seam is resolved by <see cref="Shape.TryCapture"/> before anything is patched, and a missing
/// functional anchor makes this fix report why it stayed out instead of throwing — a throw here is
/// what used to take the whole mod down. Game-side types are looked up by name, never with
/// <c>typeof</c>, because a probe that names <c>NBlitzScreen</c> would itself fail to JIT on a build
/// that dropped it.
/// </summary>
internal sealed class ExtraGameModesFix : ICompatFix
{
    // ---------------------------------------------------------------- host mod type names
    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyOverlay";
    private const string HostFlowTypeName = "Sts2LanConnect.Scripts.LanConnectHostFlow";
    private const string BindingTypeName = "Sts2LanConnect.Scripts.LanConnectMultiplayerSaveRoomBinding";
    private const string InviteButtonPatchTypeName = "Sts2LanConnect.Scripts.LanConnectInviteButtonPatch";
    private const string AvailabilityTypeName = "Sts2LanConnect.Scripts.LanConnectLanHostModeAvailability";

    // Short names, used only in the "missing:" reason line so it stays readable in a log.
    private const string OverlayShort = "LanConnectLobbyOverlay";
    private const string HostFlowShort = "LanConnectHostFlow";
    private const string BindingShort = "LanConnectMultiplayerSaveRoomBinding";
    private const string AvailabilityShort = "LanConnectLanHostModeAvailability";
    private const string InviteShort = "LanConnectInviteButtonPatch";

    // ---------------------------------------------------------------- game type names (string only)
    private const string GameModeTypeName = "MegaCrit.Sts2.Core.Runs.GameMode";
    private const string CharacterSelectTypeName =
        "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen";
    private const string DailyRunScreenTypeName =
        "MegaCrit.Sts2.Core.Nodes.Screens.DailyRun.NDailyRunScreen";
    private const string CustomRunScreenTypeName =
        "MegaCrit.Sts2.Core.Nodes.Screens.CustomRun.NCustomRunScreen";
    private const string BlitzScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Blitz.NBlitzScreen";
    private const string DreadScreenTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.Dread.NDreadScreen";

    // Full names of the two middle parameters of PushHostScreen. Compared as strings so the probe never
    // has to name the types itself.
    private const string SubmenuStackTypeName = "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NSubmenuStack";
    private const string HostGameServiceTypeName = "MegaCrit.Sts2.Core.Multiplayer.NetHostGameService";
    private const string IntTypeName = "System.Int32";

    /// <summary>The game assembly, looked up by name so no probe needs a <c>typeof</c>.</summary>
    private const string GameAssemblyName = "sts2";

    /// <summary>Dropdown id and mode value for Blitz; matches the game's own <c>GameMode.Blitz</c>.</summary>
    private const int BlitzValue = 4;

    /// <summary>Sentinel mode value for Dread, chosen because the host mod already spends 3 on Custom.</summary>
    private const int DreadSentinel = 5;

    private static MethodInfo? _isExtraMode;
    private static Shape? _shape;

    public string Id => "extra-game-modes";

    public FixGroup Group => FixGroup.Modes;

    /// <summary>
    /// Applicable when the host mod is present and every functional anchor resolves — the dropdown, the
    /// two <c>GameMode</c>-shaped mapping seams, the availability seam, the host-screen dispatch, the
    /// invite-button hook, and the five game screens the dispatch pushes.
    ///
    /// <see cref="CompatSnapshot"/> is not read: none of its six observations is a prerequisite for adding
    /// a mode, and the two that look related (<c>InitialGameInfoLostGameMode</c>) describe the wire, which
    /// this repair does not touch. The shape checks in <see cref="Shape.TryCapture"/> are the real gate.
    /// </summary>
    public bool Applies(in CompatSnapshot snapshot, out string reason)
    {
        Assembly? host = Reflect.FindAssembly(Entry.HostModAssemblyName);
        if (host == null)
        {
            reason = "host mod 'sts2_lan_connect' is not loaded";
            return false;
        }

        Shape? shape = Shape.TryCapture(host, out reason);
        if (shape == null)
        {
            return false;
        }

        _shape = shape;
        reason = string.Empty;
        return true;
    }

    public void Apply(Harmony harmony, in CompatSnapshot snapshot)
    {
        Shape shape = _shape ?? throw new InvalidOperationException(
            "extra-game-modes: Applies() did not capture the host shape; refusing to patch blind.");

        // Resolve our own IL helpers before the first patch: a null here is a coding error, and finding it
        // after three seams are already installed would leave the repair half-built.
        _isExtraMode = OwnMethod(nameof(IsExtraMode));
        MethodInfo mapCreateId = OwnMethod(nameof(MapCreateId));
        MethodInfo mapGameModeString = OwnMethod(nameof(MapGameModeString));
        MethodInfo pushHostScreen = OwnMethod(nameof(PushHostScreenReplacement));
        MethodInfo availabilityFallback = OwnMethod(nameof(IsCreateGameModeAvailableReplacement));

        // 1) Append the two entries to the create dialog's room-type dropdown.
        harmony.Patch(
            shape.BuildCreateDialog,
            postfix: new HarmonyMethod(typeof(ExtraGameModesFix), nameof(BuildCreateDialogPostfix))
            {
                priority = Priority.Last
            });

        // 2) dropdown id -> mode value, and the currently selected mode.
        ReplacementPlans[shape.GameModeFromCreateId] = mapCreateId;
        harmony.Patch(
            shape.GameModeFromCreateId,
            transpiler: new HarmonyMethod(typeof(ExtraGameModesFix), nameof(ConstantResultTranspiler))
            {
                priority = Priority.First
            });

        SelectedModePlan = (shape.SelectedCreateGameModeId, mapCreateId);
        harmony.Patch(
            shape.GetSelectedCreateGameMode,
            transpiler: new HarmonyMethod(typeof(ExtraGameModesFix), nameof(SelectedModeTranspiler))
            {
                priority = Priority.First
            });

        // 3) availability gate: the two new modes are never locked, and upstream's own answer for
        //    standard/daily/custom is preserved by falling through to its body instead of re-deriving it.
        InstallAvailability(harmony, shape, availabilityFallback);

        // 4) push the matching 0.113 screen when hosting, then re-create the invite button like upstream does.
        PatchWholeBodyWithCall(harmony, shape.PushHostScreen, pushHostScreen, null, null, null, null);

        // 5) the lobby-facing mode string, only onto a GetLobbyGameMode that takes GameMode.
        InstallLobbyGameMode(harmony, shape, mapGameModeString);

        // 6) friendly Chinese labels in the room list (decorative: absence only costs the label).
        MethodInfo? lobbyLabel = shape.GetLobbyGameModeLabel;
        if (lobbyLabel != null)
        {
            harmony.Patch(
                lobbyLabel,
                prefix: new HarmonyMethod(typeof(ExtraGameModesFix), nameof(GetLobbyGameModeLabelPrefix))
                {
                    priority = Priority.First
                });
        }
        else
        {
            Entry.Warn(
                "extra-game-modes: " + BindingTypeName + ".GetLobbyGameModeLabel(string) not found; " +
                "room rows will show the raw gameMode string for 极速/恐惧 rooms.");
        }

        Entry.Info(
            "extra-game-modes: added 极速模式 (Blitz) and 恐惧模式 (Dread) to the create-room picker " +
            $"(dropdown ids 3/4, mode values {BlitzValue}/{DreadSentinel}, availability, host screen + " +
            "invite button, lobby gameMode string, label).");
    }

    // ------------------------------------------------------------------ seam 3: availability

    /// <summary>
    /// Keeps upstream's <c>IsCreateGameModeAvailable</c> body and puts a five-instruction gate in front
    /// of it: modes 4/5 answer <c>true</c>, everything else branches into the original instructions.
    ///
    /// This replaces the previous whole-body rewrite, which re-derived upstream's switch by reflecting on
    /// the availability record's <c>Standard</c>/<c>Daily</c>/<c>Custom</c> properties. When one of those
    /// was renamed the reflection read returned nothing and every real mode silently became "locked",
    /// leaving only 极速/恐惧 selectable — a far worse failure than the feature being absent. Branching
    /// into the original body means we never have to know that record's shape at all.
    /// </summary>
    private static IEnumerable<CodeInstruction> AvailabilityTranspiler(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator generator)
    {
        List<CodeInstruction> original = instructions.ToList();
        if (original.Count == 0 || _isExtraMode == null)
        {
            foreach (CodeInstruction instruction in original)
            {
                yield return instruction;
            }

            yield break;
        }

        // The label MUST come from the generator. `new Label()` is the default struct, i.e. id 0, and
        // Harmony has already defined label 0 for the first branch target of the original body — the two
        // collide, Brfalse jumps into the middle of the switch below, and the method loops forever.
        // Verified offline: with `new Label()` modes 1/2/3 never returned (tools/availability-probe).
        //
        // Fully qualified: "using Godot" also brings a Godot.Label into scope.
        System.Reflection.Emit.Label runOriginal = generator.DefineLabel();

        yield return new CodeInstruction(OpCodes.Ldarg_0);
        yield return new CodeInstruction(OpCodes.Call, _isExtraMode);
        yield return new CodeInstruction(OpCodes.Brfalse, runOriginal);
        yield return new CodeInstruction(OpCodes.Ldc_I4_1);
        yield return new CodeInstruction(OpCodes.Ret);

        original[0].labels.Add(runOriginal);
        foreach (CodeInstruction instruction in original)
        {
            yield return instruction;
        }
    }

    private static void InstallAvailability(Harmony harmony, Shape shape, MethodInfo fallback)
    {
        try
        {
            harmony.Patch(
                shape.IsCreateGameModeAvailable,
                transpiler: new HarmonyMethod(typeof(ExtraGameModesFix), nameof(AvailabilityTranspiler))
                {
                    priority = Priority.First
                });

            Entry.Info(
                "extra-game-modes: availability check short-circuits modes 4/5 and runs upstream's own " +
                "body for standard/daily/custom.");
            return;
        }
        catch (Exception exception)
        {
            Entry.Warn(
                "extra-game-modes: the branch transpiler was rejected (" + exception.GetType().Name + ": " +
                exception.Message + "); falling back to the reflective replacement for the availability check.");
        }

        PatchWholeBodyWithCall(
            harmony,
            shape.IsCreateGameModeAvailable,
            fallback,
            null,
            shape.Availability);
    }

    // ------------------------------------------------------------------ seam 5: lobby gameMode string

    /// <summary>
    /// Installs the <c>GetLobbyGameMode</c> mapping, but only onto a method whose first parameter is exactly
    /// <c>GameMode</c>.
    ///
    /// The replacement is written in <c>GameMode</c> terms — <c>Standard = 1</c>, <c>Daily = 2</c>,
    /// <c>Custom = 3</c> — and those numbers are the whole contract. If the first parameter is any other type
    /// (a different enum, an <c>int</c>, a new mode struct), the numbers mean nothing and installing the
    /// replacement would silently relabel every room. So the shape is asserted at bind time and the seam is
    /// skipped, with both the expected and the actual type name in the log.
    ///
    /// This is the same guard every other seam in this file has; it is not about any particular upstream
    /// release. <c>Reflect.RequireMethodByName</c> matches on arity only (<c>Reflect.cs:60-76</c>), so an
    /// arity-only bind can never be trusted on its own.
    /// </summary>
    private static void InstallLobbyGameMode(Harmony harmony, Shape shape, MethodInfo mapGameModeString)
    {
        string parameterType = shape.GetLobbyGameModeFirstParameterType;

        if (!string.Equals(parameterType, GameModeTypeName, StringComparison.Ordinal))
        {
            Entry.Warn(
                "extra-game-modes: " + BindingTypeName + ".GetLobbyGameMode takes " + parameterType +
                " as its first parameter but this replacement is written for " + GameModeTypeName +
                " (Standard=1/Daily=2/Custom=3); not installing the lobby gameMode mapping, so the room " +
                "list will show the raw string for 极速/恐惧 rooms.");
            return;
        }

        ReplacementPlans[shape.GetLobbyGameMode] = mapGameModeString;
        harmony.Patch(
            shape.GetLobbyGameMode,
            transpiler: new HarmonyMethod(typeof(ExtraGameModesFix), nameof(ConstantResultTranspiler))
            {
                priority = Priority.First
            });
    }

    /// <summary>
    /// One of this class's own IL-callable helpers. Resolved before the first patch so a typo cannot leave
    /// a half-installed repair behind; a null here is a build error, not a compatibility issue.
    /// </summary>
    private static MethodInfo OwnMethod(string name) =>
        typeof(ExtraGameModesFix).GetMethod(name, BindingFlags.Public | BindingFlags.Static)
        ?? throw new MissingMethodException(typeof(ExtraGameModesFix).FullName, name);

    // ------------------------------------------------------------------ IL plans

    private static readonly Dictionary<MethodBase, MethodInfo> ReplacementPlans = new();

    private static (FieldInfo IdField, MethodInfo Helper) SelectedModePlan;

    /// <summary>Replaces a body with <c>call helper(arg0); ret</c> — valid for pure mapping methods.</summary>
    private static IEnumerable<CodeInstruction> ConstantResultTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod)
    {
        if (!ReplacementPlans.TryGetValue(__originalMethod, out MethodInfo? helper))
        {
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;
            }

            yield break;
        }

        yield return new CodeInstruction(OpCodes.Ldarg_0);
        yield return new CodeInstruction(OpCodes.Call, helper);
        yield return new CodeInstruction(OpCodes.Ret);
    }

    /// <summary>Rewrites <c>GetSelectedCreateGameMode()</c> to read the stored id and map it.</summary>
    private static IEnumerable<CodeInstruction> SelectedModeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        yield return new CodeInstruction(OpCodes.Ldarg_0);
        yield return new CodeInstruction(OpCodes.Ldfld, SelectedModePlan.IdField);
        yield return new CodeInstruction(OpCodes.Call, SelectedModePlan.Helper);
        yield return new CodeInstruction(OpCodes.Ret);
    }

    private static readonly Dictionary<MethodBase, (MethodInfo Helper, Type?[] BoxTypes)> CallPlans = new();

    /// <summary>
    /// Replaces a whole method body with a single call to <paramref name="helper"/>, forwarding every
    /// argument positionally. Avoids branch labels entirely (this Harmony build rejects a
    /// <c>CodeInstruction</c> used as a branch operand), and keeps the reimplemented semantics explicit.
    /// </summary>
    private static void PatchWholeBodyWithCall(
        Harmony harmony,
        MethodInfo target,
        MethodInfo helper,
        params Type?[] boxTypes)
    {
        CallPlans[target] = (helper, boxTypes);
        harmony.Patch(
            target,
            transpiler: new HarmonyMethod(typeof(ExtraGameModesFix), nameof(ForwardCallTranspiler))
            {
                priority = Priority.First
            });
    }

    private static IEnumerable<CodeInstruction> ForwardCallTranspiler(
        IEnumerable<CodeInstruction> instructions,
        MethodBase __originalMethod)
    {
        if (!CallPlans.TryGetValue(__originalMethod, out (MethodInfo Helper, Type?[] BoxTypes) plan))
        {
            foreach (CodeInstruction instruction in instructions)
            {
                yield return instruction;
            }

            yield break;
        }

        for (int index = 0; index < plan.BoxTypes.Length; index++)
        {
            yield return new CodeInstruction(OpCodes.Ldarg, index);
            if (plan.BoxTypes[index] is { } boxType)
            {
                yield return new CodeInstruction(OpCodes.Box, boxType);
            }
        }

        yield return new CodeInstruction(OpCodes.Call, plan.Helper);
        yield return new CodeInstruction(OpCodes.Ret);
    }

    // ------------------------------------------------------------------ Harmony entry points

    // ReSharper disable once UnusedParameter.Local — Harmony binds by position.
    private static void BuildCreateDialogPostfix(object __instance)
    {
        // The field was resolved and asserted by Shape.TryCapture; this is a read, not a lookup, so a
        // host-mod rename cannot throw inside the host's own dialog builder.
        if (_shape is not { } shape || shape.RoomTypeOption.GetValue(__instance) is not OptionButton option)
        {
            return;
        }

        AppendModeOption(option, 3, "极速模式", "极速", "Blitz");
        AppendModeOption(option, 4, "恐惧模式", "恐惧", "Dread");
    }

    /// <summary>
    /// Adds one dropdown entry unless the dropdown already offers that mode — by id (our own re-entry) or
    /// by text (a newer upstream that adds the same mode under a different id). Without the text check a
    /// host build that ships its own 极速 entry would end up with two of them.
    /// </summary>
    private static void AppendModeOption(OptionButton option, int id, string label, params string[] markers)
    {
        for (int index = 0; index < option.ItemCount; index++)
        {
            if (option.GetItemId(index) == id)
            {
                return;
            }

            string text = option.GetItemText(index);
            foreach (string marker in markers)
            {
                if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        option.AddItem(label, id);
    }

    // ReSharper disable once UnusedParameter.Local — Harmony binds by position.
    private static bool GetLobbyGameModeLabelPrefix(string? __0, ref string __result)
    {
        switch (__0?.Trim().ToLowerInvariant())
        {
            case "blitz":
                __result = "极速模式";
                return false;
            case "dread":
                __result = "恐惧模式";
                return false;
            default:
                return true;
        }
    }

    // ------------------------------------------------------------------ pure helpers used from IL

    /// <summary>Dropdown id to the mode value the rest of the host mod understands.</summary>
    public static int MapCreateId(int id) => id switch
    {
        1 => (int)GameMode.Daily,
        2 => 3,          // the host mod's baked "Custom" constant
        3 => BlitzValue,
        4 => DreadSentinel,
        _ => (int)GameMode.Standard
    };

    /// <summary>True for the two modes this fix introduces.</summary>
    public static bool IsExtraMode(int mode) => mode is BlitzValue or DreadSentinel;

    /// <summary>Maps a mode value to the string the lobby service stores and shows.</summary>
    public static string MapGameModeString(int mode) => mode switch
    {
        BlitzValue => "blitz",
        DreadSentinel => "dread",
        3 => "custom",
        (int)GameMode.Daily => "daily",
        _ => "standard"
    };

    /// <summary>
    /// Fallback for <see cref="IsCreateGameModeAvailable"/> used only when the branch transpiler cannot be
    /// installed. Kept because the branch is the preferred shape, not because this one is safe: it
    /// re-derives upstream's switch by reflecting on the availability record, and returns
    /// <c>false</c> for every real mode when those properties cannot be read.
    /// </summary>
    public static bool IsCreateGameModeAvailableReplacement(int mode, object availability)
    {
        ArgumentNullException.ThrowIfNull(availability);

        if (IsExtraMode(mode))
        {
            return true;
        }

        Type type = availability.GetType();
        bool Read(string name) =>
            type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(availability) is true;

        return mode switch
        {
            (int)GameMode.Standard => Read("Standard"),
            (int)GameMode.Daily => Read("Daily"),
            3 => Read("Custom"),
            _ => false
        };
    }

    /// <summary>
    /// Upstream's last statement in the Standard branch of <c>PushHostScreen</c>
    /// (<c>dec-official/LanConnectHostFlow.cs:545</c>), run right after the screen is pushed. Daily and Custom never
    /// had it (<c>:527-538</c>), and Blitz/Dread have no upstream counterpart at all, so only Standard is wired.
    /// </summary>
    private static void ScheduleInviteButton(
        MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen screen,
        Shape shape)
    {
        shape.ScheduleEnsureInviteButton.Invoke(null, new object?[] { screen, "push_host_screen" });
    }

    /// <summary>
    /// Full replacement for the host mod's host-screen dispatch: a faithful port of its switch plus the
    /// two new branches. Kept as one function so the original behaviour stays readable in one place.
    /// </summary>
    public static void PushHostScreenReplacement(int mode, NSubmenuStack stack, NetHostGameService netService, int maxPlayers)
    {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(netService);

        switch (mode)
        {
            case (int)GameMode.Standard:
            {
                var screen = stack.GetSubmenuType<MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen>();
                screen.InitializeMultiplayerAsHost(netService, maxPlayers);
                stack.Push(screen);
                ScheduleInviteButton(
                    screen,
                    _shape ?? throw new InvalidOperationException("extra-game-modes: shape was not captured."));
                return;
            }

            case (int)GameMode.Daily:
            {
                var screen = stack.GetSubmenuType<MegaCrit.Sts2.Core.Nodes.Screens.DailyRun.NDailyRunScreen>();
                screen.InitializeMultiplayerAsHost(netService);
                stack.Push(screen);
                return;
            }

            case BlitzValue:
            {
                NBlitzScreen screen = stack.GetSubmenuType<NBlitzScreen>();
                screen.InitializeMultiplayerAsHost(netService, maxPlayers);
                stack.Push(screen);
                Entry.Info("extra-game-modes: hosting 极速模式 (Blitz).");
                return;
            }

            case DreadSentinel:
            {
                NDreadScreen screen = stack.GetSubmenuType<NDreadScreen>();
                screen.InitializeMultiplayerAsHost(netService, maxPlayers);
                stack.Push(screen);
                Entry.Info("extra-game-modes: hosting 恐惧模式 (Dread).");
                return;
            }

            default:
            {
                var screen = stack.GetSubmenuType<MegaCrit.Sts2.Core.Nodes.Screens.CustomRun.NCustomRunScreen>();
                screen.InitializeMultiplayerAsHost(netService, maxPlayers);
                stack.Push(screen);
                return;
            }
        }
    }

    // ------------------------------------------------------------------ installability probe

    /// <summary>
    /// Everything this fix needs, resolved once before any patch is installed.
    ///
    /// Two tiers, because they fail very differently:
    /// <list type="bullet">
    /// <item><b>Functional</b> — the dropdown, the id mapping, the availability gate, the host screen and
    /// its five screen types. Missing any of them means the feature cannot work, so the fix stays out and
    /// says why.</item>
    /// <item><b>Decorative</b> — <c>GetLobbyGameModeLabel(string)</c>. Missing it only costs a label, so it
    /// is recorded as a warning and the rest still installs.</item>
    /// </list>
    ///
    /// No <c>typeof</c> on any version-specific type, no <c>Invoke</c>, no <c>Activator</c>, no Godot
    /// node, no file access: this runs during mod initialisation and must not be able to throw.
    /// </summary>
    private sealed class Shape
    {
        private Shape(
            MethodInfo buildCreateDialog,
            FieldInfo roomTypeOption,
            MethodInfo gameModeFromCreateId,
            MethodInfo getSelectedCreateGameMode,
            FieldInfo selectedCreateGameModeId,
            MethodInfo isCreateGameModeAvailable,
            Type availability,
            MethodInfo pushHostScreen,
            MethodInfo getLobbyGameMode,
            string getLobbyGameModeFirstParameterType,
            MethodInfo? getLobbyGameModeLabel,
            MethodInfo scheduleEnsureInviteButton)
        {
            BuildCreateDialog = buildCreateDialog;
            RoomTypeOption = roomTypeOption;
            GameModeFromCreateId = gameModeFromCreateId;
            GetSelectedCreateGameMode = getSelectedCreateGameMode;
            SelectedCreateGameModeId = selectedCreateGameModeId;
            IsCreateGameModeAvailable = isCreateGameModeAvailable;
            Availability = availability;
            PushHostScreen = pushHostScreen;
            GetLobbyGameMode = getLobbyGameMode;
            GetLobbyGameModeFirstParameterType = getLobbyGameModeFirstParameterType;
            GetLobbyGameModeLabel = getLobbyGameModeLabel;
            ScheduleEnsureInviteButton = scheduleEnsureInviteButton;
        }

        internal MethodInfo BuildCreateDialog { get; }

        /// <summary>The dropdown the postfix appends to (<c>OptionButton _roomTypeOption</c>).</summary>
        internal FieldInfo RoomTypeOption { get; }

        internal MethodInfo GameModeFromCreateId { get; }

        internal MethodInfo GetSelectedCreateGameMode { get; }

        internal FieldInfo SelectedCreateGameModeId { get; }

        internal MethodInfo IsCreateGameModeAvailable { get; }

        internal Type Availability { get; }

        internal MethodInfo PushHostScreen { get; }

        internal MethodInfo GetLobbyGameMode { get; }

        /// <summary>
        /// Full name of <c>GetLobbyGameMode</c>'s first parameter. The replacement is written for
        /// <c>GameMode</c>; anything else is reported and the seam is skipped.
        /// </summary>
        internal string GetLobbyGameModeFirstParameterType { get; }

        /// <summary>Decorative: may be null.</summary>
        internal MethodInfo? GetLobbyGameModeLabel { get; }

        internal MethodInfo ScheduleEnsureInviteButton { get; }

        internal static Shape? TryCapture(Assembly host, out string reason)
        {
            List<string> missing = new();

            Type? overlay = host.GetType(OverlayTypeName, throwOnError: false, ignoreCase: false);
            Type? hostFlow = host.GetType(HostFlowTypeName, throwOnError: false, ignoreCase: false);
            Type? binding = host.GetType(BindingTypeName, throwOnError: false, ignoreCase: false);
            Type? invite = host.GetType(InviteButtonPatchTypeName, throwOnError: false, ignoreCase: false);
            Type? availability = host.GetType(AvailabilityTypeName, throwOnError: false, ignoreCase: false);

            if (overlay == null) missing.Add(OverlayTypeName);
            if (hostFlow == null) missing.Add(HostFlowTypeName);
            if (binding == null) missing.Add(BindingTypeName);
            if (invite == null) missing.Add(InviteButtonPatchTypeName);
            if (availability == null) missing.Add(AvailabilityTypeName);

            // Name first (no typeof), falling back to the assembly this file already references through
            // GameMode: the fallback adds no coupling that MapCreateId does not already have, and it removes
            // a single point of failure if the game assembly is ever loaded under another simple name.
            Assembly game = Reflect.FindAssembly(GameAssemblyName) ?? typeof(GameMode).Assembly;
            foreach (string screenType in new[]
                     {
                         CharacterSelectTypeName, DailyRunScreenTypeName, CustomRunScreenTypeName,
                         BlitzScreenTypeName, DreadScreenTypeName
                     })
            {
                if (game.GetType(screenType, throwOnError: false, ignoreCase: false) == null)
                {
                    missing.Add(screenType);
                }
            }

            // Instance, not static: the real seam is "private Control BuildCreateDialog()" on the overlay
            // (dec-official LanConnectLobbyOverlay.cs:6314) and the postfix takes __instance. Demanding static
            // here disabled this whole repair on the shipped 0.6.3 build — the probe was wrong, not the host.
            MethodInfo? buildCreateDialog = Unique(overlay, "BuildCreateDialog", 0, missing);

            FieldInfo? roomTypeOption = overlay?.GetField(
                "_roomTypeOption",
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (roomTypeOption == null)
            {
                missing.Add(OverlayShort + "._roomTypeOption (OptionButton)");
            }

            MethodInfo? modeFromId = Unique(overlay, "GameModeFromCreateId", 1, missing);
            if (modeFromId != null)
            {
                if (!modeFromId.IsStatic)
                {
                    missing.Add(OverlayShort + ".GameModeFromCreateId (instance, expected static)");
                    modeFromId = null;
                }
                else if (!ParametersMatch(modeFromId, IntTypeName))
                {
                    missing.Add(OverlayShort + ".GameModeFromCreateId(int) -> GameMode (found: " +
                                Describe(modeFromId) + ")");
                    modeFromId = null;
                }
                else if (modeFromId.ReturnType.FullName != GameModeTypeName)
                {
                    missing.Add(OverlayShort + ".GameModeFromCreateId -> GameMode (found: " +
                                Short(modeFromId.ReturnType.FullName) + ")");
                    modeFromId = null;
                }
            }

            MethodInfo? selected = Unique(overlay, "GetSelectedCreateGameMode", 0, missing);
            if (selected != null)
            {
                if (selected.IsStatic)
                {
                    missing.Add(OverlayShort + ".GetSelectedCreateGameMode (static, expected an instance method)");
                    selected = null;
                }
                else if (selected.ReturnType.FullName != GameModeTypeName)
                {
                    missing.Add(OverlayShort + ".GetSelectedCreateGameMode -> GameMode (found: " +
                                Short(selected.ReturnType.FullName) + ")");
                    selected = null;
                }
            }

            FieldInfo? selectedId = overlay?.GetField(
                "_selectedCreateGameModeId",
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (selectedId == null)
            {
                missing.Add(OverlayShort + "._selectedCreateGameModeId");
            }
            else if (selectedId.FieldType != typeof(int))
            {
                missing.Add(OverlayShort + "._selectedCreateGameModeId (int) (found: " +
                            selectedId.FieldType.Name + ")");
                selectedId = null;
            }

            MethodInfo? isAvailable = Unique(overlay, "IsCreateGameModeAvailable", 2, missing);
            if (isAvailable != null)
            {
                if (!isAvailable.IsStatic)
                {
                    missing.Add(OverlayShort + ".IsCreateGameModeAvailable (instance, expected static)");
                    isAvailable = null;
                }
                else if (!ParametersMatch(isAvailable, GameModeTypeName, availability?.FullName ?? AvailabilityTypeName))
                {
                    missing.Add(OverlayShort + ".IsCreateGameModeAvailable(GameMode, " + AvailabilityShort +
                                ") -> bool (found: " + Describe(isAvailable) + ")");
                    isAvailable = null;
                }
            }

            MethodInfo? pushHostScreen = Unique(hostFlow, "PushHostScreen", 4, missing);
            if (pushHostScreen != null)
            {
                if (!pushHostScreen.IsStatic)
                {
                    missing.Add(HostFlowShort + ".PushHostScreen (instance, expected static)");
                    pushHostScreen = null;
                }
                else if (!ParametersMatch(
                             pushHostScreen, GameModeTypeName, SubmenuStackTypeName, HostGameServiceTypeName, IntTypeName))
                {
                    // The replacement switches on GameMode numbers (Standard=1/Daily=2/Custom=3) and pushes
                    // a screen per case. Binding it to any other first parameter would silently push the
                    // wrong screen for every mode instead of failing, so the shape is asserted here.
                    missing.Add(HostFlowShort + ".PushHostScreen(GameMode, NSubmenuStack, NetHostGameService, int) " +
                                "(found: " + Describe(pushHostScreen) + ")");
                    pushHostScreen = null;
                }
            }

            // Two overloads take exactly one parameter — GetLobbyGameMode(GameMode) and
            // GetLobbyGameMode(SerializableRun) (dec-official LanConnectMultiplayerSaveRoomBinding.cs:374/:401).
            // Only the GameMode one is this repair's seam, so pick by parameter type, not by arity: arity alone
            // is ambiguous and the probe used to give up, disabling the repair on the shipped build.
            MethodInfo? lobbyGameMode = binding == null
                ? null
                : binding
                    .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method => string.Equals(method.Name, "GetLobbyGameMode", StringComparison.Ordinal)
                                              && method.GetParameters() is { Length: 1 } parameters
                                              && string.Equals(parameters[0].ParameterType.FullName, GameModeTypeName, StringComparison.Ordinal));
            if (lobbyGameMode == null)
            {
                missing.Add(BindingShort + ".GetLobbyGameMode(" + GameModeTypeName + ")");
            }

            string lobbyGameModeParameter = lobbyGameMode?.GetParameters()[0].ParameterType.FullName ?? string.Empty;

            MethodInfo? lobbyLabel = binding == null
                ? null
                : binding
                    .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(method => string.Equals(method.Name, "GetLobbyGameModeLabel", StringComparison.Ordinal)
                                              && method.GetParameters().Length == 1
                                              && method.GetParameters()[0].ParameterType == typeof(string));

            MethodInfo? inviteScheduler = invite?
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => string.Equals(method.Name, "ScheduleEnsureInviteButton", StringComparison.Ordinal)
                                          && method.GetParameters().Length == 2
                                          && method.GetParameters()[0].ParameterType.FullName == CharacterSelectTypeName
                                          && method.GetParameters()[1].ParameterType == typeof(string));
            if (inviteScheduler == null)
            {
                missing.Add(InviteShort + ".ScheduleEnsureInviteButton(NCharacterSelectScreen, string)");
            }

            if (missing.Count > 0 || roomTypeOption == null)
            {
                reason = "missing: " + string.Join(", ", missing);
                return null;
            }

            reason = string.Empty;
            Shape shape = new(
                buildCreateDialog!,
                roomTypeOption!,
                modeFromId!,
                selected!,
                selectedId!,
                isAvailable!,
                availability!,
                pushHostScreen!,
                lobbyGameMode!,
                lobbyGameModeParameter,
                lobbyLabel,
                inviteScheduler!);

            return shape;
        }

        /// <summary>Exact parameter-type match, by full name, so no <c>typeof</c> is needed.</summary>
        private static bool ParametersMatch(MethodInfo method, params string[] expectedTypeNames)
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != expectedTypeNames.Length)
            {
                return false;
            }

            for (int index = 0; index < expectedTypeNames.Length; index++)
            {
                if (parameters[index].ParameterType.FullName != expectedTypeNames[index])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Compact signature, for the "we did not bind" reason line.</summary>
        private static string Describe(MethodInfo method) =>
            "(" + string.Join(", ", method.GetParameters().Select(parameter => Short(parameter.ParameterType.FullName))) +
            ") -> " + Short(method.ReturnType.FullName);

        /// <summary>Last segment of a type name. The reason line is read by a person, not by a parser.</summary>
        private static string Short(string? fullName)
        {
            if (string.IsNullOrEmpty(fullName))
            {
                return "<unknown>";
            }

            int dot = fullName.LastIndexOf('.');
            return dot >= 0 ? fullName[(dot + 1)..] : fullName;
        }

        private static MethodInfo? Unique(Type? type, string name, int parameterCount, List<string> missing)
        {
            if (type == null)
            {
                return null;
            }

            MethodInfo[] matches = type
                .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal))
                .Where(method => method.GetParameters().Length == parameterCount)
                .ToArray();

            if (matches.Length == 1)
            {
                return matches[0];
            }

            missing.Add(matches.Length == 0
                ? type.Name + "." + name + "/" + parameterCount + " param(s)"
                : type.Name + "." + name + "/" + parameterCount + " param(s) (ambiguous: " + matches.Length + " overloads)");
            return null;
        }
    }
}
