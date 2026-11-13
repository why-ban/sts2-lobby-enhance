using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using HarmonyLib;

// 判定 LanConnectTailMessageRuntime（及其闭包类型）里哪些方法"可被 Harmony 读取"。
// 不可读 = 含失效成员引用 = 无法打补丁，必须接管其可读的调用者。
// 这是补完断点 #3（DisconnectClient 4 参）与 #4（ConnectedPeers 返回类型）的前提。

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

Assembly host = Assembly.LoadFrom(hostDll);
var harmony = new Harmony("tail-readability");

// 先找出引用失效成员的方法名（从元数据表定位）
var brokenUsers = new HashSet<string>(StringComparer.Ordinal);
using (FileStream fs = File.OpenRead(hostDll))
using (var pe = new PEReader(fs))
{
    MetadataReader md = pe.GetMetadataReader();
    var brokenTokens = new List<int>();

    foreach (MemberReferenceHandle handle in md.MemberReferences)
    {
        int token = 0x0A000000 | MetadataTokens.GetRowNumber(handle);
        try
        {
            _ = host.ManifestModule.ResolveMember(token, null, null);
        }
        catch (Exception ex)
        {
            // 只有"成员真的不存在"才算断点。
            // <TypeSpecification>（泛型实例）在无泛型上下文时抛 ArgumentException/BadImageFormat，
            // 那是探针自身缺上下文导致的假阳性，必须排除。
            if (IsRealBreak(ex))
            {
                brokenTokens.Add(token);
            }
        }
    }

    var methodBodies = new List<(string Name, byte[] Il)>();
    foreach (Type type in host.GetTypes())
    {
        foreach (MethodBase method in type.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            MethodBody? body = method.GetMethodBody();
            if (body?.GetILAsByteArray() is { } il)
            {
                methodBodies.Add((type.Name + "::" + method.Name, il));
            }
        }
    }

    foreach (int token in brokenTokens)
    {
        byte[] pattern = BitConverter.GetBytes(token);
        foreach ((string name, byte[] il) in methodBodies)
        {
            if (Contains(il, pattern))
            {
                brokenUsers.Add(name);
            }
        }
    }
}

// 用 Transpiler 管道读 IL：与 Harmony 实际打补丁时走的路径一致，
// 因此这里判定的"不可读"就是真实的"补不上"。
static List<CodeInstruction> ReadIl(MethodBase method)
{
    var probe = new Harmony("tail-readability.probe");
    return PatchProcessor.GetOriginalInstructions(method);
}

static bool IsRealBreak(Exception exception)
{
    Exception root = exception;
    while (root.InnerException != null)
    {
        root = root.InnerException;
    }

    return root switch
    {
        MissingMethodException => true,
        MissingFieldException => true,
        ArgumentOutOfRangeException when root.Message.Contains("not valid in the scope", StringComparison.Ordinal) => true,
        _ => false
    };
}

static bool Contains(byte[] haystack, byte[] needle)
{
    for (int i = 0; i <= haystack.Length - needle.Length; i++)
    {
        bool hit = true;
        for (int j = 0; j < needle.Length; j++)
        {
            if (haystack[i + j] != needle[j])
            {
                hit = false;
                break;
            }
        }

        if (hit)
        {
            return true;
        }
    }

    return false;
}

Type tail = host.GetType("Sts2LanConnect.Scripts.LanConnectTailMessageRuntime", throwOnError: false)
    ?? throw new InvalidOperationException("LanConnectTailMessageRuntime not found");

Console.WriteLine("== 引用失效成员的方法（不可读，必须绕过） ==");
foreach (string name in brokenUsers.OrderBy(x => x, StringComparer.Ordinal))
{
    Console.WriteLine("  " + name);
}

Console.WriteLine();
Console.WriteLine("== LanConnectTailMessageRuntime 方法可读性 ==");
var types = new List<Type> { tail };
types.AddRange(tail.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));

// 本工具以前永远 exit 0，于是「打印了一堆 BROKEN」和「一切正常」看起来一模一样。
// 现在读不出的方法分三类，只有第三类算异常：
//   1. 引用已知失效成员（brokenUsers，打 * 号）—— 预期状态，补丁绕过这些调用者；
//   2. NotSupportedException —— 探针自身缺少泛型上下文导致的假阳性（与 IsRealBreak 里排除
//      <TypeSpecification> 是同一类问题），打印成 ? 号并计入 probeLimited，不算异常；
//   3. 其余读不出的手写方法 —— 既不是已知断点也不是已知探针限制，属新问题，返回非 0。
// 另外 examined == 0（一个方法都没枚举到）也返回非 0：类型改名了，工具什么也没验证。
int unexplainedBroken = 0;
int probeLimited = 0;
int examined = 0;

foreach (Type type in types)
{
    foreach (MethodBase method in type.GetMethods(
                 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
    {
        if (method.IsAbstract || method.GetMethodBody() == null)
        {
            continue;
        }

        string label = type.Name + "::" + method.Name;
        string status;
        bool limited = false;
        try
        {
            _ = ReadIl(method);
            status = "READABLE";
        }
        catch (Exception ex)
        {
            Exception root = ex;
            while (root.InnerException != null)
            {
                root = root.InnerException;
            }

            limited = root is NotSupportedException;
            status = "BROKEN   (" + root.GetType().Name + ")";
        }

        examined++;
        bool known = brokenUsers.Contains(label);
        string marker = known ? " *" : limited ? " ?" : string.Empty;

        if (status.StartsWith("BROKEN", StringComparison.Ordinal) && !known)
        {
            if (limited)
            {
                probeLimited++;
            }
            else
            {
                unexplainedBroken++;
            }
        }

        Console.WriteLine($"  {status,-34} {label}{marker}");
    }
}

Console.WriteLine();
Console.WriteLine("(* = 已知引用失效成员, ? = 探针缺泛型上下文导致的假阳性)");
Console.WriteLine($"examined={examined} probeLimited={probeLimited} unexplainedBroken={unexplainedBroken}");

if (examined == 0)
{
    Console.WriteLine("FAIL: no methods were examined; the tail runtime type has no methods any more?");
    return 2;
}

if (unexplainedBroken > 0)
{
    Console.WriteLine(
        $"FAIL: {unexplainedBroken} method(s) could not be read but do not reference a known broken member. " +
        "That is a new problem, not the documented 0.6.3 breakage.");
    return 2;
}

return 0;


