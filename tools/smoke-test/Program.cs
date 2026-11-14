using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;

// 不启动游戏的整体冒烟测试：
//   1. 加载 0.113 的游戏程序集与官方 LAN Connect 0.6.3
//   2. 加载 **packaging/content 里那个出货 DLL**（不是源码），反射调用 Entry.RunSilently()
//      （不走 MegaCrit 的 Log，那个在非 Godot 宿主会直接崩）
//   3. 校验 applied 的 id 集合与顺序、Harmony 确实挂上了每一项修复的关键接缝
// 目的：在启动游戏之前验证反射路径与补丁安装，避免把低级错误带进真机。
//
// 为什么必须加载出货 DLL：把 src/**/*.cs 编译进本工具（2026-11-13 之前就是这样）验证的是源码，
// 而不是用户实际下载到的那个文件。源码与 content/ 之间任何一次漏刷新都会被这样的工具放过，
// 而本工具的输出又会被当成「已验证」。现在本工具不再引用 src/，只认 packaging/content。

string gameDir = args.Length > 0
    ? args[0]
    : @"D:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64";

string hostDll = args.Length > 1
    ? args[1]
    : @"D:\zhuanban\sts2m\work\lobby\STS2-Game-Lobby\releases\sts2_lan_connect\sts2_lan_connect.dll";

string? patchDll = args.Length > 2 ? args[2] : FindShippedPatchDll();

// 必须与 src/Sts2LobbyEnhance/Scripts/Entry.cs 的 CompatFixes.Build() 逐行同步（顺序也要一致）。
// 这个列表是「出货的东西到底装了哪几项」的唯一断言：删掉 Build() 里一行，这里就会红。
// 同一份清单在 tests/Sts2LobbyEnhance.Tests/FixRegistryTests.cs 里也有，两边都写死是刻意的：
// 本工具验证的是出货 DLL，测试验证的是源码，任何一边单独被改都会被另一边抓住。
string[] expectedFixIds =
[
    "join-flow",
    "join-screen-modes",
    "host-wire",
    "tail-error-paths",
    "roster-rebaseline",
    "load-join-order",
    "abandon-run",
    "new-run-button",
    "new-run-save-binding",
    "extra-game-modes",
    "new-protocol-only",
    "room-card-pill",
    "unlock-extra-modes"
];

// 每一项修复至少要有这一处 Harmony 接缝被真正打上。id 出现在 applied 里只证明 Apply() 没抛异常，
// 不证明它挂了任何东西；这张表把「applied 里有一行」变成「方法上真的有补丁」。
// 声明类型按简单类名比较（命名空间对宿主模组是 Sts2LanConnect.Scripts.*）。
(string FixId, string DeclaringType, string MethodName)[] requiredSeams =
[
    ("join-flow",          "LanConnectLobbyManagedJoinFlow",   "BeginAsync"),
    ("join-screen-modes",  "LanConnectLobbyJoinFlow",          "PushJoinedScreen"),
    ("host-wire",          "LanConnectSerializationPatches",   "SerializeJoinResponseBody"),
    // tail-error-paths is a transpiler: it patches every method of the tail runtime that *calls*
    // AbortActiveBinding/RejectAndDisconnect, so the exact set is derived from IL. Any patched method
    // on that type proves the rewrite ran; the count is printed above for a human to eyeball.
    ("tail-error-paths",   "LanConnectTailMessageRuntime",     "*"),
    ("roster-rebaseline",  "LanConnectRosterAuthorityState",  "Accept"),
    ("load-join-order",    "LanConnectTailMessageRuntime",     "RestoreLoadJoin"),
    ("abandon-run",        "LanConnectMultiplayerSaveCompatibility", "AbandonCurrentRunAsync"),
    ("new-run-button",     "NGameOverScreen",                  "_Ready"),
    ("new-run-save-binding", "RunManager",                       "SetUpNewMultiplayer"),
    ("extra-game-modes",   "LanConnectLobbyOverlay",           "GameModeFromCreateId"),
    // The room-card mode pill is a separate Harmony postfix on a private static method of the same
    // type; without this row, deleting that patch would leave the fix "applied" with no proof.
    ("new-protocol-only",  "LanConnectLobbyOverlay",           "RefreshProtocolProfileOptionAvailability"),
    ("room-card-pill",     "LanConnectLobbyOverlay",           "GetRoomGameModePill"),
    ("unlock-extra-modes", "SaveManager",                      "GenerateUnlockStateFromProgress")
];

AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
{
    string candidate = Path.Combine(gameDir, new AssemblyName(eventArgs.Name).Name + ".dll");
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};

Console.WriteLine($"game dir : {gameDir}");
Console.WriteLine($"host dll : {hostDll}");

if (patchDll is null || !File.Exists(patchDll))
{
    Console.WriteLine("FAIL: the shipped patch DLL was not found.");
    Console.WriteLine("  Looked for packaging/content/sts2_lobby_enhance.dll walking up from " + AppContext.BaseDirectory);
    Console.WriteLine("  Run: pwsh -NoProfile -File packaging/build-content.ps1");
    Console.WriteLine("  This tool verifies the DLL that ships; it deliberately does NOT compile src/ any more,");
    Console.WriteLine("  because verifying the source is what let a stale DLL reach the release zip before.");
    return 7;
}

Console.WriteLine($"patch dll: {patchDll}");
Console.WriteLine($"patch sha: {Sha256(patchDll)}");
Console.WriteLine();

if (!File.Exists(hostDll))
{
    Console.WriteLine("SKIP: host mod dll not found.");
    return 3;
}

Assembly host = Assembly.LoadFrom(hostDll);
Console.WriteLine($"host mod : {host.GetName().Name} {host.GetName().Version}");
Console.WriteLine();

Assembly patch = Assembly.LoadFrom(patchDll);
Console.WriteLine($"patch    : {patch.GetName().Name} {patch.GetName().Version}");

Type? entry = patch.GetType("Sts2LobbyEnhance.Entry", throwOnError: false);
if (entry is null)
{
    Console.WriteLine("FAIL: the shipped DLL has no Sts2LobbyEnhance.Entry type.");
    return 7;
}

