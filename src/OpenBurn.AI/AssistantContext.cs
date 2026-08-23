namespace OpenBurn.AI;

/// <summary>What the assistant is allowed to see. Assembled by the host on demand.</summary>
public sealed record AssistantContext
{
    public required string MachineName { get; init; }
    public required double LaserWatts { get; init; }
    public required double BedWidthMm { get; init; }
    public required double BedHeightMm { get; init; }
    public required bool IsConnected { get; init; }
    public required string MachineState { get; init; }
    public required bool IsHomed { get; init; }

    public string? WorkPosition { get; init; }
    public IReadOnlyDictionary<int, double>? ControllerSettings { get; init; }

    public required IReadOnlyList<LayerSummary> Layers { get; init; }
    public required JobSummary? Job { get; init; }

    /// <summary>Recent console traffic. The single most useful thing when diagnosing a fault.</summary>
    public string? ConsoleTail { get; init; }

    public string? SelectedMaterial { get; init; }
}

public sealed record LayerSummary(
    string Name,
    string Operation,
    double SpeedMmMin,
    double PowerPercent,
    int Passes,
    double LineIntervalMm,
    bool Enabled,
    int ShapeCount);

public sealed record JobSummary(
    int LineCount,
    double WidthMm,
    double HeightMm,
    double CutLengthMm,
    double TravelLengthMm,
    string EstimatedDuration,
    IReadOnlyList<string> Issues);

/// <summary>
/// A change the assistant wants to make. Applied only through the host, and only
/// after the host has decided it is allowed.
/// </summary>
public sealed record LayerChange(
    string LayerName,
    double? SpeedMmMin = null,
    double? PowerPercent = null,
    int? Passes = null,
    double? LineIntervalMm = null,
    bool? AirAssist = null);

/// <summary>
/// A machine action the assistant is proposing. It is never executed directly —
/// the host turns it into a card the operator must click.
/// </summary>
public sealed record ProposedAction(string Kind, string Description, IReadOnlyDictionary<string, string> Parameters);

/// <summary>
/// The application surface the assistant is allowed to touch.
///
/// Deliberately narrow. Reads are unrestricted; writes are limited to layer
/// settings and generating a test grid — both fully reversible and neither of
/// which starts the machine. Anything that moves the gantry or fires the beam
/// arrives as a <see cref="ProposedAction"/> and waits for a human.
/// </summary>
public interface IAssistantHost
{
    AssistantContext BuildContext();

    /// <summary>Apply layer setting changes. Returns a description of what actually changed.</summary>
    string ApplyLayerChanges(IReadOnlyList<LayerChange> changes);

    /// <summary>Build a power × speed test grid and load it, without starting it.</summary>
    string PrepareTestGrid(double[] powers, double[] speeds, double cellSizeMm);

    /// <summary>Surface a proposed machine action for the operator to confirm or ignore.</summary>
    void ProposeAction(ProposedAction action);

    /// <summary>The greyscale bitmap of the selected image, if one is selected, as PNG bytes.</summary>
    byte[]? GetSelectedImagePng();
}
