using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Emberline.Core.Machines;

namespace Emberline.App.ViewModels;

/// <summary>One capability, as a checkbox.</summary>
public sealed partial class CapabilityToggle : ObservableObject
{
    public required MachineCapabilities Flag { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }

    [ObservableProperty]
    private bool _isSet;
}

/// <summary>
/// Add, edit, duplicate and delete machine profiles.
///
/// Profiles are JSON files, so this is a convenience rather than the only way in —
/// which is the point of the hardware-agnostic principle. Bundled profiles are
/// never overwritten: editing one produces a user copy, so an application update
/// cannot silently revert somebody's carefully measured bed size.
/// </summary>
public sealed partial class MachineEditorViewModel : ObservableObject
{
    private readonly MachineLibrary _library;
    private readonly Action<string> _log;

    public MachineEditorViewModel(MachineLibrary library, MachineProfile? initial, Action<string> log)
    {
        _library = library;
        _log = log;

        foreach (var profile in library.Profiles) Machines.Add(profile);

        Capabilities =
        [
            new CapabilityToggle { Flag = MachineCapabilities.Homing, Name = "Homing", Description = "Has limit switches and supports $H." },
            new CapabilityToggle { Flag = MachineCapabilities.SoftLimits, Name = "Soft limits", Description = "Refuses moves that would leave the bed." },
            new CapabilityToggle { Flag = MachineCapabilities.LaserMode, Name = "Laser mode", Description = "Supports GRBL $32 dynamic power. Essential for photo engraving." },
            new CapabilityToggle { Flag = MachineCapabilities.AirAssist, Name = "Air assist", Description = "Air is wired to an M-code the controller can switch." },
            new CapabilityToggle { Flag = MachineCapabilities.Framing, Name = "Framing", Description = "Can trace an outline at low power." },
            new CapabilityToggle { Flag = MachineCapabilities.Rotary, Name = "Rotary", Description = "A rotary attachment can be fitted." },
            new CapabilityToggle { Flag = MachineCapabilities.ZAxis, Name = "Z axis", Description = "Has a controllable Z, not just a manual focus." },
            new CapabilityToggle { Flag = MachineCapabilities.Camera, Name = "Camera", Description = "A bed camera is fitted or can be." },
        ];

        Selected = initial ?? library.Profiles.FirstOrDefault() ?? MachineProfile.GenericGrbl();
    }

    public ObservableCollection<MachineProfile> Machines { get; } = [];
    public IReadOnlyList<CapabilityToggle> Capabilities { get; }
    public IReadOnlyList<BedOrigin> Origins { get; } = Enum.GetValues<BedOrigin>();
    public IReadOnlyList<int> BaudRates { get; } = [9600, 19200, 38400, 57600, 115200, 230400, 250000];

    [ObservableProperty]
    private MachineProfile _selected = MachineProfile.GenericGrbl();

    [ObservableProperty]
    private string _status = string.Empty;

    // Editable fields, kept apart from the record so nothing is written until Save.
    [ObservableProperty] private string _displayName = string.Empty;
    [ObservableProperty] private string _manufacturer = string.Empty;
    [ObservableProperty] private string _model = string.Empty;
    [ObservableProperty] private double _laserWatts;
    [ObservableProperty] private double _bedWidthMm;
    [ObservableProperty] private double _bedHeightMm;
    [ObservableProperty] private BedOrigin _origin;
    [ObservableProperty] private double _maxSpeedMmMin;
    [ObservableProperty] private double _travelSpeedMmMin;
    [ObservableProperty] private int _maxSpindleValue;
    [ObservableProperty] private double _accelerationX;
    [ObservableProperty] private double _accelerationY;
    [ObservableProperty] private double _junctionDeviation;
    [ObservableProperty] private int _baudRate;
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private int _tcpPort;
    [ObservableProperty] private int _webSocketPort;
    [ObservableProperty] private string _driverId = "grbl";

    /// <summary>Bundled profiles are read-only; editing one saves a copy instead.</summary>
    public bool IsBundled => !File.Exists(UserPathFor(Selected.Id));

    public string EditNote => IsBundled
        ? "This profile ships with Emberline. Saving creates your own copy, so an update cannot overwrite it."
        : "This is your profile. Saving overwrites it.";

