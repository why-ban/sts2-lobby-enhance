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
        "E9644876FF9F563E7784AE21C10F2B67C6EEBE2FAB53C8106D0D6AE1D3814AE9";

    internal const long PinnedOfficialModSize = 2_506_752L;

    /// <summary>
    /// The official sts2_lan_connect <b>0.6.4</b> DLL: the build this mod requires since 2.0.0. Both the
    /// path constants above and this one resolve to it — 0.6.3 is out of support, and loading two builds with
    /// the same assembly identity into one process is a FileLoadException waiting to happen.
    /// </summary>
    internal const string OfficialMod064Variable = "STS2_LAN_CONNECT_064_DLL";

    /// <summary>SHA-256 of the shipped 0.6.4 build (equals the hash its own release notes publish).</summary>
    internal const string PinnedOfficial064Sha256 =
        "E9644876FF9F563E7784AE21C10F2B67C6EEBE2FAB53C8106D0D6AE1D3814AE9";

    internal const long PinnedOfficial064Size = 2_506_752L;

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

    /// <summary>The official 0.6.4 DLL, or <c>null</c> when it is not on this machine.</summary>
    internal static string? OfficialMod064Dll { get; } = ResolveOfficial064Dll();

    /// <summary>Path of sts2.dll when the game directory is known.</summary>
    internal static string? GameAssembly =>
        GameDirectory is null ? null : Path.Combine(GameDirectory, "sts2.dll");

    /// <summary>The patch DLL produced by the src project, preferring Release. Null when not built.</summary>
    internal static string? BuiltPatchAssembly { get; } = ResolveBuiltPatchAssembly();

    /// <summary>Human-readable hint appended to skip reasons so a reader knows how to enable the test.</summary>
    internal static string GameDirectoryHint =>
        $"Install Slay the Spire 2, or set {GameDirectoryVariable} to the folder that contains sts2.dll.";

    internal static string OfficialModHint =>
        $"Set {OfficialModVariable} to sts2_lan_connect.dll (0.6.4, sha256 {PinnedOfficialModSha256}).";

    internal static string OfficialMod064Hint =>
        $"Set {OfficialMod064Variable} to sts2_lan_connect.dll (0.6.4, sha256 {PinnedOfficial064Sha256}).";

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

        // 2.0.0 requires 0.6.4, so there is exactly one reference binary. Both this slot and the 064 slot
        // resolve to the same file on purpose: two builds with the same assembly name cannot be loaded into
        // one process, and the tests that used 0.6.3 now assert the shipped 0.6.4 instead.
        return ResolveOfficial064Dll();
    }

    /// <summary>
    /// The 0.6.4 release bundle: work/lobby/upstream-064/src/&lt;checkout&gt;/releases/sts2_lan_connect/...
    /// The checkout directory name is discovered rather than hard-coded, so a different export of the same
    /// tag keeps working; the pinned hash is what actually decides whether the file is the right build.
    /// </summary>
    private static string? ResolveOfficial064Dll()
    {
        string? overridden = Environment.GetEnvironmentVariable(OfficialMod064Variable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return File.Exists(overridden) ? Path.GetFullPath(overridden) : null;
        }

        string? workDirectory = Path.GetDirectoryName(CompanionRoot);
        if (workDirectory is null)
        {
            return null;
        }

        string upstreamRoot = Path.Combine(workDirectory, "lobby", "upstream-064", "src");
        if (!Directory.Exists(upstreamRoot))
        {
            return null;
        }

        string[] candidates =
        [
            .. Directory.GetDirectories(upstreamRoot)
                .Select(directory => Path.Combine(
                    directory, "releases", "sts2_lan_connect", "sts2_lan_connect.dll"))
        ];

        return candidates.FirstOrDefault(File.Exists) is { } candidate
            ? Path.GetFullPath(candidate)
            : null;
    }

    private static string? ResolveBuiltPatchAssembly()
    {
        string projectDirectory = Path.Combine(CompanionRoot, "src", "Sts2LobbyEnhance");

        string[] candidates =
        [
            Path.Combine(projectDirectory, "bin", "Release", "net9.0", "sts2_lobby_enhance.dll"),
            Path.Combine(projectDirectory, "bin", "Debug", "net9.0", "sts2_lobby_enhance.dll")
        ];

        // Newest, not "Release if it happens to exist": a stale Release next to a current Debug build would make every
        // assertion below describe a binary nobody built.
        return candidates
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() is { } newest
            ? Path.GetFullPath(newest)
            : null;
    }

    /// <summary>
    /// Newest source file of the patch project, build output excluded. The suite does not reference that project, so
    /// this is the only way for a test to notice that the DLL it scans is older than the code in this tree.
    /// </summary>
    internal static string? NewestPatchSource { get; } = ResolveNewestPatchSource();

    private static string? ResolveNewestPatchSource()
    {
        string projectDirectory = Path.Combine(CompanionRoot, "src", "Sts2LobbyEnhance");
        if (!Directory.Exists(projectDirectory))
        {
            return null;
        }

        return Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string relative = Path.GetRelativePath(projectDirectory, path);
                return !relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                       && !relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
