using System.Reflection;

namespace Sts2LobbyEnhance.Tests;

/// <summary>
/// Loads the shipped assemblies the way the CLR would load them inside the game: every unresolved
/// reference is looked up in the game's data directory.
///
/// Only metadata is ever touched. Nothing here JITs game code, runs a static constructor of a game
/// type, or calls into MegaCrit's logging (which needs a live Godot runtime and dies with an access
/// violation when it does not get one).
/// </summary>
internal static class GameAssemblyLoader
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Assembly> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static string? _resolverDirectory;

    /// <summary>
    /// Registers the assembly resolver once. Safe to call from every test; only the first call has an
    /// effect.
    /// </summary>
    internal static void InstallResolver(string gameDirectory)
    {
        lock (Gate)
        {
            if (_resolverDirectory is not null)
            {
                return;
            }

            _resolverDirectory = gameDirectory;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        }
    }

    /// <summary>Loads (or returns the already loaded copy of) an assembly from an absolute path.</summary>
    internal static Assembly Load(string path)
    {
        string fullPath = Path.GetFullPath(path);

        lock (Gate)
        {
            if (Cache.TryGetValue(fullPath, out Assembly? cached))
            {
                return cached;
            }

            Assembly loaded = Assembly.LoadFrom(fullPath);
            Cache[fullPath] = loaded;
            return loaded;
        }
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs arguments)
    {
        string? directory;
        lock (Gate)
        {
            directory = _resolverDirectory;
        }

        if (directory is null)
        {
            return null;
        }

        string candidate = Path.Combine(directory, new AssemblyName(arguments.Name).Name + ".dll");
        return File.Exists(candidate) ? Load(candidate) : null;
    }
}
