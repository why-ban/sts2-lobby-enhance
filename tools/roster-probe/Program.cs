using System.Collections;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using HarmonyLib;

// 踢人后名册重基线（roster-rebaseline）的离线复现工具。
//
// 被测的是**真正跑的那条代码路径**：官方 sts2_lan_connect 0.6.3 的 LanConnectRosterAuthorityState.Accept
// 与 LanConnectRosterCodec，加上出货的补丁 DLL（packaging/content；它还没收录本修复时退回
// src/Sts2LobbyEnhance/bin/Release 的最新构建产物，并打印用的是哪一个）。
//
// 为什么要有这个工具：拒绝发生在客户端处理一条消息的过程中，只有真机打一局才看得到，而且需要两个人各开
// 一个客户端。这里把同一条消息序列压成一次函数调用，于是「修复前必挂、修复后必过」变成一条命令。
//
// 三个阶段，缺一不可：
//   1. 不打补丁，跑五个场景 —— 必须复现日志里那几句拒绝（并固定住拒绝原文）
//   2. 装补丁（Entry.RunSilently()，和游戏里启动时一样；没被 Build() 登记就直接装这一项）
//   3. 重跑同样五个场景 —— 三个减员场景必须过，两个对照场景必须保持原状
//
// 看门狗：每个场景在独立线程上跑，超时即判失败。本工具没有网络也没有 Godot，挂死只可能是被测代码进入了
// 不返回的循环，那种情况下沉默比崩溃更糟。

const string FixTypeName = "Sts2LobbyEnhance.RosterRebaselineFix";
const string FixId = "roster-rebaseline";

string gameDir = args.Length > 0
    ? args[0]
    : @"D:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64";

string hostDll = args.Length > 1
    ? args[1]
    : @"D:\zhuanban\sts2m\work\lobby\STS2-Game-Lobby\releases\sts2_lan_connect\sts2_lan_connect.dll";

string? explicitPatchDll = args.Length > 2 ? args[2] : null;

AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
{
    string candidate = Path.Combine(gameDir, new AssemblyName(eventArgs.Name).Name + ".dll");
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};

Console.WriteLine($"game dir  : {gameDir}");
Console.WriteLine($"host dll  : {hostDll}");

if (!File.Exists(Path.Combine(gameDir, "sts2.dll")))
{
    Console.WriteLine("SKIP: sts2.dll not found in the game data directory.");
    return 3;
}

if (!File.Exists(hostDll))
{
    Console.WriteLine("SKIP: the official sts2_lan_connect.dll was not found.");
    return 3;
}

// 出货 DLL 优先（和 tools/smoke-test 一致），但它可能还没重新打包进货：先用元数据确认它有没有这个类型。
// 检查不加载程序集 —— 同一份程序集标识只允许加载一次，先加载旧的再想换新的会静默拿到旧的那份。
string? patchDll = explicitPatchDll ?? SelectPatchDll();
if (patchDll is null || !File.Exists(patchDll))
{
    Console.WriteLine("FAIL: no patch DLL to test.");
    Console.WriteLine("  Looked for packaging/content/sts2_lobby_enhance.dll walking up from " + AppContext.BaseDirectory);
    Console.WriteLine("  and for src/Sts2LobbyEnhance/bin/Release/net9.0/sts2_lobby_enhance.dll under the repo root.");
    Console.WriteLine("  Build it first: dotnet build src\\Sts2LobbyEnhance\\Sts2LobbyEnhance.csproj -c Release");
    return 7;
}

Console.WriteLine($"patch dll : {patchDll}");

if (!ContainsType(patchDll, FixTypeName))
{
    Console.WriteLine($"FAIL: {Path.GetFileName(patchDll)} has no {FixTypeName}, so the repair cannot be exercised.");
    Console.WriteLine("  Refresh the shipped DLL (packaging/build-content.ps1) or pass the fresh build as the third argument.");
    return 7;
}

_ = Assembly.LoadFrom(Path.Combine(gameDir, "sts2.dll"));
Assembly host = Assembly.LoadFrom(hostDll);
Assembly patch = Assembly.LoadFile(Path.GetFullPath(patchDll));

Console.WriteLine($"host mod  : {host.GetName().Name} {host.GetName().Version}");
Console.WriteLine($"patch     : {patch.GetName().Name} {patch.GetName().Version}");
Console.WriteLine();

