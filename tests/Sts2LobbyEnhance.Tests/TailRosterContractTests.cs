using System.Reflection;
using Xunit;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Locks the reflection contract of the shared <c>TailRoster</c> component
/// (<c>src/Sts2LobbyEnhance/Scripts/Core/TailRoster.cs</c>) against the shipped sts2_lan_connect
/// 0.6.4 binary.
///
/// Why this needs a test of its own: the roster reset is pure reflection into internal members of the
/// host mod, so a rename or a re-shape cannot fail the patch build — it would only make the reset return
/// <c>false</c> at runtime, and the visible symptom would be the very protocol mismatch this component
/// exists to prevent ("Current-state snapshots must repeat the accepted revision byte-for-byte",
/// LanConnectRosterAuthorityState.cs:86 in the decompiled 0.6.4 binary). Pinning the five anchors here
/// turns that silent degradation into a red test.
///
/// Anchors pinned (decompiled 0.6.4, <c>work/lobby/dec-official/Sts2LanConnect.Scripts/</c>):
/// <list type="bullet">
/// <item><c>LanConnectRosterAuthorityState.Reset()</c> — LanConnectRosterAuthorityState.cs:128</item>
/// <item><c>LanConnectTailMessageRuntime.Shared</c> — LanConnectTailMessageRuntime.cs:459</item>
/// <item><c>LanConnectTailMessageRuntime.GetMessageBus(INetGameService)</c> — :1670</item>
/// <item><c>LanConnectTailMessageRuntime._bindings</c> — :439</item>
/// <item><c>Binding.RequireRoster()</c> (the value type of <c>_bindings</c>) — :116, nested type at :27</item>
/// </list>
///
/// Nothing here runs game or mod code: the assembly is loaded for metadata, and no member is invoked
/// (in particular <c>Shared</c> is only reflected, never read, because reading it runs the runtime's
/// static constructor).
/// </summary>
public sealed class TailRosterContractTests
{
    private const string ScriptsNamespace = "Sts2LanConnect.Scripts";
    private const string TailRuntimeName = "LanConnectTailMessageRuntime";
    private const string RosterStateName = "LanConnectRosterAuthorityState";

    private const string MessageBusType = "MegaCrit.Sts2.Core.Multiplayer.NetMessageBus";
    private const string NetGameServiceType = "MegaCrit.Sts2.Core.Multiplayer.Game.INetGameService";

    private const BindingFlags AnyMember =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public void RosterAuthorityStateExposesAnInstanceReset()
    {
        Type rosterState = RequireHostType(RosterStateName);

        // TailRoster.Reset calls this through reflection on the authority instance that RequireRoster()
        // returns, so it has to be a parameterless instance method returning void.
        MethodInfo? reset = rosterState.GetMethod("Reset", AnyMember);
        Assert.NotNull(reset);
        Assert.False(reset!.IsStatic, $"{RosterStateName}.Reset is static; TailRoster invokes it on the instance.");
        Assert.Equal(typeof(void), reset.ReturnType);
        Assert.Empty(reset.GetParameters());
    }

    [Fact]
    public void TailRuntimeExposesTheSharedSingleton()
    {
        Type tailRuntime = RequireHostType(TailRuntimeName);

        // A get-only static property; TailRoster reads it with PropertyInfo.GetValue(null).
        PropertyInfo? shared = tailRuntime.GetProperty("Shared", AnyMember);
        Assert.NotNull(shared);

        MethodInfo? getter = shared!.GetGetMethod(nonPublic: true);
        Assert.NotNull(getter);
        Assert.True(getter!.IsStatic, $"{TailRuntimeName}.Shared must be static.");
        Assert.Equal(tailRuntime, getter.ReturnType);
    }

    [Fact]
    public void TailRuntimeExposesTheMessageBusResolver()
    {
        Type tailRuntime = RequireHostType(TailRuntimeName);

        MethodInfo[] candidates = tailRuntime.GetMethods(AnyMember)
            .Where(method => string.Equals(method.Name, "GetMessageBus", StringComparison.Ordinal))
            .ToArray();

        // Exactly one overload: TailRoster resolves it by name and arity, so a second one would make the
        // lookup ambiguous (Reflect.RequireMethodByName throws AmbiguousMatchException).
        MethodInfo getMessageBus = Assert.Single(candidates);
        Assert.True(getMessageBus.IsStatic, $"{TailRuntimeName}.GetMessageBus must be static.");
        Assert.Equal(MessageBusType, getMessageBus.ReturnType.FullName);

        ParameterInfo parameter = Assert.Single(getMessageBus.GetParameters());
        Assert.Equal(NetGameServiceType, parameter.ParameterType.FullName);
    }

    [Fact]
    public void BindingDictionaryCarriesTheRosterAuthority()
    {
        Type tailRuntime = RequireHostType(TailRuntimeName);

        FieldInfo? bindings = tailRuntime.GetField("_bindings", AnyMember);
        Assert.NotNull(bindings);

        // _bindings is Dictionary<NetMessageBus, Binding>; Binding is a nested private type, so the value
        // type can only be reached through the field's own generic arguments — exactly what TailRoster does.
        Type[] arguments = bindings!.FieldType.GetGenericArguments();
        Assert.Equal(2, arguments.Length);
        Assert.Equal(MessageBusType, arguments[0].FullName);

        Type bindingType = arguments[1];
        MethodInfo? requireRoster = bindingType.GetMethod("RequireRoster", AnyMember);
        Assert.NotNull(requireRoster);
        Assert.Empty(requireRoster!.GetParameters());

        // The reset target must be the same type TailRoster resolves by name, or the shape check in
        // TailRoster.Anchors.Capture would reject the build.
        Assert.Equal(RequireHostType(RosterStateName), requireRoster.ReturnType);
    }

    /// <summary>
    /// Loads the shipped host mod for metadata, skipping the calling test when the inputs are absent.
    /// </summary>
    private static Type RequireHostType(string simpleName)
    {
        Assembly host = RequireHostAssembly();

        // Reached only when the game and the official DLL were found: TestRunLedger counts these cases.
        TestRunLedger.Record();

        Type? type = host.GetType($"{ScriptsNamespace}.{simpleName}", throwOnError: false, ignoreCase: false);
        Assert.NotNull(type);
        return type!;
    }

    private static Assembly RequireHostAssembly()
    {
        string? gameDirectory = TestPaths.GameDirectory;
        if (gameDirectory is null)
        {
            Assert.Skip($"Game data directory not found. {TestPaths.GameDirectoryHint}");
        }

        string? officialMod = TestPaths.OfficialModDll;
        if (officialMod is null)
        {
            Assert.Skip($"Official sts2_lan_connect DLL not found. {TestPaths.OfficialModHint}");
        }

        // Tie the assertions to the exact binary the patch was written against: a different official build
        // may have a different shape, and silently tolerating that is how a reflection patch rots.
        Assert.Equal(TestPaths.PinnedOfficialModSha256, BreakpointScanner.ComputeSha256(officialMod!));

        // The host mod references sts2/GodotSharp; the resolver is what makes the types loadable here.
        GameAssemblyLoader.InstallResolver(gameDirectory!);
        return GameAssemblyLoader.Load(officialMod!);
    }
}
