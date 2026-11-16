using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

// new-run-save-binding 的机制验证工具。
//
// 它不模拟 LanConnectCurrentSaveBindingWriter，而是**直接构造那个类**（内部类，靠反射），把
// BindingTarget 的 ExpectedSaveKey 换成三种取值，看 Persist 返回什么：
//
//   现场（ExpectedSaveKey = 上一局的 key，存档已是新局的 key） -> RefusedDifferentSave   ← 用户看到的拒绝
//   清空（ExpectedSaveKey = null 或 "")                       -> 继续走完，落库成功
//   对照（ExpectedSaveKey = 当前 key）                        -> 照旧成功
//   对照（ExpectedSaveKey = null，但 NetService 不是同一个）  -> RefusedDifferentNetService
//
// 第四条同时说明：光清空是不够的，还必须发生在「RunManager.Instance.NetService 就是房间那个 service」的
// 时刻——这也正是把清空放在 RunManager.SetUpNewMultiplayer 里的原因。
//
// 为什么值得单独做一个工具：现场那条拒绝只出现在真机存档事件里，而"清空 ExpectedSaveKey 就能解掉"这个
// 结论，是整套修复的唯一支点。把它压成一次函数调用，就不必再靠读代码相信它。

string gameDir = args.Length > 0
    ? args[0]
    : @"D:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64";

string hostDll = args.Length > 1
    ? args[1]
    : @"D:\SteamLibrary\steamapps\workshop\content\2868840\3749766330\sts2_lan_connect.dll";

AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
{
    string candidate = Path.Combine(gameDir, new AssemblyName(eventArgs.Name).Name + ".dll");
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};

Console.WriteLine($"game dir : {gameDir}");
Console.WriteLine($"host dll : {hostDll}");

if (!File.Exists(hostDll))
{
    Console.WriteLine("SKIP: the official sts2_lan_connect.dll was not found.");
    return 3;
}

// 0.6.3 从 2.0.0 起就不在支持范围，对着旧二进制跑出来的 OK 没有意义
const string PinnedHostSha256 = "E9644876FF9F563E7784AE21C10F2B67C6EEBE2FAB53C8106D0D6AE1D3814AE9";
using (FileStream stream = File.OpenRead(hostDll))
using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
{
    string actual = Convert.ToHexString(sha.ComputeHash(stream));
    if (!string.Equals(actual, PinnedHostSha256, StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"HOST MISMATCH: expected the pinned 0.6.4 build {PinnedHostSha256}, got {actual}.");
        Console.WriteLine("这个探针只在 0.6.4 上有意义；换台机器请把 hostDll 路径作为第二个参数传进来。");
        return 4;
    }
}

if (File.Exists(Path.Combine(gameDir, "sts2.dll")))
{
    _ = Assembly.LoadFrom(Path.Combine(gameDir, "sts2.dll"));
}

Assembly host = Assembly.LoadFrom(hostDll);
Console.WriteLine($"host mod : {host.GetName().Name} {host.GetName().Version}");

// The two keys the cases swap between: the finished run's (still held by the session) and the new run's.
const string OldKey = WriterHarness.OldKey;
const string NewKey = WriterHarness.NewKey;

WriterHarness harness;
try
{
    harness = WriterHarness.Open(host);
}
catch (Exception exception)
{
    Console.WriteLine("FAIL: could not build the writer harness: " + exception.GetType().Name + ": " + exception.Message);
    return 3;
}

Console.WriteLine($"writer   : {harness.Describe()}");
Console.WriteLine($"save keys: bound(old)={OldKey} current(new)={NewKey}");
Console.WriteLine();

Case[] cases =
[
    new Case(
        "A",
        "现场：ExpectedSaveKey=上一局的 key，当前存档是新局的 key",
        expected: "RefusedDifferentSave",
        oldKey: OldKey,
        sameService: true,
        expectPersist: false),
    new Case(
        "B1",
        "修复：ExpectedSaveKey=null",
        expected: "Persisted",
        oldKey: null,
        sameService: true,
        expectPersist: true),
    new Case(
        "B2",
        "修复：ExpectedSaveKey=空串",
        expected: "Persisted",
        oldKey: string.Empty,
        sameService: true,
        expectPersist: true),
    new Case(
        "C1",
        "对照：ExpectedSaveKey 与当前 key 相同",
        expected: "Persisted",
        oldKey: NewKey,
        sameService: true,
        expectPersist: true),
    new Case(
        "C2",
        "对照：清空了，但 NetService 不是运行中那一个",
        expected: "RefusedDifferentNetService",
        oldKey: null,
        sameService: false,
        expectPersist: false)
];

