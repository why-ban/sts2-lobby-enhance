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
/// </summary>
internal sealed class ExtraGameModesFix : ICompatFix
{
    private const string OverlayTypeName = "Sts2LanConnect.Scripts.LanConnectLobbyOverlay";
    private const string HostFlowTypeName = "Sts2LanConnect.Scripts.LanConnectHostFlow";
    private const string BindingTypeName = "Sts2LanConnect.Scripts.LanConnectMultiplayerSaveRoomBinding";
    private const string InviteButtonPatchTypeName = "Sts2LanConnect.Scripts.LanConnectInviteButtonPatch";

    /// <summary>Dropdown id and mode value for Blitz; matches the game's own <c>GameMode.Blitz</c>.</summary>
    private const int BlitzValue = 4;

    /// <summary>Sentinel mode value for Dread, chosen because the host mod already spends 3 on Custom.</summary>
    private const int DreadSentinel = 5;

    public string Id => "extra-game-modes";

    public bool Applies(in CompatSnapshot snapshot) => true;

    public void Apply(Harmony harmony, in CompatSnapshot snapshot)
    {
        Assembly host = Reflect.FindAssembly(Entry.HostModAssemblyName)
            ?? throw new InvalidOperationException("Host mod assembly is not loaded.");

        Type overlay = Reflect.RequireType(host, OverlayTypeName);
        Type hostFlow = Reflect.RequireType(host, HostFlowTypeName);
        Type binding = Reflect.RequireType(host, BindingTypeName);

        // 1) Append the two entries to the create dialog's room-type dropdown.
        MethodInfo buildDialog = Reflect.RequireMethodByName(overlay, "BuildCreateDialog", 0);
        harmony.Patch(
            buildDialog,
            postfix: new HarmonyMethod(typeof(ExtraGameModesFix), nameof(BuildCreateDialogPostfix))
            {
                priority = Priority.Last
            });

        // 2) dropdown id -> mode value, and the currently selected mode.
        MethodInfo modeFromId = Reflect.RequireMethodByName(overlay, "GameModeFromCreateId", 1);
        ReplacementPlans[modeFromId] = typeof(ExtraGameModesFix).GetMethod(
            nameof(MapCreateId), BindingFlags.Public | BindingFlags.Static)!;
        harmony.Patch(
            modeFromId,
            transpiler: new HarmonyMethod(typeof(ExtraGameModesFix), nameof(ConstantResultTranspiler))
            {
                priority = Priority.First
            });

        MethodInfo getSelected = Reflect.RequireMethodByName(overlay, "GetSelectedCreateGameMode", 0);
        FieldInfo selectedIdField = Reflect.RequireField(overlay, "_selectedCreateGameModeId");
        SelectedModePlan = (selectedIdField, typeof(ExtraGameModesFix).GetMethod(
            nameof(MapCreateId), BindingFlags.Public | BindingFlags.Static)!);
        harmony.Patch(
            getSelected,
            transpiler: new HarmonyMethod(typeof(ExtraGameModesFix), nameof(SelectedModeTranspiler))
            {
                priority = Priority.First
            });

        // 3) availability gate: the two new modes are never locked.
        MethodInfo isAvailable = Reflect.RequireMethodByName(overlay, "IsCreateGameModeAvailable", 2);
        Type availabilityType = Reflect.RequireType(host, "Sts2LanConnect.Scripts.LanConnectLanHostModeAvailability");
        PatchWholeBodyWithCall(harmony, isAvailable, nameof(IsCreateGameModeAvailableReplacement), null, availabilityType);

        // 4) push the matching 0.113 screen when hosting, then re-create the invite button like upstream does.
        MethodInfo pushHostScreen = Reflect.RequireMethodByName(hostFlow, "PushHostScreen", 4);
        _scheduleEnsureInviteButton = Reflect.RequireStaticMethod(
            Reflect.RequireType(host, InviteButtonPatchTypeName),
            "ScheduleEnsureInviteButton",
            typeof(MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen),
            typeof(string));
        PatchWholeBodyWithCall(harmony, pushHostScreen, nameof(PushHostScreenReplacement), null, null, null, null);

        // 5) the lobby-facing mode string.
        MethodInfo getLobbyGameMode = Reflect.RequireStaticMethod(binding, "GetLobbyGameMode", typeof(GameMode));
        ReplacementPlans[getLobbyGameMode] = typeof(ExtraGameModesFix).GetMethod(
            nameof(MapGameModeString), BindingFlags.Public | BindingFlags.Static)!;
        harmony.Patch(
            getLobbyGameMode,
            transpiler: new HarmonyMethod(typeof(ExtraGameModesFix), nameof(ConstantResultTranspiler))
            {
                priority = Priority.First
            });

        // 6) friendly Chinese labels in the room list.
        MethodInfo getLabel = Reflect.RequireStaticMethod(binding, "GetLobbyGameModeLabel", typeof(string));
        harmony.Patch(
            getLabel,
            prefix: new HarmonyMethod(typeof(ExtraGameModesFix), nameof(GetLobbyGameModeLabelPrefix))
            {
                priority = Priority.First
            });

        // 7) the mode pill on each room card and in the selected-room summary. 0.6.3's
        //    GetRoomGameModePill only knows "daily", "custom" and a catch-all "STD"
        //    (LanConnectLobbyOverlay.cs:7943-7950), so the "blitz"/"dread" strings this fix feeds into
        //    GameMode were labelled STD. The colour properties are private *properties*, not fields, whose
        //    getters are one-line reads of LobbyTheme.Palette (:3492 Success, :3501 Danger, :3438
        //    SurfaceMuted); they are resolved here so a host build that drops one fails Apply instead of
        //    silently mislabelling rooms.
        MethodInfo getRoomGameModePill = Reflect.RequireMethodByName(overlay, "GetRoomGameModePill", 1);
        PillColors = (
            Reflect.RequireProperty(overlay, "SuccessColor"),
            Reflect.RequireProperty(overlay, "DangerColor"),
            Reflect.RequireProperty(overlay, "SurfaceMutedColor"));
        harmony.Patch(
            getRoomGameModePill,
            postfix: new HarmonyMethod(typeof(ExtraGameModesFix), nameof(GetRoomGameModePillPostfix))
            {
                priority = Priority.Last
            });

        Entry.Info(
            "extra-game-modes: added 极速模式 (Blitz) and 恐惧模式 (Dread) to the create-room picker " +
            $"(dropdown ids 3/4, mode values {BlitzValue}/{DreadSentinel}, availability, host screen + " +
            "invite button, lobby gameMode string, label, room-card mode pill).");
    }

