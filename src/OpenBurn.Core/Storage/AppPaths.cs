namespace OpenBurn.Core.Storage;

/// <summary>
/// Where OpenBurn keeps things.
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
        "OpenBurn");

    public static string Machines => Path.Combine(Root, "machines");
    public static string Materials => Path.Combine(Root, "materials");
    public static string Cameras => Path.Combine(Root, "cameras");
    public static string Jobs => Path.Combine(Root, "jobs");
    public static string Plugins => Path.Combine(Root, "plugins");
    public static string Logs => Path.Combine(Root, "logs");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string MaterialsFile => Path.Combine(Materials, "user-materials.json");
    public static string DatabaseFile => Path.Combine(Root, "openburn.db");

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
