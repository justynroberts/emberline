using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBurn.Core.Storage;

namespace OpenBurn.Core.Machines;

/// <summary>
/// Loads machine profiles from JSON on disk, so adding a machine is a text file
/// rather than a code change and a release.
/// </summary>
public sealed class MachineLibrary
{
    private readonly List<MachineProfile> _profiles = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public IReadOnlyList<MachineProfile> Profiles => _profiles;

    /// <summary>Bundled profiles first, then anything the user has added.</summary>
    public static MachineLibrary Load()
    {
        var library = new MachineLibrary();
        library.LoadFolder(AppPaths.BundledDevices);
        library.LoadFolder(AppPaths.Machines);

        if (library._profiles.Count == 0)
        {
            // Never leave the user with no machine at all — the simulator always works.
            library._profiles.Add(MachineProfile.GenericGrbl());
            library._profiles.Add(MachineProfile.BlazeXM5Pro());
            library._profiles.Add(MachineProfile.Virtual());
        }
        else if (!library._profiles.Any(p => p.Connections.Contains(ConnectionKind.Virtual)))
        {
            library._profiles.Add(MachineProfile.Virtual());
        }

        return library;
    }

    public void LoadFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;

        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order())
        {
            try
            {
                var profile = JsonSerializer.Deserialize<MachineProfile>(File.ReadAllText(file), JsonOptions);
                if (profile is null) continue;
                Add(profile);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // One malformed device file must not stop the others loading.
                Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    /// <summary>Files that failed to load, so the UI can say which and why.</summary>
    public List<string> Errors { get; } = [];

    public void Add(MachineProfile profile)
    {
        var index = _profiles.FindIndex(p => p.Id == profile.Id);
        if (index >= 0) _profiles[index] = profile;
        else _profiles.Add(profile);
    }

    public MachineProfile? Find(string id) => _profiles.FirstOrDefault(p => p.Id == id);

    public MachineProfile Default => _profiles.FirstOrDefault(p => p.DriverId != "grbl" || !p.Connections.Contains(ConnectionKind.Virtual))
                                     ?? _profiles.FirstOrDefault()
                                     ?? MachineProfile.GenericGrbl();

    public void Save(MachineProfile profile)
    {
        Directory.CreateDirectory(AppPaths.Machines);
        var path = Path.Combine(AppPaths.Machines, $"{Sanitise(profile.Id)}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
        Add(profile);
    }

    public bool Delete(string id)
    {
        var path = Path.Combine(AppPaths.Machines, $"{Sanitise(id)}.json");
        var removed = _profiles.RemoveAll(p => p.Id == id) > 0;
        if (File.Exists(path)) File.Delete(path);
        return removed;
    }

    private static string Sanitise(string id) =>
        string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
}