    /// <summary>The profile currently in the form, ready to save.</summary>
    public MachineProfile Build() => Selected with
    {
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? Selected.DisplayName : DisplayName.Trim(),
        Manufacturer = Manufacturer.Trim(),
        Model = Model.Trim(),
        LaserWatts = Math.Max(0.1, LaserWatts),
        BedWidthMm = Math.Max(1, BedWidthMm),
        BedHeightMm = Math.Max(1, BedHeightMm),
        Origin = Origin,
        MaxSpeedMmMin = Math.Max(1, MaxSpeedMmMin),
        TravelSpeedMmMin = Math.Max(1, TravelSpeedMmMin),
        MaxSpindleValue = Math.Max(1, MaxSpindleValue),
        AccelerationX = Math.Max(1, AccelerationX),
        AccelerationY = Math.Max(1, AccelerationY),
        JunctionDeviation = Math.Clamp(JunctionDeviation, 0.001, 1),
        BaudRate = BaudRate,
        Host = string.IsNullOrWhiteSpace(Host) ? null : Host.Trim(),
        TcpPort = TcpPort <= 0 ? 23 : TcpPort,
        WebSocketPort = WebSocketPort <= 0 ? 81 : WebSocketPort,
        DriverId = string.IsNullOrWhiteSpace(DriverId) ? "grbl" : DriverId.Trim(),
        Capabilities = Capabilities
            .Where(c => c.IsSet)
            .Aggregate(MachineCapabilities.None, (all, c) => all | c.Flag),
    };

    partial void OnSelectedChanged(MachineProfile value) => LoadForm(value);

    private void LoadForm(MachineProfile profile)
    {
        DisplayName = profile.DisplayName;
        Manufacturer = profile.Manufacturer;
        Model = profile.Model;
        LaserWatts = profile.LaserWatts;
        BedWidthMm = profile.BedWidthMm;
        BedHeightMm = profile.BedHeightMm;
        Origin = profile.Origin;
        MaxSpeedMmMin = profile.MaxSpeedMmMin;
        TravelSpeedMmMin = profile.TravelSpeedMmMin;
        MaxSpindleValue = profile.MaxSpindleValue;
        AccelerationX = profile.AccelerationX;
        AccelerationY = profile.AccelerationY;
        JunctionDeviation = profile.JunctionDeviation;
        BaudRate = profile.BaudRate;
        Host = profile.Host ?? string.Empty;
        TcpPort = profile.TcpPort;
        WebSocketPort = profile.WebSocketPort;
        DriverId = profile.DriverId;

        foreach (var capability in Capabilities) capability.IsSet = profile.Capabilities.HasFlag(capability.Flag);

        Status = string.Empty;
        OnPropertyChanged(nameof(IsBundled));
        OnPropertyChanged(nameof(EditNote));
    }

    [RelayCommand]
    private void Save()
    {
        var profile = Build();

        // Never overwrite a bundled profile: an update would silently revert it.
        if (IsBundled) profile = profile with { Id = UniqueId(profile) };

        _library.Save(profile);
        RefreshList(profile.Id);

        Status = $"Saved to {UserPathFor(profile.Id)}";
        _log($"Machine profile saved: {profile.DisplayName}");
    }

    [RelayCommand]
    private void Duplicate()
    {
        var profile = Build() with
        {
            Id = UniqueId(Build()),
            DisplayName = Build().DisplayName + " copy",
        };

        _library.Save(profile);
        RefreshList(profile.Id);
        Status = "Duplicated. Edit the copy and save.";
    }

    [RelayCommand]
    private void AddNew()
    {
        var profile = MachineProfile.GenericGrbl() with
        {
            Id = $"machine-{Guid.NewGuid().ToString("N")[..8]}",
            DisplayName = "New machine",
            Manufacturer = "Unknown",
            Model = "GRBL laser",
        };

        _library.Save(profile);
        RefreshList(profile.Id);
        Status = "New profile created. Fill in the bed size and wattage, then save.";
    }

    [RelayCommand]
    private void Delete()
    {
        if (IsBundled)
        {
            Status = "Bundled profiles cannot be deleted. Delete the file in devices/ instead.";
            return;
        }

        if (Machines.Count <= 1)
        {
            Status = "There has to be at least one machine.";
            return;
        }

        var doomed = Selected.Id;
        _library.Delete(doomed);
        RefreshList(_library.Profiles.FirstOrDefault()?.Id);
        Status = "Deleted.";
        _log($"Machine profile deleted: {doomed}");
    }

    private void RefreshList(string? selectId)
    {
        Machines.Clear();
        foreach (var profile in _library.Profiles) Machines.Add(profile);

        Selected = Machines.FirstOrDefault(m => m.Id == selectId) ?? Machines.FirstOrDefault() ?? MachineProfile.GenericGrbl();
        OnPropertyChanged(nameof(IsBundled));
        OnPropertyChanged(nameof(EditNote));
    }

    private string UniqueId(MachineProfile profile)
    {
        var basis = $"{profile.Manufacturer}-{profile.Model}"
            .ToLowerInvariant()
            .Replace(' ', '-');

        if (string.IsNullOrWhiteSpace(basis.Trim('-'))) basis = "machine";

        var candidate = basis;
        var suffix = 2;
        while (_library.Find(candidate) is not null && candidate != profile.Id)
        {
            candidate = $"{basis}-{suffix++}";
        }
        return candidate;
    }

    private static string UserPathFor(string id) =>
        Path.Combine(Core.Storage.AppPaths.Machines, $"{Sanitise(id)}.json");

    private static string Sanitise(string id) =>
        string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
}
