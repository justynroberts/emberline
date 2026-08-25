using System.Text.Json;
using System.Text.Json.Serialization;
using Emberline.Core.Storage;

namespace Emberline.AI;

/// <summary>
/// Configuration for the optional assistant.
///
/// Opt-in by design, per the local-first principle in the PRD: Emberline is fully
/// functional with this switched off, nothing is sent anywhere until the user
/// types a question, and the only outbound host is api.anthropic.com.
/// </summary>
public sealed record AiOptions
{
    public bool Enabled { get; init; }

    /// <summary>
    /// Read from the environment or a local file, never committed and never sent
    /// anywhere but Anthropic.
    /// </summary>
    [JsonIgnore]
    public string? ApiKey { get; init; }

    public string Model { get; init; } = "claude-opus-5";

    /// <summary>Cap on a single reply. Generous — a settings explanation is worth a few paragraphs.</summary>
    public int MaxTokens { get; init; } = 8000;

    /// <summary>Reasoning depth. Medium suits a shop-floor assistant; raise it for diagnosis.</summary>
    public string Effort { get; init; } = "medium";

    /// <summary>
    /// Hard safety switch. When true — the default, and not exposed in the UI —
    /// the assistant can never drive the machine, only propose an action the
    /// operator has to confirm with a click.
    /// </summary>
    [JsonIgnore]
    public bool RequireConfirmationForMotion { get; init; } = true;

    public static AiOptions Load()
    {
        var path = Path.Combine(AppPaths.Root, "ai.json");
        var stored = new AiOptions();

        if (File.Exists(path))
        {
            try
            {
                stored = JsonSerializer.Deserialize<AiOptions>(File.ReadAllText(path)) ?? new AiOptions();
            }
            catch (JsonException)
            {
                stored = new AiOptions();
            }
        }

        return stored with { ApiKey = ResolveApiKey() };
    }

    /// <summary>
    /// Environment variable first, then a key file in the application folder. The
    /// key is deliberately never written into settings.json, which users paste into
    /// forum posts when asking for help.
    /// </summary>
    public static string? ResolveApiKey()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();

        var keyFile = Path.Combine(AppPaths.Root, "anthropic.key");
        if (!File.Exists(keyFile)) return null;

        try
        {
            var text = File.ReadAllText(keyFile).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Where the key lives when it is not in the environment.</summary>
    public static string KeyFilePath => Path.Combine(AppPaths.Root, "anthropic.key");

    /// <summary>True when the key comes from the environment, which the app must not overwrite.</summary>
    public static bool KeyComesFromEnvironment =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

    /// <summary>
    /// Write the key to its own file, readable only by this user.
    ///
    /// Deliberately not settings.json: that is the file people paste into forum
    /// posts when asking for help, and a key in it would be published with the
    /// question. Passing null or blank deletes the file rather than storing an
    /// empty one, so "clear it" genuinely clears it.
    /// </summary>
    public static void SaveApiKey(string? key)
    {
        Directory.CreateDirectory(AppPaths.Root);
        var path = KeyFilePath;

        if (string.IsNullOrWhiteSpace(key))
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        File.WriteAllText(path, key.Trim());
        RestrictToOwner(path);
    }

    /// <summary>Owner read/write only. Best effort — Windows has no equivalent mode.</summary>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The key is still no more exposed than the folder it sits in.
        }
    }

    /// <summary>
    /// A key with its middle removed, for showing that one is stored without
    /// putting it back on screen where it can be read over a shoulder.
    /// </summary>
    public static string Mask(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        var trimmed = key.Trim();
        return trimmed.Length <= 12
            ? new string('•', trimmed.Length)
            : $"{trimmed[..7]}…{trimmed[^4..]}";
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        var path = Path.Combine(AppPaths.Root, "ai.json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public bool IsUsable => Enabled && !string.IsNullOrWhiteSpace(ApiKey);
}
