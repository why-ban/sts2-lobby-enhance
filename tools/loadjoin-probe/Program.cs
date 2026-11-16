using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

// 离线复现「读档续玩必须按原顺序加入」：
//   1. 反射加载 0.113 的 sts2.dll、官方 sts2_lan_connect 0.6.3、本仓出货/最新构建的 sts2_lobby_enhance.dll
//      （全部从磁盘加载，无编译期引用，所以成员改名只会打印一行而不是编译失败）
//   2. 用真实类型搭一个 ClientLoadJoinResponseMessage：
//        serializableRun.Players  = 存档序（决定快照载体 RealSlotId 的那个顺序）
//        playersAlreadyConnected  = 加入序（宿主原样发的那个列表）
//   3. 快照由 LanConnectRosterAuthorityState.CommitHostSnapshot 提交，载体 RealSlotId = 存档下标
//      —— 与 tests/Sts2LobbyEnhance.Tests/RosterEpochRegressionTests.cs 的 RosterLab 同一套路
//   4. 修复前：反射调用 RestoreLoadJoin 必须抛
//        InvalidDataException: Loaded-lobby vanilla membership disagrees with the Tail roster.
//   5. 修复后：Entry.RunSilently() 装好补丁，同一输入必须成功返回，且返回列表就是存档序
//
// 退出码：0 全部符合预期 / 2 修复前没复现 / 3 修复后仍失败 / 4 对照用例失败 / 5 补丁装不上 / 7 输入缺失

const ulong HostId = 5_018_570_539_104_672_532UL;
const ulong PlayerX = 6_803_874_689_479_207_972UL;
const ulong PlayerY = 1_234_567_890_123_456_789UL;
const string MembershipDisagreement = "Loaded-lobby vanilla membership disagrees with the Tail roster.";
const BindingFlags AnyMember =
    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

string gameDir = args.Length > 0
    ? args[0]
    : @"D:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64";

string hostDll = args.Length > 1
    ? args[1]
    : @"D:\SteamLibrary\steamapps\workshop\content\2868840\3749766330\sts2_lan_connect.dll";

string? patchDll = args.Length > 2 ? args[2] : FindPatchDll();

Console.WriteLine("loadjoin-probe: load-join membership ORDER, offline reproduction");
Console.WriteLine("  game dir : " + gameDir);
Console.WriteLine("  host dll : " + hostDll);
Console.WriteLine("  patch dll: " + (patchDll ?? "(not found)"));
Console.WriteLine();

if (!File.Exists(Path.Combine(gameDir, "sts2.dll")))
{
    Console.WriteLine("SKIP: sts2.dll is not in that game data directory. Pass the directory as the first argument.");
    return 7;
}

if (!File.Exists(hostDll))
{
    Console.WriteLine("SKIP: host mod dll not found. Pass it as the second argument.");
    return 7;
}

if (patchDll is null || !File.Exists(patchDll))
{
    Console.WriteLine("SKIP: patch dll not found. Build it, or pass it as the third argument:");
    Console.WriteLine("  dotnet build src/Sts2LobbyEnhance/Sts2LobbyEnhance.csproj -c Release -p:SkipModCopy=true");
    return 7;
}

// 这些代码全部跑在宿主模组的协议路径里，死循环的表现不是异常而是工具静默挂住，所以必须有看门狗。
var watchdog = new Thread(() =>
{
    Thread.Sleep(TimeSpan.FromSeconds(120));
    Console.WriteLine();
    Console.WriteLine("WATCHDOG: 120 s elapsed without finishing; a patched method never returned.");
    Environment.Exit(9);
})
{ IsBackground = true, Name = "loadjoin-watchdog" };
watchdog.Start();

AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
{
    string candidate = Path.Combine(gameDir, new AssemblyName(eventArgs.Name).Name + ".dll");
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};

Assembly game = Assembly.LoadFrom(Path.Combine(gameDir, "sts2.dll"));
Assembly host = Assembly.LoadFrom(hostDll);
Assembly patch = Assembly.LoadFrom(patchDll);
Assembly harmonyAssembly = Assembly.LoadFrom(Path.Combine(gameDir, "0Harmony.dll"));

Console.WriteLine("  game     : " + game.GetName().Name + " " + game.GetName().Version);
Console.WriteLine("  host     : " + host.GetName().Name + " " + host.GetName().Version);
Console.WriteLine("  patch    : " + patch.GetName().Name + " " + patch.GetName().Version);
Console.WriteLine();

