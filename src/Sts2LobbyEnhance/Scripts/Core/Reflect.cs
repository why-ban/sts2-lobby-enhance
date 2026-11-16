using System.Reflection;

namespace Sts2LobbyEnhance;

/// <summary>
/// Reflection helpers for reaching into the LAN Connect assembly, which is deliberately not referenced at
/// compile time: a renamed member must cost one anchor, not module load.
///
/// Lookups accept instance and static members alike because upstream mixes the two, and a lookup that
/// silently missed a static member would degrade behaviour instead of failing. <c>Require*</c> throws,
/// <c>Try*</c> returns null.
/// </summary>
internal static class Reflect
{
    private const BindingFlags AnyMember =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    internal static Assembly? FindAssembly(string simpleName) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
            string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase));

    internal static Type RequireType(Assembly assembly, string fullName) =>
        assembly.GetType(fullName, throwOnError: false, ignoreCase: false)
        ?? throw new MissingTypeException(assembly.GetName().Name ?? "<unnamed>", fullName);

    internal static FieldInfo RequireField(Type type, string name) =>
        type.GetField(name, AnyMember)
        ?? throw new MissingMemberException(type.FullName, name);

    internal static PropertyInfo RequireProperty(Type type, string name) =>
        type.GetProperty(name, AnyMember)
        ?? throw new MissingMemberException(type.FullName, name);

    internal static MethodInfo RequireInstanceMethod(Type type, string name, params Type[] parameters) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, types: parameters, modifiers: null)
        ?? throw new MissingMemberException(type.FullName, name);

    internal static MethodInfo RequireMethodByName(Type type, string name, int parameterCount)
    {
        MethodInfo[] matches = type.GetMethods(AnyMember)
            .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal))
            .Where(method => method.GetParameters().Length == parameterCount)
            .ToArray();

        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new MissingMemberException(
                type.FullName, $"{name} with {parameterCount} parameter(s)"),
            _ => throw new AmbiguousMatchException(
                $"{type.FullName}.{name}: {matches.Length} overloads take {parameterCount} parameter(s); " +
                "the contract is no longer specific enough to bind safely.")
        };
    }

    /// <summary>A <c>typeof</c> on a renamed type throws inside the probe itself, which is what this avoids</summary>
    internal static Type? TryType(Assembly assembly, string fullName)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        try
        {
            return assembly.GetType(fullName, throwOnError: false, ignoreCase: false);
        }
        catch (Exception)
        {
            // A dynamic or half-loaded assembly must not take the caller down; "cannot tell" is null.
            return null;
        }
    }
}

internal sealed class MissingTypeException : Exception
{
    internal MissingTypeException(string assemblyName, string typeName)
        : base($"Type '{typeName}' was not found in assembly '{assemblyName}'.")
    {
        AssemblyName = assemblyName;
        TypeName = typeName;
    }

    internal string AssemblyName { get; }

    internal string TypeName { get; }
}