bool ok = true;
foreach (Case testCase in cases)
{
    (string result, string detail, bool persisted, string? recorded) =
        Guarded(harness, testCase, out string? watchdog);

    bool matched = string.Equals(result, testCase.Expected, StringComparison.Ordinal)
                   && persisted == testCase.ExpectPersist;
    ok &= matched;

    Console.WriteLine($"  {testCase.Id} {testCase.Description}");
    Console.WriteLine($"      Persist -> {result}{(string.IsNullOrEmpty(detail) ? string.Empty : " (" + detail + ")")}" +
                      $"{(recorded == null ? string.Empty : ", recordedSaveKey=" + recorded)}" +
                      $"   {(matched ? "OK" : "MISMATCH（期望 " + testCase.Expected + "）")}");

    if (watchdog != null)
    {
        Console.WriteLine("      " + watchdog);
    }
}

Console.WriteLine();
bool writerOk = ok;

// ---- 修复本身：锚点是否解析得出来、补丁是否真的挂上了 ----
// 这一段证明的是「修复能装上」，不是「装上后在真机上一定生效」——后者要有活着的 Godot 与被托管的房间。
Console.WriteLine("---- 修复接线（new-run-save-binding）----");
string fixSection = CheckFixWiring(out int fixExitCode);
Console.WriteLine(fixSection);

Console.WriteLine();
if (!writerOk)
{
    Console.WriteLine("FAIL: the writer does not behave the way the repair assumes.");
    return 1;
}

if (fixExitCode != 0)
{
    return fixExitCode;
}

Console.WriteLine(
    "OK: a stale ExpectedSaveKey refuses the new save (the field failure), an empty one lets it through when the " +
    "service is the run's own, the matching-key path is unchanged, and the repair installs on " +
    "RunManager.SetUpNewMultiplayer.");
return 0;

