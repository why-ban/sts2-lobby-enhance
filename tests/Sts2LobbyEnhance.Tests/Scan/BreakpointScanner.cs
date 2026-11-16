using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// One metadata token from a shipped assembly that the CLR cannot resolve against the installed game.
/// </summary>
/// <param name="Token">Metadata token, e.g. <c>0x0A000B3C</c> for a MemberReference row.</param>
/// <param name="Table">Metadata table the token belongs to.</param>
/// <param name="DeclaringTypeSimpleName">Simple name of the declaring type, as used in the project docs.</param>
/// <param name="DeclaringTypeDisplay">Fully qualified declaring type, or a best-effort type description.</param>
/// <param name="MemberName">Member name exactly as written in the reference.</param>
/// <param name="FailureKind">Runtime exception type name reported by <c>Module.ResolveMember</c>.</param>
/// <param name="FailureMessage">Runtime exception message reported by <c>Module.ResolveMember</c>.</param>
internal sealed record UnresolvedReference(
    int Token,
    string Table,
    string DeclaringTypeSimpleName,
    string DeclaringTypeDisplay,
    string MemberName,
    string FailureKind,
    string FailureMessage)
{
    /// <summary>Form used by the project documentation, e.g. <c>SaveManager::UpdateProgressWithRunData</c>.</summary>
    internal string ShortName => $"{DeclaringTypeSimpleName}::{MemberName}";

    internal string TokenText => $"0x{Token:X8}";

    internal string Describe() => $"{ShortName} [{TokenText}] {FailureKind}: {FailureMessage}";
}

/// <summary>
/// Result of scanning every member reference and every definition signature in a shipped assembly.
///
/// The three lists are disjoint and together cover every token that failed to resolve:
/// <list type="bullet">
/// <item><see cref="GenuineBreaks"/> — the CLR cannot resolve the token, and the declaring type is a
/// plain type. These are the real incompatibilities.</item>
/// <item><see cref="GenericContextOnly"/> — the token's declaring type is a generic instantiation such
/// as <c>List&lt;Player&gt;</c>. <c>Module.ResolveMember</c> cannot resolve those without the generic
/// arguments, so the failure says nothing about the game; each entry here was independently confirmed
/// to name a member that the declaring type really declares.</item>
/// <item><see cref="Unexplained"/> — failed to resolve, but neither of the two categories above
/// applies. Any entry here is a defect in the probe, not a fact about the game.</item>
/// </list>
/// </summary>
internal sealed record ScanReport(
    string AssemblyPath,
    long AssemblySize,
    string AssemblySha256,
    string GameAssemblyDirectory,
    string GameAssemblyVersion,
    long GameAssemblySize,
    string GameAssemblySha256,
    int MemberReferenceCount,
    int FieldDefinitionCount,
    int MethodDefinitionCount,
    IReadOnlyList<UnresolvedReference> GenuineBreaks,
    IReadOnlyList<UnresolvedReference> GenericContextOnly,
    IReadOnlyList<UnresolvedReference> Unexplained)
{
    internal int CandidateCount => MemberReferenceCount + FieldDefinitionCount + MethodDefinitionCount;

    internal int UnresolvedCount => GenuineBreaks.Count + GenericContextOnly.Count + Unexplained.Count;
}

/// <summary>Raised when an input file the scan needs is not on this machine.</summary>
internal sealed class ScanInputMissingException(string message) : Exception(message);

