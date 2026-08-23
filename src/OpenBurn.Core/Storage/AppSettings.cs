using System.Text.Json;
using System.Text.Json.Serialization;
using OpenBurn.Core.Units;

namespace OpenBurn.Core.Storage;

public enum ThemeMode { System, Light, Dark }

/// <summary>A workpiece the user described and asked to keep.</summary>
public sealed record SavedWorkpiece
{
    public string Name { get; init; } = "";
    public bool Round { get; init; }
    public double WidthMm { get; init; } = 100;
    public double HeightMm { get; init; } = 100;
    public double CornerRadiusMm { get; init; }
}

/// <summary>Everything the application remembers between sessions. One versioned file.</summary>
public sealed record AppSettings
{
    /// <summary>Bumped whenever the shape changes, so old files can be migrated rather than discarded.</summary>
    public int Version { get; init; } = 1;

    public ThemeMode Theme { get; init; } = ThemeMode.System;
    public LengthUnit DisplayUnit { get; init; } = LengthUnit.Millimetres;

    public string? LastMachineId { get; init; }
    public string? LastSerialPort { get; init; }
    public string? LastNetworkAddress { get; init; }

    /// <summary>Status polls per second. Six is live without flooding a wireless link.</summary>
    public double StatusPollHz { get; init; } = 6;

    public double JogStepMm { get; init; } = 10;
    public double JogFeedMmMin { get; init; } = 3000;

    /// <summary>Keep a copy of the G-code for every completed job.</summary>
    public bool ArchiveJobGcode { get; init; } = true;

    public bool ShowGridLines { get; init; } = true;
    public bool ShowTravelMoves { get; init; }
    public bool ShowRulers { get; init; } = true;

    /// <summary>
    /// Blanks the user has described themselves, kept so a shape cut repeatedly
    /// does not have to be measured and typed in again every session.
    /// </summary>
    public IReadOnlyList<SavedWorkpiece> SavedWorkpieces { get; init; } = [];

    /// <summary>Warn before starting a job when the machine has not been homed.</summary>
    public bool WarnWhenNotHomed { get; init; } = true;

    /// <summary>Always frame before the first run of a job.</summary>
    public bool RequireFrameBeforeStart { get; init; }

    public string? LastOpenedFolder { get; init; }
    public IReadOnlyList<string> RecentFiles { get; init; } = [];

    /// <summary>
    /// Load plugins from the plugins folder.
    ///
    /// Off by default: a plugin is ordinary code running in this process with the
    /// user's permissions, and there is no sandbox. Turning it on is a decision
    /// about trust, so it has to be made deliberately.
    /// </summary>
    public bool PluginsEnabled { get; init; }

    /// <summary>Opt-in. Everything works without it; nothing is sent anywhere until it is set.</summary>
    public bool AiEnabled { get; init; }
    public string? AiModel { get; init; } = "claude-opus-5";

    public static readonly AppSettings Default = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        if (!File.Exists(path)) return Default;

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
            return settings is null ? Default : Migrate(settings);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt settings file must never stop the application starting.
            // Keep the bad file so it can be looked at, and carry on with defaults.
            TryQuarantine(path);
            return Default;
        }
    }

    public void Save(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Write to a temporary file and move it into place, so a crash mid-write
        // cannot leave a half-written settings file behind.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    private static AppSettings Migrate(AppSettings settings) => settings.Version switch
    {
        <= 1 => settings with { Version = 1 },
        _ => settings,
    };

    private static void TryQuarantine(string path)
    {
        try
        {
            File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
            // Nothing more to do — defaults will be used either way.
        }
    }

    public AppSettings WithRecentFile(string path)
    {
        var recent = new List<string> { path };
        recent.AddRange(RecentFiles.Where(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase)));
        return this with { RecentFiles = recent.Take(12).ToList(), LastOpenedFolder = Path.GetDirectoryName(path) };
    }
}