// ---------------------------------------------------------------------------- 锚点
Type messageType = Require(game, "MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby.ClientLoadJoinResponseMessage");
Type runType = Require(game, "MegaCrit.Sts2.Core.Saves.SerializableRun");
Type savedPlayerType = Require(game, "MegaCrit.Sts2.Core.Saves.Runs.SerializablePlayer");
Type lobbyPlayerType = Require(game, "MegaCrit.Sts2.Core.Entities.Multiplayer.LoadRunLobbyPlayer");
Type writerType = Require(game, "MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter");

Type runtimeType = Require(host, "Sts2LanConnect.Scripts.LanConnectTailMessageRuntime");
Type snapshotType = Require(host, "Sts2LanConnect.Scripts.LanConnectRosterSnapshot");
Type authorityType = Require(host, "Sts2LanConnect.Scripts.LanConnectRosterAuthorityState");
Type codecType = Require(host, "Sts2LanConnect.Scripts.LanConnectRosterCodec");
Type bindingType = runtimeType.GetNestedType("Binding", AnyMember)
    ?? throw new MissingMemberException(runtimeType.FullName, "Binding");

MethodInfo restoreLoadJoin = UniqueMethod(runtimeType, "RestoreLoadJoin", 4);
MethodInfo restoreLoadJoinPlayers = UniqueMethod(runtimeType, "RestoreLoadJoinPlayers", 3)
    .MakeGenericMethod(lobbyPlayerType);
MethodInfo commitHostSnapshot = UniqueMethod(authorityType, "CommitHostSnapshot", 1);
DecodeSpan decode = (DecodeSpan)UniqueMethod(codecType, "Decode", 1).CreateDelegate(typeof(DecodeSpan));

FieldInfo messageRunField = RequireField(messageType, "serializableRun");
FieldInfo messageConnectedField = RequireField(messageType, "playersAlreadyConnected");
PropertyInfo runPlayers = RequireProperty(runType, "Players");
PropertyInfo savedPlayerNetId = RequireProperty(savedPlayerType, "NetId");
FieldInfo lobbyPlayerId = RequireField(lobbyPlayerType, "id");
PropertyInfo writerBitPosition = RequireProperty(writerType, "BitPosition");
PropertyInfo writerBuffer = RequireProperty(writerType, "Buffer");
PropertyInfo writerWarnOnGrow = RequireProperty(writerType, "WarnOnGrow");
MethodInfo lobbyPlayerSerialize = UniqueMethod(lobbyPlayerType, "Serialize", 1);
PropertyInfo snapshotPlayers = RequireProperty(snapshotType, "Players");

Console.WriteLine("RestoreLoadJoin        : " + Describe(restoreLoadJoin));
Console.WriteLine("RestoreLoadJoinPlayers : " + Describe(restoreLoadJoinPlayers));
Console.WriteLine("message fields         : serializableRun=" + messageRunField.FieldType.Name +
                  ", playersAlreadyConnected=" + messageConnectedField.FieldType.Name);
Console.WriteLine();

// ------------------------------------------------------------ 场景：加入序 != 存档序
// 存档序 [H, X, Y]（房主先存），加入序 [H, Y, X]（Y 先加入）。这正是现场「第 2 个不按顺序的人」看到的形状：
// 已连接列表 3 项，第 2、3 位与存档序相反。
ulong[] saveOrder = [HostId, PlayerX, PlayerY];
ulong[] joinOrder = [HostId, PlayerY, PlayerX];

Console.WriteLine("---- BEFORE: RestoreLoadJoinPlayers (the comparison itself) ----");
object rawSnapshot = PublishSnapshot(saveOrder, joinOrder);
try
{
    _ = restoreLoadJoinPlayers.Invoke(null, [rawSnapshot, SavedPlayers(saveOrder), ConnectedList(joinOrder)]);
    Console.WriteLine("  UNEXPECTED: accepted join order [" + Ids(joinOrder) + "]");
}
catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException invalid)
{
    Console.WriteLine("  threw " + invalid.GetType().Name + ": " + invalid.Message);
}