/// <summary>
/// Finds the metadata tokens in a shipped assembly that the installed game can no longer satisfy.
///
/// This is the same question the CLR asks when it first JITs a method that uses the token: if the
/// answer is "no", the method can never run and Harmony cannot even read its IL. Scanning the metadata
/// up front turns "the mod crashes at a random moment" into "these N call sites are dead".
///
/// Three tables are scanned:
/// <list type="bullet">
/// <item><c>MemberReferences</c> — calls and field accesses into other assemblies. This is where every
/// real 0.113 break shows up.</item>
/// <item><c>FieldDefinitions</c> — a field signature that names a removed type would make the whole
/// assembly unloadable, so it is worth proving they all resolve.</item>
/// <item><c>MethodDefinitions</c> — same reasoning for method signatures.</item>
/// </list>
/// Method *bodies* need no separate pass: every external call or field access inside them is already a
/// MemberReference row.
/// </summary>
internal static class BreakpointScanner
{
    private const BindingFlags AnyMember =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>Scans <paramref name="assemblyPath"/> against the assemblies in <paramref name="gameDirectory"/>.</summary>
    /// <exception cref="ScanInputMissingException">A required input file is missing.</exception>
    internal static ScanReport Scan(string assemblyPath, string gameDirectory)
    {
        if (!File.Exists(assemblyPath))
        {
            throw new ScanInputMissingException($"Assembly to scan not found: {assemblyPath}");
        }

        string gameAssemblyPath = Path.Combine(gameDirectory, "sts2.dll");
        if (!File.Exists(gameAssemblyPath))
        {
            throw new ScanInputMissingException($"Game assembly not found: {gameAssemblyPath}");
        }

        GameAssemblyLoader.InstallResolver(gameDirectory);

        string fullPath = Path.GetFullPath(assemblyPath);
        Assembly assembly = GameAssemblyLoader.Load(fullPath);
        Module module = assembly.ManifestModule;

        var fileInfo = new FileInfo(fullPath);
        string sha256 = ComputeSha256(fullPath);
        var gameInfo = new FileInfo(gameAssemblyPath);
        // The scan's answer is only meaningful for one game build: recording which one turns "this used to pass" into
        // something the next reader can check without a debugger.
        string gameVersion = FileVersionInfo.GetVersionInfo(gameAssemblyPath).FileVersion ?? string.Empty;
        string gameSha256 = ComputeSha256(gameAssemblyPath);

        using FileStream stream = File.OpenRead(fullPath);
        using var peReader = new PEReader(stream);
        MetadataReader reader = peReader.GetMetadataReader();
        var typeProvider = new TypeResolverProvider(module);

        List<Candidate> candidates = ReadCandidates(reader);

        var genuineBreaks = new List<UnresolvedReference>();
        var genericContextOnly = new List<UnresolvedReference>();
        var unexplained = new List<UnresolvedReference>();

        foreach (Candidate candidate in candidates)
        {
            if (TryResolve(module, candidate.Token, out string failureKind, out string failureMessage))
            {
                continue;
            }

            UnresolvedReference reference = new(
                candidate.Token,
                candidate.Table,
                candidate.DeclaringTypeSimpleName,
                candidate.DeclaringTypeDisplay,
                candidate.MemberName,
                failureKind,
                failureMessage);

            if (candidate.GenericInstantiation is not { } specification)
            {
                genuineBreaks.Add(reference);
                continue;
            }

            Type? declaringType = TryDecodeDeclaringType(reader, typeProvider, specification);
            if (declaringType is not null && DeclaresMember(declaringType, candidate.MemberName))
            {
                genericContextOnly.Add(reference with { DeclaringTypeDisplay = declaringType.ToString() });
            }
            else
            {
                unexplained.Add(reference with
                {
                    DeclaringTypeDisplay = declaringType?.ToString() ?? "<unresolved generic instantiation>"
                });
            }
        }

        return new ScanReport(
            fullPath,
            fileInfo.Length,
            sha256,
            gameDirectory,
            gameVersion,
            gameInfo.Length,
            gameSha256,
            reader.MemberReferences.Count,
            reader.FieldDefinitions.Count,
            reader.MethodDefinitions.Count,
            genuineBreaks,
            genericContextOnly,
            unexplained);
    }

