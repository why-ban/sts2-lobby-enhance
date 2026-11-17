using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;

// 不启动游戏的整体冒烟测试：
//   1. 加载 0.113 的游戏程序集与官方 LAN Connect 0.6.4
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

// 宿主模组默认取 **0.6.4**（manifest 的 min_version）：补丁的 16 项锚点是对着它校验的。
// 覆盖顺序与 tests 一致：命令行 > STS2_LAN_CONNECT_064_DLL > 在工作树里发现 upstream-064 的出货 DLL。
const string DefaultHostMod064 =
    @"D:\zhuanban\sts2m\work\lobby\upstream-064\src\emptylower-STS2-Game-Lobby-1db4e10\releases\sts2_lan_connect\sts2_lan_connect.dll";

string? hostOverride = args.Length > 1 ? args[1] : Environment.GetEnvironmentVariable("STS2_LAN_CONNECT_064_DLL");
string hostDll = string.IsNullOrWhiteSpace(hostOverride)
    ? FindHostMod064() ?? DefaultHostMod064
    : hostOverride;

string? patchDll = args.Length > 2 ? args[2] : FindShippedPatchDll();

// 必须与 src/Sts2LobbyEnhance/Scripts/Entry.cs 的 CompatFixes.Build() 逐行同步（顺序也要一致）。
// 这个列表是「出货的东西到底装了哪几项」的唯一断言：删掉 Build() 里一行，这里就会红。
// 同一份清单在 tests/Sts2LobbyEnhance.Tests/FixRegistryTests.cs 里也有，两边都写死是刻意的：
// 本工具验证的是出货 DLL，测试验证的是源码，任何一边单独被改都会被另一边抓住。
string[] expectedFixIds =
[
    "roster-rebaseline",
    "load-join-order",
    "begin-run-order",
    "new-run-button",
    "new-run-save-binding",
    "new-protocol-only",
    "room-version-filter",
    "touch-chat-scroll",
    "chat-server-switch",
    "announcement-source",
    "server-pool-refresh",
    "server-pool-join",
    "server-pool-create",
    "server-pool-create-picker",
    "lobby-entry-direct",
    "room-chat-hint-passthrough",
    "sidebar-scroll"
];

