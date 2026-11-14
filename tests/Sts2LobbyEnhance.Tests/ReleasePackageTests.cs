using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Release gate for the Steam Workshop upload workspace.
///
/// Everything here mirrors something the official uploader or Steam itself enforces, plus the two rules
/// this project cannot afford to break: the workspace must never contain a file from the original mod,
/// and it must not be uploaded with a stale build of the patch.
/// </summary>
public sealed class ReleasePackageTests
{
    private static readonly string[] AllowedContentFiles =
    [
        "sts2_lobby_enhance.dll",
        "sts2_lobby_enhance.json",
        "LICENSE",
        "THIRD_PARTY_NOTICES"
    ];

    private static string Workspace => TestPaths.PackagingDirectory;

    private static string ContentDirectory => Path.Combine(Workspace, "content");

    [Fact]
    public void WorkspaceHasWhatTheUploaderValidates()
    {
        // UploadCommand.UploadWorkspace refuses to run unless all three exist; failing here is cheaper
        // than failing halfway through an upload with Steam open.
        Assert.True(File.Exists(Path.Combine(Workspace, "workshop.json")), $"Missing {Workspace}/workshop.json");
        Assert.True(File.Exists(Path.Combine(Workspace, "image.png")), $"Missing {Workspace}/image.png");
        Assert.True(Directory.Exists(ContentDirectory), $"Missing {ContentDirectory}");
        Assert.True(File.Exists(Path.Combine(Workspace, "README.md")), $"Missing {Workspace}/README.md");
    }

