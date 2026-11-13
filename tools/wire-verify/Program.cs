using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Lobby;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using Sts2LobbyEnhance;

namespace WireVerify;

/// <summary>
/// Second Harmony prefix installed on the same target, with a priority above the fix's
/// <see cref="Priority.First"/> so it runs first and can observe the positional arguments Harmony
/// actually bound. It returns true so the fix's prefix still runs afterwards.
/// </summary>
internal static class BindingProbe
{
    internal static int Calls;
    internal static PacketWriter? Writer;
    internal static ClientLobbyJoinResponseMessage Message;
    internal static int Width = int.MinValue;

    internal static void Reset()
    {
        Calls = 0;
        Writer = null;
        Message = default;
        Width = int.MinValue;
    }

    internal static bool Prefix(PacketWriter __0, ClientLobbyJoinResponseMessage __1, int __2)
    {
        Calls++;
        Writer = __0;
        Message = __1;
        Width = __2;
        return true;
    }
}

internal static class Program
{
    private const string SerializationPatchesTypeName = "Sts2LanConnect.Scripts.LanConnectSerializationPatches";
    private const string BodyMethodName = "SerializeJoinResponseBody";

    private static int _failures;
    private static int _checks;

    private static MethodInfo _body = null!;
    private static int _targetIlBytes;

