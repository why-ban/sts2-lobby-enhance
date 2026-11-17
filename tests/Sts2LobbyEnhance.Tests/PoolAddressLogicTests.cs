using System.Reflection;
using Xunit;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Two pure helpers decide whether two servers are the same one and whether two game versions match. Both feed the
/// pool's room key and its "same version" filter, so a regression here would be silent and would show up as duplicate
/// rooms or a filter that hides the wrong ones.
/// </summary>
public sealed class PoolAddressLogicTests
{
    [Fact]
    public void AddressEqualityIgnoresCaseAndTrailingSlashOnly()
    {
        MethodInfo same = RequireMethod(RequireType("Sts2LobbyEnhance.PoolAddress"), "Same", 2);

        Assert.True(Same(same, "http://A.example:8788/", "http://a.example:8788"));
        Assert.True(Same(same, "http://a.example:8788", "http://a.example:8788/"));
        Assert.False(Same(same, "http://a.example:8788", "http://b.example:8788"));
        Assert.False(Same(same, "http://a.example:8788", "https://a.example:8788"));
        Assert.False(Same(same, "http://a.example:8788", "http://a.example:8789"));
    }

    [Fact]
    public void VersionKeysIgnoreALeadingVAndSurroundingSpaceOnly()
    {
        Type version = RequireType("Sts2LobbyEnhance.PoolVersion");
        MethodInfo normalize = RequireMethod(version, "Normalize", 1);
        MethodInfo same = RequireMethod(version, "Same", 2);

        Assert.Equal("0.113.1", normalize.Invoke(null, [" v0.113.1 "]) as string);
        Assert.Equal("", normalize.Invoke(null, [null]) as string);

        Assert.True(Same(same, "v0.113.1", "0.113.1"));
        Assert.False(Same(same, "0.113.0", "0.113.1"));
        Assert.False(Same(same, "", "0.113.1"));
        Assert.False(Same(same, null, null));
    }

    private static bool Same(MethodInfo same, string? left, string? right) =>
        (bool)same.Invoke(null, [left, right])!;

    private static Type RequireType(string name) => PatchAssembly.GetType(name, throwOnError: true)!;

    private static MethodInfo RequireMethod(Type owner, string name, int arity) =>
        owner.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method => method.Name == name && method.GetParameters().Length == arity)
        ?? throw new InvalidOperationException(owner.Name + "." + name + "/" + arity + " not found");

    private static Assembly PatchAssembly
    {
        get
        {
            string? gameDirectory = TestPaths.GameDirectory;
            if (gameDirectory is null)
            {
                Assert.Skip("game directory not found, so the shipped patch cannot be loaded. " + TestPaths.GameDirectoryHint);
            }

            GameAssemblyLoader.InstallResolver(gameDirectory!);
            string? patch = TestPaths.BuiltPatchAssembly;
            if (patch is null)
            {
                Assert.Skip("the patch assembly has not been built. " + TestPaths.BuildHint);
            }

            TestRunLedger.Record();
            return GameAssemblyLoader.Load(patch!);
        }
    }
}