static string CheckFixWiring(out int exitCode)
{
    exitCode = 0;

    string? patchDll = SelectPatchDll();
    if (patchDll == null)
    {
        return "SKIP: no patch DLL found (build src/Sts2LobbyEnhance first); the writer cases above still stand.";
    }

    const string fixTypeName = "Sts2LobbyEnhance.NewRunSaveBindingFix";
    Assembly patch;
    try
    {
        patch = Assembly.LoadFile(patchDll);
    }
    catch (Exception exception)
    {
        exitCode = 2;
        return $"FAIL: could not load {patchDll}: {exception.GetType().Name}: {exception.Message}";
    }

    Type? fixType = patch.GetType(fixTypeName, throwOnError: false);
    Type? entryType = patch.GetType("Sts2LobbyEnhance.Entry", throwOnError: false);
    if (fixType == null || entryType == null)
    {
        return $"SKIP: {Path.GetFileName(patchDll)} predates this repair (no {fixTypeName}).";
    }

    // 补丁的日志走 MegaCrit 的日志器，它的静态构造在非 Godot 宿主里是访问冲突（Entry.cs 的 _suppressLogging
    // 就是为这种宿主准备的）。先按住，否则 Apply 的第一行 Info 就会把工具带走。
    entryType.GetField("_suppressLogging", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, true);

    object fix = Activator.CreateInstance(fixType, nonPublic: true)!;

    // Since 2.0.0 Applies() takes no game-version snapshot: the only out-parameter is the skip reason.
    object?[] appliesArguments = [null];
    bool applies = (bool)fixType.GetMethod("Applies", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .Invoke(fix, appliesArguments)!;
    string reason = appliesArguments[0] as string ?? string.Empty;

    if (!applies)
    {
        exitCode = 2;
        return $"FAIL: Applies() = false ({reason}); the repair would be skipped on this host mod build.";
    }

    var harmony = new HarmonyLib.Harmony("sts2_lobby_enhance.newrun.probe");
    fixType.GetMethod("Apply", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .Invoke(fix, [harmony]);

    MethodBase? seam = harmony.GetPatchedMethods().FirstOrDefault(method =>
        string.Equals(method.Name, "SetUpNewMultiplayer", StringComparison.Ordinal)
        && string.Equals(method.DeclaringType?.Name, "RunManager", StringComparison.Ordinal));

    // 前缀在没有任何房间时也必须安静地放过原方法：真机上它每次开局都会跑。
    fixType.GetMethod("SetUpNewMultiplayerPrefix", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
        .Invoke(null, [null]);

    if (seam == null)
    {
        exitCode = 2;
        return "FAIL: Applies() = true but RunManager::SetUpNewMultiplayer is not patched.";
    }

    string anchors = DescribeAnchors(fixType);
    return $"Applies() = true; seam: RunManager::SetUpNewMultiplayer PATCHED; prefix with no lobby returns cleanly.{anchors}";
}

/// <summary>
/// Reads the captured shape back out, so "host_origin was skipped" cannot hide behind a suppressed warning.
/// </summary>
static string DescribeAnchors(Type fixType)
{
    object? shape = fixType.GetField("_shape", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
    Type? shapeType = shape?.GetType();
    if (shapeType == null)
    {
        return string.Empty;
    }

    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    bool session = shapeType.GetField("BoundSaveKey", Any)?.GetValue(shape) != null
                   && shapeType.GetField("SessionNetService", Any)?.GetValue(shape) != null;
    bool origin = shapeType.GetField("HostOrigin", Any)?.GetValue(shape) != null;
    string? note = shapeType.GetField("OptionalNote", Any)?.GetValue(shape) as string;

    return Environment.NewLine +
           $"      anchors: hosted_room session={(session ? "ok" : "MISSING")}, " +
           $"host_origin={(origin ? "ok" : "MISSING")}" +
           (note == null ? string.Empty : Environment.NewLine + "      note: " + note);
}

static string? SelectPatchDll()
{
    string? shipped = WalkUp("packaging", "content", "sts2_lobby_enhance.dll");
    string? built = WalkUp("src", "Sts2LobbyEnhance", "bin", "Release", "net9.0", "sts2_lobby_enhance.dll");
    return built ?? shipped;
}

static string? WalkUp(params string[] relativeParts)
{
    DirectoryInfo? directory = new(AppContext.BaseDirectory);
    while (directory != null)
    {
        string candidate = Path.Combine([directory.FullName, .. relativeParts]);
        if (File.Exists(candidate))
        {
            return Path.GetFullPath(candidate);
        }

        directory = directory.Parent;
    }

    return null;
}

// ---------------------------------------------------------------- runner

static (string Result, string Detail, bool Persisted, string? Recorded) Guarded(
    WriterHarness harness,
    Case testCase,
    out string? watchdog)
{
    watchdog = null;
    string result = "<did not return>";
    string detail = string.Empty;
    bool persisted = false;
    string? recorded = null;
    Exception? failure = null;

    var thread = new Thread(() =>
    {
        try
        {
            (result, detail, persisted, recorded) = harness.Persist(testCase.OldKey, testCase.SameService);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
    })
    {
        IsBackground = true,
        Name = "persist-" + testCase.Id
    };

    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(20)))
    {
        watchdog = $"WATCHDOG: Persist did not return within 20 s (thread '{thread.Name}' is still running).";
        return ("<timeout>", watchdog, false, null);
    }

    if (failure != null)
    {
        // Reflection wraps whatever the writer threw; report the real cause.
        Exception real = failure is TargetInvocationException { InnerException: not null } wrapped
            ? wrapped.InnerException!
            : failure;
        return ("<threw " + real.GetType().Name + ">", real.Message, false, null);
    }

    return (result, detail, persisted, recorded);
}

// ---------------------------------------------------------------- model

sealed class Case(
    string id,
    string description,
    string expected,
    string? oldKey,
    bool sameService,
    bool expectPersist)
{
    internal string Id { get; } = id;

    internal string Description { get; } = description;

    /// <summary>PersistResult name the writer must return.</summary>
    internal string Expected { get; } = expected;

    /// <summary>What the room session still holds as <c>BoundSaveKey</c> when the save_event arrives.</summary>
    internal string? OldKey { get; } = oldKey;

    /// <summary>Whether <c>RunManager.Instance.NetService</c> is the service the target was built from.</summary>
    internal bool SameService { get; } = sameService;

    internal bool ExpectPersist { get; } = expectPersist;
}

/// <summary>
/// Reflection harness over <c>LanConnectCurrentSaveBindingWriter</c>. Everything the writer needs is a delegate over
/// one of its own internal nested types (<c>LoadResult</c>, <c>LoadedSave</c>, <c>PersistenceRequest</c>,
/// <c>BindingTarget</c>), so the four constructor arguments are built with expression trees: a holder method that
/// speaks <c>object</c> is called and the result is cast back to the internal type. <c>MethodInfo.Invoke</c> cannot do
/// that job — it cannot produce a <c>Func&lt;LoadResult&gt;</c> when <c>LoadResult</c> only exists at run time.
/// </summary>
internal sealed class WriterHarness
{
    private const BindingFlags Any =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private const string WriterTypeName = "Sts2LanConnect.Scripts.LanConnectCurrentSaveBindingWriter";

    /// <summary>The finished run's save key: what the room session still holds when the new run is saved.</summary>
    internal const string OldKey = "917866c0deadbeef";

    /// <summary>The new run's save key. Any value works: the writer never compares it to anything but ExpectedSaveKey.</summary>
    internal const string NewKey = "5c1f0a2bnewrun";

    private readonly Type _writerType;
    private readonly Type _loadResultType;
    private readonly object _writer;
    private readonly MethodInfo _persist;
    private readonly PropertyInfo _outcomeResult;
    private readonly PropertyInfo _outcomeFailure;
    private readonly PropertyInfo _outcomeSaveKey;
    private readonly object _saveObject;
    private readonly object _serviceA;
    private readonly object _serviceB;

    // Per-call state the four delegates read. The writer is synchronous, and each case builds its own target, so a
    // plain field is enough; the guard thread serialises the calls.
    private string _currentSaveKey = string.Empty;
    private object? _netService;
    private bool _persistCalled;
    private string? _recordedKey;

    private WriterHarness(Assembly host)
    {
        _writerType = host.GetType(WriterTypeName, throwOnError: false, ignoreCase: false)
            ?? throw new InvalidOperationException($"Type '{WriterTypeName}' was not found.");

        _loadResultType = _writerType.GetNestedType("LoadResult", Any)
            ?? throw new InvalidOperationException($"{WriterTypeName}.LoadResult was not found.");
        Type loadedSaveType = _writerType.GetNestedType("LoadedSave", Any)
            ?? throw new InvalidOperationException($"{WriterTypeName}.LoadedSave was not found.");
        Type requestType = _writerType.GetNestedType("PersistenceRequest", Any)
            ?? throw new InvalidOperationException($"{WriterTypeName}.PersistenceRequest was not found.");
        Type targetType = _writerType.GetNestedType("BindingTarget", Any)
            ?? throw new InvalidOperationException($"{WriterTypeName}.BindingTarget was not found.");
        Type outcomeType = _writerType.GetNestedType("PersistOutcome", Any)
            ?? throw new InvalidOperationException($"{WriterTypeName}.PersistOutcome was not found.");
        Type bindingType = host.GetType("Sts2LanConnect.Scripts.LanConnectSavedRoomBinding", throwOnError: false, ignoreCase: false)
            ?? throw new InvalidOperationException("LanConnectSavedRoomBinding was not found.");

        _persist = _writerType.GetMethod("Persist", Any)
            ?? throw new MissingMethodException(WriterTypeName, "Persist");

        _outcomeResult = outcomeType.GetProperty("Result", Any)
            ?? throw new MissingMemberException(outcomeType.FullName, "Result");
        _outcomeFailure = outcomeType.GetProperty("FailureReason", Any)
            ?? throw new MissingMemberException(outcomeType.FullName, "FailureReason");
        _outcomeSaveKey = outcomeType.GetProperty("SaveKey", Any)
            ?? throw new MissingMemberException(outcomeType.FullName, "SaveKey");

        _saveObject = new object();
        _serviceA = new object();
        _serviceB = new object();

        object writer = Construct(
            _writerType,
            Bind(typeof(Func<>).MakeGenericType(_loadResultType), nameof(LoadCurrent)),
            Bind(typeof(Func<>).MakeGenericType(typeof(object)), nameof(CurrentNetService)),
            Bind(typeof(Func<,>).MakeGenericType(typeof(string), bindingType), nameof(ReadBinding)),
            Bind(typeof(Func<,,>).MakeGenericType(loadedSaveType, requestType, typeof(bool)), nameof(PersistOverride)));

        _writer = writer;
        _targetType = targetType;
    }

    private readonly Type _targetType;

    internal static WriterHarness Open(Assembly host) => new(host);

    internal string Describe() =>
        $"{_writerType.FullName}.Persist, target={_targetType.Name}, result={_outcomeResult.PropertyType.Name}";

    internal (string Result, string Detail, bool Persisted, string? Recorded) Persist(string? expectedSaveKey, bool sameService)
    {
        _currentSaveKey = NewKey;
        _netService = sameService ? _serviceA : _serviceB;
        _persistCalled = false;
        _recordedKey = null;

        object target = Construct(
            _targetType,
            _serviceA,
            false,
            expectedSaveKey,
            "房间",
            null,
            "standard",
            "lobby",
            null,
            Bind(typeof(Action<>).MakeGenericType(typeof(string)), nameof(RecordPersistedSaveKey)));

        object outcome = _persist.Invoke(_writer, [target, "save_event"])!;
        string result = _outcomeResult.GetValue(outcome)?.ToString() ?? "<null>";
        string detail = _outcomeFailure.GetValue(outcome) as string ?? string.Empty;
        return (result, detail, _persistCalled, _recordedKey);
    }

    // ---- holders the writer's delegates end up calling ----

    public object? LoadCurrent()
    {
        // LoadResult(bool Success, string? SaveKey, object? Value, string FailureReason)
        return Construct(_loadResultType, true, _currentSaveKey, _saveObject, string.Empty);
    }

    public object? CurrentNetService() => _netService;

    public object? ReadBinding(object? key) => null;

    public bool PersistOverride(object? loadedSave, object? request)
    {
        _persistCalled = true;
        return true;
    }

    public void RecordPersistedSaveKey(object? saveKey) => _recordedKey = saveKey as string;

    // ---- plumbing ----

    private static object Construct(Type type, params object?[] arguments)
    {
        try
        {
            return Activator.CreateInstance(
                type,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: arguments,
                culture: null)
                ?? throw new InvalidOperationException($"Could not construct {type.FullName}.");
        }
        catch (MissingMethodException exception)
        {
            throw new InvalidOperationException(
                $"{type.FullName} has no constructor taking {arguments.Length} argument(s): " +
                string.Join(", ", type.GetConstructors(Any).Select(c => "(" + string.Join(", ", c.GetParameters().Select(p => p.ParameterType.Name)) + ")")),
                exception);
        }
    }

    /// <summary>
    /// Builds a delegate of the writer's own delegate type whose body calls one of the holders above. The holder
    /// speaks <c>object</c>, so each argument is boxed/cast on the way in and the result is cast back on the way out.
    /// </summary>
    private Delegate Bind(Type delegateType, string holderName)
    {
        MethodInfo holder = typeof(WriterHarness).GetMethod(holderName, Any)
            ?? throw new MissingMethodException(typeof(WriterHarness).FullName, holderName);

        MethodInfo invoke = delegateType.GetMethod("Invoke")
            ?? throw new MissingMethodException(delegateType.FullName, "Invoke");
        ParameterInfo[] parameters = invoke.GetParameters();

        ParameterExpression[] arguments = parameters
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();

        Expression call = Expression.Call(
            Expression.Constant(this),
            holder,
            arguments.Select(argument => (Expression)Expression.Convert(argument, typeof(object))));

        Expression body = call.Type == invoke.ReturnType ? call : Expression.Convert(call, invoke.ReturnType);
        return Expression.Lambda(delegateType, body, arguments).Compile();
    }
}
