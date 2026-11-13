using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Checks the artifact this project actually ships: the patch DLL built from <c>src/</c>.
///
/// The official DLL is scanned because it defines the problem. This one is scanned because a patch that
/// itself references a member 0.113 removed would fail to load at all — and the failure would look like
/// "the mod does nothing", which is the hardest kind of bug to report.
/// </summary>
public sealed class BuiltPatchTests
{
    private const string PatchNamespace = "Sts2LobbyEnhance";
    private const string EntryTypeName = "Entry";

    [Fact]
    public void PatchAssemblyResolvesCompletelyAgainstTheInstalledGame()
    {
        string assemblyPath = RequireBuiltAssembly();
        string gameDirectory = RequireGameDirectory();

        ScanReport report = BreakpointScanner.Scan(assemblyPath, gameDirectory);

        Assert.True(
            report.GenuineBreaks.Count == 0,
            "The patch itself references members the installed game does not provide: " +
            string.Join("; ", report.GenuineBreaks.Select(reference => reference.Describe())));

        Assert.True(
            report.Unexplained.Count == 0,
            "The scan could not classify " + report.Unexplained.Count + " reference(s): " +
            string.Join("; ", report.Unexplained.Select(reference => reference.Describe())));

        Assert.True(report.CandidateCount > 0, "Scanned an assembly with no metadata candidates; wrong file?");
    }

    [Fact]
    public void PatchAssemblyDeclaresTheModInitializer()
    {
        // Metadata only: the check has to work on a machine without the game installed, and it must not
        // load the patch into the test process just to read one attribute.
        string assemblyPath = RequireBuiltAssembly();

        using FileStream stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        MetadataReader reader = peReader.GetMetadataReader();
        var typeNames = new TypeNameProvider();

        TypeDefinitionHandle entry = FindType(reader, PatchNamespace, EntryTypeName)
            ?? throw new InvalidOperationException(
                $"{assemblyPath} does not define {PatchNamespace}.{EntryTypeName}; the game would never call into the patch.");

        List<string> initializerArguments = [];
        foreach (CustomAttributeHandle handle in reader.GetTypeDefinition(entry).GetCustomAttributes())
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);
            string attributeType = DescribeAttributeType(reader, attribute);
            if (attributeType != "ModInitializerAttribute")
            {
                continue;
            }

            CustomAttributeValue<string> value = attribute.DecodeValue(typeNames);
            initializerArguments.AddRange(
                value.FixedArguments.Select(argument => argument.Value as string ?? "<non-string>"));
        }

        string initializerMethod = Assert.Single(initializerArguments);
        Assert.Equal("Init", initializerMethod);

        // The attribute only names the method; it has to exist, be static, and take no arguments.
        bool found = reader.GetTypeDefinition(entry).GetMethods()
            .Select(handle => reader.GetMethodDefinition(handle))
            .Where(method => reader.GetString(method.Name) == initializerMethod)
            .Any(method =>
                method.Attributes.HasFlag(MethodAttributes.Static)
                && method.DecodeSignature(typeNames, genericContext: null).ParameterTypes.Length == 0);

        Assert.True(found, $"Entry declares [ModInitializer(\"{initializerMethod}\")] but no matching static method.");
    }

    private static TypeDefinitionHandle? FindType(MetadataReader reader, string @namespace, string name)
    {
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition definition = reader.GetTypeDefinition(handle);
            if (reader.GetString(definition.Name) == name && reader.GetString(definition.Namespace) == @namespace)
            {
                return handle;
            }
        }

        return null;
    }

    private static string DescribeAttributeType(MetadataReader reader, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
            {
                MemberReference reference = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                return reference.Parent.Kind switch
                {
                    HandleKind.TypeReference => reader.GetString(
                        reader.GetTypeReference((TypeReferenceHandle)reference.Parent).Name),
                    HandleKind.TypeDefinition => reader.GetString(
                        reader.GetTypeDefinition((TypeDefinitionHandle)reference.Parent).Name),
                    _ => string.Empty
                };
            }

            case HandleKind.MethodDefinition:
            {
                MethodDefinition method = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                return reader.GetString(reader.GetTypeDefinition(method.GetDeclaringType()).Name);
            }

            default:
                return string.Empty;
        }
    }

    private static string RequireBuiltAssembly()
    {
        if (TestPaths.BuiltPatchAssembly is null)
        {
            Assert.Skip(
                "The patch assembly has not been built yet. " + TestPaths.BuildHint);
        }

        return TestPaths.BuiltPatchAssembly!;
    }

    private static string RequireGameDirectory()
    {
        if (TestPaths.GameDirectory is null)
        {
            Assert.Skip($"Game data directory not found. {TestPaths.GameDirectoryHint}");
        }

        // Reached only when the game is installed: TestRunLedger counts this case as game-dependent.
        TestRunLedger.Record();
        return TestPaths.GameDirectory!;
    }
}