Console.WriteLine();
Console.WriteLine("---- BEFORE: RestoreLoadJoin (production entry point) ----");
RestoreResult before = InvokeRestoreLoadJoin(saveOrder, joinOrder);
if (before.Failure is null)
{
    Console.WriteLine("  NO FAILURE: the order disagreement did not reproduce; restored [" + Ids(before.ResultIds) + "]");
    return 2;
}

Console.WriteLine("  threw " + before.Failure.GetType().Name + ": " + before.Failure.Message);
if (!string.Equals(before.Failure.Message, MembershipDisagreement, StringComparison.Ordinal))
{
    Console.WriteLine("  UNEXPECTED: that is not the membership-order rejection this probe is about.");
    return 2;
}

Console.WriteLine("  REPRODUCED (exit code 2 would mean it did not reproduce)");

RestoreResult controlBefore = InvokeRestoreLoadJoin(saveOrder, saveOrder);
Console.WriteLine(
    "  control (join order == save order) before the fix: " +
    (controlBefore.Failure is null
        ? "accepted, restored [" + Ids(controlBefore.ResultIds) + "]"
        : "threw " + controlBefore.Failure.Message));

// ---------------------------------------------------------------------------- 装补丁
Console.WriteLine();
Console.WriteLine("---- installing the patch ----");

Type entryType = Require(patch, "Sts2LobbyEnhance.Entry");

// Entry.Info/Warn 走 MegaCrit 的 Log，而那个类的静态构造要在活的 Godot 里解析 Godot.OS，进程外是
// 0xC0000005（捕获不到）。RunSilently 只在 Execute 期间抑制日志，之后任何 Entry.Info 都会崩掉本工具，
// 所以从这里开始由本工具自己把 Entry._suppressLogging 按住；要证明修复做了什么，读 LoadJoinOrderFix.LastReorder。
RequireField(entryType, "_suppressLogging").SetValue(null, true);

MethodInfo runSilently = entryType.GetMethod("RunSilently", AnyMember)
    ?? throw new MissingMethodException(entryType.FullName, "RunSilently");

object report = runSilently.Invoke(null, null)!;
RequireField(entryType, "_suppressLogging").SetValue(null, true);

List<string> applied = StringList(report, "Applied");
List<string> skipped = StringList(report, "Skipped");
List<string> failed = StringList(report, "Failed");
Console.WriteLine("  applied: [" + string.Join(", ", applied) + "]");
Console.WriteLine("  skipped: [" + string.Join(", ", skipped) + "]");
Console.WriteLine("  failed : [" + string.Join(", ", failed) + "]");

bool installedByEntry = applied.Contains("load-join-order", StringComparer.Ordinal);
if (!installedByEntry)
{
    // Entry.CompatFixes.Build() 目前还没有这一项（新增修复要单独加一行 yield）。为了能验证修复本身，
    // 这里按 ICompatFix 契约直接把它装上：Applies(in snapshot, out reason) -> Apply(harmony, in snapshot)。
    Console.WriteLine("  note: Entry.RunSilently() did not install load-join-order (not yet in CompatFixes.Build());");
    Console.WriteLine("        installing the shipped fix directly through ICompatFix.");
    if (!InstallFixDirectly(patch, harmonyAssembly, entryType, out string why))
    {
        Console.WriteLine("  FAIL: could not install the fix: " + why);
        return 5;
    }
}

// ------------------------------------------------------------ 场景：装补丁后同一输入
Console.WriteLine();
Console.WriteLine("---- AFTER: RestoreLoadJoin (same inputs) ----");

RestoreResult after = InvokeRestoreLoadJoin(saveOrder, joinOrder);
if (after.Failure is not null)
{
    Console.WriteLine("  FAIL: still rejected: " + after.Failure.GetType().Name + ": " + after.Failure.Message);
    Console.WriteLine("  LastReorder: " + LastReorder(patch));
    return 3;
}

Console.WriteLine("  accepted, restored [" + Ids(after.ResultIds) + "]");
Console.WriteLine("  LastReorder: " + LastReorder(patch));

bool savedOrderOk = after.ResultIds.SequenceEqual(saveOrder);
Console.WriteLine("  restored order == save order [" + Ids(saveOrder) + "]: " + savedOrderOk);