    internal static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool TryResolve(Module module, int token, out string failureKind, out string failureMessage)
    {
        try
        {
            _ = module.ResolveMember(token, null, null);
            failureKind = string.Empty;
            failureMessage = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            Exception root = exception;
            while (root.InnerException is not null)
            {
                root = root.InnerException;
            }

            failureKind = root.GetType().Name;
            failureMessage = root.Message;
            return false;
        }
    }

    private static Type? TryDecodeDeclaringType(
        MetadataReader reader,
        TypeResolverProvider provider,
        TypeSpecificationHandle specification)
    {
        try
        {
            return reader.GetTypeSpecification(specification).DecodeSignature(provider, genericContext: null);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool DeclaresMember(Type type, string memberName)
    {
        try
        {
            return type.GetMember(memberName, AnyMember).Length > 0;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static List<Candidate> ReadCandidates(MetadataReader reader)
    {
        List<Candidate> candidates = new(
            reader.MemberReferences.Count + reader.FieldDefinitions.Count + reader.MethodDefinitions.Count);

        foreach (MemberReferenceHandle handle in reader.MemberReferences)
        {
            MemberReference reference = reader.GetMemberReference(handle);
            (string simpleName, string display) = DescribeParent(reader, reference.Parent);
            TypeSpecificationHandle? specification = reference.Parent.Kind == HandleKind.TypeSpecification
                ? (TypeSpecificationHandle)reference.Parent
                : null;

            candidates.Add(new Candidate(
                0x0A000000 | MetadataTokens.GetRowNumber(handle),
                "MemberReference",
                simpleName,
                display,
                reader.GetString(reference.Name),
                specification));
        }

        foreach (FieldDefinitionHandle handle in reader.FieldDefinitions)
        {
            FieldDefinition field = reader.GetFieldDefinition(handle);
            (string simpleName, string display) = DescribeTypeDefinition(reader, field.GetDeclaringType());

            candidates.Add(new Candidate(
                0x04000000 | MetadataTokens.GetRowNumber(handle),
                "FieldDefinition",
                simpleName,
                display,
                reader.GetString(field.Name),
                null));
        }

        foreach (MethodDefinitionHandle handle in reader.MethodDefinitions)
        {
            MethodDefinition method = reader.GetMethodDefinition(handle);
            (string simpleName, string display) = DescribeTypeDefinition(reader, method.GetDeclaringType());

            candidates.Add(new Candidate(
                0x06000000 | MetadataTokens.GetRowNumber(handle),
                "MethodDefinition",
                simpleName,
                display,
                reader.GetString(method.Name),
                null));
        }

        return candidates;
    }

    private static (string SimpleName, string Display) DescribeParent(MetadataReader reader, EntityHandle parent) =>
        parent.Kind switch
        {
            HandleKind.TypeReference => DescribeTypeReference(reader, (TypeReferenceHandle)parent),
            HandleKind.TypeDefinition => DescribeTypeDefinition(reader, (TypeDefinitionHandle)parent),
            HandleKind.TypeSpecification => ("<generic instantiation>", "<generic instantiation>"),
            _ => ($"<{parent.Kind}>", $"<{parent.Kind}>")
        };

    private static (string SimpleName, string Display) DescribeTypeReference(
        MetadataReader reader,
        TypeReferenceHandle handle)
    {
        TypeReference reference = reader.GetTypeReference(handle);
        string name = reader.GetString(reference.Name);
        string @namespace = reader.GetString(reference.Namespace);

        return (name, string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name);
    }

    private static (string SimpleName, string Display) DescribeTypeDefinition(
        MetadataReader reader,
        TypeDefinitionHandle handle)
    {
        TypeDefinition definition = reader.GetTypeDefinition(handle);
        string name = reader.GetString(definition.Name);
        string @namespace = reader.GetString(definition.Namespace);

        return (name, string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name);
    }

    private readonly record struct Candidate(
        int Token,
        string Table,
        string DeclaringTypeSimpleName,
        string DeclaringTypeDisplay,
        string MemberName,
        TypeSpecificationHandle? GenericInstantiation);
}
