using System.Reflection;
using System.Runtime.Loader;
using OpenBurn.Core.Storage;

namespace OpenBurn.Plugins;

public sealed record LoadedPlugin(string Name, string Version, string Description, string Source);

public sealed record PluginLoadReport(
    IReadOnlyList<LoadedPlugin> Loaded,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Messages)
{
    public static readonly PluginLoadReport Empty = new([], [], []);

    public bool AnythingLoaded => Loaded.Count > 0;

    public string Summary => Loaded.Count switch
    {
        0 when Failures.Count == 0 => "No plugins installed.",
        0 => $"No plugins loaded; {Failures.Count} failed.",
        _ => $"{Loaded.Count} plugin(s) loaded" + (Failures.Count > 0 ? $", {Failures.Count} failed." : "."),
    };
}

/// <summary>
/// Loads plugins from the plugins folder.
///
/// A plugin is ordinary .NET code running in this process with this process's
/// permissions. There is no sandbox and this does not pretend otherwise: the
/// mitigations here are that loading is opt-in, that every plugin is named in the
/// UI, that a plugin cannot displace a built-in importer or driver, and that a
/// plugin which throws during registration is skipped with its failure reported
/// rather than taking the application down with it.
///
/// Each plugin gets its own <see cref="AssemblyLoadContext"/> so two plugins can
/// depend on different versions of the same library without one of them silently
/// getting the other's.
/// </summary>
public static class PluginHost
{
    /// <summary>Where plugins live: one subfolder per plugin, or loose DLLs.</summary>
    public static string DefaultDirectory => AppPaths.Plugins;

    public static PluginLoadReport Load(PluginRegistry registry, string? directory = null, bool enabled = true)
    {
        if (!enabled) return PluginLoadReport.Empty;

        var folder = directory ?? DefaultDirectory;
        if (!Directory.Exists(folder)) return PluginLoadReport.Empty;

        var loaded = new List<LoadedPlugin>();
        var failures = new List<string>();

        foreach (var path in FindCandidates(folder))
        {
            try
            {
                var context = new PluginLoadContext(path);
                var assembly = context.LoadFromFileWithoutLocking(path);
                var found = RegisterFrom(assembly, registry, Path.GetFileName(path));

                if (found.Count == 0)
                {
                    failures.Add($"{Path.GetFileName(path)}: no type implementing IOpenBurnPlugin.");
                    continue;
                }

                loaded.AddRange(found);
            }
            catch (Exception ex)
            {
                // A plugin that will not load is the plugin's problem, not the
                // application's. Name it and carry on.
                failures.Add($"{Path.GetFileName(path)}: {ex.GetType().Name} — {ex.Message}");
            }
        }

        return new PluginLoadReport(loaded, failures, registry.Messages);
    }

    /// <summary>
    /// Register every plugin type in an assembly. Exposed so the discovery logic
    /// can be tested without writing a DLL to disk.
    /// </summary>
    public static IReadOnlyList<LoadedPlugin> RegisterFrom(Assembly assembly, PluginRegistry registry, string source)
    {
        var loaded = new List<LoadedPlugin>();

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // A partially loadable assembly still has usable types in it.
            types = [.. ex.Types.Where(t => t is not null)!];
        }

        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface) continue;
            if (!typeof(IOpenBurnPlugin).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null) continue;

            try
            {
                if (Activator.CreateInstance(type) is not IOpenBurnPlugin plugin) continue;

                registry.BeginPlugin(plugin.Name);
                plugin.Register(registry);

                loaded.Add(new LoadedPlugin(plugin.Name, plugin.Version, plugin.Description, source));
            }
            catch (Exception ex)
            {
                registry.BeginPlugin(type.Name);
                registry.Log($"failed during registration: {ex.GetType().Name} — {ex.Message}");
            }
        }

        return loaded;
    }

    private static IEnumerable<string> FindCandidates(string folder)
    {
        // Loose DLLs directly in the folder...
        foreach (var file in Directory.EnumerateFiles(folder, "*.dll")) yield return file;

        // ...and one DLL per subfolder, matching the folder name, which is how a
        // plugin with its own dependencies should be laid out.
        foreach (var subfolder in Directory.EnumerateDirectories(folder))
        {
            var name = Path.GetFileName(subfolder);
            var candidate = Path.Combine(subfolder, name + ".dll");

            if (File.Exists(candidate))
            {
                yield return candidate;
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(subfolder, "*.dll")) yield return file;
        }
    }

    /// <summary>
    /// One load context per plugin, resolving the plugin's own dependencies from
    /// beside it while sharing OpenBurn's own assemblies with the host — otherwise
    /// a plugin's <c>ILaserDevice</c> would be a different type from ours and every
    /// registration would fail the cast.
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        /// <summary>
        /// Load an assembly by reading it, rather than by mapping the file.
        ///
        /// LoadFromAssemblyPath holds the file open for the life of the process.
        /// On Unix that is invisible — an open file can still be deleted — but on
        /// Windows it locks the DLL, so a plugin cannot be updated or removed
        /// while OpenBurn is running, and the operator gets "access denied" with
        /// nothing to explain it. Reading the bytes first costs a copy and leaves
        /// nothing held.
        /// </summary>
        public Assembly LoadFromFileWithoutLocking(string path)
        {
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            return LoadFromStream(stream);
        }

        public PluginLoadContext(string pluginPath) : base(isCollectible: false) =>
            _resolver = new AssemblyDependencyResolver(pluginPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Anything OpenBurn already has loaded must be shared, not duplicated.
            if (assemblyName.Name?.StartsWith("OpenBurn", StringComparison.Ordinal) == true) return null;

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            // Dependencies must not lock their files either.
            return path is null ? null : LoadFromFileWithoutLocking(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