    [Fact]
    public void ImageIsASquarePngUnderTheSteamLimit()
    {
        string path = Path.Combine(Workspace, "image.png");
        if (!File.Exists(path))
        {
            Assert.Skip("image.png has not been generated yet.");
        }

        var file = new FileInfo(path);
        Assert.True(
            file.Length < 1024 * 1024,
            $"image.png is {file.Length} B; Steam rejects previews of 1 MiB or more.");

        using FileStream stream = File.OpenRead(path);
        byte[] header = new byte[24];
        stream.ReadExactly(header);

        byte[] pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        Assert.Equal(pngSignature, header[..8]);

        int width = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));

        Assert.True(width == height, $"Workshop previews are square; this one is {width}x{height}.");
        Assert.True(width >= 256, $"Preview is only {width}px wide; text would be unreadable in the browser.");
    }

    [Fact]
    public void WorkshopJsonDescribesThisMod()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Workspace, "workshop.json")));
        JsonElement root = document.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        string title = root.GetProperty("title").GetString() ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(title));

        // The item is no longer a "0.113 compatibility patch": the rename to sts2_lobby_enhance happened
        // because the feature set outgrew 0.113 (join/restart/lobby features). The title has to name this
        // mod, and it must not keep advertising itself as a compatibility patch - that wording both
        // mis-sells the item and buries the new features in Workshop search.
        Assert.Contains("游戏大厅增强", title, StringComparison.Ordinal);
        Assert.DoesNotContain("兼容补丁", title, StringComparison.Ordinal);

        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("description").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("changeNote").GetString()));

        // Public. The maintainer's own account cannot upload at all (no app licence), so the item is
        // published through a licensed friend running the one-click upload kit. That flow has no review
        // step by design, therefore the workspace must already say "public" when the kit runs.
        Assert.Equal("public", root.GetProperty("visibility").GetString());

        Assert.Equal(JsonValueKind.Array, root.GetProperty("tags").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("contentDescriptors").ValueKind);

        // minBranch/maxBranch are intentionally absent: the uploader sends "" for a missing value, which
        // means "supports every branch". Setting them wrongly would hide the mod from one of the branches.
        Assert.False(root.TryGetProperty("minBranch", out _));
        Assert.False(root.TryGetProperty("maxBranch", out _));
    }

    [Fact]
    public void WorkshopJsonDependsOnTheOfficialMod()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Workspace, "workshop.json")));
        JsonElement dependencies = document.RootElement.GetProperty("dependencies");

        Assert.Equal(JsonValueKind.Array, dependencies.ValueKind);

        ulong[] ids = [.. dependencies.EnumerateArray().Select(entry => entry.GetUInt64())];

        // Without this dependency Steam would let users install a patch that has nothing to patch.
        Assert.Equal([TestPaths.OfficialWorkshopItemId], ids);
    }

    [Fact]
    public void WorkshopItemIdIsRecordedSoAnUpdateNeverCreatesASecondItem()
    {
        // mod_id.txt is what turns the next upload into an *update*. Losing it is the only
        // irreversible mistake in this pipeline: the uploader creates a brand-new item and the
        // existing subscribers cannot be migrated to it. The value is pinned deliberately - if the
        // item ever has to be recreated, this assertion must be changed by hand, which is exactly
        // the review moment we want.
        string path = Path.Combine(Workspace, "mod_id.txt");

        Assert.True(
            File.Exists(path),
            "packaging/mod_id.txt is missing. Without it the uploader creates a second workshop item " +
            "and the subscribers of 3839101755 can never be moved over.");

        byte[] raw = File.ReadAllBytes(path);
        Assert.False(
            raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF,
            "mod_id.txt must not carry a UTF-8 BOM; the uploader parses the file as a plain number.");

        string text = File.ReadAllText(path).Trim();
        Assert.True(
            ulong.TryParse(text, out ulong itemId),
            $"mod_id.txt must contain nothing but a decimal item id; it contains '{text}'.");

        Assert.Equal(3839101755UL, itemId);
    }

    [Fact]
    public void ContentShipsThePatchManifest()
    {
        string manifestPath = Path.Combine(ContentDirectory, "sts2_lobby_enhance.json");
        Assert.True(File.Exists(manifestPath), $"Missing {manifestPath}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement root = document.RootElement;

        // Field names follow the game's own ModManifest record (MegaCrit.Sts2.Core.Modding.ModManifest),
        // serialised with a snake_case policy.
        Assert.Equal("sts2_lobby_enhance", root.GetProperty("id").GetString());
        // Bump this together with the manifest and the release zip name: the Workshop item shows this string to
        // subscribers, so a frozen value would advertise 1.0.0 for every release.
        Assert.Equal("1.8.0", root.GetProperty("version").GetString());
        Assert.True(root.GetProperty("has_dll").GetBoolean());
        Assert.False(root.GetProperty("has_pck").GetBoolean());
        Assert.Equal("0.113.0", root.GetProperty("min_game_version").GetString());
        // The mod list in-game shows this string. It is pinned so a rename cannot land in the id and the
        // DLL but be forgotten here (the id, the DLL name and the display name are one change).
        Assert.Equal("游戏大厅增强", root.GetProperty("name").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("author").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("description").GetString()));

        JsonElement dependency = Assert.Single(root.GetProperty("dependencies").EnumerateArray().ToList());
        Assert.Equal("sts2_lan_connect", dependency.GetProperty("id").GetString());
        Assert.Equal("0.6.3", dependency.GetProperty("min_version").GetString());
    }

    [Fact]
    public void ContentShipsNothingFromTheOriginalMod()
    {
        Assert.True(Directory.Exists(ContentDirectory), $"Missing {ContentDirectory}");

        Assert.Empty(Directory.GetDirectories(ContentDirectory));

        string[] unexpected = Directory.GetFiles(ContentDirectory)
            .Select(path => Path.GetFileName(path))
            .Where(name => !AllowedContentFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unexpected.Length == 0,
            "content/ may only contain the files this project produces (" + string.Join(", ", AllowedContentFiles) +
            "). Unexpected: " + string.Join(", ", unexpected) +
            ". The original mod is a Workshop dependency and must never be bundled.");
    }

    [Fact]
    public void ContentAssemblyIsTheCurrentBuild()
    {
        string packaged = Path.Combine(ContentDirectory, "sts2_lobby_enhance.dll");

        if (!File.Exists(packaged))
        {
            Assert.Skip($"No DLL in content/. {TestPaths.BuildHint} then run packaging/build-content.ps1.");
        }

        if (TestPaths.BuiltPatchAssembly is null)
        {
            Assert.Skip($"The patch assembly has not been built. {TestPaths.BuildHint}");
        }

        // A stale DLL here is the most likely way to ship the wrong binary: the workspace is uploaded
        // as-is, so the copy has to be byte-identical to the build output.
        string packagedSha = BreakpointScanner.ComputeSha256(packaged);
        string builtSha = BreakpointScanner.ComputeSha256(TestPaths.BuiltPatchAssembly!);

        Assert.True(
            packagedSha == builtSha,
            $"content/{Path.GetFileName(packaged)} is stale: sha256 {packagedSha} but the current build is {builtSha}. " +
            "Re-run packaging/build-content.ps1 before uploading.");
    }

    [Fact]
    public void LatestReleaseZipShipsTheContentAssembly()
    {
        // The 2026-11-13 v1.5 incident: dist/ held a zip whose DLL was from 22:36 while packaging/content
        // (and the manifest *inside that same zip*) were from 23:21 — an old binary next to a new manifest.
        // Nothing in the suite looked at dist/ at all, so the mixed package passed every gate.
        //
        // This test closes that hole from the other side: whatever zip is in dist/ has to contain exactly
        // the bytes that are in packaging/content, which build-content.ps1 has already proved to be the
        // current build. tools/pack-release/pack-release.ps1 does the same check when it writes the zip;
        // this one catches a hand-made or stale zip that was dropped in afterwards.
        string? releaseDirectory = ReleaseDirectory();

        if (releaseDirectory is null)
        {
            Assert.Skip(
                "dist/ does not exist next to this workspace (" + ExpectedReleaseDirectory() + "), so there is " +
                "no release package to compare. Run tools/pack-release/pack-release.ps1 first. " +
                "Skipped, not passed: without a zip this check proves nothing.");
        }

        string[] zips = Directory.GetFiles(releaseDirectory!, "sts2-lobby-enhance-v*.zip");

        Assert.True(
            zips.Length > 0,
            "No sts2-lobby-enhance-v*.zip in " + releaseDirectory +
            ". Run tools/pack-release/pack-release.ps1.");

        Assert.True(
            zips.Length == 1,
            "More than one release package in " + releaseDirectory + ": " +
            string.Join(", ", zips.Select(Path.GetFileName)) +
            ". Two candidate packages in one folder is how the wrong one gets uploaded; delete the stale one.");

        string zipPath = zips[0];

        using ZipArchive archive = ZipFile.OpenRead(zipPath);

        Assert.Equal(
            AllowedContentFiles.OrderBy(name => name, StringComparer.Ordinal),
            archive.Entries.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal));

        foreach (string name in new[] { "sts2_lobby_enhance.dll", "sts2_lobby_enhance.json" })
        {
            string sourcePath = Path.Combine(ContentDirectory, name);
            Assert.True(File.Exists(sourcePath), $"Missing {sourcePath}; run packaging/build-content.ps1.");

            ZipArchiveEntry entry = Assert.Single(archive.Entries, e => e.Name == name);
            string packagedSha = ComputeEntrySha256(entry);
            string contentSha = BreakpointScanner.ComputeSha256(sourcePath);

            Assert.True(
                packagedSha == contentSha,
                $"{Path.GetFileName(zipPath)} ships a stale {name}: sha256 {packagedSha} but packaging/content has " +
                $"{contentSha}. Rebuild with pwsh -NoProfile -File tools/pack-release/pack-release.ps1.");
        }
    }

    [Fact]
    public void LicenseAndNoticesTravelWithTheUpload()
    {
        foreach (string name in new[] { "LICENSE", "THIRD_PARTY_NOTICES" })
        {
            string packaged = Path.Combine(ContentDirectory, name);
            string source = TestPaths.DocumentationSource(name);

            if (!File.Exists(packaged))
            {
                Assert.Skip($"{packaged} is missing; run packaging/build-content.ps1.");
            }

            Assert.Equal(BreakpointScanner.ComputeSha256(source), BreakpointScanner.ComputeSha256(packaged));
        }
    }

    [Fact]
    public void DocumentationSetIsComplete()
    {
        (string File, int MinimumBytes)[] required =
        [
            ("README.md", 4_000),
            ("CHANGELOG.md", 500),
            ("UPLOAD.md", 2_000),
            ("THIRD_PARTY_NOTICES", 500),
            ("LICENSE", 30_000)
        ];

        foreach ((string file, int minimumBytes) in required)
        {
            string path = TestPaths.DocumentationSource(file);
            Assert.True(File.Exists(path), $"Missing documentation file: {path}");
            Assert.True(
                new FileInfo(path).Length >= minimumBytes,
                $"{file} is only {new FileInfo(path).Length} B; expected at least {minimumBytes} B.");
        }
    }

    [Fact]
    public void NoticesNameTheUpstreamProjectAndItsLicence()
    {
        string notices = File.ReadAllText(Path.Combine(TestPaths.DocsDirectory, "THIRD_PARTY_NOTICES"));

        Assert.Contains("sts2_lan_connect", notices, StringComparison.Ordinal);
        Assert.Contains(TestPaths.OfficialWorkshopItemId.ToString(), notices, StringComparison.Ordinal);
        Assert.Contains("GPL", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void UploadGuideExplainsHowUpdatesFindTheExistingItem()
    {
        string guide = File.ReadAllText(Path.Combine(TestPaths.DocsDirectory, "UPLOAD.md"));

        // mod_id.txt is what makes a second upload an *update* instead of a second workshop item; losing
        // it is the one mistake that cannot be undone without deleting the item.
        Assert.Contains("mod_id.txt", guide, StringComparison.Ordinal);
        Assert.Contains("ModUploader", guide, StringComparison.Ordinal);
        Assert.Contains(TestPaths.SlayTheSpire2AppId, guide, StringComparison.Ordinal);
    }

    [Fact]
    public void LicenseIsTheUnmodifiedGnuGplText()
    {
        string license = Path.Combine(TestPaths.CompanionRoot, "LICENSE");
        string text = File.ReadAllText(license);

        Assert.Contains("GNU GENERAL PUBLIC LICENSE", text, StringComparison.Ordinal);
        Assert.Contains("Version 3, 29 June 2007", text, StringComparison.Ordinal);
        Assert.Contains("TERMS AND CONDITIONS", text, StringComparison.Ordinal);

        // The project copies the upstream licence verbatim instead of re-typing it.
        string? upstream = UpstreamLicensePath();
        if (upstream is null)
        {
            Assert.Skip("Upstream repository licence not available on this machine to compare against.");
        }

        Assert.Equal(BreakpointScanner.ComputeSha256(upstream!), BreakpointScanner.ComputeSha256(license));
    }

    /// <summary>
    /// The folder holding the release packages: <c>&lt;repo&gt;/dist</c>, i.e. two levels above the
    /// companion project (work/companion -&gt; work -&gt; repo). <c>null</c> when it does not exist.
    /// </summary>
    private static string? ReleaseDirectory()
    {
        string expected = ExpectedReleaseDirectory();
        return Directory.Exists(expected) ? expected : null;
    }

    private static string ExpectedReleaseDirectory()
    {
        string? workDirectory = Path.GetDirectoryName(TestPaths.CompanionRoot);
        string? repositoryRoot = workDirectory is null ? null : Path.GetDirectoryName(workDirectory);
        return repositoryRoot is null ? "dist" : Path.Combine(repositoryRoot, "dist");
    }

    private static string ComputeEntrySha256(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string? UpstreamLicensePath()
    {
        string? workDirectory = Path.GetDirectoryName(TestPaths.CompanionRoot);
        if (workDirectory is null)
        {
            return null;
        }

        string candidate = Path.Combine(workDirectory, "lobby", "STS2-Game-Lobby", "LICENSE");
        return File.Exists(candidate) ? candidate : null;
    }
}
