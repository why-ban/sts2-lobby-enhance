using System.Text.RegularExpressions;
using Xunit;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Pins down the *inventory* of repairs and the three repairs that had no automation at all.
///
/// Why this file exists: until it was added, nothing in the suite asserted how many repairs the patch
/// installs, so deleting one line from <c>Entry.cs</c>'s <c>CompatFixes.Build()</c> left every case green.
/// <c>join-screen-modes</c>, <c>mid-run-restart</c> and <c>unlock-extra-modes</c> had zero coverage of any
/// kind, and the v1.5 headline feature was <c>join-screen-modes</c>.
///
/// Scope, stated honestly:
///   * These assertions read the **source** of the patch. They are not a build and not a runtime test;
///     they cannot prove the patch behaves correctly in a game. They do prove that a repair cannot be
///     added, removed, renamed or reordered without someone editing an expectation on purpose.
///   * Runtime evidence that each repair actually installs its Harmony seams is asserted by
///     <c>tools/smoke-test</c>, which loads the shipped <c>packaging/content</c> DLL ("one real seam per
///     fix"). The two layers are complementary: this one cannot be skipped on a machine without the
///     game, and that one cannot be fooled by a stale build.
/// </summary>
public sealed class FixRegistryTests
{
    /// <summary>
    /// The exact repair ids <c>CompatFixes.Build()</c> must yield, in order.
    ///
    /// This list must stay identical to:
    ///   * <c>src/Sts2LobbyEnhance/Scripts/Entry.cs</c> — <c>CompatFixes.Build()</c>;
    ///   * <c>tools/smoke-test/Program.cs</c> — <c>expectedFixIds</c>.
    /// Three copies is deliberate: the source list, the shipped-DLL list and the human-facing list would
    /// all have to be edited together for a repair to disappear unnoticed.
    /// </summary>
    private static readonly string[] ExpectedFixIds =
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
        "mid-run-restart",
        "extra-game-modes",
        "new-protocol-only",
        "room-card-pill",
        "unlock-extra-modes"
    ];

    private static string ScriptsDirectory =>
        Path.Combine(TestPaths.CompanionRoot, "src", "Sts2LobbyEnhance", "Scripts");

    [Fact]
    public void CompatFixesAreExactlyTheExpectedIdsInOrder()
    {
        string entry = ReadScript("Entry.cs");
        string body = ExtractBuildBody(entry);

        List<string> classNames = Regex
            .Matches(body, @"yield\s+return\s+new\s+(?<type>\w+)\s*\(\s*\)\s*;")
            .Select(match => match.Groups["type"].Value)
            .ToList();

        Assert.False(
            classNames.Count == 0,
            "CompatFixes.Build() yielded no fixes; the extraction regex no longer matches Entry.cs.");

        List<string> actualIds = classNames.Select(ResolveFixId).ToList();

        Assert.True(
            ExpectedFixIds.SequenceEqual(actualIds, StringComparer.Ordinal),
            "CompatFixes.Build() does not yield the expected repairs." + Environment.NewLine +
            "  expected: [" + string.Join(", ", ExpectedFixIds) + "]" + Environment.NewLine +
            "  actual  : [" + string.Join(", ", actualIds) + "]" + Environment.NewLine +
            "  classes : [" + string.Join(", ", classNames) + "]" + Environment.NewLine +
            "Update this list, tools/smoke-test/Program.cs expectedFixIds and the documentation together.");

        // A rename inside a fix must not be able to slip through as "still 9 entries".
        Assert.Equal(classNames.Count, classNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void JoinScreenModesDispatchesEveryModeToTheGamesOwnScreen()
    {
        string source = ReadScript(Path.Combine("Join", "JoinScreenDispatchFix.cs"));

        Assert.Equal("join-screen-modes", ExtractFixId(source));

        // 0.113 gate: this repair must follow the game-side flag, not apply unconditionally. docs/README.md
        // claimed the opposite for a while; the code is the authority. Since v1.8.0 the gate is an early
        // return inside Applies(in, out reason) — the reason string is what the log prints when it skips.
        Assert.Contains(
            "public bool Applies(in CompatSnapshot snapshot, out string reason)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("if (!snapshot.InitialGameInfoLostGameMode)", source, StringComparison.Ordinal);

        // The host mod's own dispatch is replaced by a prefix on PushJoinedScreen(NSubmenuStack,
        // NetClientGameService, JoinResult). Since v1.8.0 that 3-parameter shape is verified in
        // Shape.TryCapture before anything is patched, by comparing parameter type *names* — naming a
        // version-dependent game type with typeof would make the probe itself fail to JIT — and a mismatch is
        // reported as a missing anchor instead of throwing.
        Assert.Contains("private const string DispatchMethodName = \"PushJoinedScreen\";", source, StringComparison.Ordinal);
        Assert.Contains("method.GetParameters() is { Length: 3 } parameters", source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(parameters[0].ParameterType.FullName, SubmenuStackTypeName", source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(parameters[1].ParameterType.FullName, NetClientServiceTypeName", source, StringComparison.Ordinal);
        Assert.Contains("string.Equals(parameters[2].ParameterType.FullName, JoinResultTypeName", source, StringComparison.Ordinal);

        // The prefix must only skip the original when it handled the state, and must return true
        // (run the original) otherwise: "the fix never invents behaviour for states it does not model".
        Assert.Contains("return !TryPushJoinedScreen(stack, netService, joinResult);", source, StringComparison.Ordinal);

        // The dispatch table itself, copied from the game's NJoinFriendScreen.JoinGameAsync.
        Dictionary<string, string> lobby = MapCasesToScreens(source, "TryPushLobbyScreen");
        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Standard"] = "NCharacterSelectScreen",
                ["Daily"] = "NDailyRunScreen",
                ["Blitz"] = "NBlitzScreen",
                ["Dread"] = "NDreadScreen"
            },
            lobby);

        Dictionary<string, string> load = MapCasesToScreens(source, "TryPushLoadScreen");
        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Standard"] = "NMultiplayerLoadGameScreen",
                ["Blitz"] = "NMultiplayerLoadGameScreen",
                ["Dread"] = "NMultiplayerLoadGameScreen",
                ["Daily"] = "NDailyRunLoadScreen"
            },
            load);

        // Custom stopped being a GameMode member in 0.113; it is decided by joinResult.isCustom and must
        // be checked *before* the enum switch, otherwise a custom run lands on its base mode's screen.
        string lobbyBody = ExtractMethodBody(source, "TryPushLobbyScreen");
        int customCheck = lobbyBody.IndexOf("if (joinResult.isCustom)", StringComparison.Ordinal);
        int enumSwitch = lobbyBody.IndexOf("switch (joinResult.gameMode)", StringComparison.Ordinal);
        Assert.True(
            customCheck >= 0 && enumSwitch >= 0 && customCheck < enumSwitch,
            "TryPushLobbyScreen must decide joinResult.isCustom before switching on gameMode.");
        Assert.Contains("GetSubmenuType<NCustomRunScreen>()", lobbyBody, StringComparison.Ordinal);

        string loadBody = ExtractMethodBody(source, "TryPushLoadScreen");
        int loadCustomCheck = loadBody.IndexOf("if (joinResult.isCustom)", StringComparison.Ordinal);
        int loadEnumSwitch = loadBody.IndexOf("switch (joinResult.gameMode)", StringComparison.Ordinal);
        Assert.True(
            loadCustomCheck >= 0 && loadEnumSwitch >= 0 && loadCustomCheck < loadEnumSwitch,
            "TryPushLoadScreen must decide joinResult.isCustom before switching on gameMode.");
        Assert.Contains("GetSubmenuType<NCustomRunLoadScreen>()", loadBody, StringComparison.Ordinal);
    }

    [Fact]
    public void UnlockExtraModesInjectsExactlyTwoEpochsAndNeverWritesToDisk()
    {
        string source = ReadScript(Path.Combine("Unlock", "UnlockExtraModesFix.cs"));

        Assert.Equal("unlock-extra-modes", ExtractFixId(source));

        // 注入的 epoch id 集合：恰好两个，且从游戏自己的 id 表取（EpochModel.GetId<T>），
        // 这样它们永远与 StartRunLobby 的 host 侧校验用同一份 id。
        MatchCollection injections = Regex.Matches(
            source,
            @"TryAdd\(ids,\s*nameof\((?<epoch>\w+)\),\s*static\s*\(\)\s*=>\s*EpochModel\.GetId<(?<type>\w+)>\(\)\);");

        string[] epochs = injections.Select(match => match.Groups["epoch"].Value).ToArray();
        string[] types = injections.Select(match => match.Groups["type"].Value).ToArray();

        Assert.Equal(["BlitzEpoch", "DreadEpoch"], epochs);
        Assert.Equal(epochs, types);

        // Both seams are postfixes: the repair changes what a read returns, it never takes over the call.
        Assert.Equal(2, Regex.Matches(source, @"postfix:\s*new HarmonyMethod").Count);
        Assert.DoesNotContain("prefix: new HarmonyMethod", source, StringComparison.Ordinal);

        // 不写盘。Comments are stripped first so the doc comment ("Nothing here writes to the save file")
        // cannot make this pass, and so a mention of WriteToDisk in prose cannot make it fail.
        string code = StripComments(source);
        foreach (string forbidden in new[]
                 {
                     "WriteToDisk", "File.WriteAll", "File.Create", "File.Delete",
                     "SaveManager.Save", "ProgressState.Epochs", ".Epochs.Add(", "IsEpochObtained"
                 })
        {
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
        }

        // The reported state is rebuilt from the public round trip instead of mutating UnlockState's
        // private HashSet, which is what keeps the injection free of side effects.
        Assert.Contains("__result.ToSerializable()", code, StringComparison.Ordinal);
        Assert.Contains("__result = new UnlockState(", code, StringComparison.Ordinal);
        Assert.Contains("serializable.UnlockedEpochs.Add(id)", code, StringComparison.Ordinal);

        // The two seams themselves. Since v1.8.0 they are resolved by name inside Shape.TryCapture, which is
        // metadata-only and never names a version-dependent game type with typeof, so the assertion pins the
        // name literals plus the required parameter/return shapes instead of a RequireInstanceMethod call.
        Assert.Contains("\"GenerateUnlockStateFromProgress\"", code, StringComparison.Ordinal);
        Assert.Contains("\"IsEpochRevealed\"", code, StringComparison.Ordinal);
        Assert.Contains("UnlockStateTypeName", code, StringComparison.Ordinal);
        Assert.Contains("BooleanTypeName", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MidRunRestartKeyAnchorsStillExist()
    {
        string source = ReadScript(Path.Combine("Restart", "MidRunRestartFix.cs"));

        Assert.Equal("mid-run-restart", ExtractFixId(source));

        // 这些锚点只证明「要接管的方法名与签名还在源码里」。它们**不能**证明运行时行为：
        // 「重开真的回到本层起点」「存档与房间绑定真的没被动」只有真机验证能证明，本仓库没有自动化。
        // 运行期「这些接缝真的被打上补丁」由 tools/smoke-test 的 one-real-seam-per-fix 断言覆盖。
        // Since v1.8.0 every anchor is resolved by Shape.TryCapture (metadata only, name + exact parameter
        // types), so the assertion pins the names and the two Disconnect prefixes rather than the old
        // Reflect.RequireInstanceMethod call shapes.
        Assert.Contains("\"StartHostedRunRestartAsync\"", source, StringComparison.Ordinal);
        Assert.Contains("\"StartRestartFlowAsync\"", source, StringComparison.Ordinal);
        Assert.Contains("\"HandshakeSucceeded\"", source, StringComparison.Ordinal);
        Assert.Contains("private static bool HostDisconnectPrefix", source, StringComparison.Ordinal);
        Assert.Contains("private static bool ClientDisconnectPrefix", source, StringComparison.Ordinal);

        // The panel takeover is deliberately optional (it is the only patch that touches a UI type); if it
        // is ever made mandatory, this comment and the try/catch have to change together.
        Assert.Contains("InstallPanelRestartFlowPatch(harmony, host, targets.StartHostedRestart);", source, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static string ReadScript(string relativePath)
    {
        string path = Path.Combine(ScriptsDirectory, relativePath);
        Assert.True(File.Exists(path), $"Missing source file: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Body of <c>CompatFixes.Build()</c> in Entry.cs.</summary>
    private static string ExtractBuildBody(string entrySource)
    {
        Match match = Regex.Match(
            entrySource,
            @"internal\s+static\s+IEnumerable<ICompatFix>\s+Build\s*\(\s*\)\s*\{(?<body>.*?)\r?\n\s{4}\}",
            RegexOptions.Singleline);

        Assert.True(match.Success, "Could not locate CompatFixes.Build() in Entry.cs.");
        return match.Groups["body"].Value;
    }

    /// <summary>Reads <c>public string Id =&gt; "..."</c> out of the fix's own file.</summary>
    private static string ResolveFixId(string className)
    {
        string[] candidates = Directory.GetFiles(ScriptsDirectory, className + ".cs", SearchOption.AllDirectories);
        Assert.True(
            candidates.Length == 1,
            $"Expected exactly one source file for {className}, found {candidates.Length}.");

        Match match = Regex.Match(File.ReadAllText(candidates[0]), @"public\s+string\s+Id\s*=>\s*""(?<id>[^""]+)"";");
        Assert.True(match.Success, $"Could not find 'public string Id => ...' in {candidates[0]}.");
        return match.Groups["id"].Value;
    }

    private static string ExtractFixId(string source)
    {
        Match match = Regex.Match(source, @"public\s+string\s+Id\s*=>\s*""(?<id>[^""]+)"";");
        Assert.True(match.Success, "Could not find 'public string Id => ...' in the fix source.");
        return match.Groups["id"].Value;
    }

    /// <summary>
    /// Source text of one private static method, from its signature up to the next member declaration.
    /// Good enough for the two dispatch methods: both are the last-but-one member of their file region
    /// and neither contains a nested "private static" declaration.
    /// </summary>
    private static string ExtractMethodBody(string source, string methodName)
    {
        string signature = "private static bool " + methodName + "(";
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {methodName} in JoinScreenDispatchFix.cs.");

        int end = source.IndexOf("\n    private static", start + signature.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            end = source.IndexOf("\n    ///", start + signature.Length, StringComparison.Ordinal);
        }

        return end < 0 ? source[start..] : source[start..end];
    }

    /// <summary>Maps every <c>case GameMode.X:</c> in a method to the screen type it pushes.</summary>
    private static Dictionary<string, string> MapCasesToScreens(string source, string methodName)
    {
        string body = ExtractMethodBody(source, methodName);
        Dictionary<string, string> map = new(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(body, @"case\s+GameMode\.(?<mode>\w+)\s*:"))
        {
            string mode = match.Groups["mode"].Value;
            Match screen = Regex.Match(body[match.Index..], @"GetSubmenuType<(?<type>\w+)>");

            Assert.True(screen.Success, $"case GameMode.{mode} in {methodName} pushes no GetSubmenuType<> screen.");
            map[mode] = screen.Groups["type"].Value;
        }

        return map;
    }

    /// <summary>
    /// Removes comments so "the code must not call X" cannot be satisfied or broken by prose.
    /// Not a full C# lexer: it assumes no string literal in the file contains "//" or "/*".
    /// </summary>
    private static string StripComments(string source)
    {
        string withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\r\n]*", string.Empty);
    }
}
