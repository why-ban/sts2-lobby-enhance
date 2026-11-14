using System.Runtime.CompilerServices;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Locates the binaries and the release workspace the suite inspects.
///
/// Every input has an environment-variable override so the suite is not tied to one machine layout.
/// Nothing here throws when an input is missing: tests decide whether a missing input means "skip"
/// (the normal case for a developer without the game installed) or "fail" (a pinned file that is
/// present but wrong).
/// </summary>
internal static class TestPaths
{
    /// <summary>Data directory of Slay the Spire 2, i.e. the folder holding sts2.dll.</summary>
    internal const string GameDirectoryVariable = "STS2_GAME_DATA_DIR";

    /// <summary>Path of the official sts2_lan_connect 0.6.3 DLL to scan.</summary>
    internal const string OfficialModVariable = "STS2_LAN_CONNECT_DLL";

    /// <summary>Root of the companion project (work/companion).</summary>
    internal const string CompanionRootVariable = "STS2_COMPANION_ROOT";

    /// <summary>Workshop upload workspace (defaults to &lt;companion&gt;/packaging).</summary>
    internal const string PackagingVariable = "STS2_PACKAGING_DIR";

    /// <summary>
    /// SHA-256 of the exact official build these assertions were pinned against. The patch is written
    /// against this binary; a different official build may have a different set of broken members, so
    /// a mismatch is reported as a failure instead of being silently tolerated.
    /// </summary>
    internal const string PinnedOfficialModSha256 =
        "197E5D2E67C9BC805170F842D7CB5BF14F150F2E478C658C9DC25BEB2AD59D4B";

    internal const long PinnedOfficialModSize = 2_473_472L;

    /// <summary>Workshop item ID of the official mod; also the value that must appear in workshop.json.</summary>
    internal const ulong OfficialWorkshopItemId = 3_749_766_330UL;

    /// <summary>Steam application ID of Slay the Spire 2, used by the uploader's steam_appid.txt.</summary>
    internal const string SlayTheSpire2AppId = "2868840";

    private static readonly string ThisFile = ResolveThisFile();

    /// <summary>Root of the companion project, i.e. the folder holding src/, tests/, packaging/, docs/.</summary>
    internal static string CompanionRoot { get; } = ResolveCompanionRoot();

    internal static string PackagingDirectory { get; } = ResolvePackagingDirectory();

    internal static string DocsDirectory => Path.Combine(CompanionRoot, "docs");

    /// <summary>
    /// Source file the upload ships, by name. The licence sits at the repository root so GitHub recognises
    /// it; the packaging script and these tests follow that one copy instead of keeping a second one in docs/.
    /// </summary>
    internal static string DocumentationSource(string name) =>
        name == "LICENSE" ? Path.Combine(CompanionRoot, name) : Path.Combine(DocsDirectory, name);

    internal static string TestsDirectory => Path.Combine(CompanionRoot, "tests");

    /// <summary>
    /// The data directory of the installed game, or <c>null</c> when it cannot be found. Searched in
    /// order: environment override, the layout used by this project, then the two standard Steam
    /// library locations.
    /// </summary>
    internal static string? GameDirectory { get; } = ResolveGameDirectory();

    /// <summary>The official 0.6.3 DLL, or <c>null</c> when it is not on this machine.</summary>
    internal static string? OfficialModDll { get; } = ResolveOfficialModDll();

    /// <summary>Path of sts2.dll when the game directory is known.</summary>
    internal static string? GameAssembly =>
        GameDirectory is null ? null : Path.Combine(GameDirectory, "sts2.dll");

    /// <summary>The patch DLL produced by the src project, preferring Release. Null when not built.</summary>
    internal static string? BuiltPatchAssembly { get; } = ResolveBuiltPatchAssembly();

    /// <summary>Human-readable hint appended to skip reasons so a reader knows how to enable the test.</summary>
    internal static string GameDirectoryHint =>
        $"Install Slay the Spire 2, or set {GameDirectoryVariable} to the folder that contains sts2.dll.";

    internal static string OfficialModHint =>
        $"Set {OfficialModVariable} to sts2_lan_connect.dll (0.6.3, sha256 {PinnedOfficialModSha256}).";

    internal static string BuildHint =>
        "Build it first: dotnet build src/Sts2LobbyEnhance/Sts2LobbyEnhance.csproj -c Release -p:SkipModCopy=true";

    private static string ResolveThisFile([CallerFilePath] string path = "") => path;

    private static string ResolveCompanionRoot()
    {
        string? overridden = Environment.GetEnvironmentVariable(CompanionRootVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        // tests/Sts2LobbyEnhance.Tests/TestPaths.cs -> companion root
        string? testDirectory = Path.GetDirectoryName(ThisFile);
        string? testsDirectory = testDirectory is null ? null : Path.GetDirectoryName(testDirectory);
        string? companionRoot = testsDirectory is null ? null : Path.GetDirectoryName(testsDirectory);

        return companionRoot is null
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(companionRoot);
    }

    private static string ResolvePackagingDirectory()
    {
        string? overridden = Environment.GetEnvironmentVariable(PackagingVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(CompanionRoot, "packaging")
            : Path.GetFullPath(overridden);
    }

    private static string? ResolveGameDirectory()
    {
        string? overridden = Environment.GetEnvironmentVariable(GameDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return Directory.Exists(overridden) ? Path.GetFullPath(overridden) : null;
        }

        string[] candidates =
        [
            @"D:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64",
            @"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64",
            @"C:\Program Files\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64",
            @"E:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64"
        ];

        return candidates.FirstOrDefault(directory =>
            File.Exists(Path.Combine(directory, "sts2.dll")));
    }

    private static string? ResolveOfficialModDll()
    {
        string? overridden = Environment.GetEnvironmentVariable(OfficialModVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return File.Exists(overridden) ? Path.GetFullPath(overridden) : null;
        }

        // The upstream release bundle lives next to this workspace: work/lobby/STS2-Game-Lobby/releases/...
        string? workDirectory = Path.GetDirectoryName(CompanionRoot);
        if (workDirectory is null)
        {
            return null;
        }

        string candidate = Path.Combine(
            workDirectory,
            "lobby",
            "STS2-Game-Lobby",
            "releases",
            "sts2_lan_connect",
            "sts2_lan_connect.dll");

        return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
    }

    private static string? ResolveBuiltPatchAssembly()
    {
        string projectDirectory = Path.Combine(CompanionRoot, "src", "Sts2LobbyEnhance");

        string[] candidates =
        [
            Path.Combine(projectDirectory, "bin", "Release", "net9.0", "sts2_lobby_enhance.dll"),
            Path.Combine(projectDirectory, "bin", "Debug", "net9.0", "sts2_lobby_enhance.dll")
        ];

        return candidates.FirstOrDefault(File.Exists);
    }
}