FieldInfo? harmonyIdField = entry.GetField("HarmonyId", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
string harmonyId = harmonyIdField?.GetRawConstantValue() as string
    ?? harmonyIdField?.GetValue(null) as string
    ?? string.Empty;

if (string.IsNullOrEmpty(harmonyId))
{
    Console.WriteLine("FAIL: Entry.HarmonyId is missing from the shipped DLL.");
    return 7;
}

Console.WriteLine($"harmony  : {harmonyId}");
Console.WriteLine();

MethodInfo? runSilently = entry.GetMethod("RunSilently", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
if (runSilently is null)
{
    Console.WriteLine("FAIL: Entry.RunSilently() is missing from the shipped DLL.");
    return 7;
}

Console.WriteLine("---- Entry.RunSilently() (from the shipped DLL) ----");
object? report;
try
{
    report = runSilently.Invoke(null, null);
}
catch (TargetInvocationException exception)
{
    Console.WriteLine("RunSilently threw: " + exception.InnerException);
    return 1;
}
catch (Exception exception)
{
    Console.WriteLine("RunSilently threw: " + exception);
    return 1;
}

if (report is null)
{
    Console.WriteLine("FAIL: RunSilently() returned null.");
    return 1;
}

object? snapshot = GetProperty(report, "Snapshot");

// There is no global "requires patch" switch any more: every repair answers for itself through
// ICompatFix.Applies, and the host-missing case is the only whole-mod gate left. What is still worth
// printing is how many of the six 0.113 signature changes this build shows, because that is the input
// every probe sees.
string[] breakFlags =
[
    "InitialGameInfoLostGameMode",
    "JoinRequestLostMaxAscensionUnlocked",
    "DisconnectClientGainedParameter",
    "ConnectedPeersChangedType",
    "GenerateRelicFightGainedParameter",
    "UpdateProgressGainedReturnValue"
];
int breakCount = breakFlags.Count(name => GetProperty(snapshot, name) is true);
string describe = (string)(snapshot?.GetType().GetMethod("Describe", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
    ?.Invoke(snapshot, null) ?? "(no Describe)");

List<string> applied = GetStringList(report, "Applied");
List<string> skipped = GetStringList(report, "Skipped");
List<string> failed = GetStringList(report, "Failed");

Console.WriteLine("probe       : " + describe);
Console.WriteLine("breaks      : " + breakCount + "/6");
Console.WriteLine("inert       : " + GetProperty(report, "Inert"));
Console.WriteLine("hostMissing : " + GetProperty(report, "HostMissing"));
Console.WriteLine("applied     : " + string.Join(", ", applied));
Console.WriteLine("skipped     : " + string.Join(", ", skipped));
foreach (string failure in failed)
{
    Console.WriteLine("FAILED      : " + failure);
}

// Per-group detail. GroupReport.Skipped entries are "id: reason" — that reason is the whole point of the
// per-repair gating, so the harness has to show it instead of just printing a bare id.
object? groups = GetProperty(report, "Groups");
if (groups is System.Collections.IEnumerable groupList)
{
    foreach (object? group in groupList)
    {
        if (group is null)
        {
            continue;
        }

        Console.WriteLine(
            "group       : " + GetProperty(group, "Group") + " [" + GetProperty(group, "HarmonyId") + "] " +
            "rolledBack=" + GetProperty(group, "RolledBack"));

        foreach (string reason in GetStringList(group, "Skipped"))
        {
            Console.WriteLine("   skipped  : " + reason);
        }

        foreach (string reason in GetStringList(group, "Failed"))
        {
            Console.WriteLine("   failed   : " + reason);
        }
    }
}

Console.WriteLine();
Console.WriteLine("---- installed Harmony patches ----");
// Patches are installed under one Harmony id per FixGroup, and GetPatchedMethods() only reports the
// instance it is called on, so every id has to be asked. Entry.AllHarmonyIds() is the single source.
MethodInfo? allIdsMethod = entry.GetMethod("AllHarmonyIds", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
IEnumerable<string> harmonyIds = allIdsMethod?.Invoke(null, null) as IEnumerable<string> ?? new[] { harmonyId };
List<MethodBase> patched = harmonyIds
    .SelectMany(id => new Harmony(id).GetPatchedMethods())
    .Distinct()
    .ToList();
foreach (MethodBase method in patched)
{
    Console.WriteLine($"  {method.DeclaringType?.Name}::{method.Name}");
}

bool joinPatched = patched.Any(method =>
    string.Equals(method.Name, "BeginAsync", StringComparison.Ordinal)
    && method.DeclaringType?.Name.Contains("LanConnectLobbyManagedJoinFlow", StringComparison.Ordinal) == true);

Console.WriteLine();
Console.WriteLine($"join flow patched: {joinPatched}");

// ---------------------------------------------------------------- 断言 1：applied 的 id 集合与顺序
Console.WriteLine();
Console.WriteLine("---- applied fix ids ----");

bool idsOk = applied.Count == expectedFixIds.Length;
for (int index = 0; index < expectedFixIds.Length; index++)
{
    string expected = expectedFixIds[index];
    string actual = index < applied.Count ? applied[index] : "(missing)";
    bool ok = string.Equals(expected, actual, StringComparison.Ordinal);
    idsOk &= ok;
    Console.WriteLine($"  [{index}] expected {expected,-20} actual {actual,-20} {(ok ? "OK" : "MISMATCH")}");
}

if (applied.Count > expectedFixIds.Length)
{
    foreach (string extra in applied.Skip(expectedFixIds.Length))
    {
        Console.WriteLine($"  [extra] {extra}  MISMATCH");
    }

    idsOk = false;
}

if (failed.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("FAIL: some repairs failed to install.");
    return 2;
}

if (skipped.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("FAIL: " + skipped.Count + " repair(s) did not apply on this build: " + string.Join(", ", skipped));
    Console.WriteLine("  The shipped patch is supposed to install every fix on 0.113.");
    return 2;
}

if (!joinPatched)
{
    Console.WriteLine();
    Console.WriteLine("FAIL: join takeover was not installed.");
    return 2;
}

if (!idsOk)
{
    Console.WriteLine();
    Console.WriteLine("FAIL: the shipped DLL does not apply the expected fix ids in the expected order.");
    Console.WriteLine("  expected: " + string.Join(", ", expectedFixIds));
    Console.WriteLine("  actual  : " + string.Join(", ", applied));
    Console.WriteLine("  Update this list and tests/.../FixRegistryTests.cs together with Entry.cs CompatFixes.Build().");
    return 5;
}

// ------------------------------------------------- 断言 2：每一项修复至少有一处真实接缝
Console.WriteLine();
Console.WriteLine("---- one real seam per fix ----");

bool seamsOk = true;
foreach ((string fixId, string declaringType, string methodName) in requiredSeams)
{
    bool found = patched.Any(method =>
        string.Equals(method.DeclaringType?.Name, declaringType, StringComparison.Ordinal)
        && (methodName == "*" || string.Equals(method.Name, methodName, StringComparison.Ordinal)));

    seamsOk &= found;
    string shown = methodName == "*" ? "(any method)" : methodName;
    Console.WriteLine($"  {fixId,-20} {declaringType}::{shown,-32} {(found ? "PATCHED" : "MISSING")}");
}

if (!seamsOk)
{
    Console.WriteLine();
    Console.WriteLine("FAIL: at least one repair reported itself as applied but installed no patch on its seam.");
    return 6;
}

// ---- 语义验证：直接调用被打过补丁的映射函数 ----
Console.WriteLine();
Console.WriteLine("---- extra-game-modes mapping ----");
bool mappingsOk = true;
Type overlay = host.GetType("Sts2LanConnect.Scripts.LanConnectLobbyOverlay", throwOnError: false)!;
MethodInfo? modeFromId = overlay?.GetMethod(
    "GameModeFromCreateId",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
    null,
    new[] { typeof(int) },
    null);

if (modeFromId == null)
{
    // 以前这里打印 "skipping mapping checks" 并保持 mappingsOk = true，于是映射全错也能 OK + exit 0。
    // extra-game-modes 是本补丁的核心功能之一，找不到映射函数就是失败，不是跳过。
    Console.WriteLine("FAIL: LanConnectLobbyOverlay.GameModeFromCreateId was not found; the extra-game-modes mapping cannot be verified.");
    return 4;
}

(int Id, int Expected, string Label)[] cases =
{
    (0, 1, "Standard"),
    (1, 2, "Daily"),
    (2, 3, "Custom"),
    (3, 4, "Blitz"),
    (4, 5, "Dread"),
    (9, 1, "Standard (unknown id)")
};

foreach ((int id, int expected, string label) in cases)
{
    int actual = (int)modeFromId.Invoke(null, new object[] { id })!;
    bool ok = actual == expected;
    mappingsOk &= ok;
    Console.WriteLine($"  id={id} -> {actual} (expected {expected}) {(ok ? "OK" : "MISMATCH")}   [{label}]");
}

// ---- availability 判断：必须"回得来" ----
// 2026-11-14 现场事故：extra-game-modes 对 IsCreateGameModeAvailable 的 transpiler 用 `new Label()`
// 当前缀跳转目标，那是一个 id=0 的默认标签，和宿主方法体里 Harmony 已定义的 0 号标签撞号，
// Brfalse 跳进 switch 中间形成回边 —— 方法永远不返回。表现不是异常，而是点「创建房间」整个游戏卡死，
// 日志一行都没有。下面的看门狗就是为这一类故障写的：smoke-test 以前只验接缝"在不在"，
// 从不调用这个方法，所以它照样报 OK。
Console.WriteLine();
Console.WriteLine("---- extra-game-modes availability gate returns ----");

MethodInfo isCreateModeAvailable = overlay!
    .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    .First(m => m.Name == "IsCreateGameModeAvailable"
                && m.GetParameters() is { Length: 2 } ps
                && ps[0].ParameterType == typeof(GameMode));

Type availabilityType = host.GetType("Sts2LanConnect.Scripts.LanConnectLanHostModeAvailability", throwOnError: false)!;
object availability = Activator.CreateInstance(availabilityType, new object[] { true, true, true })!;

bool availabilityOk = true;
foreach (int mode in new[] { 1, 2, 3, 4, 5 })
{
    object argument = Enum.ToObject(typeof(GameMode), mode);
    object? result = null;
    Exception? thrown = null;

    var probe = new System.Threading.Thread(() =>
    {
        try
        {
            result = isCreateModeAvailable.Invoke(null, new[] { argument, availability });
        }
        catch (TargetInvocationException exception)
        {
            thrown = exception.InnerException ?? exception;
        }
        catch (Exception exception)
        {
            thrown = exception;
        }
    })
    { IsBackground = true, Name = "availability-" + mode };

    probe.Start();

    if (!probe.Join(8000))
    {
        Console.WriteLine($"  mode={mode} -> NEVER RETURNED within 8000 ms   <<< infinite loop, the game would freeze");
        availabilityOk = false;
        continue;
    }

    if (thrown != null)
    {
        Console.WriteLine($"  mode={mode} -> THREW {thrown.GetType().Name}: {thrown.Message}");
        availabilityOk = false;
        continue;
    }

    bool ok = result is true;
    availabilityOk &= ok;
    Console.WriteLine($"  mode={mode} -> {result} (expected True) {(ok ? "OK" : "MISMATCH")}");
}

if (!availabilityOk)
{
    Console.WriteLine();
    Console.WriteLine("FAIL: the availability gate is wrong or never returns. Modes 1/2/3 loop = the create-room freeze.");
    return 6;
}

Type binding = host.GetType("Sts2LanConnect.Scripts.LanConnectMultiplayerSaveRoomBinding", throwOnError: false)!;
MethodInfo? getLobbyGameMode = binding?.GetMethod(
    "GetLobbyGameMode",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
    null,
    new[] { typeof(GameMode) },
    null);

if (getLobbyGameMode == null)
{
    Console.WriteLine("FAIL: LanConnectMultiplayerSaveRoomBinding.GetLobbyGameMode was not found; the lobby game-mode mapping cannot be verified.");
    return 4;
}

(int Mode, string Expected)[] modeCases =
{
    (1, "standard"),
    (2, "daily"),
    (3, "custom"),
    (4, "blitz"),
    (5, "dread")
};

foreach ((int mode, string expected) in modeCases)
{
    object enumValue = Enum.ToObject(typeof(MegaCrit.Sts2.Core.Runs.GameMode), mode);
    string actual = (string)getLobbyGameMode.Invoke(null, new[] { enumValue })!;
    bool ok = actual == expected;
    mappingsOk &= ok;
    Console.WriteLine($"  mode={mode} -> '{actual}' (expected '{expected}') {(ok ? "OK" : "MISMATCH")}");
}

// ---- 房间卡片模式标签：直接调用打过 postfix 的 GetRoomGameModePill ----
// 上面两段只证明「mode -> 字符串」这一层；用户看到的是卡片上的 pill，它由 GetRoomGameModePill 把
// 字符串再映射成 (文本, 边框色, 背景色)。0.6.3 只认识 daily/custom，其余一律 "STD"，所以这一段是
// 「极速/恐惧 真的不再显示成 STD」的唯一自动化证据。
Console.WriteLine();
Console.WriteLine("---- extra-game-modes room-card mode pill ----");

MethodInfo? getRoomGameModePill = overlay?.GetMethod(
    "GetRoomGameModePill",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
    null,
    new[] { typeof(string) },
    null);

if (getRoomGameModePill == null)
{
    Console.WriteLine("FAIL: LanConnectLobbyOverlay.GetRoomGameModePill(string) was not found; the room-card label cannot be verified.");
    return 4;
}

(string? Mode, string Expected, string Label)[] pillCases =
{
    ("blitz", "极速", "Blitz"),
    ("dread", "恐惧", "Dread"),
    (" BLITZ ", "极速", "Blitz, untrimmed + uppercase"),
    ("daily", "挑战", "Daily must keep its own label"),
    ("custom", "自定义", "Custom must keep its own label"),
    ("standard", "STD", "Standard must keep its own label"),
    (null, "STD", "null must keep the original fallback")
};

bool pillOk = true;
foreach ((string? mode, string expected, string label) in pillCases)
{
    object pill = getRoomGameModePill.Invoke(null, new object?[] { mode })!;
    string text = (string)pill.GetType().GetField("Item1")!.GetValue(pill)!;
    bool ok = string.Equals(text, expected, StringComparison.Ordinal);
    pillOk &= ok;
    Console.WriteLine($"  gameMode={mode ?? "<null>",-9} -> '{text}' (expected '{expected}') {(ok ? "OK" : "MISMATCH")}   [{label}]");
}

Console.WriteLine();
if (!pillOk)
{
    Console.WriteLine("FAIL: the room-card mode pill is wrong; the postfix on GetRoomGameModePill is not taking effect.");
    return 4;
}

if (!mappingsOk)
{
    Console.WriteLine("FAIL: extra-game-modes mapping is wrong.");
    return 4;
}

Console.WriteLine($"OK ({applied.Count} fixes applied from {Path.GetFileName(patchDll)})");
return 0;

// ---------------------------------------------------------------- helpers

static string? FindShippedPatchDll()
{
    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    while (directory is not null)
    {
        string candidate = Path.Combine(directory.FullName, "packaging", "content", "sts2_lobby_enhance.dll");
        if (File.Exists(candidate))
        {
            return Path.GetFullPath(candidate);
        }

        directory = directory.Parent;
    }

    return null;
}

static string Sha256(string path)
{
    using FileStream stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static object? GetProperty(object? target, string name)
{
    if (target is null)
    {
        return null;
    }

    PropertyInfo? property = target.GetType().GetProperty(
        name,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    return property?.GetValue(target);
}

static List<string> GetStringList(object? target, string name)
{
    if (GetProperty(target, name) is IEnumerable sequence)
    {
        return sequence.Cast<object?>().Select(item => item?.ToString() ?? string.Empty).ToList();
    }

    return new List<string>();
}
