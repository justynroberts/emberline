using System.Text.Json;
using System.Text.Json.Serialization;
using Emberline.Camera;
using Emberline.Core.Storage;

namespace Emberline.Vision;

/// <summary>
/// One camera calibrated against one machine.
///
/// Stored per machine-and-camera pair, because moving the camera or changing the
/// machine invalidates it completely — and a stale calibration is worse than none,
/// since it produces an overlay that looks right and is wrong by ten millimetres.
/// </summary>
public sealed record CameraCalibration
{
    public required string MachineId { get; init; }
    public required string CameraId { get; init; }

    public LensParameters Lens { get; init; } = LensParameters.None;

    /// <summary>Bed corners as they appear in the camera image, in view order: TL, TR, BR, BL.</summary>
    public required IReadOnlyList<Point2> ImageCorners { get; init; }

    /// <summary>Bed size the corners correspond to, millimetres.</summary>
    public required double BedWidthMm { get; init; }
    public required double BedHeightMm { get; init; }

    public DateTimeOffset CalibratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Residual of the corner fit in pixels. Above a couple of pixels, re-do it.</summary>
    public double ResidualPixels { get; init; }

    [JsonIgnore]
    public bool IsUsable => ImageCorners.Count == 4 && BedWidthMm > 0 && BedHeightMm > 0;

    [JsonIgnore]
    public string Quality => ResidualPixels switch
    {
        < 1.0 => "Excellent",
        < 2.5 => "Good",
        < 6 => "Usable — re-calibrate if placement looks off",
        _ => "Poor — re-calibrate before trusting the overlay",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string PathFor(string machineId, string cameraId) =>
        Path.Combine(AppPaths.Cameras, $"{Sanitise(machineId)}__{Sanitise(cameraId)}.json");

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Cameras);
        File.WriteAllText(PathFor(MachineId, CameraId), JsonSerializer.Serialize(this, JsonOptions));
    }

    public static CameraCalibration? Load(string machineId, string cameraId)
    {
        var path = PathFor(machineId, cameraId);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<CameraCalibration>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Sanitise(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
}

public readonly record struct Point2(double X, double Y)
{
    public static implicit operator (double X, double Y)(Point2 p) => (p.X, p.Y);
    public static implicit operator Point2((double X, double Y) t) => new(t.X, t.Y);
}