// 对照一：已经按存档序时，修复不得做任何改动（LastReorder 不该被刷新）
string? reorderBeforeControl = LastReorder(patch);
RestoreResult controlAfter = InvokeRestoreLoadJoin(saveOrder, saveOrder);
string? reorderAfterControl = LastReorder(patch);
bool controlUntouched = string.Equals(reorderBeforeControl, reorderAfterControl, StringComparison.Ordinal);
bool controlOk = controlAfter.Failure is null && controlAfter.ResultIds.SequenceEqual(saveOrder) && controlUntouched;
Console.WriteLine(
    "  control (join order == save order) after the fix: " +
    (controlAfter.Failure is null
        ? "accepted, restored [" + Ids(controlAfter.ResultIds) + "]"
        : "threw " + controlAfter.Failure.Message) +
    ", reorder untouched: " + controlUntouched);

// 对照二：缺员也要过 —— 存档 3 人、只有 2 人续玩，且这两人的加入序与存档序相反
ulong[] partialJoin = [PlayerY, HostId];
RestoreResult partial = InvokeRestoreLoadJoin(saveOrder, partialJoin);
bool partialOk = partial.Failure is null && partial.ResultIds.SequenceEqual([HostId, PlayerY]);
Console.WriteLine(
    "  sub-set (2 of 3, reversed): " +
    (partial.Failure is null
        ? "accepted, restored [" + Ids(partial.ResultIds) + "]"
        : "threw " + partial.Failure.Message));

Console.WriteLine();
if (!savedOrderOk)
{
    Console.WriteLine("FAIL: the restored list is not in save order.");
    return 4;
}

if (!controlOk)
{
    Console.WriteLine("FAIL: the already-ordered case changed behaviour.");
    return 4;
}

if (!partialOk)
{
    Console.WriteLine("FAIL: a reversed sub-set is still rejected.");
    return 4;
}

Console.WriteLine(
    "PASS: join order [" + Ids(joinOrder) + "] is rejected before the fix and accepted after it, " +
    "restored as save order [" + Ids(saveOrder) + "]" +
    (installedByEntry ? " (patch installed by Entry.RunSilently)" : " (patch installed directly from the dll)"));
return 0;

// ---------------------------------------------------------------------------- 场景构造

/// <summary>
/// 按宿主的做法发布一份快照：载体只包含「已连接」的那些人，RealSlotId 是他在 SerializableRun.Players 里的下标
/// （LanConnectTailMessageRuntime.cs:1282-1294），因此 Decode 回来是存档序。
/// </summary>
object PublishSnapshot(ulong[] saved, ulong[] connected)
{
    var carriers = new List<Carrier>(connected.Length);
    foreach (ulong id in connected)
    {
        int slot = Array.IndexOf(saved, id);
        if (slot < 0)
        {
            throw new InvalidOperationException("probe bug: " + id + " is not in the save order");
        }

        carriers.Add(SerializeLobbyPlayer(id, (byte)slot));
    }

    object decoded = decode(BuildCanonical(HostId, 1u, carriers));
    object authority = NewAuthority(HostId);
    object players = snapshotPlayers.GetValue(decoded)!;
    return commitHostSnapshot.Invoke(authority, [players])!;
}

object SavedPlayers(ulong[] order)
{
    object list = Activator.CreateInstance(typeof(List<>).MakeGenericType(savedPlayerType))!;
    foreach (ulong id in order)
    {
        object player = Activator.CreateInstance(savedPlayerType)!;
        savedPlayerNetId.SetValue(player, id);
        ((IList)list).Add(player);
    }

    return list;
}

object ConnectedList(ulong[] order)
{
    object list = Activator.CreateInstance(typeof(List<>).MakeGenericType(lobbyPlayerType))!;
    foreach (ulong id in order)
    {
        ((IList)list).Add(NewLobbyPlayer(id));
    }

    return list;
}

object NewLobbyPlayer(ulong id)
{
    object player = Activator.CreateInstance(lobbyPlayerType)!;
    lobbyPlayerId.SetValue(player, id);
    return player;
}

Carrier SerializeLobbyPlayer(ulong id, byte slot)
{
    object player = NewLobbyPlayer(id);
    RequireField(lobbyPlayerType, "isModded").SetValue(player, true);
    RequireField(lobbyPlayerType, "isReady").SetValue(player, true);

    object writer = Activator.CreateInstance(writerType)!;
    writerWarnOnGrow.SetValue(writer, false);
    _ = lobbyPlayerSerialize.Invoke(player, [writer]);

    int bits = (int)writerBitPosition.GetValue(writer)!;
    byte[] buffer = (byte[])writerBuffer.GetValue(writer)!;
    return new Carrier(id, slot, (uint)bits, buffer[..((bits + 7) / 8)]);
}