RosterHarness harness = RosterHarness.Open(host);
Console.WriteLine($"roster    : {harness.Describe()}");
Console.WriteLine(
    $"peer ids  : host={RosterHarness.HostId} guest={RosterHarness.GuestId} " +
    $"kicked={RosterHarness.KickedId} joiner={RosterHarness.JoinerId}");
Console.WriteLine();

Scenario[] scenarios =
[
    new Scenario(
        "S1",
        "已接受 A{1,501,1547}，房主发来 B{1,501}（StateTransition，BeginRun）",
        before: RosterHarness.MembershipRejection,
        after: null,
        run: h =>
        {
            object hostAuthority = h.NewAuthority(RosterHarness.HostId);
            object publishedThree = h.Commit(hostAuthority, Roster3());
            object publishedTwo = h.Commit(hostAuthority, Roster2());
            object client = h.NewAuthority(RosterHarness.HostId);
            h.Accept(client, publishedThree, "Bootstrap");
            h.Accept(client, publishedTwo, "StateTransition");
            Console.WriteLine(
                $"      A rev={h.RevisionOf(publishedThree)} B rev={h.RevisionOf(publishedTwo)}; " +
                $"Current=[{string.Join(",", h.CurrentPlayerIds(client))}]");
        }),
    new Scenario(
        "S2",
        "已接受 A（3 人），减员后有人加入：房主发来 C{1,501,new}（MembershipMutation）",
        before: RosterHarness.PeerSetRejection,
        after: null,
        run: h =>
        {
            object hostAuthority = h.NewAuthority(RosterHarness.HostId);
            object publishedThree = h.Commit(hostAuthority, Roster3());
            object publishedJoin = h.Commit(hostAuthority, RosterJoin());
            object client = h.NewAuthority(RosterHarness.HostId);
            h.Accept(client, publishedThree, "Bootstrap");
            h.Accept(
                client,
                publishedJoin,
                "MembershipMutation",
                new HashSet<ulong>
                {
                    RosterHarness.HostId, RosterHarness.GuestId, RosterHarness.KickedId, RosterHarness.JoinerId
                },
                RosterHarness.JoinerId);
            Console.WriteLine(
                $"      C rev={h.RevisionOf(publishedJoin)}; Current=[{string.Join(",", h.CurrentPlayerIds(client))}]");
        }),
    new Scenario(
        "S3",
        "已接受 A（3 人），续局/重开时房主发来 B（CurrentState）",
        before: RosterHarness.CurrentStateRejection,
        after: null,
        run: h =>
        {
            object hostAuthority = h.NewAuthority(RosterHarness.HostId);
            object publishedThree = h.Commit(hostAuthority, Roster3());
            object publishedTwo = h.Commit(hostAuthority, Roster2());
            object client = h.NewAuthority(RosterHarness.HostId);
            h.Accept(client, publishedThree, "Bootstrap");
            h.Accept(client, publishedTwo, "CurrentState");
            Console.WriteLine($"      Current=[{string.Join(",", h.CurrentPlayerIds(client))}]");
        }),
    new Scenario(
        "S4",
        "对照：只增不减（旧成员全在），MembershipMutation 必须照旧通过",
        before: null,
        after: null,
        run: h =>
        {
            object hostAuthority = h.NewAuthority(RosterHarness.HostId);
            object publishedThree = h.Commit(hostAuthority, Roster3());
            object publishedFour = h.Commit(hostAuthority, Roster4());
            object client = h.NewAuthority(RosterHarness.HostId);
            h.Accept(client, publishedThree, "Bootstrap");
            h.Accept(
                client,
                publishedFour,
                "MembershipMutation",
                new HashSet<ulong>
                {
                    RosterHarness.HostId, RosterHarness.GuestId, RosterHarness.KickedId, RosterHarness.JoinerId
                },
                RosterHarness.JoinerId);
            Console.WriteLine($"      Current=[{string.Join(",", h.CurrentPlayerIds(client))}]");
        }),
    new Scenario(
        "S5",
        "对照：只增不减走 StateTransition，上游语义必须保持拒绝（本修复不接管这一种）",
        before: RosterHarness.MembershipRejection,
        after: RosterHarness.MembershipRejection,
        run: h =>
        {
            object hostAuthority = h.NewAuthority(RosterHarness.HostId);
            object publishedThree = h.Commit(hostAuthority, Roster3());
            object publishedFour = h.Commit(hostAuthority, Roster4());
            object client = h.NewAuthority(RosterHarness.HostId);
            h.Accept(client, publishedThree, "Bootstrap");
            h.Accept(client, publishedFour, "StateTransition");
        })
];

