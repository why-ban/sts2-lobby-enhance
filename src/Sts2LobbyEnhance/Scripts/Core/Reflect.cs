using System.Globalization;
using System.Reflection;

namespace Sts2LobbyEnhance;

/// <summary>
/// Reflection helpers for reaching into the LAN Connect assembly, which is deliberately not referenced at
/// compile time: a renamed member must cost one anchor, not module load.
///
/// Lookups accept instance and static members alike because upstream mixes the two, and a lookup that
/// silently missed a static member would degrade behaviour instead of failing. <c>Require*</c> throws,
/// <c>Try*</c> returns null, and ambiguity counts as "not found" so a probe never binds to an arbitrary
/// overload.
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

    /// <summary>Upstream parameter types are internal here, so only the name is usable</summary>
    internal static MethodInfo RequireMethodByName(Type type, string name) =>
        type.GetMethods(AnyMember)
            .FirstOrDefault(method => string.Equals(method.Name, name, StringComparison.Ordinal))
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

    internal static FieldInfo? TryField(Type type, string name)
    {
        ArgumentNullException.ThrowIfNull(type);

        try
        {
            return type.GetField(name, AnyMember);
        }
        catch (AmbiguousMatchException)
        {
            return null;
        }
    }

    internal static PropertyInfo? TryProperty(Type type, string name)
    {
        ArgumentNullException.ThrowIfNull(type);

        try
        {
            return type.GetProperty(name, AnyMember);
        }
        catch (AmbiguousMatchException)
        {
            return null;
        }
    }

    internal static MethodInfo? TryMethodByName(Type type, string name)
    {
        ArgumentNullException.ThrowIfNull(type);

        MethodInfo[] matches = type.GetMethods(AnyMember)
            .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal))
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    internal static MethodInfo? TryMethodByName(Type type, string name, int parameterCount)
    {
        ArgumentNullException.ThrowIfNull(type);

        MethodInfo[] matches = type.GetMethods(AnyMember)
            .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal))
            .Where(method => method.GetParameters().Length == parameterCount)
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>Null means the field holds null, not that the field is missing</summary>
    internal static object? GetField(object instance, string name)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return RequireField(instance.GetType(), name).GetValue(instance);
    }

    internal static void SetField(object instance, string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(instance);
        RequireField(instance.GetType(), name).SetValue(instance, value);
    }

    internal static object? GetPropertyValue(object instance, string name)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return RequireProperty(instance.GetType(), name).GetValue(instance);
    }

    internal static object RequireStaticPropertyValue(Type type, string name) =>
        RequireProperty(type, name).GetValue(null)
        ?? throw new InvalidOperationException($"{type.FullName}.{name} returned null.");

    internal static object? InvokeStatic(Type type, string name, params object?[] arguments)
    {
        MethodInfo method = RequireMethodByName(type, name, arguments.Length);
        if (!method.IsStatic)
        {
            throw new MissingMethodException(
                type.FullName, $"{name} is an instance method, not static.");
        }

        return method.Invoke(null, arguments);
    }

    internal static bool HasField(Type type, string name) => type.GetField(name, AnyMember) != null;

    internal static string Describe(params object?[] values) =>
        string.Join(", ", values.Select(value => value?.ToString() ?? "<null>"));
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
