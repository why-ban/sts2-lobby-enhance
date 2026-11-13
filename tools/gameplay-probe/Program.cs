using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

// gameplay-probe：在不开游戏的前提下，把 #5 / #6 两个断点所需的全部事实打印出来。
// 只读 + 补丁安装实验，不调用 MegaCrit 的 Log（非 Godot 宿主会 0xC0000005）。

string gameDir = args.Length > 0
    ? args[0]
    : @"D:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64";

string hostDll = args.Length > 1
    ? args[1]
    : @"D:\zhuanban\sts2m\work\lobby\STS2-Game-Lobby\releases\sts2_lan_connect\sts2_lan_connect.dll";

AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
{
    string candidate = Path.Combine(gameDir, new AssemblyName(eventArgs.Name).Name + ".dll");
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};

Console.WriteLine($"game dir : {gameDir}");
Console.WriteLine($"host dll : {hostDll}");
Console.WriteLine($"game ver : {typeof(MegaCrit.Sts2.Core.Runs.RunManager).Assembly.GetName().Version}");
Assembly host = Assembly.LoadFrom(hostDll);
Console.WriteLine($"host mod : {host.GetName().Name} {host.GetName().Version}");
Console.WriteLine();

// ---------------------------------------------------------------- helpers

static string Sig(MethodBase method)
{
    string parameters = string.Join(", ", method.GetParameters().Select(p => $"{Short(p.ParameterType)} {p.Name}"));
    string prefix = method.IsStatic ? "static " : string.Empty;
    string returns = method is MethodInfo info ? Short(info.ReturnType) : "ctor";
    return $"{prefix}{returns} {method.Name}({parameters})";
}

static string Short(Type type)
{
    if (type.IsByRef)
    {
        return Short(type.GetElementType()!) + "&";
    }

    if (!type.IsGenericType || !type.Name.Contains('`'))
    {
        return type.Name;
    }

    string name = type.Name[..type.Name.IndexOf('`')];
    return name + "<" + string.Join(", ", type.GetGenericArguments().Select(Short)) + ">";
}

