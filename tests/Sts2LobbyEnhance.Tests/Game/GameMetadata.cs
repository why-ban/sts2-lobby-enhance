using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Read-only view of the game's own assembly, answered purely from metadata.
///
/// Deliberately does not load sts2.dll into the test process: the shape assertions then cannot be
/// influenced by whatever the runtime decides to do with a type, and they keep working on a machine
/// where GodotSharp is not loadable. (The breakpoint scan does load the assembly — that is inherent to
/// asking the CLR whether a token resolves — but these checks stay independent of it.)
/// </summary>
internal sealed class GameMetadata : IDisposable
{
    private readonly FileStream _stream;
    private readonly PEReader _peReader;
    private readonly TypeNameProvider _typeNames = new();
    private readonly string _assemblyPath;

    private GameMetadata(string assemblyPath, FileStream stream, PEReader peReader)
    {
        _assemblyPath = assemblyPath;
        _stream = stream;
        _peReader = peReader;
        Reader = peReader.GetMetadataReader();
    }

    internal MetadataReader Reader { get; }

    internal static GameMetadata Open(string gameAssemblyPath)
    {
        FileStream stream = File.OpenRead(gameAssemblyPath);
        try
        {
            return new GameMetadata(Path.GetFullPath(gameAssemblyPath), stream, new PEReader(stream));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Full name of the type, or <c>null</c> when the assembly does not define it.</summary>
    internal string? FindType(string @namespace, string name) =>
        FindTypeDefinition(@namespace, name) is { } handle
            ? @namespace + "." + Reader.GetString(Reader.GetTypeDefinition(handle).Name)
            : null;

    /// <summary>Every type whose simple name equals <paramref name="simpleName"/>, fully qualified.</summary>
    internal IReadOnlyList<string> FindTypesBySimpleName(string simpleName) =>
        Reader.TypeDefinitions
            .Select(handle => Reader.GetTypeDefinition(handle))
            .Where(definition => Reader.GetString(definition.Name) == simpleName)
            .Select(definition =>
            {
                string @namespace = Reader.GetString(definition.Namespace);
                string name = Reader.GetString(definition.Name);
                return string.IsNullOrEmpty(@namespace) ? name : @namespace + "." + name;
            })
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    /// <summary>Names of all fields the type declares, in metadata order.</summary>
    internal IReadOnlyList<string> GetFieldNames(string @namespace, string typeName)
    {
        TypeDefinitionHandle handle = RequireType(@namespace, typeName);
        TypeDefinition definition = Reader.GetTypeDefinition(handle);

        return definition.GetFields()
            .Select(fieldHandle => Reader.GetString(Reader.GetFieldDefinition(fieldHandle).Name))
            .ToList();
    }

    /// <summary>Parameter counts of every method with the given name. Empty when no such method exists.</summary>
    internal IReadOnlyList<int> GetMethodParameterCounts(string @namespace, string typeName, string methodName)
    {
        TypeDefinitionHandle handle = RequireType(@namespace, typeName);
        TypeDefinition definition = Reader.GetTypeDefinition(handle);

        return definition.GetMethods()
            .Select(methodHandle => Reader.GetMethodDefinition(methodHandle))
            .Where(method => Reader.GetString(method.Name) == methodName)
            .Select(method => method.DecodeSignature(_typeNames, genericContext: null).ParameterTypes.Length)
            .ToList();
    }

    /// <summary>Declared return type of every method with the given name, rendered by <see cref="TypeNameProvider"/>.</summary>
    internal IReadOnlyList<string> GetMethodReturnTypes(string @namespace, string typeName, string methodName)
    {
        TypeDefinitionHandle handle = RequireType(@namespace, typeName);
        TypeDefinition definition = Reader.GetTypeDefinition(handle);

        return definition.GetMethods()
            .Select(methodHandle => Reader.GetMethodDefinition(methodHandle))
            .Where(method => Reader.GetString(method.Name) == methodName)
            .Select(method => method.DecodeSignature(_typeNames, genericContext: null).ReturnType)
            .ToList();
    }

    /// <summary>Rendered type of the property, or <c>null</c> when the type does not declare it.</summary>
    internal string? GetPropertyType(string @namespace, string typeName, string propertyName)
    {
        TypeDefinitionHandle handle = RequireType(@namespace, typeName);
        TypeDefinition definition = Reader.GetTypeDefinition(handle);

        foreach (PropertyDefinitionHandle propertyHandle in definition.GetProperties())
        {
            PropertyDefinition property = Reader.GetPropertyDefinition(propertyHandle);
            if (Reader.GetString(property.Name) == propertyName)
            {
                return property.DecodeSignature(_typeNames, genericContext: null).ReturnType;
            }
        }

        return null;
    }

    /// <summary>True when the type exists and declares a field with exactly this name.</summary>
    internal bool HasField(string @namespace, string typeName, string fieldName) =>
        GetFieldNames(@namespace, typeName).Contains(fieldName, StringComparer.Ordinal);

    /// <summary>Simple names of every type the assembly defines. Useful in failure messages.</summary>
    internal int TypeDefinitionCount => Reader.TypeDefinitions.Count;

    public void Dispose()
    {
        _peReader.Dispose();
        _stream.Dispose();
    }

    private TypeDefinitionHandle? FindTypeDefinition(string @namespace, string name)
    {
        foreach (TypeDefinitionHandle handle in Reader.TypeDefinitions)
        {
            TypeDefinition definition = Reader.GetTypeDefinition(handle);
            if (Reader.GetString(definition.Name) == name && Reader.GetString(definition.Namespace) == @namespace)
            {
                return handle;
            }
        }

        return null;
    }

    private TypeDefinitionHandle RequireType(string @namespace, string typeName) =>
        FindTypeDefinition(@namespace, typeName)
        ?? throw new InvalidOperationException(
            $"Type '{@namespace}.{typeName}' is not defined by {_assemblyPath}.");
}
