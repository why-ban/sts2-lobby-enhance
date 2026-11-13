using System.Reflection;

namespace Sts2LobbyEnhance;

/// <summary>
/// Collects the anchors a repair needs and the ones it could not find, so a host-mod or game update
/// produces one readable line instead of an exception on the first lookup.
///
/// The point is the difference between "missing: LanConnectLobbyOverlay.GameModeFromCreateId,
/// GameMode.Blitz" and a <c>MissingMethodException</c> for the first of the two: the first tells a user
/// (and the next release) what changed, the second only says where it stopped. Usage is deliberately
/// boring — resolve everything, then decide:
///
/// <code>
/// var anchors = AnchorProbe.For("extra-game-modes");
/// Type? overlay = anchors.TypeIn(host, "Sts2LanConnect.Scripts.LanConnectLobbyOverlay");
/// MethodInfo? fromId = overlay is null ? null : anchors.MethodOn(overlay, "GameModeFromCreateId", 1);
/// if (!anchors.Report(out string reason))
/// {
///     // reason = "extra-game-modes: missing: ..." — answer false from Applies, do not throw.
/// }
/// </code>
///
/// Nothing here throws and nothing here patches. That is the whole contract: a caller resolves every
/// anchor first and only then calls <c>harmony.Patch</c> (see <see cref="ICompatFix"/>), so a miss can
/// never leave a half-installed repair behind.
/// </summary>
internal sealed class AnchorProbe
{
    private readonly List<string> _missing = new();
    private readonly string _subject;

    private AnchorProbe(string subject) => _subject = subject;

    /// <summary>Starts a probe for one repair. <paramref name="subject"/> only labels the report.</summary>
    internal static AnchorProbe For(string subject) => new(subject);

    /// <summary>Anchors that were not found, in the order they were asked for.</summary>
    internal IReadOnlyList<string> Missing => _missing;

    /// <summary>True when every anchor asked for so far was found.</summary>
    internal bool Ok => _missing.Count == 0;

    /// <summary>Human-readable "missing: A, B", or <c>null</c> when nothing is missing.</summary>
    internal string? Reason => _missing.Count == 0 ? null : "missing: " + string.Join(", ", _missing);

    /// <summary>Looks up a type by full name and records it as missing when it is gone.</summary>
    internal Type? TypeIn(Assembly assembly, string fullName)
    {
        Type? type = Reflect.TryType(assembly, fullName);
        if (type == null)
        {
            Miss(fullName);
        }

        return type;
    }

    internal FieldInfo? FieldOn(Type type, string name)
    {
        FieldInfo? field = Reflect.TryField(type, name);
        if (field == null)
        {
            Miss($"{type.Name}.{name}");
        }

        return field;
    }

    internal PropertyInfo? PropertyOn(Type type, string name)
    {
        PropertyInfo? property = Reflect.TryProperty(type, name);
        if (property == null)
        {
            Miss($"{type.Name}.{name}");
        }

        return property;
    }

    /// <summary>Looks up a method by name alone; an ambiguous name counts as missing.</summary>
    internal MethodInfo? MethodOn(Type type, string name)
    {
        MethodInfo? method = Reflect.TryMethodByName(type, name);
        if (method == null)
        {
            Miss($"{type.Name}.{name}");
        }

        return method;
    }

    /// <summary>Looks up a method by name and parameter count; anything but exactly one match is missing.</summary>
    internal MethodInfo? MethodOn(Type type, string name, int parameterCount)
    {
        MethodInfo? method = Reflect.TryMethodByName(type, name, parameterCount);
        if (method == null)
        {
            Miss($"{type.Name}.{name}/{parameterCount} param(s)");
        }

        return method;
    }

    /// <summary>
    /// Records an anchor this class cannot look up itself — a shape check on a found member, an IL
    /// pattern, a static initializer that returned null. Use it instead of throwing so the report stays
    /// complete.
    /// </summary>
    internal void Miss(string anchor)
    {
        if (!_missing.Contains(anchor, StringComparer.Ordinal))
        {
            _missing.Add(anchor);
        }
    }

    /// <summary>
    /// Answers whether the repair may patch. On failure <paramref name="reason"/> is a non-empty sentence
    /// naming every missing anchor, ready to be returned from <see cref="ICompatFix.Applies"/>.
    /// </summary>
    internal bool Report(out string reason)
    {
        if (_missing.Count == 0)
        {
            reason = string.Empty;
            return true;
        }

        reason = $"{_subject}: {Reason}";
        return false;
    }
}