    // ------------------------------------------------------------------ IL plans

    private static readonly Dictionary<MethodBase, MethodInfo> ReplacementPlans = new();

    private static (FieldInfo IdField, MethodInfo Helper) SelectedModePlan;

    /// <summary>
    /// <c>LanConnectInviteButtonPatch.ScheduleEnsureInviteButton(NCharacterSelectScreen, string)</c>, captured during
    /// <see cref="Apply"/>. Upstream calls it as the last statement of the Standard branch of <c>PushHostScreen</c>
    /// (<c>dec-official/LanConnectHostFlow.cs:545</c>); without that call a Standard host never gets an invite button
    /// for the room it just created. Captured eagerly so a host mod that stops exposing it fails this fix loudly
    /// instead of silently losing the button.
    /// </summary>
    private static MethodInfo? _scheduleEnsureInviteButton;

    /// <summary>
    /// <c>LanConnectLobbyOverlay</c>'s private static <c>Color</c> properties read by
    /// <see cref="GetRoomGameModePillPostfix"/>, captured during <see cref="Apply"/>.
    ///
    /// They are properties, not fields (<c>LanConnectLobbyOverlay.cs:3492</c> Success,
    /// <c>:3501</c> Danger, <c>:3438</c> SurfaceMuted), and each getter is a single read of
    /// <c>LobbyTheme.Palette</c>. The values are therefore re-read on every pill instead of being
    /// cached here: the theme can change while the lobby is open (<c>LanConnectLobbyThemes.Changed</c>,
    /// <c>LanConnectLobbyThemes.cs:197</c>), and a cached <c>Color</c> would keep the old palette.
    /// </summary>
    private static (PropertyInfo Success, PropertyInfo Danger, PropertyInfo SurfaceMuted) PillColors;

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
        string helperName,
        params Type?[] boxTypes)
    {
        MethodInfo helper = typeof(ExtraGameModesFix).GetMethod(helperName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(typeof(ExtraGameModesFix).FullName, helperName);

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
        if (Reflect.GetField(__instance, "_roomTypeOption") is not OptionButton option)
        {
            return;
        }

        AppendModeOption(option, 3, "极速模式");
        AppendModeOption(option, 4, "恐惧模式");
    }

    private static void AppendModeOption(OptionButton option, int id, string label)
    {
        for (int index = 0; index < option.ItemCount; index++)
        {
            if (option.GetItemId(index) == id)
            {
                return;
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

    /// <summary>
    /// Postfix on <c>LanConnectLobbyOverlay.GetRoomGameModePill(string?)</c> — the pill drawn on every
    /// room card (<c>LanConnectLobbyOverlay.cs:8357</c>), in the selected-room summary (<c>:5971</c>) and
    /// in the accessibility announcement (<c>:8317</c>).
    ///
    /// 0.6.3 maps the mode string to exactly three labels: "daily" → 挑战, "custom" → 自定义, and
    /// anything else → "STD" (<c>:7943-7950</c>). The two strings this fix produces for the extra modes
    /// (<see cref="MapGameModeString"/>: <c>blitz</c>/<c>dread</c>) are "anything else", which is why the
    /// user saw STD on those rooms. Only those two inputs are rewritten; daily, custom, standard and null
    /// keep whatever the original returned.
    /// </summary>
    private static void GetRoomGameModePillPostfix(
        string? gameMode,
        ref (string Text, Color Border, Color Background) __result)
    {
        switch (gameMode?.Trim().ToLowerInvariant())
        {
            case "blitz":
                __result = (
                    "极速",
                    PillColor(PillColors.Success, "SuccessColor"),
                    PillColor(PillColors.SurfaceMuted, "SurfaceMutedColor"));
                break;
            case "dread":
                __result = (
                    "恐惧",
                    PillColor(PillColors.Danger, "DangerColor"),
                    PillColor(PillColors.SurfaceMuted, "SurfaceMutedColor"));
                break;
        }
    }

    /// <summary>Reads one of the captured static <c>Color</c> properties, failing loudly if it is gone.</summary>
    private static Color PillColor(PropertyInfo? property, string name)
    {
        if (property is null)
        {
            throw new InvalidOperationException(
                $"extra-game-modes: {OverlayTypeName}.{name} was not captured by Apply().");
        }

        return property.GetValue(null) is Color color
            ? color
            : throw new InvalidOperationException(
                $"extra-game-modes: {OverlayTypeName}.{name} did not return a Color.");
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
    /// Full replacement for the host mod's availability check. Reimplemented rather than wrapped because
    /// the original takes a value type we cannot pass through Harmony, and its logic is a plain switch on
    /// the mode value.
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
    private static void ScheduleInviteButton(MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen screen)
    {
        MethodInfo scheduler = _scheduleEnsureInviteButton
            ?? throw new InvalidOperationException(
                "extra-game-modes: " + InviteButtonPatchTypeName + ".ScheduleEnsureInviteButton was not captured; " +
                "the Standard host screen would be pushed without an invite button.");

        scheduler.Invoke(null, new object?[] { screen, "push_host_screen" });
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
                ScheduleInviteButton(screen);
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
}