/// <summary>
/// 与 LanConnectRosterCodec.Encode 同布局（LanConnectRosterCodec.cs:21-67）：15 字节头 + 每人 13 字节头 + 载体字节，
/// 玩家按 (RealSlotId, PlayerId) 排序 —— Decode 会拒绝非规范序（:109-116）。
/// </summary>
byte[] BuildCanonical(ulong authorityPeerId, uint revision, List<Carrier> carriers)
{
    List<Carrier> ordered = [.. carriers.OrderBy(carrier => carrier.Slot).ThenBy(carrier => carrier.PlayerId)];
    int total = 15;
    foreach (Carrier carrier in ordered)
    {
        total += 13 + carrier.Bytes.Length;
    }

    byte[] payload = new byte[total];
    payload[0] = 1;
    payload[1] = 1;
    BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(2, 8), authorityPeerId);
    BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(10, 4), revision);
    payload[14] = (byte)ordered.Count;

    int offset = 15;
    foreach (Carrier carrier in ordered)
    {
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(offset, 8), carrier.PlayerId);
        offset += 8;
        payload[offset++] = carrier.Slot;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset, 4), carrier.BitLength);
        offset += 4;
        carrier.Bytes.CopyTo(payload, offset);
        offset += carrier.Bytes.Length;
    }

    return payload;
}

/// <summary>
/// 把 RestoreLoadJoin 的四个参数按宿主的方式摆好再调：宿主 (:1082) 把同一个箱子同时当 boxedMessage 和
/// 强转后的 message 传，这里照做 —— 反射会为第二个参数自动拆箱。
/// </summary>
RestoreResult InvokeRestoreLoadJoin(ulong[] saved, ulong[] connected)
{
    object box = Activator.CreateInstance(messageType)!;
    object run = Activator.CreateInstance(runType)!;
    runPlayers.SetValue(run, SavedPlayers(saved));
    messageRunField.SetValue(box, run);
    messageConnectedField.SetValue(box, ConnectedList(connected));

    object snapshot = PublishSnapshot(saved, connected);
    object binding = NewBinding(HostId);

    try
    {
        _ = restoreLoadJoin.Invoke(null, [box, box, snapshot, binding]);
    }
    catch (TargetInvocationException exception) when (exception.InnerException != null)
    {
        // 把上游的异常原样交出来，打印的类型和消息才和现场一致
        return new RestoreResult(null, [], exception.InnerException);
    }

    return new RestoreResult(box, ReadIds(box), null);
}

/// <summary>
/// RestoreLoadJoin 的第四个参数是 Binding，而 Binding 的构造函数要真实的 INetGameService + NetMessageBus。
/// 这个方法里只会走到 binding.RequireRoster()（:116-122），所以补一个未初始化的实例：它 lock 的那把 Sync 和
/// 一个尚未 Accept 的 _roster 就够了，其余成员不会被读到。
/// </summary>
object NewBinding(ulong hostPeerId)
{
    object binding = RuntimeHelpers.GetUninitializedObject(bindingType);
    RequireField(bindingType, "<Sync>k__BackingField").SetValue(binding, new object());
    RequireField(bindingType, "_roster").SetValue(binding, NewAuthority(hostPeerId));
    return binding;
}

object NewAuthority(ulong hostPeerId) =>
    Activator.CreateInstance(
        authorityType,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        args: [hostPeerId],
        culture: null)!;

IReadOnlyList<ulong> ReadIds(object box)
{
    var ids = new List<ulong>();
    if (messageConnectedField.GetValue(box) is not IEnumerable items)
    {
        return ids;
    }

    foreach (object? item in items)
    {
        if (item is not null)
        {
            ids.Add((ulong)lobbyPlayerId.GetValue(item)!);
        }
    }

    return ids;
}

// ---------------------------------------------------------------------------- 补丁安装

