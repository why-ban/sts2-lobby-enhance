using System.Reflection;

// 契约检查：验证兼容补丁依赖的上游成员真实存在且形状正确。
//
// 这是本项目的一道防线：补丁大量依赖运行时反射（编译期不能引用 sts2_lan_connect.dll），而反射的
// 失败模式是"运行时才发现"。把依赖显式列出来逐个校验，上游一改就立刻暴露。
//
// 2026-11-15（适配上游 0.6.4，补丁 2.0.0）：原本打印 30 条，其中 27 条只服务于已删除的 8 项修复
//（join-flow / host-wire / tail-error-paths / abandon-run），随修复一起删掉。剩下的 3 条
//（1 个类型 + 2 个成员）服务于 Core/TailRoster.cs —— 它被 roster-rebaseline 与 new-run-button 共用，是当前唯一还直接依赖
// LanConnectTailMessageRuntime 的运行时组件。**这不是"契约都满足了"的完整覆盖**：其余保留项
//（load-join-order / new-protocol-only / 新开一局存档绑定）的反射锚点尚未纳入本工具，
// 覆盖靠 tools/smoke-test 的 one-real-seam-per-fix 与 tests/ 里的源码级断言。
//
// 退出码：0 全部通过 / 2 有缺失 / 3 找不到官方 DLL

string gameDir = args.Length > 0
    ? args[0]
    : @"D:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64";

string hostDll = args.Length > 1
    ? args[1]
    : @"D:\zhuanban\sts2m\work\lobby\upstream-064\src\emptylower-STS2-Game-Lobby-1db4e10\releases\sts2_lan_connect\sts2_lan_connect.dll";

AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
{
    string candidate = Path.Combine(gameDir, new AssemblyName(eventArgs.Name).Name + ".dll");
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};

if (!File.Exists(hostDll))
{
    Console.WriteLine($"SKIP: host mod dll not found at {hostDll}");
    return 3;
}

Assembly host = Assembly.LoadFrom(hostDll);
Console.WriteLine($"host: {host.GetName().Name} {host.GetName().Version}");
Console.WriteLine();

const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

// 大多数被检查的成员是 static，查找时必须同时允许 Instance|Static。
const BindingFlags AnyMember = AnyInstance | AnyStatic;

var failures = new List<string>();
int checkedCount = 0;

Type? RequireType(string fullName)
{
    checkedCount++;
    Type? type = host.GetType(fullName, throwOnError: false);
    if (type == null)
    {
        failures.Add($"type missing: {fullName}");
    }

    return type;
}

void RequireProperty(Type? type, string name, string label)
{
    if (type == null)
    {
        return;
    }

    checkedCount++;
    if (type.GetProperty(name, AnyMember) == null)
    {
        failures.Add($"{label}: property missing: {type.Name}.{name}");
    }
}

// ---------------------------------------------------------------- Core/TailRoster.cs
// 被 roster-rebaseline（客户端丢掉过期的 roster epoch）与 new-run-button（重建大厅前重置）共用。
// 它从 LanConnectTailMessageRuntime.Shared 拿单例，再用 GetMessageBus(service) 反查该会话的 binding。
Console.WriteLine("[tail-roster]");
Type? tailRuntime = RequireType("Sts2LanConnect.Scripts.LanConnectTailMessageRuntime");
RequireProperty(tailRuntime, "Shared", "tail-roster");
if (tailRuntime != null)
{
    checkedCount++;
    if (tailRuntime.GetMethods(AnyMember).All(m =>
            !string.Equals(m.Name, "GetMessageBus", StringComparison.Ordinal) || m.GetParameters().Length != 1))
    {
        failures.Add("tail-roster: method missing: LanConnectTailMessageRuntime.GetMessageBus with 1 parameter");
    }
}

// ---------------------------------------------------------------- 汇总
Console.WriteLine();
Console.WriteLine($"checked {checkedCount} contracts, {failures.Count} failed.");
foreach (string failure in failures)
{
    Console.WriteLine("  FAIL " + failure);
}

if (failures.Count == 0)
{
    Console.WriteLine("OK: all contracts satisfied.");
    return 0;
}

return 2;