Console.WriteLine("---- 阶段 1：未打补丁（复现拒绝）----");
bool beforeOk = RunPhase(harness, scenarios, before: true);

Console.WriteLine();
Console.WriteLine("---- 安装补丁 ----");
bool installed = Install(patch, out bool registeredByBuild, out string installDetail);
Console.WriteLine(installDetail);
Console.WriteLine($"registered by CompatFixes.Build(): {registeredByBuild}");

if (!installed)
{
    Console.WriteLine("FAIL: roster-rebaseline could not be installed.");
    return 7;
}

Console.WriteLine();
Console.WriteLine("---- 阶段 2：已打补丁 ----");
bool afterOk = RunPhase(harness, scenarios, before: false);

Console.WriteLine();
if (!beforeOk)
{
    Console.WriteLine("FAIL: the unpatched run does not reproduce the reported rejections, so \"after\" proves nothing.");
    return 1;
}

if (!afterOk)
{
    Console.WriteLine("FAIL: the patch did not repair every shrunken-roster scenario.");
    return 2;
}

Console.WriteLine("OK: unpatched rejects all three shrunken-roster scenarios, patched accepts them, controls unchanged.");
return 0;

// ---------------------------------------------------------------- scenarios

static (ulong Id, byte Slot)[] Roster3() =>
    [(RosterHarness.HostId, 0), (RosterHarness.GuestId, 1), (RosterHarness.KickedId, 2)];

static (ulong Id, byte Slot)[] Roster2() => [(RosterHarness.HostId, 0), (RosterHarness.GuestId, 1)];

static (ulong Id, byte Slot)[] RosterJoin() =>
    [(RosterHarness.HostId, 0), (RosterHarness.GuestId, 1), (RosterHarness.JoinerId, 2)];

static (ulong Id, byte Slot)[] Roster4() =>
    [(RosterHarness.HostId, 0), (RosterHarness.GuestId, 1), (RosterHarness.KickedId, 2), (RosterHarness.JoinerId, 3)];

static bool RunPhase(RosterHarness harness, Scenario[] scenarios, bool before)
{
    bool ok = true;

    foreach (Scenario scenario in scenarios)
    {
        // 场景标题先打：场景体在它自己的线程里跑，它输出的证据行必须落在这条标题下面。
        Console.WriteLine($"  {scenario.Id} {scenario.Title}");

        bool threw = Guarded(scenario.Run, scenario.Id + (before ? "-before" : "-after"), harness, out string message, out string? watchdog);

        string? expected = before ? scenario.Before : scenario.After;
        bool matched = expected == null ? !threw : threw && string.Equals(message, expected, StringComparison.Ordinal);
        ok &= matched;

        string actual = threw ? "rejected: \"" + message + "\"" : "accepted";
        string verdict = matched
            ? (expected == null ? "OK" : "OK（如期拒绝）")
            : "MISMATCH（期望 " + (expected == null ? "不抛异常" : "\"" + expected + "\"") + "）";

        Console.WriteLine($"      {actual}   {verdict}");

        if (watchdog != null)
        {
            Console.WriteLine("      " + watchdog);
        }
    }

    return ok;
}

// 每个场景独自一条线程：被测代码一旦不返回，这里必须在有限时间内给出结论，而不是让整个工具静默挂住。
static bool Guarded(
    Action<RosterHarness> body,
    string name,
    RosterHarness harness,
    out string message,
    out string? watchdog)
{
    watchdog = null;
    message = string.Empty;
    Exception? failure = null;

    var thread = new Thread(() =>
    {
        try
        {
            body(harness);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            failure = exception.InnerException;
        }
        catch (Exception exception)
        {
            failure = exception;
        }
    })
    {
        IsBackground = true,
        Name = name
    };

    thread.Start();
    if (thread.Join(TimeSpan.FromSeconds(20)))
    {
        if (failure == null)
        {
            return false;
        }

        message = failure.Message;
        return true;
    }

    // 线程还活着：标记超时并继续（后台线程，进程退出时一起结束），由调用方把结果记成不匹配。
    watchdog = $"WATCHDOG: the scenario did not return within 20 s (thread '{name}' is still running).";
    message = watchdog;
    return true;
}

// ---------------------------------------------------------------- install

