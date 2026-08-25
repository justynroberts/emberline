namespace Emberline.Core.Storage;

/// <summary>
/// Where Emberline keeps things.
///
/// Everything is local, per the local-first principle: no account, no cloud, no
/// telemetry. On every platform this lands somewhere the user can open in a file
/// manager, back up, and copy to another machine.
/// </summary>
public static class AppPaths
{
    private static string? _overrideRoot;

    /// <summary>Point the whole application at a different root. Used by tests.</summary>
    public static void OverrideRoot(string? root) => _overrideRoot = root;

    public static string Root => _overrideRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                                  Environment.SpecialFolderOption.Create),
        "Emberline");

    /// <summary>Where this application's data lived before it was renamed.</summary>
    private static string LegacyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                                  Environment.SpecialFolderOption.Create),
        "OpenBurn");

    /// <summary>
    /// Bring settings, machine profiles, materials and job history across from the
    /// folder the application used before it was called Emberline.
    ///
    /// Renaming a product must not silently discard somebody's measured bed sizes
    /// and tested material settings. Copies rather than moves, and only when there
    /// is nothing here yet, so it can never overwrite newer work and running the
    /// old build afterwards still finds its own data.
    /// </summary>
    public static void MigrateLegacyData()
    {
        try
        {
            if (_overrideRoot is not null) return;
            if (!Directory.Exists(LegacyRoot)) return;
            if (Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any()) return;

            CopyTree(new DirectoryInfo(LegacyRoot), new DirectoryInfo(Root));
        }
        catch (Exception)
        {
            // Starting with fresh settings is a far better outcome than refusing
            // to start because an old folder could not be read.
        }
    }

    private static void CopyTree(DirectoryInfo from, DirectoryInfo to)
    {
        to.Create();

        foreach (var file in from.GetFiles())
        {
            file.CopyTo(Path.Combine(to.FullName, file.Name), overwrite: false);
        }

        foreach (var directory in from.GetDirectories())
        {
            CopyTree(directory, new DirectoryInfo(Path.Combine(to.FullName, directory.Name)));
        }
    }

    public static string Machines => Path.Combine(Root, "machines");
    public static string Materials => Path.Combine(Root, "materials");
    public static string Cameras => Path.Combine(Root, "cameras");
    public static string Jobs => Path.Combine(Root, "jobs");
    public static string Plugins => Path.Combine(Root, "plugins");
    public static string Logs => Path.Combine(Root, "logs");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string MaterialsFile => Path.Combine(Materials, "user-materials.json");
    public static string DatabaseFile => Path.Combine(Root, "emberline.db");

    public static void EnsureCreated()
    {
        foreach (var dir in new[] { Root, Machines, Materials, Cameras, Jobs, Plugins, Logs })
        {
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>Device profiles shipped with the application, next to the executable.</summary>
    public static string BundledDevices
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var candidate = Path.Combine(baseDir, "devices");
            if (Directory.Exists(candidate)) return candidate;

            // Running from the repository during development.
            var dir = new DirectoryInfo(baseDir);
            while (dir is not null)
            {
                var repo = Path.Combine(dir.FullName, "devices");
                if (Directory.Exists(repo) && File.Exists(Path.Combine(repo, "generic-grbl.json"))) return repo;
                dir = dir.Parent;
            }
            return candidate;
        }
    }
}
