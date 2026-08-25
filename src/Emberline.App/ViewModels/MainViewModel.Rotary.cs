using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emberline.Core.Machines;

namespace Emberline.App.ViewModels;

/// <summary>
/// The rotary attachment panel.
///
/// Only shown when the machine profile declares the capability, because offering
/// rotary settings on a machine that has no rotary is a good way to have somebody
/// engrave a flat job at 0.42 scale and not understand why.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty]
    private bool _rotaryEnabled;

    [ObservableProperty]
    private RotaryKind _rotaryKind = RotaryKind.Roller;

    [ObservableProperty]
    private double _rotaryWorkpieceDiameterMm = 60;

    [ObservableProperty]
    private double _rotaryRollerDiameterMm = 20;

    [ObservableProperty]
    private double _rotaryStepsPerRotation = 6400;

    public IReadOnlyList<RotaryKind> RotaryKinds { get; } = Enum.GetValues<RotaryKind>();

    public bool MachineHasRotary => SelectedMachine.SupportsRotary;

    /// <summary>Built from the panel's values plus the machine's own steps-per-millimetre.</summary>
    public RotarySetup Rotary => new()
    {
        Enabled = RotaryEnabled && MachineHasRotary,
        Kind = RotaryKind,
        WorkpieceDiameterMm = RotaryWorkpieceDiameterMm,
        RollerDiameterMm = RotaryRollerDiameterMm,
        StepsPerRotation = RotaryStepsPerRotation,
        AxisStepsPerMm = AxisStepsPerMm,
        Axis = 'Y',
    };

    /// <summary>
    /// Steps per millimetre for the Y axis, read from the controller's $101 when
    /// available. Guessing this is how a rotary job comes out at the wrong scale,
    /// so the panel says where the number came from.
    /// </summary>
    public double AxisStepsPerMm =>
        _device?.Settings.TryGetValue(101, out var value) == true && value > 0 ? value : 80;

    public string AxisStepsSource =>
        _device?.Settings.ContainsKey(101) == true
            ? $"Y steps/mm: {AxisStepsPerMm:0.###} (read from $101)"
            : $"Y steps/mm: {AxisStepsPerMm:0.###} (assumed — connect and read $$ to use the machine's own value)";

    public string RotarySummary => Rotary.Describe();

    public IReadOnlyList<string> RotaryWarnings => Rotary.Check(Design.Bounds.Height);

    public bool HasRotaryWarnings => RotaryWarnings.Count > 0;

    public string RotaryWarningText => string.Join("\n", RotaryWarnings);

    public bool IsChuck => RotaryKind == RotaryKind.Chuck;

    partial void OnRotaryEnabledChanged(bool value)
    {
        if (value)
        {
            Console.AppendInfo("Rotary mode on. " + Rotary.Describe());
            Console.AppendInfo("Remove or raise the flat bed before running, and re-zero on the workpiece surface.");
        }
        RaiseRotaryState();
    }

    partial void OnRotaryKindChanged(RotaryKind value)
    {
        OnPropertyChanged(nameof(IsChuck));
        RaiseRotaryState();
    }

    partial void OnRotaryWorkpieceDiameterMmChanged(double value) => RaiseRotaryState();
    partial void OnRotaryRollerDiameterMmChanged(double value) => RaiseRotaryState();
    partial void OnRotaryStepsPerRotationChanged(double value) => RaiseRotaryState();

    private void RaiseRotaryState()
    {
        OnPropertyChanged(nameof(Rotary));
        OnPropertyChanged(nameof(RotarySummary));
        OnPropertyChanged(nameof(RotaryWarnings));
        OnPropertyChanged(nameof(HasRotaryWarnings));
        OnPropertyChanged(nameof(RotaryWarningText));
        OnPropertyChanged(nameof(AxisStepsPerMm));
        OnPropertyChanged(nameof(AxisStepsSource));
        QueueRegenerate();
    }

    /// <summary>
    /// Spin the workpiece one full turn so the operator can check it runs true
    /// before committing a job to it. Motion, so it goes through the same guard as
    /// everything else.
    /// </summary>
    [RelayCommand]
    private Task TestRotationAsync() => GuardAsync(async device =>
    {
        var setup = Rotary;
        if (!setup.IsUsable)
        {
            Console.AppendError("Complete the rotary settings first.");
            return;
        }

        // One full turn is the circumference in surface millimetres, which the
        // scale factor converts to the distance to command.
        var commanded = setup.CircumferenceMm * setup.ScaleFactor;

        Console.AppendInfo($"Turning one full rotation — commanding {commanded:0.###} mm on {setup.Axis}. " +
                           "Watch that the workpiece runs true and does not walk along the rollers.");

        await device.JogAsync(0, commanded, 0, Math.Min(1200, SelectedMachine.MaxSpeedMmMin)).ConfigureAwait(true);
    }, "Rotary test");
}
