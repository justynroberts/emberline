using System.Text.Json.Serialization;

namespace Emberline.Core.Machines;

/// <summary>Where the machine's origin sits relative to the bed as the operator faces it.</summary>
public enum BedOrigin
{
    FrontLeft,
    FrontRight,
    RearLeft,
    RearRight,
    Center,
}

public enum ConnectionKind
{
    Serial,
    Tcp,
    WebSocket,
    Http,
    Virtual,

    /// <summary>
    /// ESP3D's web interface, as built into Grbl_Esp32: commands over HTTP, replies
    /// over a WebSocket. Neither the Tcp nor the WebSocket kind works with it.
    /// </summary>
    Esp3d,
}

[Flags]
public enum MachineCapabilities
{
    None = 0,
    Homing = 1 << 0,
    SoftLimits = 1 << 1,
    AirAssist = 1 << 2,
    Rotary = 1 << 3,
    ZAxis = 1 << 4,
    Camera = 1 << 5,
    Framing = 1 << 6,
    /// <summary>GRBL $32 dynamic power. Without it, photo engraving looks wrong.</summary>
    LaserMode = 1 << 7,
}

/// <summary>
/// Everything Emberline needs to know about one machine. Stored as JSON in the
/// devices/ folder so adding a machine is a text file, not a code change — that
/// is the "hardware agnostic" principle made concrete.
/// </summary>
public sealed record MachineProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Manufacturer { get; init; } = "Generic";
    public string Model { get; init; } = "GRBL Laser";

    /// <summary>Friendly name the user sees and can change — "Workshop laser".</summary>
    public string DisplayName { get; init; } = "GRBL Laser";

    /// <summary>Optical output in watts. Drives the material-preset lookup.</summary>
    public double LaserWatts { get; init; } = 10;

    public double BedWidthMm { get; init; } = 400;
    public double BedHeightMm { get; init; } = 400;
    public BedOrigin Origin { get; init; } = BedOrigin.FrontLeft;

    public string Firmware { get; init; } = "GRBL 1.1";

    public IReadOnlyList<ConnectionKind> Connections { get; init; } = [ConnectionKind.Serial];

    /// <summary>
    /// What the Wi-Fi button connects with: the first network kind the profile
    /// lists. A fixed choice of raw TCP used to live in the button, which meant a
    /// machine with no telnet console could not be reached over Wi-Fi at all.
    /// </summary>
    [JsonIgnore]
    public ConnectionKind NetworkConnection =>
        Connections.FirstOrDefault(k => k is ConnectionKind.Esp3d or ConnectionKind.Tcp or ConnectionKind.WebSocket or ConnectionKind.Http,
                                   ConnectionKind.Tcp);

    /// <summary>mm/min.</summary>
    public double MaxSpeedMmMin { get; init; } = 12000;

    /// <summary>mm/min used for rapids and framing.</summary>
    public double TravelSpeedMmMin { get; init; } = 6000;

    /// <summary>The S value that means 100 % power. GRBL's $30.</summary>
    public int MaxSpindleValue { get; init; } = 1000;

    /// <summary>mm/sec², used for job-time estimation. GRBL's $120/$121.</summary>
    public double AccelerationX { get; init; } = 1500;
    public double AccelerationY { get; init; } = 1500;

    /// <summary>GRBL's $11.</summary>
    public double JunctionDeviation { get; init; } = 0.01;

    public MachineCapabilities Capabilities { get; init; } =
        MachineCapabilities.Homing | MachineCapabilities.Framing | MachineCapabilities.LaserMode;

    /// <summary>Sent verbatim after connecting — units, absolute mode, laser off.</summary>
    public IReadOnlyList<string> InitCommands { get; init; } = ["G21", "G90", "G17", "M5"];

    /// <summary>M-codes for air assist, when the machine has it wired to one.</summary>
    public string AirAssistOnCommand { get; init; } = "M8";
    public string AirAssistOffCommand { get; init; } = "M9";

    /// <summary>Default serial baud. 115200 is universal on GRBL 1.1.</summary>
    public int BaudRate { get; init; } = 115200;

    /// <summary>Last known network endpoint, remembered between sessions.</summary>
    public string? Host { get; init; }
    public int TcpPort { get; init; } = 23;
    public int WebSocketPort { get; init; } = 81;

    /// <summary>Device driver key. Lets a manufacturer plug in non-standard behaviour.</summary>
    public string DriverId { get; init; } = "grbl";

    [JsonIgnore]
    public bool SupportsRotary => Capabilities.HasFlag(MachineCapabilities.Rotary);

    [JsonIgnore]
    public bool SupportsCamera => Capabilities.HasFlag(MachineCapabilities.Camera);

    [JsonIgnore]
    public string Description => $"{Manufacturer} {Model} · {LaserWatts:0.#} W · {BedWidthMm:0}×{BedHeightMm:0} mm";

    /// <summary>Convert a 0–100 % power to the machine's S value.</summary>
    public int PowerToSpindle(double percent) =>
        (int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * MaxSpindleValue);

    public static MachineProfile GenericGrbl() => new()
    {
        Id = "generic-grbl",
        Manufacturer = "Generic",
        Model = "GRBL Laser",
        DisplayName = "Generic GRBL Laser",
        LaserWatts = 10,
        BedWidthMm = 400,
        BedHeightMm = 400,
        Connections = [ConnectionKind.Serial, ConnectionKind.Tcp, ConnectionKind.WebSocket],
    };

    /// <summary>
    /// The reference machine for Emberline 0.1.
    ///
    /// Its Wi-Fi was confirmed against a real unit rather than assumed: the board
    /// runs Grbl_Esp32 1.3a with ESP3D, has no telnet console on 23, and ignores
    /// commands written to its WebSocket — so the network connection is Esp3d.
    /// Saying ESP32 in <see cref="Firmware"/> also matters over USB: it stops the
    /// serial port pulsing DTR on connect, which resets an ESP32.
    /// </summary>
    public static MachineProfile BlazeXM5Pro() => new()
    {
        Id = "blazex-m5-pro-10w",
        Manufacturer = "BlazeX",
        Model = "M5 Pro",
        DisplayName = "BlazeX M5 Pro 10W",
        LaserWatts = 10,
        BedWidthMm = 400,
        BedHeightMm = 400,
        Origin = BedOrigin.FrontLeft,
        Firmware = "Grbl_Esp32 1.3a (ESP3D)",
        Connections = [ConnectionKind.Serial, ConnectionKind.Esp3d],
        MaxSpeedMmMin = 24000,
        TravelSpeedMmMin = 12000,
        MaxSpindleValue = 1000,
        AccelerationX = 3000,
        AccelerationY = 3000,
        Capabilities = MachineCapabilities.Homing | MachineCapabilities.Framing |
                       MachineCapabilities.LaserMode | MachineCapabilities.AirAssist |
                       MachineCapabilities.Rotary | MachineCapabilities.Camera |
                       MachineCapabilities.SoftLimits,
        DriverId = "blazex",
        TcpPort = 23,
        WebSocketPort = 81,
    };

    public static MachineProfile Virtual() => new()
    {
        Id = "virtual-laser",
        Manufacturer = "Emberline",
        Model = "Virtual Laser",
        DisplayName = "Virtual Laser (simulator)",
        LaserWatts = 10,
        BedWidthMm = 400,
        BedHeightMm = 400,
        Connections = [ConnectionKind.Virtual],
        DriverId = "grbl",
    };
}
