using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emberline.Devices;
using Emberline.GCode.Grbl;

namespace Emberline.App.ViewModels;

public sealed class SettingGroupViewModel
{
    public required string Name { get; init; }
    public required IReadOnlyList<GrblSettingViewModel> Settings { get; init; }
}

public sealed class SettingWarningViewModel
{
    public required string Text { get; init; }
    public required bool IsError { get; init; }
    public string ColourKey => IsError ? "StateAlarm" : "StateHold";
}

/// <summary>
/// The controller settings editor.
///
/// Two rules it exists to enforce. Nothing is written until the operator presses
/// Apply, because a settings table that writes as you type will write `$100=8`
/// on the way to `$100=80`. And the settings that can drive a machine into itself
/// need confirming separately, per the PRD — the confirmation is asked by the
/// window, so this view model stays free of dialogs.
/// </summary>
public sealed partial class ControllerSettingsViewModel : ObservableObject
{
    private readonly ILaserDevice? _device;
    private readonly Action<string> _log;
    private readonly List<GrblSettingViewModel> _all = [];

    /// <summary>Asks the operator to confirm a dangerous change. Supplied by the window.</summary>
    public Func<IReadOnlyList<GrblSettingViewModel>, Task<bool>>? ConfirmDangerous { get; set; }

    public ControllerSettingsViewModel(ILaserDevice? device, Action<string> log)
    {
        _device = device;
        _log = log;
        Load();
    }

    public ObservableCollection<SettingGroupViewModel> Groups { get; } = [];
    public ObservableCollection<SettingWarningViewModel> Warnings { get; } = [];

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private string _filter = string.Empty;

    public bool HasChanges => _all.Any(s => s.IsDirty);

    partial void OnFilterChanged(string value) => Rebuild();

    private void Load()
    {
        _all.Clear();

        var values = _device?.Settings;
        if (values is null || values.Count == 0)
        {
            Status = _device is null
                ? "Not connected. Connect to a machine to read its settings."
                : "No settings have been read yet. Press Re-read.";
            Rebuild();
            return;
        }

        foreach (var (key, value) in values.OrderBy(kv => kv.Key))
        {
            _all.Add(new GrblSettingViewModel(GrblSettings.Describe(key), value, WriteAsync));
        }

        Status = $"{_all.Count} settings read from the controller.";
        Rebuild();
        RefreshWarnings(values);
    }

    private async Task WriteAsync(int key, double value)
    {
        if (_device is null) return;
        await _device.WriteSettingAsync(key, value).ConfigureAwait(true);
        _log($"Set ${key} = {value:0.###}");
    }

    private void Rebuild()
    {
        Groups.Clear();

        var filter = Filter?.Trim();
        var matching = string.IsNullOrEmpty(filter)
            ? _all
            : _all.Where(s =>
                s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                s.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var name in GrblSettings.Groups)
        {
            var settings = matching.Where(s => s.Group == name).OrderBy(s => s.Key).ToList();
            if (settings.Count > 0) Groups.Add(new SettingGroupViewModel { Name = name, Settings = settings });
        }

        // Anything the firmware reported that is not in the GRBL 1.1 core set.
        var known = GrblSettings.Groups.ToHashSet(StringComparer.Ordinal);
        var extras = matching.Where(s => !known.Contains(s.Group)).OrderBy(s => s.Key).ToList();
        if (extras.Count > 0)
        {
            Groups.Add(new SettingGroupViewModel { Name = "Firmware-specific", Settings = extras });
        }

        OnPropertyChanged(nameof(HasChanges));
    }

    private void RefreshWarnings(IReadOnlyDictionary<int, double> values)
    {
        Warnings.Clear();
        foreach (var warning in GrblSettings.Audit(values))
        {
            Warnings.Add(new SettingWarningViewModel { Text = $"${warning.Key}: {warning.Text}", IsError = warning.IsError });
        }
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (_device is null)
        {
            Status = "Not connected.";
            return;
        }

        try
        {
            Status = "Reading…";
            var values = await _device.ReadSettingsAsync().ConfigureAwait(true);

            if (_all.Count == 0)
            {
                Load();
                return;
            }

            foreach (var setting in _all)
            {
                if (values.TryGetValue(setting.Key, out var value)) setting.UpdateFromMachine(value);
            }

            Status = $"{values.Count} settings read from the controller.";
            RefreshWarnings(values);
            OnPropertyChanged(nameof(HasChanges));
        }
        catch (Exception ex)
        {
            Status = $"Could not read settings: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ApplyAllAsync()
    {
        var dirty = _all.Where(s => s.IsDirty).ToList();
        if (dirty.Count == 0) return;

        var invalid = dirty.Where(s => !s.IsValid).ToList();
        if (invalid.Count > 0)
        {
            Status = $"These are not numbers: {string.Join(", ", invalid.Select(s => s.Label))}";
            return;
        }

        var dangerous = dirty.Where(s => s.IsDangerous).ToList();
        if (dangerous.Count > 0 && ConfirmDangerous is not null)
        {
            if (!await ConfirmDangerous(dangerous).ConfigureAwait(true))
            {
                Status = "Nothing was written.";
                return;
            }
        }

        var written = 0;
        foreach (var setting in dirty)
        {
            try
            {
                if (await setting.ApplyAsync().ConfigureAwait(true)) written++;
            }
            catch (Exception ex)
            {
                Status = $"${setting.Key} was rejected: {ex.Message}";
                OnPropertyChanged(nameof(HasChanges));
                return;
            }
        }

        Status = $"Wrote {written} setting(s) to the controller.";
        OnPropertyChanged(nameof(HasChanges));

        if (_device is not null) RefreshWarnings(_device.Settings);
    }

    [RelayCommand]
    private void RevertAll()
    {
        foreach (var setting in _all) setting.Revert();
        Status = "Reverted to the values on the machine.";
        OnPropertyChanged(nameof(HasChanges));
    }
}