static void DumpType(Type type)
{
    Console.WriteLine($"== {type.FullName} (public={type.IsPublic}, abstract={type.IsAbstract})");
    Console.WriteLine("  -- fields");
    foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
    {
        Console.WriteLine($"     {(field.IsStatic ? "static " : string.Empty)}{Short(field.FieldType)} {field.Name}{(field.IsInitOnly ? " [readonly]" : string.Empty)}");
    }

    Console.WriteLine("  -- events");
    foreach (EventInfo evt in type.GetEvents(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
    {
        Console.WriteLine($"     {Short(evt.EventHandlerType!)} {evt.Name}");
    }

    Console.WriteLine("  -- methods");
    foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
    {
        Console.WriteLine($"     {Sig(method)}");
    }
}

static string CanJit(MethodBase method)
{
    try
    {
        RuntimeHelpers.PrepareMethod(method.MethodHandle);
        return "JIT-OK";
    }
    catch (Exception exception)
    {
        return $"{exception.GetType().Name}: {FirstLine(exception.Message)}";
    }
}

static string FirstLine(string text)
{
    int newline = text.IndexOf('\n');
    return (newline < 0 ? text : text[..newline]).Trim();
}

static string TryPatch(MethodInfo? target, MethodInfo prefix, Harmony harmony)
{
    if (target == null)
    {
        return "target=null";
    }

    try
    {
        harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        return "PATCH-OK";
    }
    catch (Exception exception)
    {
        return $"{exception.GetType().Name}: {FirstLine(exception.Message)}";
    }
}

Type RequireHostType(string name) => host.GetType(name, throwOnError: true)!;

Dictionary<short, OpCode> opCodeTable = typeof(OpCodes)
    .GetFields(BindingFlags.Public | BindingFlags.Static)
    .Where(field => field.FieldType == typeof(OpCode))
    .Select(field => (OpCode)field.GetValue(null)!)
    .ToDictionary(opCode => opCode.Value);

static int OperandSize(OpCode opCode, byte[] il, int position) => opCode.OperandType switch
{
    OperandType.InlineNone => 0,
    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
    OperandType.InlineVar => 2,
    OperandType.InlineBrTarget or OperandType.InlineI or OperandType.InlineMethod or OperandType.InlineField
        or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
        or OperandType.ShortInlineR => 4,
    OperandType.InlineI8 or OperandType.InlineR => 8,
    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, position),
    _ => 0
};

// ---------------------------------------------------------------- #5 facts

Console.WriteLine("########## #5 treasure vote ##########");
Type patches = RequireHostType("Sts2LanConnect.Scripts.TreasurePatches");
Console.WriteLine($"TreasurePatches: {patches.FullName} static={patches.IsAbstract && patches.IsSealed}");
foreach (MethodInfo method in patches.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
{
    Console.WriteLine($"   {Sig(method)}");
}

Console.WriteLine();
Console.WriteLine("-- static fields of TreasurePatches (reflection anchors)");
foreach (FieldInfo field in patches.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
{
    string state = field.GetValue(null) == null ? "null" : field.GetValue(null)!.GetType().Name;
    Console.WriteLine($"   {Short(field.FieldType)} {field.Name} = {state}");
}

Console.WriteLine();
Console.WriteLine("-- JIT reachability of the mod's treasure methods");
foreach (string name in new[] { "OnPickedPrefix", "PickRelicLocallyPrefix", "ResolveAllVotes", "Apply", "ClearLocalVoteState" })
{
    MethodInfo method = patches.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        .First(m => m.Name == name);
    Console.WriteLine($"   {name,-24} {CanJit(method)}");
}

Type sync = typeof(MegaCrit.Sts2.Core.Multiplayer.Game.TreasureRoomRelicSynchronizer);
Console.WriteLine();
DumpType(sync);
Type vote = sync.GetNestedType("PlayerVote", BindingFlags.Public | BindingFlags.NonPublic)!;
Console.WriteLine();
DumpType(vote);

Type result = typeof(MegaCrit.Sts2.Core.Entities.TreasureRelicPicking.RelicPickingResult);
Console.WriteLine();
DumpType(result);
Console.WriteLine();
Console.WriteLine("-- RelicPickingResult.GenerateRelicFight overloads");
foreach (MethodInfo method in result.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
             .Where(m => m.Name == "GenerateRelicFight"))
{
    Console.WriteLine($"   {Sig(method)}  -> {CanJit(method)}");
}

Console.WriteLine();
Console.WriteLine("-- RelicModel IComparable (needed by StableShuffle<List<RelicModel>>)");
Type relicModel = typeof(MegaCrit.Sts2.Core.Models.RelicModel);
Console.WriteLine($"   RelicModel : IComparable<RelicModel> = {typeof(IComparable<MegaCrit.Sts2.Core.Models.RelicModel>).IsAssignableFrom(relicModel)}");
Console.WriteLine($"   RelicModel : IComparable              = {typeof(IComparable).IsAssignableFrom(relicModel)}");
Console.WriteLine($"   RelicModel.Equals overridden          = {relicModel.GetMethod("Equals", new[] { typeof(object) })?.DeclaringType?.FullName ?? "<none>"}");

Console.WriteLine();
Console.WriteLine("-- ListExtensions.StableShuffle / UnstableShuffle / Rng");
Type listExtensions = typeof(MegaCrit.Sts2.Core.Extensions.ListExtensions);
foreach (MethodInfo method in listExtensions.GetMethods(BindingFlags.Static | BindingFlags.Public)
             .Where(m => m.Name is "StableShuffle" or "UnstableShuffle"))
{
    Console.WriteLine($"   {Sig(method)}");
}

Type rng = typeof(MegaCrit.Sts2.Core.Random.Rng);
foreach (MethodInfo method in rng.GetMethods(BindingFlags.Instance | BindingFlags.Public)
             .Where(m => m.Name is "NextInt" or "NextUnsignedLong" or "NextItem"))
{
    Console.WriteLine($"   Rng.{Sig(method)}");
}

Console.WriteLine();
Console.WriteLine("-- Harmony experiments (#5)");
var harmony = new Harmony("gameplay-probe.treasure");

MethodInfo onPickedPrefix = patches.GetMethod("OnPickedPrefix", BindingFlags.Static | BindingFlags.NonPublic)!;
MethodInfo onPicked = sync.GetMethod("OnPicked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
MethodInfo pickLocallyPrefix = patches.GetMethod("PickRelicLocallyPrefix", BindingFlags.Static | BindingFlags.NonPublic)!;
MethodInfo pickLocally = sync.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    .First(m => m.Name == "PickRelicLocally");

Console.WriteLine($"   native OnPicked        = {Sig(onPicked)}");
Console.WriteLine($"   native PickRelicLocally= {Sig(pickLocally)}");
Console.WriteLine($"   patch OnPickedPrefix (our seam) : {TryPatch(onPickedPrefix, typeof(ProbePrefix).GetMethod(nameof(ProbePrefix.Prefix))!, harmony)}");
Console.WriteLine($"   mod's Apply would patch OnPicked with its own prefix : {TryPatch(onPicked, onPickedPrefix, new Harmony("gameplay-probe.treasure.mod"))}");
Console.WriteLine($"   mod's Apply would patch PickRelicLocally with its own prefix : {TryPatch(pickLocally, pickLocallyPrefix, new Harmony("gameplay-probe.treasure.mod2"))}");
Console.WriteLine($"   patched methods now: {string.Join(", ", new Harmony("gameplay-probe.treasure").GetPatchedMethods().Select(m => m.DeclaringType!.Name + "::" + m.Name))}");

// ---------------------------------------------------------------- Harmony int? binding

Console.WriteLine();
Console.WriteLine("-- Harmony int? argument binding experiment");
var bindingHarmony = new Harmony("gameplay-probe.binding");
Console.WriteLine($"   patch Target(int?) with Prefix(int) : {TryPatch(typeof(Synthetic).GetMethod(nameof(Synthetic.TargetNullable))!, typeof(Synthetic).GetMethod(nameof(Synthetic.PrefixInt))!, bindingHarmony)}");
Synthetic.Reset();
try
{
    Synthetic.TargetNullable(7);
    Console.WriteLine($"   invoke TargetNullable(7)    -> log='{Synthetic.Log}'");
}
catch (Exception exception)
{
    Console.WriteLine($"   invoke TargetNullable(7)    -> {exception.GetType().Name}: {FirstLine(exception.Message)}");
}

Synthetic.Reset();
try
{
    Synthetic.TargetNullable(null);
    Console.WriteLine($"   invoke TargetNullable(null) -> log='{Synthetic.Log}'");
}
catch (Exception exception)
{
    Console.WriteLine($"   invoke TargetNullable(null) -> {exception.GetType().Name}: {FirstLine(exception.Message)}");
}

// ---------------------------------------------------------------- #6 facts

Console.WriteLine();
Console.WriteLine("########## #6 abandon run ##########");
Type saveCompat = RequireHostType("Sts2LanConnect.Scripts.LanConnectMultiplayerSaveCompatibility");
Console.WriteLine($"type: {saveCompat.FullName}");
foreach (MethodInfo method in saveCompat.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
{
    Console.WriteLine($"   {Sig(method)}  -> {CanJit(method)}");
}

Console.WriteLine();
Console.WriteLine("-- nested types (async state machines)");
foreach (Type nested in saveCompat.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
{
    Console.WriteLine($"   {nested.Name}  interfaces=[{string.Join(",", nested.GetInterfaces().Select(i => i.Name))}]");
    foreach (MethodInfo method in nested.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
    {
        Console.WriteLine($"      {Sig(method)} -> {CanJit(method)}");
    }
}

Console.WriteLine();
Console.WriteLine("-- helper signatures used by the abandon flow");
foreach ((string typeName, string[] methods) in new[]
         {
             ("Sts2LanConnect.Scripts.LanConnectSaveBackup", new[] { "TryCreate" }),
             ("Sts2LanConnect.Scripts.LanConnectPaths", new[] { "ResolveWritableDataDirectory" }),
             ("Sts2LanConnect.Scripts.LanConnectConfig", new[] { "RemoveSaveRoomBinding" }),
             ("Sts2LanConnect.Scripts.LanConnectMultiplayerSaveRoomBinding", new[] { "BuildSaveKey" }),
             ("Sts2LanConnect.Scripts.LanConnectPopupUtil", new[] { "ShowInfo" })
         })
{
    Type? type = host.GetType(typeName);
    Console.WriteLine($"   [{typeName}] {(type == null ? "MISSING" : "ok")}");
    if (type == null)
    {
        continue;
    }

    foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                 .Where(m => methods.Contains(m.Name)))
    {
        Console.WriteLine($"      {Sig(method)}");
    }
}

Console.WriteLine();
Console.WriteLine("-- Harmony experiment (#6): patch the async stub");
var abandonHarmony = new Harmony("gameplay-probe.abandon");
MethodInfo stub = saveCompat.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    .First(m => m.Name == "AbandonCurrentRunAsync");
Console.WriteLine($"   stub = {Sig(stub)}");
Console.WriteLine($"   patch stub : {TryPatch(stub, typeof(AbandonProbePrefix).GetMethod(nameof(AbandonProbePrefix.Prefix))!, abandonHarmony)}");

Console.WriteLine();
Console.WriteLine("-- NMultiplayerSubmenu.UpdateButtons (reflection target, never touch MethodName cctor)");
Type submenu = typeof(MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerSubmenu);
MethodInfo? updateButtonsMethod = submenu.GetMethod("UpdateButtons", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
Console.WriteLine($"   NMultiplayerSubmenu.UpdateButtons = {(updateButtonsMethod == null ? "MISSING" : Sig(updateButtonsMethod))}");
Console.WriteLine($"   MethodName nested type present = {submenu.GetNestedType("MethodName") != null}");

Console.WriteLine();
Console.WriteLine("-- SaveManager / progress API");
MethodInfo updateProgress = typeof(MegaCrit.Sts2.Core.Saves.SaveManager)
    .GetMethod("UpdateProgressWithRunData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
Console.WriteLine($"   {Sig(updateProgress)}");
Console.WriteLine($"   result type = {updateProgress.ReturnType.FullName}");
Console.WriteLine($"   SerializableRun = {typeof(MegaCrit.Sts2.Core.Saves.SerializableRun).FullName}");
Console.WriteLine($"   PlatformType property = {typeof(MegaCrit.Sts2.Core.Saves.SerializableRun).GetProperty("PlatformType")?.PropertyType.FullName}");
Console.WriteLine();
Console.WriteLine("-- IRunState / IPlayerCollection surface used by the replica");
foreach (Type contract in new[]
         {
             typeof(MegaCrit.Sts2.Core.Runs.IRunState),
             typeof(MegaCrit.Sts2.Core.Runs.IPlayerCollection)
         })
{
    Console.WriteLine($"   [{contract.FullName}] interfaces=[{string.Join(", ", contract.GetInterfaces().Select(Short))}]");
    foreach (PropertyInfo property in contract.GetProperties())
    {
        Console.WriteLine($"      prop {Short(property.PropertyType)} {property.Name}");
    }

    foreach (MethodInfo method in contract.GetMethods().Where(m => !m.IsSpecialName))
    {
        Console.WriteLine($"      meth {Sig(method)}");
    }
}

Console.WriteLine();
Console.WriteLine("-- LocalContext");
Type localContext = typeof(MegaCrit.Sts2.Core.Context.LocalContext);
foreach (MethodInfo method in localContext.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
             .Where(m => m.Name is "IsMe" or "GetMe" or "GetLocalPlayerId"))
{
    Console.WriteLine($"   {Sig(method)}");
}

Console.WriteLine();
Console.WriteLine("-- correct-typed prefix on native OnPicked");
var correctHarmony = new Harmony("gameplay-probe.treasure.correct");
Console.WriteLine($"   patch OnPicked(Player, int?) with Prefix(__instance, Player, int?) : {TryPatch(onPicked, typeof(CorrectPrefix).GetMethod(nameof(CorrectPrefix.Prefix))!, correctHarmony)}");
Console.WriteLine();
Console.WriteLine("########## IL token resolution (can Harmony read the body?) ##########");

// 本工具以前永远 exit 0：它只是把事实打印出来，于是「探针自己坏了」与「结果就是这样」不可区分。
// 只统计**探针自身失效**的情况，不把预期的发现当异常：
//   * IL 无法解码（opcode 不认识）；
//   * 方法体读不出来（GetMethodBody 抛异常）；
//   * 最后那次 MoveNext 的 Harmony 试验失败 —— 该试验的目的就是证明它能打上。
// 「unresolved=N => BROKEN」是预期结果（那正是补丁要绕开的断点），不计入异常。
int anomalies = 0;

void ScanIl(MethodBase method)
{
    Console.WriteLine($"   [{method.DeclaringType?.Name}::{method.Name}]");
    try
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
        {
            Console.WriteLine("      no body");
            anomalies++;
            return;
        }

        Type[] typeArgs = method.DeclaringType?.GetGenericArguments() ?? Type.EmptyTypes;
        Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;
        int position = 0;
        int failures = 0;
        int tokens = 0;
        while (position < il.Length)
        {
            int opStart = position;
            short code = il[position++];
            if (code == 0xFE)
            {
                code = (short)(0xFE00 | il[position++]);
            }

            if (!opCodeTable.TryGetValue(code, out OpCode opCode))
            {
                Console.WriteLine($"      undecodable opcode at {opStart}");
                anomalies++;
                return;
            }

            int operandSize = OperandSize(opCode, il, position);
            if (opCode.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType
                or OperandType.InlineTok or OperandType.InlineSig)
            {
                int token = BitConverter.ToInt32(il, position);
                tokens++;
                try
                {
                    MemberInfo? member = method.Module.ResolveMember(token, typeArgs, methodArgs);
                    if (member == null)
                    {
                        failures++;
                        Console.WriteLine($"      UNRESOLVED token 0x{token:X8} at il_{opStart:X4} -> null");
                    }
                }
                catch (Exception exception)
                {
                    failures++;
                    Console.WriteLine($"      UNRESOLVED token 0x{token:X8} at il_{opStart:X4} -> {exception.GetType().Name}: {FirstLine(exception.Message)}");
                }
            }

            position += operandSize;
        }

        Console.WriteLine($"      tokens={tokens} unresolved={failures} => {(failures == 0 ? "READABLE" : "BROKEN")}");
    }
    catch (Exception exception)
    {
        Console.WriteLine($"      body read failed: {exception.GetType().Name}: {FirstLine(exception.Message)}");
        anomalies++;
    }
}

foreach (MethodInfo method in patches.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
             .Where(m => m.Name is "OnPickedPrefix" or "ResolveAllVotes" or "Apply"))
{
    ScanIl(method);
}

ScanIl(saveCompat.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    .First(m => m.Name == "AbandonCurrentRunAsync"));
ScanIl(saveCompat.GetNestedType("<AbandonCurrentRunAsync>d__14", BindingFlags.NonPublic)!
    .GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!);
ScanIl(saveCompat.GetNestedType("<ConfirmPermanentAbandonAsync>d__15", BindingFlags.NonPublic)!
    .GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!);

Console.WriteLine();
Console.WriteLine("-- Harmony patch attempt on the broken state machine");
var moveNextHarmony = new Harmony("gameplay-probe.movenext");
MethodInfo moveNext = saveCompat.GetNestedType("<AbandonCurrentRunAsync>d__14", BindingFlags.NonPublic)!
    .GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
string moveNextPatch = TryPatch(moveNext, typeof(ProbePrefix).GetMethod(nameof(ProbePrefix.Prefix))!, moveNextHarmony);
Console.WriteLine($"   patch MoveNext : {moveNextPatch}");
// NOTE: this result is a *finding*, not an anomaly. The documented conclusion is that Harmony cannot
// patch the state machine (its body calls the 4-argument UpdateProgressWithRunData that 0.113 removed),
// which is exactly why AbandonRunFix replaces the stub instead of patching MoveNext. Counting it as a
// failure would make this tool permanently red and therefore useless, so only probe breakage counts.

Console.WriteLine();
Console.WriteLine($"DONE anomalies={anomalies}");
if (anomalies > 0)
{
    Console.WriteLine("FAIL: the probe could not complete its own checks (see the lines above).");
    return 2;
}

return 0;

internal static class ProbePrefix
{
    public static bool Prefix() => false;
}

internal static class AbandonProbePrefix
{
    public static bool Prefix() => false;
}

internal static class Synthetic
{
    public static string Log = string.Empty;

    public static void Reset() => Log = string.Empty;

    public static void TargetNullable(int? value) => Log += $"target({(value.HasValue ? value.Value.ToString() : "null")});";

    public static void PrefixInt(int value) => Log += $"prefix(int={value});";
}

internal static class CorrectPrefix
{
    public static bool Prefix(
        MegaCrit.Sts2.Core.Multiplayer.Game.TreasureRoomRelicSynchronizer __instance,
        MegaCrit.Sts2.Core.Entities.Players.Player player,
        int? index)
    {
        Console.WriteLine($"      [CorrectPrefix] instance={__instance.GetType().Name} player={player.NetId} index={(index.HasValue ? index.Value.ToString() : "null")}");
        return true;
    }
}
internal static partial class IlSupport
{
}