using System.Reflection;

namespace Sts2LobbyEnhance;

/// <summary>
/// Anchor lookups that REPORT rather than throw.
///
/// <see cref="Reflect"/> has two families: <c>Require*</c> throws on the first miss, <c>Try*</c> returns
/// null. Neither fits the install-time probes in the repairs: a probe has to name every anchor a game
/// build no longer has, in one pass, because the operator reads that list from the log and cannot be asked
/// to relaunch the game to discover the next missing name. These lookups therefore take the caller's
/// <c>List&lt;string&gt;</c>, append a human-readable description of each miss, and return null.
///
/// Nothing here throws for a missing member, and nothing here guesses: an ambiguous name (more than one
/// matching overload) is reported as a miss, never bound to an arbitrary candidate. That rule is the whole
/// point — a repair that binds to the wrong overload degrades behaviour instead of failing loudly.
///
/// The message text is an operator-facing contract (docs and log lines quote it), which is why lookups
/// with genuinely different messages stay separate instead of sharing one parameterised helper:
///   * <see cref="Field"/>/<see cref="Property"/> are instance-only and name the member as
///     <c>DeclaringType.FullName.Name</c>; the <c>Static*</c> twins add a <c>(static)</c> suffix, because
///     "no such field" and "no such *static* field" are different repairs for a reader.
///   * <see cref="FieldOf"/>/<see cref="PropertyOf"/>/<see cref="MethodOf"/> accept instance and static
///     members alike (upstream mixes the two freely) and describe a method by name plus parameter count.
///   * <see cref="UniqueMethod"/> is the same lookup with the short type name, for the overlay probes
///     whose reason lines are already scoped by the surrounding <c>missing:</c> prefix.
///   * <see cref="Exact"/> is the only one that compares *type names*, which is what a Harmony target
///     needs: arity alone cannot tell <c>Disconnect(NetError, bool)</c> from
///     <c>Disconnect(NetErrorInfo, bool)</c>.
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
    /// Finds a type by full name across the loaded assemblies. No <c>typeof</c>, so a game build that renamed the type
    /// costs this anchor, not the whole probe.
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
    /// Finds one instance method by name and *exact* parameter types.
    ///
    /// Arity alone is not enough to bind a patch: <c>Disconnect(NetError, bool)</c> and
    /// <c>Disconnect(NetError, bool, string)</c> differ in count, but <c>Disconnect(NetError, bool)</c> and
    /// <c>Disconnect(NetErrorInfo, bool)</c> do not — and patching the wrong one would silently hook an unrelated
    /// path. Every call site below therefore spells out the parameter types it expects.
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

        MethodInfo[] byName = owner
            .GetMethods(scope | BindingFlags.Public | BindingFlags.NonPublic)
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
    /// Verifies the parameter *names* of a method we patch.
    ///
    /// Harmony binds a prefix/postfix argument to the target parameter with the same name, so a rename
    /// (<c>reason</c> to <c>error</c>) silently breaks the binding: the patch either fails to apply or, worse, binds
    /// nothing and the guard stops guarding. Parameter types are checked separately; this is the name half.
    /// </summary>
    internal static void RequireParameterNames(List<string> problems, MethodInfo? method, params string[] names)
    {
        if (method == null)
        {
            return;
        }

        ParameterInfo[] parameters = method.GetParameters();
        for (int index = 0; index < parameters.Length && index < names.Length; index++)
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

    /// <summary>
    /// Same as <see cref="Property"/> but for a static member — <c>LanConnectTailMessageRuntime.Shared</c> is a
    /// static property, and the instance-only lookup above would never find it.
    /// </summary>
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

    internal static FieldInfo? FieldOf(Type type, string name, List<string> missing)
    {
        FieldInfo? field = type.GetField(name, AnyMember);
        if (field == null)
        {
            missing.Add($"{type.FullName}.{name}");
        }

        return field;
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

    internal static FieldInfo? InstanceFieldOf(Type type, string name, List<string> missing)
    {
        FieldInfo? field = type.GetField(
            name,
            InstanceMember);
        if (field == null)
        {
            missing.Add($"{type.FullName}.{name}");
        }

        return field;
    }

    internal static MethodInfo? UniqueMethod(Type? type, string name, int parameterCount, List<string> missing)
    {
        if (type == null)
        {
            return null;
        }

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
            ? type.Name + "." + name + "/" + parameterCount + " param(s)"
            : type.Name + "." + name + "/" + parameterCount + " param(s) (ambiguous: " + matches.Length + " overloads)");
        return null;
    }

    /// <summary>
    /// Finds one method by name, static-ness and exact parameter/return type names. Names are compared as
    /// strings so the probe never has to <c>typeof</c> a type that a future build might have dropped.
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

    /// <summary>
    /// Last segment of a type name, keeping the by-ref marker. The reason line is read by a person
    /// looking at a log, and <c>SerializableRun&amp;</c> says the same thing as
    /// <c>MegaCrit.Sts2.Core.Runs.SerializableRun&amp;</c> in a tenth of the width.
    /// </summary>
    private static string Short(string typeName)
    {
        bool byRef = typeName.EndsWith("&", StringComparison.Ordinal);
        string bare = byRef ? typeName[..^1] : typeName;
        int dot = bare.LastIndexOf('.');
        string shortName = dot >= 0 ? bare[(dot + 1)..] : bare;
        return byRef ? shortName + "&" : shortName;
    }
}