    private static int Main(string[] args)
    {
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

        if (!File.Exists(hostDll))
        {
            Console.WriteLine("SKIP: host mod dll not found.");
            return 3;
        }

        Assembly host = Assembly.LoadFrom(hostDll);
        Console.WriteLine($"host mod : {host.GetName().Name} {host.GetName().Version}");
        Console.WriteLine();

        Section("1. 补丁安装（走真实入口 Entry.RunSilently）");
        StartupReport report = Entry.RunSilently();

        // MegaCrit 的 Log 在非 Godot 宿主会 0xC0000005，宿主机里把补丁的日志静默掉再驱动它。
        SuppressEntryLogging();
        Check("Entry.RunSilently() 无失败项", report.Failed.Count == 0, string.Join(" | ", report.Failed));
        Check("host-wire 被 applied", report.Applied.Contains("host-wire"), string.Join(",", report.Applied));

        Type patchesType = host.GetType(SerializationPatchesTypeName, throwOnError: false)
            ?? throw new InvalidOperationException($"type not found: {SerializationPatchesTypeName}");

        _body = patchesType.GetMethod(
                    BodyMethodName,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: new[] { typeof(PacketWriter), typeof(ClientLobbyJoinResponseMessage), typeof(int) },
                    modifiers: null)
                ?? throw new MissingMethodException(patchesType.FullName, BodyMethodName);

        // One Harmony id per FixGroup; GetPatchedMethods() only reports the instance it is called on.
List<MethodBase> patched = Entry.AllHarmonyIds()
    .SelectMany(id => new Harmony(id).GetPatchedMethods())
    .Distinct()
    .ToList();
        Check("Harmony 已挂在 SerializeJoinResponseBody 上", patched.Contains(_body), Describe(patched));

        Section("2. 位宽来源与触发条件（模拟 compat_4x5_v1 会话选择）");
        bool selectionInstalled = TryInstallCompatSelection(host);
        Check("可注入 Compat4x5V1 会话选择", selectionInstalled, "reflection into LanConnectSessionProtocolState.Shared");

        if (selectionInstalled)
        {
            Type profiles = host.GetType("Sts2LanConnect.Scripts.LanConnectProtocolProfiles")!;
            Type wirePatches = host.GetType("Sts2LanConnect.Scripts.LanConnectCompatWirePatches")!;

            int activeWidth = (int)InvokeStatic(profiles, "GetActiveLobbyListBitWidth");
            int wireWidth = (int)InvokeStatic(wirePatches, "GetLobbyListBitWidth");
            bool forced = (bool)InvokeStatic(wirePatches, "ShouldForceCompatWireAtMessageBusBoundary");

            Check("LanConnectProtocolProfiles.GetActiveLobbyListBitWidth() == 5", activeWidth == 5, activeWidth.ToString());
            Check("LanConnectCompatWirePatches.GetLobbyListBitWidth() == 5", wireWidth == 5, wireWidth.ToString());
            Check("ShouldForceCompatWireAtMessageBusBoundary() == true", forced, forced.ToString());
        }

        Type serializationPatches = host.GetType(SerializationPatchesTypeName)!;
        bool boundaryDesktop = (bool)InvokeStatic(serializationPatches, "ShouldPatchCompatMessageBusBoundary", false);
        bool boundaryAndroid = (bool)InvokeStatic(serializationPatches, "ShouldPatchCompatMessageBusBoundary", true);
        Check("ShouldPatchCompatMessageBusBoundary(isAndroid:false) == true", boundaryDesktop, boundaryDesktop.ToString());
        Check("ShouldPatchCompatMessageBusBoundary(isAndroid:true) == false", !boundaryAndroid, boundaryAndroid.ToString());

        Section("3. Harmony 位置参数绑定 (__0 / __1 / __2)");
        InstallBindingProbe(_body);

        ClientLobbyJoinResponseMessage sample = BuildMessage(seed: "TESTSEED123", GameMode.Dread, isCustom: true, ascension: 20);

        BindingProbe.Reset();
        PacketWriter probeWriter = NewWriter();
        int bits5 = InvokeBody(probeWriter, sample, 5);

        Check("prefix 被调用（探针命中 1 次）", BindingProbe.Calls == 1, $"calls={BindingProbe.Calls}");
        Check("__0 与调用方传入的 writer 同一实例", ReferenceEquals(BindingProbe.Writer, probeWriter), "ReferenceEquals");
        Check("__1 与调用方传入的 message 一致", SameMessage(BindingProbe.Message, sample), Describe(BindingProbe.Message));
        Check("__2 等于调用方传入的 lobbyListBitWidth=5", BindingProbe.Width == 5, $"__2={BindingProbe.Width}");

        BindingProbe.Reset();
        PacketWriter probeWriter3 = NewWriter();
        int bits3 = InvokeBody(probeWriter3, sample, 3);
        Check("__2 随调用方变化（width=3）", BindingProbe.Width == 3, $"__2={BindingProbe.Width}");
        Check("prefix 每次调用都被命中", BindingProbe.Calls == 1, $"calls={BindingProbe.Calls}");
        Check("位宽参数生效：5bit 比 3bit 多 2 bit", bits5 - bits3 == 2, $"bits3={bits3}, bits5={bits5}");

        Section("4. 与 0.113 原版 Serialize 逐字节比对（宽度取 3 时唯一差异应消失）");
        VerifyAgainstVanilla("空列表 / 无 seed / None", BuildMessage(seed: null, GameMode.None, isCustom: false, ascension: 0));
        VerifyAgainstVanilla("Dread / 自定义 / 20 级", sample);
        VerifyAgainstVanilla("Blitz / 自定义 / 31 级 + dailyTime", BuildMessage(seed: "SEED", GameMode.Blitz, isCustom: true, ascension: 31, withDailyTime: true));
        VerifyAgainstVanilla("Daily / 无自定义 / 无 seed + dailyTime", BuildMessage(seed: null, GameMode.Daily, isCustom: false, ascension: 5, withDailyTime: true));
        VerifyAgainstVanilla("非 ASCII seed（多字节 UTF-8）", BuildMessage(seed: "中文种子-A", GameMode.Standard, isCustom: false, ascension: 1));

        Section("5. 接收端可解析性（用游戏自己的 0.113 Deserialize 回读）");
        RoundTripThroughVanillaReader("宽度 3（原版布局）", sample, 3);
        RoundTripThroughVanillaReader("宽度 5（compat_4x5_v1，先按 5bit 读表头再回灌）", sample, 5);
        RoundTripThroughVanillaReader("宽度 5 + dailyTime", BuildMessage(seed: "SEED", GameMode.Blitz, isCustom: true, ascension: 31, withDailyTime: true), 5);

        Section("6. 复现 0.6.3 的 0.111 报文（确认症状来自这里）");
        ReproduceLegacyBody(sample);

        Section("7. 字段位偏移实测（宽度 5，seed=TESTSEED123, Dread, isCustom, ascension=20）");
        PrintLayout(sample, 5);

        Section("8. 补丁覆盖面证据");
        _targetIlBytes = IlSize(_body);
        Console.WriteLine($"目标方法 IL 字节数        : {_targetIlBytes}");
        Check("目标方法 IL 足够大，JIT 不会把它内联进调用方（>64 字节）", _targetIlBytes > 64, $"{_targetIlBytes} bytes");

        List<string> callers = FindCallers(host, _body);
        Console.WriteLine("调用点（IL call/callvirt 扫描）:");
        foreach (string caller in callers)
        {
            Console.WriteLine("  " + caller);
        }

        Check("SerializeJoinResponseBody 只有 1 个调用点", callers.Count == 1, string.Join("; ", callers));
        Check("唯一调用点就是 message-bus 边界 prefix", callers.Count == 1 && callers[0].Contains("SerializeJoinResponseAtMessageBusPrefix", StringComparison.Ordinal), callers.FirstOrDefault() ?? "<none>");

        Section("9. bodyBytes 换算");
        int headerBits = 8 + 64;
        Console.WriteLine($"host 的 WriteCompatHeaderAndMeasure 计算 length = ceil((8 + 64 + bodyBits) / 8)");
        Console.WriteLine($"  样例 bodyBits(width=5) = {bits5} -> bodyBytes = {Ceil(bits5 + headerBits, 8)}");
        Console.WriteLine($"  样例 bodyBits(width=3) = {bits3} -> bodyBytes = {Ceil(bits3 + headerBits, 8)}");
        Console.WriteLine($"  0.111 破损 body 少写 4 bit，故同一消息的 bodyBytes 可能相同、但字段边界已整体错位");

        Console.WriteLine();
        if (_failures == 0)
        {
            Console.WriteLine($"OK — {_checks} 项检查全部通过。");
            return 0;
        }

        Console.WriteLine($"FAIL — {_failures}/{_checks} 项检查失败。");
        return 2;
    }