/// <summary>按 ICompatFix 契约直接安装一项修复，用于 Entry.CompatFixes.Build() 还没收录它的时候。</summary>
bool InstallFixDirectly(Assembly patchAssembly, Assembly harmonyLib, Type entry, out string why)
{
    why = string.Empty;

    Type? fixType = patchAssembly.GetType("Sts2LobbyEnhance.LoadJoinOrderFix", throwOnError: false);
    MethodInfo? applies = fixType?.GetMethod("Applies", BindingFlags.Instance | BindingFlags.Public);
    MethodInfo? apply = fixType?.GetMethod("Apply", BindingFlags.Instance | BindingFlags.Public);

    if (fixType is null || applies is null || apply is null)
    {
        why = "the dll has no Sts2LobbyEnhance.LoadJoinOrderFix with Applies/Apply.";
        return false;
    }

    object fix = Activator.CreateInstance(fixType, nonPublic: true)!;

    // Since 2.0.0 Applies() takes no game-version snapshot: the only out-parameter is the skip reason.
    object?[] appliesArguments = [null];
    if (applies.Invoke(fix, appliesArguments) is not true)
    {
        why = "Applies returned false: " + (appliesArguments[0] as string ?? "(no reason)");
        return false;
    }

    object group = fixType.GetProperty("Group")!.GetValue(fix)!;
    MethodInfo harmonyIdFor = entry.GetMethod("HarmonyIdFor", AnyMember)!;
    string harmonyId = (string)harmonyIdFor.Invoke(null, [group])!;

    Type harmonyType = harmonyLib.GetType("HarmonyLib.Harmony")!;
    object harmony = Activator.CreateInstance(harmonyType, harmonyId)!;
    _ = apply.Invoke(fix, [harmony]);

    Console.WriteLine("  installed load-join-order under Harmony id '" + harmonyId + "' (group " + group + ")");
    return true;
}

string? LastReorder(Assembly patchAssembly) =>
    patchAssembly.GetType("Sts2LobbyEnhance.LoadJoinOrderFix", throwOnError: false)
        ?.GetProperty("LastReorder", BindingFlags.Static | BindingFlags.Public)
        ?.GetValue(null) as string;

// ---------------------------------------------------------------------------- 反射小工具

Type Require(Assembly assembly, string fullName) =>
    assembly.GetType(fullName, throwOnError: false, ignoreCase: false)
    ?? throw new InvalidOperationException("Type '" + fullName + "' not found in " + assembly.GetName().Name + ".");

FieldInfo RequireField(Type owner, string name) =>
    owner.GetField(name, AnyMember)
    ?? throw new MissingFieldException(owner.FullName, name);

PropertyInfo RequireProperty(Type owner, string name) =>
    owner.GetProperty(name, AnyMember)
    ?? throw new MissingMemberException(owner.FullName, name);

MethodInfo UniqueMethod(Type owner, string name, int parameterCount)
{
    MethodInfo[] matches = owner
        .GetMethods(AnyMember)
        .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal))
        .Where(method => method.GetParameters().Length == parameterCount)
        .ToArray();

    return matches.Length == 1
        ? matches[0]
        : throw new MissingMethodException(
            owner.FullName, name + "/" + parameterCount + " (" + matches.Length + " match(es))");
}

string Describe(MethodInfo method) =>
    method.Name + "(" + string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name)) + ")";

List<string> StringList(object target, string propertyName)
{
    if (target.GetType().GetProperty(propertyName, AnyMember)?.GetValue(target) is IEnumerable sequence)
    {
        return sequence.Cast<object?>().Select(item => item?.ToString() ?? string.Empty).ToList();
    }

    return [];
}

string Ids(IEnumerable<ulong> ids) => string.Join(",", ids);

static string? FindPatchDll()
{
    // 先找当前构建产物：这个工具验证的是「刚加的修复」，packaging/content/ 可能还没刷新。
    string[] candidates =
    [
        @"src\Sts2LobbyEnhance\bin\Release\net9.0\sts2_lobby_enhance.dll",
        @"src\Sts2LobbyEnhance\bin\Debug\net9.0\sts2_lobby_enhance.dll",
        @"packaging\content\sts2_lobby_enhance.dll"
    ];

    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    while (directory is not null)
    {
        foreach (string relative in candidates)
        {
            string candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        directory = directory.Parent;
    }

    return null;
}

internal readonly record struct Carrier(ulong PlayerId, byte Slot, uint BitLength, byte[] Bytes);

internal readonly record struct RestoreResult(object? Box, IReadOnlyList<ulong> ResultIds, Exception? Failure);

internal delegate object DecodeSpan(ReadOnlySpan<byte> payload);
