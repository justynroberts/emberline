using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenBurn.GCode.Grbl;

namespace OpenBurn.App.ViewModels;

/// <summary>
/// One row in the controller settings editor.
///
/// Keeps the value the machine reported separately from the value being edited,
/// so the editor can show what is dirty and can revert. Writing happens through
/// the shell, never from here — a settings row must not be able to talk to a
/// laser directly.
/// </summary>
public sealed partial class GrblSettingViewModel : ObservableObject
{
    private readonly Func<int, double, Task> _write;

    public GrblSettingViewModel(GrblSettingDef definition, double value, Func<int, double, Task> write)
    {
        Definition = definition;
        _machineValue = value;
        _editedText = Format(value);
        _write = write;
    }

    public GrblSettingDef Definition { get; }

    public int Key => Definition.Key;
    public string Name => Definition.Name;
    public string Unit => Definition.Unit;
    public string Group => Definition.Group;
    public string Description => Definition.Description;
    public string Label => $"${Definition.Key}";

    public bool IsDangerous => Definition.IsDangerous;
    public string? DangerNote => Definition.DangerNote;

    private double _machineValue;

    [ObservableProperty]
    private string _editedText;

    /// <summary>What the machine currently has, formatted for display.</summary>
    public string MachineText => Format(_machineValue);

    /// <summary>A human reading of the value — "Enabled", "X, Y", and so on.</summary>
    public string Interpretation => GrblSettings.Format(Definition, _machineValue);

    /// <summary>
    /// Whether this row differs from what the machine has.
    ///
    /// Text that does not parse counts as dirty. Treating it as unchanged means
    /// typing nonsense and pressing Apply does nothing at all and says nothing
    /// about why, which is the worst of both.
    /// </summary>
    public bool IsDirty => TryParse(EditedText, out var parsed)
        ? Math.Abs(parsed - _machineValue) > 1e-9
        : !string.Equals(EditedText?.Trim(), MachineText, StringComparison.Ordinal);

    public bool IsValid => TryParse(EditedText, out _);

    partial void OnEditedTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsValid));
    }

    /// <summary>Push the edited value to the machine. Returns false if it does not parse.</summary>
    public async Task<bool> ApplyAsync()
    {
        if (!TryParse(EditedText, out var parsed)) return false;
        if (Math.Abs(parsed - _machineValue) < 1e-9) return true;

        await _write(Definition.Key, parsed).ConfigureAwait(true);

        _machineValue = parsed;
        EditedText = Format(parsed);

        OnPropertyChanged(nameof(MachineText));
        OnPropertyChanged(nameof(Interpretation));
        OnPropertyChanged(nameof(IsDirty));
        return true;
    }

    public void Revert()
    {
        EditedText = Format(_machineValue);
        OnPropertyChanged(nameof(IsDirty));
    }

    public void UpdateFromMachine(double value)
    {
        _machineValue = value;
        EditedText = Format(value);

        OnPropertyChanged(nameof(MachineText));
        OnPropertyChanged(nameof(Interpretation));
        OnPropertyChanged(nameof(IsDirty));
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string Format(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e9
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);
}
