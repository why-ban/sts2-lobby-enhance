using System.Globalization;
using System.Reflection;

namespace Sts2LobbyEnhance;

/// <summary>
/// Reflection helpers for reaching into the LAN Connect assembly.
///
/// The companion deliberately does NOT reference sts2_lan_connect.dll at compile time. Everything it
/// touches lives behind an <c>internal</c> type whose shape can change between upstream releases, so
/// the coupling is expressed once, here, and every lookup names the member it wanted instead of
/// silently binding to the wrong thing. This also means the companion keeps loading even if the user
/// temporarily removes LAN Connect — it just refuses to activate.
///
/// Lookups intentionally accept instance *and* static members. Upstream mixes the two freely (for
/// example <c>LanConnectTailMessageRuntime.Shared</c> is a static property while
/// <c>BindClient</c> on the returned object is an instance method), and a lookup that silently
/// misses a static member would degrade behaviour instead of failing — the worst possible outcome
/// for a compatibility patch.
///
/// Two families live here:
///   * <c>Require*</c> throws with the member name. For call sites that genuinely cannot continue
///     without the member, where "found nothing" is a bug worth a stack trace.
///   * <c>Try*</c> returns <c>null</c> and never throws. For probes and repairs that must report
///     *every* missing anchor instead of dying on the first one (see <see cref="ReflectScope"/>).
///     Ambiguity counts as "not found" there: a probe must never bind to an arbitrary overload.
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

    /// <summary>
    /// Finds a method by name alone, without an exact signature. Used where upstream parameter types
    /// are themselves internal (so we cannot name them) but the arity is stable.
    /// </summary>
    internal static MethodInfo RequireMethodByName(Type type, string name) =>
        type.GetMethods(AnyMember)
            .FirstOrDefault(method => string.Equals(method.Name, name, StringComparison.Ordinal))
        ?? throw new MissingMemberException(type.FullName, name);

    /// <summary>Finds a method by name and parameter count; throws if the arity does not match.</summary>
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

    /// <summary>
    /// Non-throwing type lookup. Used by the compatibility probes, which must not throw: a
    /// compile-time reference (<c>typeof</c>) to a renamed type throws inside the probe itself, which
    /// is exactly the failure this family exists to avoid.
    /// </summary>
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

    /// <summary>Non-throwing field lookup. Ambiguity is treated as "not found".</summary>
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

    /// <summary>Non-throwing property lookup. Ambiguity is treated as "not found".</summary>
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

    /// <summary>
    /// Non-throwing method lookup by name alone. Zero matches and more than one match both return
    /// <c>null</c>: an ambiguous name is not a usable anchor, and guessing would bind the repair to a
    /// different overload than the one that was audited.
    /// </summary>
    internal static MethodInfo? TryMethodByName(Type type, string name)
    {
        ArgumentNullException.ThrowIfNull(type);

        MethodInfo[] matches = type.GetMethods(AnyMember)
            .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal))
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>Non-throwing method lookup by name and parameter count; exactly one match or null.</summary>
    internal static MethodInfo? TryMethodByName(Type type, string name, int parameterCount)
    {
        ArgumentNullException.ThrowIfNull(type);

        MethodInfo[] matches = type.GetMethods(AnyMember)
            .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal))
            .Where(method => method.GetParameters().Length == parameterCount)
            .ToArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>Reads an instance field. Returns <c>null</c> only if the field genuinely holds null.</summary>
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

    /// <summary>Reads a static property, e.g. <c>LanConnectTailMessageRuntime.Shared</c>.</summary>
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