static bool Install(Assembly patch, out bool registeredByBuild, out string detail)
{
    registeredByBuild = false;
    Type? entry = patch.GetType("Sts2LobbyEnhance.Entry", throwOnError: false);
    if (entry == null)
    {
        detail = "FAIL: the patch DLL has no Sts2LobbyEnhance.Entry.";
        return false;
    }

    var lines = new List<string>();

    MethodInfo? runSilently = entry.GetMethod(
        "RunSilently",
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

    if (runSilently != null)
    {
        object? report = runSilently.Invoke(null, null);
        List<string> applied = StringList(report, "Applied");
        registeredByBuild = applied.Contains(FixId, StringComparer.Ordinal);
        lines.Add("Entry.RunSilently(): applied=[" + string.Join(",", applied) + "]");
        foreach (string failure in StringList(report, "Failed"))
        {
            lines.Add("  failed: " + failure);
        }
    }
    else
    {
        lines.Add("Entry.RunSilently() is missing from the patch DLL.");
    }

    // 真实游戏里这些日志走 MegaCrit 的日志器，它的静态构造在非 Godot 宿主里是访问冲突（Entry.cs 的
    // _suppressLogging 就是为这种宿主准备的）。本工具正是这种宿主：装完补丁必须把它按住，否则修复第一次
    // 真正触发时，光打一行 Warn 就会把工具带走，看起来像修复崩了。
    entry.GetField("_suppressLogging", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, true);
    lines.Add("note: patch logging is suppressed (MegaCrit's logger needs a live Godot process), so the " +
              "re-baseline Warn line is not printed by this tool.");

    if (!registeredByBuild)
    {
        if (!TryInstallDirect(patch, entry, lines, out string failure))
        {
            detail = string.Join(Environment.NewLine, lines) + Environment.NewLine + failure;
            return false;
        }

        lines.Add($"{FixId} is not in Build(), so it was installed directly from {Path.GetFileName(patch.Location)}.");
    }

    MethodBase? seam = PatchedAccept(entry);
    lines.Add(seam == null
        ? "seam: LanConnectRosterAuthorityState::Accept is NOT patched."
        : "seam: LanConnectRosterAuthorityState::Accept PATCHED");

    detail = string.Join(Environment.NewLine, lines);
    return seam != null;
}

static bool TryInstallDirect(Assembly patch, Type entry, List<string> lines, out string failure)
{
    failure = string.Empty;

    Type? fixType = patch.GetType(FixTypeName, throwOnError: false);
    if (fixType == null)
    {
        failure = $"FAIL: {FixTypeName} is missing from {Path.GetFileName(patch.Location)}.";
        return false;
    }

    object fix = Activator.CreateInstance(fixType, nonPublic: true)
        ?? throw new InvalidOperationException($"Could not construct {FixTypeName}.");

    // Since 2.0.0 Applies() takes no game-version snapshot: the only out-parameter is the skip reason.
    object?[] appliesArguments = [null];
    bool applies = (bool)fixType
        .GetMethod("Applies", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .Invoke(fix, appliesArguments)!;

    string reason = appliesArguments[0] as string ?? string.Empty;
    lines.Add("Applies(): " + applies + (applies ? string.Empty : " (" + reason + ")"));

    if (!applies)
    {
        failure = "FAIL: roster-rebaseline does not apply to this host mod build, so the repair cannot be proven.";
        return false;
    }

    var harmony = new Harmony(HarmonyId(entry, ".roster"));
    fixType
        .GetMethod("Apply", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .Invoke(fix, [harmony]);

    return true;
}

static string HarmonyId(Type entry, string suffix) =>
    (entry.GetField("HarmonyId", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
        ?.GetRawConstantValue() as string ?? "sts2_lobby_enhance") + suffix;

static MethodBase? PatchedAccept(Type entry) =>
    new Harmony(HarmonyId(entry, ".roster"))
        .GetPatchedMethods()
        .FirstOrDefault(method =>
            string.Equals(method.Name, "Accept", StringComparison.Ordinal)
            && string.Equals(method.DeclaringType?.Name, "LanConnectRosterAuthorityState", StringComparison.Ordinal));

// ---------------------------------------------------------------- paths

static string? SelectPatchDll()
{
    string? shipped = WalkUp("packaging", "content", "sts2_lobby_enhance.dll");
    if (shipped != null && ContainsType(shipped, FixTypeName))
    {
        return shipped;
    }

    string? built = WalkUp("src", "Sts2LobbyEnhance", "bin", "Release", "net9.0", "sts2_lobby_enhance.dll");
    if (built != null)
    {
        Console.WriteLine("note      : the shipped content DLL predates this repair; testing the last Release build.");
        return built;
    }

    return shipped ?? built;
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

/// <summary>
/// Reads the type table without loading the assembly: the CLR keeps one copy per assembly identity, so a stale
/// shipped DLL has to be rejected *before* it is loaded or the freshly built one could never be tested.
/// </summary>
static bool ContainsType(string path, string fullName)
{
    try
    {
        using FileStream stream = File.OpenRead(path);
        using var reader = new PEReader(stream);
        if (!reader.HasMetadata)
        {
            return false;
        }

        MetadataReader metadata = reader.GetMetadataReader();
        foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
        {
            TypeDefinition definition = metadata.GetTypeDefinition(handle);
            string name = metadata.GetString(definition.Namespace) + "." + metadata.GetString(definition.Name);
            if (string.Equals(name, fullName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
    catch (Exception exception)
    {
        Console.WriteLine($"warning   : could not read {Path.GetFileName(path)}: {exception.Message}");
        return false;
    }
}

static List<string> StringList(object? target, string name)
{
    if (target?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(target) is IEnumerable sequence)
    {
        return sequence.Cast<object?>().Select(item => item?.ToString() ?? string.Empty).ToList();
    }

    return [];
}

// ---------------------------------------------------------------- scenario model

sealed class Scenario(string scenarioId, string title, string? before, string? after, Action<RosterHarness> run)
{
    internal string Id { get; } = scenarioId;

    internal string Title { get; } = title;

    /// <summary>Exception message the unpatched run must produce, or null when it must be accepted.</summary>
    internal string? Before { get; } = before;

    /// <summary>Exception message the patched run must still produce, or null when it must be accepted.</summary>
    internal string? After { get; } = after;

    internal Action<RosterHarness> Run { get; } = run;
}

/// <summary>
/// Reflection harness over the pinned 0.6.3 roster types. Nothing here models the host mod: snapshots and
/// carriers are produced by <c>LanConnectRosterCodec.Decode</c> from canonical bytes, i.e. the objects the wire
/// would produce, and revisions come from the real <c>CommitHostSnapshot</c>.
///
/// Two constraints shape it:
/// <list type="bullet">
/// <item><c>Decode</c> takes <c>ReadOnlySpan&lt;byte&gt;</c>, which <c>MethodInfo.Invoke</c> cannot bind, so it is
/// bound through a delegate with a matching signature.</item>
/// <item>Every roster type is internal to the host mod, so instances are made through
/// <c>Activator.CreateInstance</c> over the non-public constructor and every call goes through reflection.</item>
/// </list>
///
/// The carrier payload bytes are deliberately opaque: the codec validates only bit/byte length consistency
/// (8 bits in one byte) and nothing here deserializes a lobby player, which is what keeps this tool free of the
/// game's model-id table.
/// </summary>
internal sealed class RosterHarness
{
    internal const ulong HostId = 1UL;

    internal const ulong GuestId = 501UL;

    internal const ulong KickedId = 1547UL;

    internal const ulong JoinerId = 2600UL;

    internal const string MembershipRejection =
        "State-transition snapshot changed roster membership or real slot assignments.";

    internal const string PeerSetRejection =
        "Roster snapshot differs from the authoritative connected-peer set.";

    internal const string CurrentStateRejection =
        "Current-state snapshots must repeat the accepted revision byte-for-byte.";

    private const BindingFlags Any =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private const string Scripts = "Sts2LanConnect.Scripts";

    private delegate object DecodeSpan(ReadOnlySpan<byte> payload);

    private readonly Type _authorityType;
    private readonly Type _useType;
    private readonly MethodInfo _accept;
    private readonly MethodInfo _commit;
    private readonly PropertyInfo _current;
    private readonly PropertyInfo _players;
    private readonly PropertyInfo _playerId;
    private readonly PropertyInfo _revision;
    private readonly DecodeSpan _decode;

    private RosterHarness(Assembly host)
    {
        _authorityType = Require(host, Scripts + ".LanConnectRosterAuthorityState");
        Type snapshotType = Require(host, Scripts + ".LanConnectRosterSnapshot");
        _useType = Require(host, Scripts + ".LanConnectRosterSnapshotUse");
        Require(host, Scripts + ".LanConnectRosterPlayerCarrier");
        Type codec = Require(host, Scripts + ".LanConnectRosterCodec");

        _accept = _authorityType.GetMethod("Accept", Any)
            ?? throw new MissingMethodException(_authorityType.FullName, "Accept");
        _commit = _authorityType.GetMethod("CommitHostSnapshot", Any)
            ?? throw new MissingMethodException(_authorityType.FullName, "CommitHostSnapshot");
        _current = _authorityType.GetProperty("Current", Any)
            ?? throw new MissingMemberException(_authorityType.FullName, "Current");
        _players = snapshotType.GetProperty("Players", Any)
            ?? throw new MissingMemberException(snapshotType.FullName, "Players");
        _revision = snapshotType.GetProperty("RosterRevision", Any)
            ?? throw new MissingMemberException(snapshotType.FullName, "RosterRevision");

        Type carrier = _players.PropertyType.GetGenericArguments()[0];
        _playerId = carrier.GetProperty("PlayerId", Any)
            ?? throw new MissingMemberException(carrier.FullName, "PlayerId");

        MethodInfo decode = codec.GetMethod("Decode", Any)
            ?? throw new MissingMethodException(codec.FullName, "Decode");
        _decode = (DecodeSpan)decode.CreateDelegate(typeof(DecodeSpan));
    }

    internal static RosterHarness Open(Assembly host) => new(host);

    internal string Describe() =>
        $"{_authorityType.Name}.Accept/{_accept.GetParameters().Length} params, " +
        $"uses=[{string.Join(",", Enum.GetNames(_useType))}]";

    internal object NewAuthority(ulong hostPeerId) =>
        Activator.CreateInstance(
            _authorityType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [hostPeerId],
            culture: null)!;

    /// <summary>Host side: publishes a roster and returns the snapshot carrying the revision upstream assigned.</summary>
    internal object Commit(object authority, (ulong Id, byte Slot)[] players)
    {
        object built = Snapshot(HostId, 1u, players);
        return _commit.Invoke(authority, [_players.GetValue(built)])!;
    }

    internal void Accept(
        object authority,
        object snapshot,
        string use,
        IReadOnlyCollection<ulong>? membership = null,
        ulong? connectedPeerId = null) =>
        _ = _accept.Invoke(
            authority,
            [HostId, snapshot, Enum.Parse(_useType, use), membership, connectedPeerId]);

    internal IReadOnlyList<ulong> CurrentPlayerIds(object authority) =>
        _current.GetValue(authority) is { } current ? PlayerIdsOf(current) : [];

    internal uint RevisionOf(object snapshot) => (uint)_revision.GetValue(snapshot)!;

    private IReadOnlyList<ulong> PlayerIdsOf(object snapshot)
    {
        List<ulong> ids = [];
        if (_players.GetValue(snapshot) is IEnumerable players)
        {
            foreach (object? player in players)
            {
                if (player != null && _playerId.GetValue(player) is ulong id)
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    private object Snapshot(ulong authorityPeerId, uint revision, (ulong Id, byte Slot)[] players) =>
        _decode(BuildCanonical(authorityPeerId, revision, players));

    /// <summary>
    /// Assembles the canonical roster payload <c>LanConnectRosterCodec.Encode</c> produces
    /// (LanConnectRosterCodec.cs:21-67): a 15-byte header, then 13 fixed bytes plus the carrier bytes per player.
    /// </summary>
    private static byte[] BuildCanonical(ulong authorityPeerId, uint revision, (ulong Id, byte Slot)[] players)
    {
        const int bitLength = 8;
        const int bytesPerPlayer = 1;

        byte[] payload = new byte[15 + players.Length * (13 + bytesPerPlayer)];
        payload[0] = 1;
        payload[1] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(2, 8), authorityPeerId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(10, 4), revision);
        payload[14] = (byte)players.Length;

        int offset = 15;
        foreach ((ulong id, byte slot) in players)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(offset, 8), id);
            offset += 8;
            payload[offset++] = slot;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(offset, 4), bitLength);
            offset += 4;
            payload[offset++] = 0x5A;
        }

        return payload;
    }

    private static Type Require(Assembly assembly, string fullName) =>
        assembly.GetType(fullName, throwOnError: false, ignoreCase: false)
        ?? throw new InvalidOperationException(
            $"Type '{fullName}' was not found in assembly '{assembly.GetName().Name}'.");
}
