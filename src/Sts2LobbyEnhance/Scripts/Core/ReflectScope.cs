using System.Reflection;

namespace Sts2LobbyEnhance;

/// <summary>
/// Anchor lookups that report every miss instead of throwing on the first one.
///
/// Ambiguity counts as a miss and is never bound to an arbitrary candidate: a repair bound to the wrong
/// overload degrades behaviour instead of failing. The message text is quoted in docs and log lines, so
/// lookups with different messages stay separate rather than sharing one parameterised helper.
/// </summary>
internal static class ReflectScope
{
    private const BindingFlags InstanceMember =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private const BindingFlags StaticMember =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private const BindingFlags AnyMember = InstanceMember | StaticMember;
    internal static Type? HostType(Assembly host, string fullName, List<string> problems)
    {
        Type? type = host.GetType(fullName, throwOnError: false, ignoreCase: false);
        if (type == null)
        {
            problems.Add($"type {fullName} (host mod)");
        }

        return type;
    }

    internal static Type? GameType(string fullName, List<string> problems)
    {
        Type? type = FindType(fullName);
        if (type == null)
        {
            problems.Add($"type {fullName} (game)");
        }

        return type;
    }

    /// <summary>
    /// No <c>typeof</c>, so a renamed type costs this anchor instead of the whole probe
    /// </summary>
    internal static Type? FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                Type? type = assembly.GetType(fullName, throwOnError: false, ignoreCase: false);
                if (type != null)
                {
                    return type;
                }
            }
            catch (Exception)
            {
                // Dynamic and reflection-only assemblies refuse GetType; none of them can own a game type.
            }
        }

        return null;
    }

    /// <summary>
    /// Arity alone cannot bind a patch: overloads can share a parameter count and differ in types, and
    /// hooking the wrong one is silent
    /// </summary>
    internal static MethodInfo? InstanceMethod(
        List<string> problems,
        Type? owner,
        string name,
        params string[] parameterTypeNames) =>
        MatchMethod(problems, owner, name, BindingFlags.Instance, parameterTypeNames);

    internal static MethodInfo? StaticMethod(
        List<string> problems,
        Type? owner,
        string name,
        params string[] parameterTypeNames) =>
        MatchMethod(problems, owner, name, BindingFlags.Static, parameterTypeNames);

    internal static MethodInfo? MatchMethod(
        List<string> problems,
        Type? owner,
        string name,
        BindingFlags scope,
        string[] parameterTypeNames)
    {
        // A missing owner type was already reported by HostType/GameType; repeating it here would bury the reason.
        if (owner == null)
        {
            return null;
        }

        // DeclaredOnly: an inherited member with the same name on a base type is a different member, and binding to it
        // would patch a method the host does not call. Every anchor here names the type that declares it.
        MethodInfo[] byName = owner
            .GetMethods(scope | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal)
                             && method.GetParameters().Length == parameterTypeNames.Length)
            .ToArray();

        MethodInfo[] exact = byName
            .Where(method => ParametersMatch(method.GetParameters(), parameterTypeNames))
            .ToArray();

        if (exact.Length == 1)
        {
            return exact[0];
        }

        string expected = $"{owner.FullName}.{name}({string.Join(", ", parameterTypeNames)})";
        if (exact.Length > 1)
        {
            problems.Add($"{expected} is ambiguous ({exact.Length} overloads)");
        }
        else if (byName.Length > 0)
        {
            problems.Add($"{expected} has {byName.Length} same-arity overload(s) with different parameter types");
        }
        else
        {
            problems.Add($"{expected} (not found)");
        }

        return null;
    }

    internal static bool ParametersMatch(ParameterInfo[] parameters, string[] parameterTypeNames)
    {
        for (int index = 0; index < parameters.Length; index++)
        {
            // By-ref parameters report "T&" as their FullName; the call sites spell that out explicitly so a
            // by-value overload can never be mistaken for the by-ref one.
            if (!string.Equals(parameters[index].ParameterType.FullName, parameterTypeNames[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Harmony binds a prefix/postfix argument to the target parameter by name, so a rename silently
    /// breaks the binding and the guard stops guarding
    /// </summary>
    internal static void RequireParameterNames(List<string> problems, MethodInfo? method, params string[] names)
    {
        if (method == null)
        {
            return;
        }

        ParameterInfo[] parameters = method.GetParameters();
        if (names.Length > parameters.Length)
        {
            // More names than parameters: the signature shrank under the pinned list, and the extra names would be
            // compared against nothing. A shorter list is deliberate - only the parameters this patch binds by name
            // are pinned, and those are always the first ones.
            problems.Add(
                $"{method.DeclaringType?.Name}.{method.Name} takes {parameters.Length} parameter(s), " +
                $"but {names.Length} name(s) were pinned (Harmony binds by name)");
            return;
        }

        for (int index = 0; index < names.Length; index++)
        {
            if (!string.Equals(parameters[index].Name, names[index], StringComparison.Ordinal))
            {
                problems.Add(
                    $"{method.DeclaringType?.Name}.{method.Name} parameter {index} is '{parameters[index].Name}', " +
                    $"expected '{names[index]}' (Harmony binds by name)");
            }
        }
    }

    internal static FieldInfo? Field(List<string> problems, Type? owner, string name)
    {
        if (owner == null)
        {
            return null;
        }

        FieldInfo? field = owner.GetField(name, InstanceMember);
        if (field == null)
        {
            problems.Add($"{owner.FullName}.{name}");
        }

        return field;
    }

    internal static FieldInfo? StaticField(List<string> problems, Type? owner, string name)
    {
        if (owner == null)
        {
            return null;
        }

        FieldInfo? field = owner.GetField(name, StaticMember);
        if (field == null)
        {
            problems.Add($"{owner.FullName}.{name} (static)");
        }

        return field;
    }

    internal static PropertyInfo? Property(List<string> problems, Type? owner, string name)
    {
        if (owner == null)
        {
            return null;
        }

        PropertyInfo? property = owner.GetProperty(name, InstanceMember);
        if (property == null)
        {
            problems.Add($"{owner.FullName}.{name}");
        }

        return property;
    }

    internal static PropertyInfo? StaticProperty(List<string> problems, Type? owner, string name)
    {
        if (owner == null)
        {
            return null;
        }

        PropertyInfo? property = owner.GetProperty(name, StaticMember);
        if (property == null)
        {
            problems.Add($"{owner.FullName}.{name} (static)");
        }

        return property;
    }

    internal static PropertyInfo? PropertyOf(Type type, string name, List<string> missing)
    {
        PropertyInfo? property = type.GetProperty(name, AnyMember);
        if (property == null)
        {
            missing.Add($"{type.FullName}.{name}");
        }

        return property;
    }

    internal static MethodInfo? MethodOf(Type type, string name, int parameterCount, List<string> missing)
    {
        MethodInfo[] matches = type
            .GetMethods(AnyMember)
            .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal))
            .Where(method => method.GetParameters().Length == parameterCount)
            .ToArray();

        if (matches.Length == 1)
        {
            return matches[0];
        }

        missing.Add(matches.Length == 0
            ? $"{type.FullName}.{name} ({parameterCount} parameter(s))"
            : $"{type.FullName}.{name} ({parameterCount} parameter(s)) is ambiguous: {matches.Length} overloads");
        return null;
    }

    /// <summary>
    /// Compares type names as strings so the probe never has to <c>typeof</c> a type a future build might drop
    /// </summary>
    internal static MethodInfo? Exact(
        Type? type,
        string name,
        bool requireStatic,
        string[] parameterTypeNames,
        string? returnTypeName,
        List<string> missing)
    {
        if (type == null)
        {
            return null;
        }

        MethodInfo[] matches = type
            .GetMethods(AnyMember)
            .Where(method => string.Equals(method.Name, name, StringComparison.Ordinal))
            .Where(method => method.IsStatic == requireStatic)
            .Where(method => method.GetParameters().Length == parameterTypeNames.Length)
            .Where(method => method.GetParameters()
                .Select(parameter => parameter.ParameterType.FullName)
                .SequenceEqual(parameterTypeNames))
            .Where(method => returnTypeName == null || method.ReturnType.FullName == returnTypeName)
            .ToArray();

        if (matches.Length == 1)
        {
            return matches[0];
        }

        missing.Add(matches.Length == 0
            ? type.Name + "." + name + "(" + string.Join(", ", parameterTypeNames.Select(Short)) + ")" +
              (returnTypeName == null ? string.Empty : " -> " + Short(returnTypeName))
            : type.Name + "." + name + " (ambiguous: " + matches.Length + " overloads)");
        return null;
    }

    private static string Short(string typeName)
    {
        bool byRef = typeName.EndsWith("&", StringComparison.Ordinal);
        string bare = byRef ? typeName[..^1] : typeName;
        int dot = bare.LastIndexOf('.');
        string shortName = dot >= 0 ? bare[(dot + 1)..] : bare;
        return byRef ? shortName + "&" : shortName;
    }
}
