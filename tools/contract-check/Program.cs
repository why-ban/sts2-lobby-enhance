using System.Reflection;

// 契约检查：验证兼容补丁依赖的每一个上游成员都真实存在且形状正确。
//
// 这是本项目最重要的一道防线：补丁大量依赖运行时反射（因为编译期不能引用 sts2_lan_connect.dll），
// 反射的失败模式是"运行时才发现"。把依赖显式列出来逐个校验，上游一改就立刻暴露，
// 而不是等玩家在联机时崩一次。
//
// 退出码：0 全部通过 / 2 有缺失 / 3 找不到官方 DLL

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

void RequireField(Type? type, string name, string label)
{
    if (type == null)
    {
        return;
    }

    checkedCount++;
    if (type.GetField(name, AnyMember) == null)
    {
        failures.Add($"{label}: field missing: {type.Name}.{name}");
    }
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

void RequireMethod(Type? type, string name, string label, params Type[] parameters)
{
    if (type == null)
    {
        return;
    }

    checkedCount++;
    MethodInfo? method = type.GetMethod(name, AnyMember, binder: null, types: parameters, modifiers: null);
    if (method == null)
    {
        failures.Add(
            $"{label}: method missing: {type.Name}.{name}(" +
            string.Join(", ", parameters.Select(p => p.Name)) + ")");
    }
}

// ---------------------------------------------------------------- JoinTakeoverFix
Console.WriteLine("[join-flow]");
Type? flow = RequireType("Sts2LanConnect.Scripts.LanConnectLobbyManagedJoinFlow");
RequireMethod(flow, "BeginAsync", "join-flow", typeof(object), typeof(Godot.SceneTree));
RequireProperty(flow, "NetService", "join-flow");
RequireProperty(flow, "CancelToken", "join-flow");
RequireField(flow, "_protocolOffer", "join-flow");
RequireField(flow, "_protocolSelection", "join-flow");
RequireField(flow, "_protocolFlowNonce", "join-flow");
RequireField(flow, "_peerNativeBusTypeId", "join-flow");

Type? factory = RequireType("Sts2LanConnect.Scripts.LanConnectNetGameServiceFactory");
RequireMethod(factory, "CreateClient", "join-flow");

Type? tailRuntime = RequireType("Sts2LanConnect.Scripts.LanConnectTailMessageRuntime");
RequireProperty(tailRuntime, "Shared", "join-flow");
if (tailRuntime != null)
{
    checkedCount++;
    MethodInfo? bind = tailRuntime.GetMethods(AnyMember)
        .FirstOrDefault(m => string.Equals(m.Name, "BindClient", StringComparison.Ordinal) && m.GetParameters().Length == 5);
    if (bind == null)
    {
        failures.Add("join-flow: method missing: LanConnectTailMessageRuntime.BindClient with 5 parameters");
    }

    checkedCount++;
    MethodInfo? unbind = tailRuntime.GetMethods(AnyMember)
        .FirstOrDefault(m => string.Equals(m.Name, "Unbind", StringComparison.Ordinal) && m.GetParameters().Length == 1);
    if (unbind == null)
    {
        failures.Add("join-flow: method missing: LanConnectTailMessageRuntime.Unbind with 1 parameter");
    }
}

// ---------------------------------------------------------------- HostWireFix
Console.WriteLine("[host-wire]");
Type? serialization = RequireType("Sts2LanConnect.Scripts.LanConnectSerializationPatches");
if (serialization != null)
{
    checkedCount++;
    MethodInfo? body = serialization.GetMethods(AnyMember)
        .FirstOrDefault(m =>
            string.Equals(m.Name, "SerializeJoinResponseBody", StringComparison.Ordinal)
            && m.GetParameters() is { Length: 3 } p
            && p[0].ParameterType == typeof(MegaCrit.Sts2.Core.Multiplayer.Serialization.PacketWriter)
            && p[1].ParameterType == typeof(MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby.ClientLobbyJoinResponseMessage)
            && p[2].ParameterType == typeof(int));
    if (body == null)
    {
        failures.Add("host-wire: method missing: LanConnectSerializationPatches.SerializeJoinResponseBody(PacketWriter, ClientLobbyJoinResponseMessage, int)");
    }
}

Type? wirePatches = RequireType("Sts2LanConnect.Scripts.LanConnectCompatWirePatches");
RequireMethod(wirePatches, "GetLobbyListBitWidth", "host-wire");

// ---------------------------------------------------------------- treasure-vote：已移除，刻意不留契约
// treasure-vote 修复项已删除（2026-11-13 的「0.113 原生已支持跳过票」结论；那份一次性审计报告已不在
// 仓库里，结论落在 docs/CHANGELOG.md 与 docs/README.md）。这里原本有 3 条契约
// （TreasurePatches 类型、OnPickedPrefix 3 参、ResolveAllVotes），它们只是那次「给官方 prefix 装
// 守卫」的反射锚点；补丁源码里已没有任何一处再引用 TreasurePatches，继续断言等于替上游内部成员
// 背书 —— 上游一改名就红，而我们根本不用它。
//
// 不改成「断言 TreasurePatches 不存在」：该类型在官方 0.6.3 里真实存在，只是它在 0.113 上自己跳过
// 了 OnPicked 补丁（dec-official/TreasurePatches.cs:122-127、137-140、163-167），断言它不存在是假契约。
// 也不改成「断言我们自己的 fix 列表里没有 treasure-vote」：本工具只加载官方 DLL，看不到补丁程序集；
// 为它去 LoadFrom 构建产物的话，产物没构建时只能 SKIP，于是打印的契约总数会随构建状态变化 ——
// 那正是「说检查了其实没检查」。这条回归防线放在能无条件断言的地方：Entry.cs 的 fix 列表已无该行，
// tools/smoke-test 打印的 applied 列表也直接可见（验收清单按 9 项逐条核对）。

// ---------------------------------------------------------------- TailErrorPathFix
Console.WriteLine("[tail-error-paths]");
if (tailRuntime != null)
{
    foreach ((string tailMethod, int arity) in new[] { ("AbortActiveBinding", 3), ("RejectAndDisconnect", 3), ("ClearPendingOutgoing", 1) })
    {
        checkedCount++;
        if (tailRuntime.GetMethods(AnyMember).All(m =>
                !string.Equals(m.Name, tailMethod, StringComparison.Ordinal) || m.GetParameters().Length != arity))
        {
            failures.Add($"tail-error-paths: method missing: LanConnectTailMessageRuntime.{tailMethod} with {arity} parameter(s)");
        }
    }
}

Type? remoteLobby = RequireType("Sts2LanConnect.Scripts.LanConnectRemoteLobbyPlayerPatches");
if (remoteLobby != null)
{
    checkedCount++;
    if (remoteLobby.GetMethods(AnyMember).All(m =>
            !string.Equals(m.Name, "ScheduleDelayedDisconnect", StringComparison.Ordinal)
            || m.GetParameters().Length != 3))
    {
        failures.Add("tail-error-paths: method missing: LanConnectRemoteLobbyPlayerPatches.ScheduleDelayedDisconnect with 3 parameters");
    }
}

// ---------------------------------------------------------------- JoinTakeoverFix · tail 绑定成员
// BindClient 的第 4 参是 ReadOnlySpan<byte>，无法装箱，MethodInfo.Invoke 必抛；
// 补丁改为调用私有 Bind(byte[] nonce)，所以这里必须校验 Bind 存在且正好 7 参。
if (tailRuntime != null)
{
    checkedCount++;
    MethodInfo? bindPrivate = tailRuntime.GetMethods(AnyMember)
        .FirstOrDefault(m => string.Equals(m.Name, "Bind", StringComparison.Ordinal) && m.GetParameters().Length == 7);
    if (bindPrivate == null)
    {
        failures.Add("join-flow: method missing: LanConnectTailMessageRuntime.Bind with 7 parameters (byte[] nonce path)");
    }
    else
    {
        ParameterInfo[] bindParams = bindPrivate.GetParameters();
        checkedCount++;
        if (bindParams[5].ParameterType != typeof(byte[]))
        {
            failures.Add(
                $"join-flow: LanConnectTailMessageRuntime.Bind parameter 6 is {bindParams[5].ParameterType.Name}, expected byte[]");
        }
    }

    checkedCount++;
    if (tailRuntime.GetMethods(AnyMember).All(m =>
            !string.Equals(m.Name, "GetMessageBus", StringComparison.Ordinal) || m.GetParameters().Length != 1))
    {
        failures.Add("join-flow: method missing: LanConnectTailMessageRuntime.GetMessageBus with 1 parameter");
    }
}

Type? codec = RequireType("Sts2LanConnect.Scripts.LanConnectSidecarFrameCodec");
if (codec != null)
{
    checkedCount++;
    System.Reflection.FieldInfo? nonceBytes = codec.GetField(
        "FlowNonceBytes", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
    if (nonceBytes?.GetRawConstantValue() is not int)
    {
        failures.Add("join-flow: const missing: LanConnectSidecarFrameCodec.FlowNonceBytes");
    }
}

// ---------------------------------------------------------------- AbandonRunFix
Console.WriteLine("[abandon-run]");
Type? saveCompat = RequireType("Sts2LanConnect.Scripts.LanConnectMultiplayerSaveCompatibility");
RequireMethod(
    saveCompat,
    "AbandonCurrentRunAsync",
    "abandon-run",
    typeof(MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMultiplayerSubmenu));

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