    // ---------------------------------------------------------------- checks

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("---- " + title + " ----");
    }

    private static void Check(string name, bool ok, string? detail)
    {
        _checks++;
        if (!ok)
        {
            _failures++;
        }

        Console.WriteLine($"[{((ok) ? "PASS" : "FAIL")}] {name}" + (string.IsNullOrEmpty(detail) ? string.Empty : $"  ({detail})"));
    }

    private static void VerifyAgainstVanilla(string label, ClientLobbyJoinResponseMessage message)
    {
        PacketWriter vanillaWriter = NewWriter();
        message.Serialize(vanillaWriter);
        int vanillaBits = vanillaWriter.BitPosition;
        byte[] vanilla = Slice(vanillaWriter);

        PacketWriter patchedWriter = NewWriter();
        int patchedBits = InvokeBody(patchedWriter, message, 3);
        byte[] patched = Slice(patchedWriter);

        bool same = vanillaBits == patchedBits && vanilla.AsSpan().SequenceEqual(patched);
        Check(
            $"{label}: 与 0.113 Serialize 逐字节一致",
            same,
            same ? $"{vanillaBits} bits / {vanilla.Length} B" : $"vanilla={vanillaBits}bits {Hex(vanilla)} patched={patchedBits}bits {Hex(patched)}");
    }

    private static void RoundTripThroughVanillaReader(string label, ClientLobbyJoinResponseMessage message, int listWidth)
    {
        PacketWriter writer = NewWriter();
        int bits = InvokeBody(writer, message, listWidth);
        byte[] body = Slice(writer);

        if (listWidth != 3)
        {
            body = RetagListWidth(body, listWidth, 3, bits, out bits);
        }

        ClientLobbyJoinResponseMessage readBack;
        try
        {
            PacketReader reader = new();
            reader.Reset(body);
            readBack = default;
            readBack.Deserialize(reader);
        }
        catch (Exception exception)
        {
            Check($"{label}: 0.113 Deserialize 回读成功", false, exception.GetType().Name + ": " + exception.Message);
            return;
        }

        bool same = SameMessage(readBack, message);
        Check(
            $"{label}: 0.113 Deserialize 回读全部字段一致",
            same,
            same
                ? $"gameMode={readBack.gameMode} isCustom={readBack.isCustom} ascension={readBack.ascension} seed={readBack.seed ?? "<null>"}"
                : $"期望 gameMode={message.gameMode} isCustom={message.isCustom} ascension={message.ascension} seed={message.seed ?? "<null>"} / 实得 gameMode={readBack.gameMode} isCustom={readBack.isCustom} ascension={readBack.ascension} seed={readBack.seed ?? "<null>"}");
    }

    private static void ReproduceLegacyBody(ClientLobbyJoinResponseMessage message)
    {
        const int listWidth = 5;

        PacketWriter writer = NewWriter();
        WriteLegacy0111Body(writer, message, listWidth);
        int legacyBits = writer.BitPosition;
        byte[] legacy = Slice(writer);

        PacketWriter fixedWriter = NewWriter();
        int fixedBits = InvokeBody(fixedWriter, message, listWidth);
        byte[] fixedBody = Slice(fixedWriter);

        Console.WriteLine($"0.111 报文 {legacyBits} bits / {legacy.Length} B  vs  0.113 报文 {fixedBits} bits / {fixedBody.Length} B  (差 {fixedBits - legacyBits} bit)");
        Check("0.111 报文比 0.113 少 4 bit（gameMode 3 + isCustom 1）", fixedBits - legacyBits == 4, $"{fixedBits - legacyBits}");

        // (a) 同宽度接收端：先按 5bit 读表头，其余字段按 0.113 读。
        byte[] asCompatPeer = RetagListWidth(legacy, listWidth, 3, legacyBits, out int compatBits);
        try
        {
            PacketReader reader = new();
            reader.Reset(asCompatPeer);
            ClientLobbyJoinResponseMessage decoded = default;
            decoded.Deserialize(reader);
            Console.WriteLine($"  (a) 同档位接收端解析结果: gameMode={decoded.gameMode} isCustom={decoded.isCustom} ascension={decoded.ascension} modifiers={decoded.modifiers?.Count} seed={decoded.seed ?? "<null>"}");
            Console.WriteLine($"      期望值           : gameMode={message.gameMode} isCustom={message.isCustom} ascension={message.ascension} modifiers={message.modifiers.Count} seed={message.seed ?? "<null>"}");
            Check(
                "(a) 同档位接收端解出错误字段（这就是现场症状的成因）",
                decoded.gameMode != message.gameMode || decoded.ascension != message.ascension || decoded.isCustom != message.isCustom,
                $"gameMode {message.gameMode}->{decoded.gameMode}, isCustom {message.isCustom}->{decoded.isCustom}, ascension {message.ascension}->{decoded.ascension}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"  (a) 同档位接收端解析抛异常: {exception.GetType().Name}: {exception.Message}");
            // 以前这里是 Check(..., true, ...)：抛什么异常都 PASS，包括本工具自己的空引用 bug。
            // 只接受「解码/越界」这一族异常 —— 那才是格式错位的症状；其它异常说明校验器本身坏了。
            Check(
                "(a) 同档位接收端解析失败（与现场症状一致）",
                exception is IndexOutOfRangeException
                    or ArgumentOutOfRangeException
                    or ArgumentException
                    or InvalidOperationException
                    or BadImageFormatException,
                exception.GetType().Name);
        }

        // (b) 表头按 3bit 读（未协商到 compat_4x5_v1 的对端）：表头少读 2 bit，后面全部跟着错位。
        try
        {
            PacketReader reader = new();
            reader.Reset(legacy);
            ClientLobbyJoinResponseMessage decoded = default;
            decoded.Deserialize(reader);
            Console.WriteLine($"  (b) 3bit 表头接收端解析结果: gameMode={decoded.gameMode} isCustom={decoded.isCustom} ascension={decoded.ascension} modifiers={decoded.modifiers?.Count} seed={decoded.seed ?? "<null>"}");
            // 以前这里是 Check(..., true, ...)：无条件 PASS。真正要断言的是「解出来的字段与发送值不同」，
            // 那才是 3bit 表头读 5bit 报文的症状。如果解出的字段全都正确，这条检查就没有意义。
            Check(
                "(b) 3bit 表头接收端无法正确解析",
                decoded.gameMode != message.gameMode
                    || decoded.isCustom != message.isCustom
                    || decoded.ascension != message.ascension
                    || (decoded.modifiers?.Count ?? -1) != message.modifiers.Count,
                $"gameMode {message.gameMode}->{decoded.gameMode}, isCustom {message.isCustom}->{decoded.isCustom}, " +
                $"ascension {message.ascension}->{decoded.ascension}, modifiers {message.modifiers.Count}->{decoded.modifiers?.Count}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"  (b) 3bit 表头接收端抛异常: {exception.GetType().Name}: {exception.Message}");
            Check("(b) 3bit 表头接收端越界（IndexOutOfRangeException 现场症状）", exception is IndexOutOfRangeException || exception is ArgumentOutOfRangeException, exception.GetType().Name);
        }

        // (c) 报文尾部 4 bit 落在 packet 之外：modifiers 计数会吃到包外/上一包的残留字节。
        int countStart = ReplayToModifierCount(asCompatPeer, 3);
        int countEnd = countStart + 32;
        Console.WriteLine($"  (c) 接收端读 modifiers 计数的窗口 = bit {countStart}..{countEnd - 1}，报文只有 {compatBits} bit（{Ceil(compatBits, 8)} 字节）");
        Check("(c) modifiers 计数窗口越过报文末尾（越界读的机制）", countEnd > compatBits, $"{countEnd} > {compatBits}");

        // (d) 计数窗口非 0 时（真实房间带 >=16 个 modifier，或复用 buffer 残留位为 1）就会真的越界。
        byte[] withModifiers = LegacyBodyWithModifierCount(message, listWidth, 16, out int withModifiersBits);
        byte[] legacyWithModifiers = RetagListWidth(withModifiers, listWidth, 3, withModifiersBits, out _);
        int decodedCount = PeekInt(legacyWithModifiers, countStart, 32);
        Console.WriteLine($"  (d) 把 0.111 报文里的 modifiers 计数设为 16 后，接收端解出的计数 = {decodedCount}");
        Check("(d1) 计数非 0，接收端会进入元素解析", decodedCount > 0, decodedCount.ToString());

        int elementStart = countStart + 32;
        try
        {
            PeekInt(legacyWithModifiers, elementStart, 32);
            Check("(d2) 从元素起点读取越过报文末尾", false, "未抛异常");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"  (d2) 从 bit {elementStart} 读 32 bit（报文只有 {compatBits} bit）-> {exception.GetType().Name}");
            Check("(d2) 元素起点已在报文之外 -> IndexOutOfRangeException", exception is IndexOutOfRangeException, exception.GetType().Name);
        }

        try
        {
            PacketReader reader = new();
            reader.Reset(legacyWithModifiers);
            ClientLobbyJoinResponseMessage decoded = default;
            decoded.Deserialize(reader);
            Check("(d3) 整条 Deserialize 应当失败", false, $"未抛异常，modifiers={decoded.modifiers?.Count}");
        }
        catch (Exception exception)
        {
            // 宿主机里 ModelIdSerializationCache 未初始化，会先于越界读触发；游戏里它已初始化，
            // 同一次读取会一直走到报文末尾并抛 IndexOutOfRangeException。
            bool expected = exception is IndexOutOfRangeException
                            || exception.Message.Contains("ModelIdSerializationCache", StringComparison.Ordinal);
            Console.WriteLine($"  (d3) Deserialize 抛异常: {exception.GetType().Name}: {exception.Message}");
            Check("(d3) Deserialize 失败（越界，或先被 ModelId 缓存守卫拦下）", expected, exception.GetType().Name);
        }
    }

    /// <summary>Replays the 0.113 reader up to (not including) the modifiers count and returns that bit offset.</summary>
    private static int ReplayToModifierCount(byte[] body, int listWidth)
    {
        PacketReader reader = new();
        reader.Reset(body);
        reader.ReadList<StartRunLobbyPlayer>(listWidth);
        if (reader.ReadBool())
        {
            reader.Read<MegaCrit.Sts2.Core.Daily.TimeServerResult>();
        }

        if (reader.ReadBool())
        {
            reader.ReadString();
        }

        reader.ReadEnum<GameMode>();
        reader.ReadBool();
        reader.ReadInt(5);
        return reader.BitPosition;
    }

    private static int PeekInt(byte[] body, int bitOffset, int bits)
    {
        PacketReader reader = new();
        reader.Reset(body);
        while (reader.BitPosition < bitOffset)
        {
            reader.ReadByte(Math.Min(8, bitOffset - reader.BitPosition));
        }

        return reader.ReadInt(bits);
    }

    private static byte[] LegacyBodyWithModifierCount(ClientLobbyJoinResponseMessage message, int listWidth, int modifierCount, out int bits)
    {
        PacketWriter writer = NewWriter();
        writer.WriteList(message.playersInLobby!, listWidth);
        writer.WriteBool(message.dailyTime.HasValue);
        if (message.dailyTime.HasValue)
        {
            writer.Write(message.dailyTime.Value);
        }

        writer.WriteBool(message.seed != null);
        if (message.seed != null)
        {
            writer.WriteString(message.seed);
        }

        writer.WriteInt(message.ascension, 5);
        writer.WriteInt(modifierCount, 32);
        bits = writer.BitPosition;
        return Slice(writer);
    }

    private static void PrintLayout(ClientLobbyJoinResponseMessage message, int listWidth)
    {
        PacketWriter writer = NewWriter();
        int bits = InvokeBody(writer, message, listWidth);
        byte[] body = Slice(writer);
        PacketReader reader = new();
        reader.Reset(body);

        int mark = reader.BitPosition;
        int count = reader.ReadInt(listWidth);
        Line("playersInLobby.count", mark, reader.BitPosition, $"WriteList(list, {listWidth}) → WriteInt(count, {listWidth})", count);

        mark = reader.BitPosition;
        bool hasDaily = reader.ReadBool();
        Line("dailyTime.HasValue", mark, reader.BitPosition, "WriteBool", hasDaily);

        if (hasDaily)
        {
            mark = reader.BitPosition;
            reader.ReadLong();
            reader.ReadLong();
            Line("dailyTime (serverTime+localReceivedTime)", mark, reader.BitPosition, "Write(TimeServerResult) → 2×WriteLong(64)", "128 bit");
        }

        mark = reader.BitPosition;
        bool hasSeed = reader.ReadBool();
        Line("seed != null", mark, reader.BitPosition, "WriteBool", hasSeed);

        if (hasSeed)
        {
            mark = reader.BitPosition;
            int length = reader.ReadInt();
            Line("seed.length", mark, reader.BitPosition, "WriteString → WriteInt(len, 32)", length);

            mark = reader.BitPosition;
            reader.ReadBytes(new byte[Math.Max(length, 1)], length);
            Line("seed.bytes", mark, reader.BitPosition, "WriteBytes(len×8)", length * 8 + " bit");
        }

        mark = reader.BitPosition;
        int gameModeRaw = reader.ReadInt(EnumBits<GameMode>());
        Line("gameMode", mark, reader.BitPosition, $"WriteEnum → WriteInt(v, {EnumBits<GameMode>()})", $"{(GameMode)gameModeRaw} ({gameModeRaw})");

        mark = reader.BitPosition;
        bool isCustom = reader.ReadBool();
        Line("isCustom", mark, reader.BitPosition, "WriteBool", isCustom);

        mark = reader.BitPosition;
        int ascension = reader.ReadInt(5);
        Line("ascension", mark, reader.BitPosition, "WriteInt(v, 5)", ascension);

        mark = reader.BitPosition;
        int modifiers = reader.ReadInt(32);
        Line("modifiers.count", mark, reader.BitPosition, "WriteList(modifiers) → WriteInt(count, 32)", modifiers);

        Console.WriteLine($"  合计 {bits} bit = {Ceil(bits, 8)} 字节（含尾部填充 {Ceil(bits, 8) * 8 - bits} bit）");
    }

    private static void Line(string field, int from, int to, string how, object value)
    {
        Console.WriteLine($"  bit {from,4}..{to - 1,-4} ({to - from,3} bit)  {field,-42} {how,-52} = {value}");
    }

    private static int EnumBits<T>() where T : struct, Enum =>
        (int)Math.Ceiling(Math.Log2(MaxEnumValueCache.Get<T>()) + 1.0);

    // ---------------------------------------------------------------- helpers

    /// <summary>Silences <see cref="Entry"/>'s log helpers; MegaCrit's logger needs a Godot host.</summary>
    private static void SuppressEntryLogging()
    {
        typeof(Entry)
            .GetField("_suppressLogging", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, true);
    }

    private static void InstallBindingProbe(MethodInfo target)
    {
        HarmonyMethod prefix = new(typeof(BindingProbe).GetMethod(nameof(BindingProbe.Prefix), BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!)
        {
            priority = 900
        };

        new Harmony("wire-verify-binding-probe").Patch(target, prefix: prefix);
    }

    private static int InvokeBody(PacketWriter writer, ClientLobbyJoinResponseMessage message, int listBitWidth)
    {
        try
        {
            _body.Invoke(null, new object?[] { writer, message, listBitWidth });
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            throw exception.InnerException;
        }

        return writer.BitPosition;
    }

    /// <summary>Verbatim re-implementation of LAN Connect 0.6.3's hand-written 0.111 body.</summary>
    private static void WriteLegacy0111Body(PacketWriter writer, ClientLobbyJoinResponseMessage message, int listBitWidth)
    {
        writer.WriteList(message.playersInLobby!, listBitWidth);
        writer.WriteBool(message.dailyTime.HasValue);
        if (message.dailyTime.HasValue)
        {
            writer.Write(message.dailyTime.Value);
        }

        writer.WriteBool(message.seed != null);
        if (message.seed != null)
        {
            writer.WriteString(message.seed);
        }

        writer.WriteInt(message.ascension, 5);
        writer.WriteList(message.modifiers, 32);
    }

    /// <summary>
    /// Rewrites only the list-length header from <paramref name="fromWidth"/> to <paramref name="toWidth"/>
    /// bits, copying every remaining bit verbatim. Used to isolate "the header width" from "the rest of
    /// the body" when feeding a compat-width packet to the game's fixed 3-bit reader.
    /// </summary>
    private static byte[] RetagListWidth(byte[] body, int fromWidth, int toWidth, int totalBits, out int newBits)
    {
        PacketReader reader = new();
        reader.Reset(body);
        List<StartRunLobbyPlayer> players = reader.ReadList<StartRunLobbyPlayer>(fromWidth);
        int tailStart = reader.BitPosition;

        PacketWriter writer = NewWriter();
        writer.WriteList(players, toWidth);

        while (reader.BitPosition < totalBits)
        {
            int take = Math.Min(8, totalBits - reader.BitPosition);
            writer.WriteByte(reader.ReadByte(take), take);
        }

        newBits = writer.BitPosition;
        return Slice(writer);
    }

    private static PacketWriter NewWriter() => new()
    {
        WarnOnGrow = false
    };

    private static byte[] Slice(PacketWriter writer) =>
        writer.Buffer.AsSpan(0, Ceil(writer.BitPosition, 8)).ToArray();

    private static int Ceil(int value, int divisor) => (value + divisor - 1) / divisor;

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    private static ClientLobbyJoinResponseMessage BuildMessage(
        string? seed,
        GameMode gameMode,
        bool isCustom,
        int ascension,
        bool withDailyTime = false)
    {
        return new ClientLobbyJoinResponseMessage
        {
            playersInLobby = new List<StartRunLobbyPlayer>(),
            dailyTime = withDailyTime
                ? new MegaCrit.Sts2.Core.Daily.TimeServerResult
                {
                    serverTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                    localReceivedTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_042)
                }
                : null,
            gameMode = gameMode,
            isCustom = isCustom,
            ascension = ascension,
            seed = seed,
            modifiers = new List<MegaCrit.Sts2.Core.Saves.Runs.SerializableModifier>()
        };
    }

    private static bool SameMessage(ClientLobbyJoinResponseMessage left, ClientLobbyJoinResponseMessage right) =>
        (left.playersInLobby?.Count ?? 0) == (right.playersInLobby?.Count ?? 0)
        && left.dailyTime.Equals(right.dailyTime)
        && left.gameMode == right.gameMode
        && left.isCustom == right.isCustom
        && left.ascension == right.ascension
        && string.Equals(left.seed, right.seed, StringComparison.Ordinal)
        && (left.modifiers?.Count ?? 0) == (right.modifiers?.Count ?? 0);

    private static string Describe(ClientLobbyJoinResponseMessage message) =>
        $"players={message.playersInLobby?.Count ?? -1} daily={message.dailyTime.HasValue} " +
        $"gameMode={message.gameMode} isCustom={message.isCustom} ascension={message.ascension} " +
        $"seed={message.seed ?? "<null>"} modifiers={message.modifiers?.Count ?? -1}";

    private static string Describe(List<MethodBase> methods) =>
        string.Join(", ", methods.Select(method => $"{method.DeclaringType?.Name}::{method.Name}"));

    private static object InvokeStatic(Type type, string name, params object?[] arguments)
    {
        MethodInfo method = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal)
                                                             && candidate.GetParameters().Length == arguments.Length)
                            ?? throw new MissingMethodException(type.FullName, name);

        return method.Invoke(null, arguments)!;
    }

    /// <summary>
    /// Freezes a compat_4x5_v1 selection into the host mod's session state so the profile-dependent
    /// width/trigger helpers can be evaluated without a live lobby.
    /// </summary>
    private static bool TryInstallCompatSelection(Assembly host)
    {
        try
        {
            Type selectionType = host.GetType("Sts2LanConnect.Scripts.LanConnectProtocolSelection")!;
            Type stateType = host.GetType("Sts2LanConnect.Scripts.LanConnectSessionProtocolState")!;
            Type snapshotType = host.GetType("Sts2LanConnect.Scripts.LanConnectSessionProtocolSnapshot")!;
            Type phaseType = host.GetType("Sts2LanConnect.Scripts.LanConnectSessionProtocolPhase")!;
            Type roleType = host.GetType("Sts2LanConnect.Scripts.LanConnectSessionProtocolRole")!;

            MethodInfo create = selectionType.GetMethod(
                                    "CreateLocalCompat",
                                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                                ?? throw new MissingMethodException(selectionType.FullName, "CreateLocalCompat");

            object selection = create.Invoke(null, new object?[] { 4, "0.113.0", null })!;

            object shared = stateType
                                .GetProperty("Shared", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                                .GetValue(null)!;

            ConstructorInfo constructor = snapshotType.GetConstructors()
                .First(candidate => candidate.GetParameters().Length == 5);

            object snapshot = constructor.Invoke(new[]
            {
                Enum.Parse(phaseType, "Frozen"),
                Enum.Parse(roleType, "Host"),
                "wire-verify",
                1L,
                selection
            });

            stateType.GetField("_current", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(shared, snapshot);

            return true;
        }
        catch (Exception exception)
        {
            Console.WriteLine("  (模拟会话选择失败: " + exception.GetType().Name + ": " + exception.Message + ")");
            return false;
        }
    }

    private static int IlSize(MethodBase method)
    {
        try
        {
            return method.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Heuristic scan for call/callvirt instructions whose metadata token is <paramref name="target"/>.</summary>
    private static List<string> FindCallers(Assembly assembly, MethodInfo target)
    {
        List<string> callers = new();
        Type[] types;

        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            types = exception.Types.Where(type => type != null).Cast<Type>().ToArray();
        }

        foreach (Type type in types)
        {
            MethodBase[] methods;

            try
            {
                methods = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Cast<MethodBase>()
                    .Concat(type.GetConstructors(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    .ToArray();
            }
            catch
            {
                continue;
            }

            foreach (MethodBase method in methods)
            {
                byte[]? il = null;

                try
                {
                    il = method.GetMethodBody()?.GetILAsByteArray();
                }
                catch
                {
                    // Method without a readable body.
                }

                if (il == null || il.Length < 5)
                {
                    continue;
                }

                for (int index = 0; index + 4 < il.Length; index++)
                {
                    if (il[index] != 0x28 && il[index] != 0x6F)
                    {
                        continue;
                    }

                    int token = BitConverter.ToInt32(il, index + 1);
                    if (token == target.MetadataToken)
                    {
                        callers.Add($"{type.FullName}::{method.Name}  (IL_{index:X4}, opcode 0x{il[index]:X2})");
                        break;
                    }
                }
            }
        }

        return callers;
    }
}