// 每一项修复至少要有这一处 Harmony 接缝被真正打上。id 出现在 applied 里只证明 Apply() 没抛异常，
// 不证明它挂了任何东西；这张表把「applied 里有一行」变成「方法上真的有补丁」。
// 声明类型按简单类名比较（命名空间对宿主模组是 Sts2LanConnect.Scripts.*）。
(string FixId, string DeclaringType, string MethodName)[] requiredSeams =
[
    ("roster-rebaseline",  "LanConnectRosterAuthorityState",  "Accept"),
    ("load-join-order",    "LanConnectTailMessageRuntime",     "RestoreLoadJoin"),
    ("begin-run-order",    "StartRunLobby",                     "BeginRunForAllPlayers"),
    ("new-run-button",     "NGameOverScreen",                  "_Ready"),
    ("new-run-save-binding", "RunManager",                       "SetUpNewMultiplayer"),
    ("new-protocol-only",  "LanConnectLobbyOverlay",           "GetSelectedCreateProtocolId"),
    ("room-version-filter", "LanConnectLobbyOverlay",           "GetFilteredRooms"),
    ("touch-chat-scroll",   "LanConnectRichMessageView",        "_Ready"),
    ("touch-chat-scroll",   "LanConnectRichMessageView",        "RaiseReferencePressed"),
    ("chat-server-switch",  "LanConnectBasicChatPanel",         "UpdateAvailability"),
    ("announcement-source", "LobbyApiClient",                   "GetAnnouncementsAsync"),
    ("server-pool-refresh", "LanConnectLobbyOverlay",           "RefreshRoomsAsync"),
    ("server-pool-refresh", "LanConnectLobbyOverlay",           "_Process"),
    ("server-pool-refresh", "LanConnectLobbyOverlay",           "BuildRoomMetaRow"),
    ("server-pool-join",    "LanConnectLobbyOverlay",           "BeginJoinRoomAsync"),
    ("server-pool-create",  "LanConnectLobbyOverlay",           "CreateRoomAsync"),
    ("server-pool-create-picker", "LanConnectLobbyOverlay",     "BuildCreateDialog"),
    ("server-pool-create-picker", "LanConnectLobbyOverlay",     "OpenCreateDialogInternal"),
    ("lobby-entry-direct",  "LanConnectServerSelectionStartup", "Show"),
    ("room-chat-hint-passthrough", "LanConnectRoomChatOverlay", "RefreshFromSource"),
    ("sidebar-scroll", "LanConnectLobbyOverlay", "RebuildMainContentLayout")
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

// There is no global "requires patch" switch any more: every repair answers for itself through
// ICompatFix.Applies, and the host-missing case is the only whole-mod gate left. There is also no
// game-version snapshot any more (2.0.0 deleted the six-break probe along with the repairs that read it).
List<string> applied = GetStringList(report, "Applied");
List<string> skipped = GetStringList(report, "Skipped");
List<string> failed = GetStringList(report, "Failed");

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

// ------------------------------------------------- 断言 1b：每项修复必须挂在自己声明的分组里
//
// 分组决定回滚边界。把 chat-server-switch 挪进 Pool、或把 server-pool-join 挪进 Chat，id 顺序与接缝断言
// 全都还是绿的 —— 只有这张表会红。这就是它存在的唯一理由，别删。
(string FixId, string Group)[] expectedGroups =
[
    ("roster-rebaseline", "Roster"),
    ("load-join-order", "LoadOrder"),
    ("begin-run-order", "Roster"),
    ("new-run-button", "NewRun"),
    ("new-run-save-binding", "NewRun"),
    ("new-protocol-only", "Modes"),
    ("room-version-filter", "Filter"),
    ("touch-chat-scroll", "Touch"),
    ("chat-server-switch", "Chat"),
    ("announcement-source", "Announce"),
    ("server-pool-refresh", "Pool"),
    ("server-pool-join", "Pool"),
    ("server-pool-create", "Pool"),
    ("server-pool-create-picker", "Pool"),
    ("lobby-entry-direct", "Pool"),
    ("room-chat-hint-passthrough", "Chat"),
    ("sidebar-scroll", "Layout")
];

Console.WriteLine();
Console.WriteLine("---- fix to group mapping ----");

var actualGroups = new Dictionary<string, string>(StringComparer.Ordinal);
if (groups is System.Collections.IEnumerable groupItems)
{
    foreach (object? group in groupItems)
    {
        if (group is null)
        {
            continue;
        }

        string groupName = GetProperty(group, "Group")?.ToString() ?? "(null)";
        foreach (string id in GetStringList(group, "Applied"))
        {
            actualGroups[id] = groupName;
        }
    }
}

bool groupsOk = true;
foreach ((string fixId, string expectedGroup) in expectedGroups)
{
    string actualGroup = actualGroups.TryGetValue(fixId, out string? found) ? found : "(not applied)";
    bool ok = string.Equals(expectedGroup, actualGroup, StringComparison.Ordinal);
    groupsOk &= ok;
    Console.WriteLine($"  {fixId,-20} expected {expectedGroup,-9} actual {actualGroup,-9} {(ok ? "OK" : "MISMATCH")}");
}

if (!groupsOk)
{
    Console.WriteLine();
    Console.WriteLine("FAIL: a repair is installed in a different FixGroup than the one it belongs to.");
    return 8;
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
// Two seams share a method name with an unrelated overload (LanConnectServerSelectionStartup.Show): both take four
// parameters and differ only in the type of the second one, so the pin has to name that type.
var requiredSeamParameter = new Dictionary<(string Type, string Method), (int Index, string TypeName)>
{
    [("LanConnectServerSelectionStartup", "Show")] = (1, "Func`2"),
};

foreach ((string fixId, string declaringType, string methodName) in requiredSeams)
{
    bool pinned = requiredSeamParameter.TryGetValue((declaringType, methodName), out (int Index, string TypeName) wanted);
    bool found = patched.Any(method =>
        string.Equals(method.DeclaringType?.Name, declaringType, StringComparison.Ordinal)
        && string.Equals(method.Name, methodName, StringComparison.Ordinal)
        && (!pinned
            || (method.GetParameters().Length > wanted.Index
                && string.Equals(method.GetParameters()[wanted.Index].ParameterType.Name, wanted.TypeName, StringComparison.Ordinal))));

    seamsOk &= found;
    Console.WriteLine($"  {fixId,-20} {declaringType}::{methodName,-32} {(found ? "PATCHED" : "MISSING")}");
}

if (!seamsOk)
{
    Console.WriteLine();
    Console.WriteLine("FAIL: at least one repair reported itself as applied but installed no patch on its seam.");
    return 6;
}

Console.WriteLine($"OK ({applied.Count} fixes applied from {Path.GetFileName(patchDll)})");
return 0;

// ---------------------------------------------------------------- helpers

/// <summary>
/// The 0.6.4 release bundle: walk up to the work root, then look for
/// <c>lobby/upstream-064/src/&lt;checkout&gt;/releases/...</c>. The checkout name is discovered rather than hard-coded;
/// the bytes are what the run reports. <c>tools/contract-check</c> takes explicit paths instead, so it shares no
/// resolution code with this helper.
/// </summary>
static string? FindHostMod064()
{
    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    while (directory is not null)
    {
        string upstream = Path.Combine(directory.FullName, "lobby", "upstream-064", "src");
        if (Directory.Exists(upstream))
        {
            foreach (string checkout in Directory.GetDirectories(upstream))
            {
                string candidate = Path.Combine(
                    checkout, "releases", "sts2_lan_connect", "sts2_lan_connect.dll");
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        directory = directory.Parent;
    }

    return null;
}

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