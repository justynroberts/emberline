using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenBurn.Core.Machines;
using OpenBurn.Materials;

namespace OpenBurn.App.ViewModels;

/// <summary>One step of the wizard: what it is called and what it is for.</summary>
public sealed record WizardStep(string Title, string Question);

/// <summary>
/// A guided run through a first job.
///
/// Every step here is reachable from the main window already; the wizard exists
/// because knowing they exist, and in what order, is the part that has to be
/// learned. It sets up the same objects the panels do — there is no separate
/// wizard state to get out of step with the document — so anything done here can
/// be undone or adjusted afterwards in the ordinary way.
///
/// It never starts the job. The last step hands back to the main window with the
/// machine ready and the artwork placed, because pressing Start is a decision to
/// take while looking at the bed, not at a dialog.
/// </summary>
public sealed partial class WizardViewModel : ObservableObject
{
    private readonly MainViewModel _shell;

    public WizardViewModel(MainViewModel shell)
    {
        _shell = shell;
        _selectedMachine = shell.SelectedMachine;
    }

    public IReadOnlyList<WizardStep> Steps { get; } =
    [
        new("Machine", "Which laser are you using, and is it connected?"),
        new("Material", "What is on the bed?"),
        new("Artwork", "What are you burning onto it?"),
        new("Settings", "How hard, and how fast?"),
        new("Check", "Anything wrong before you run it?"),
    ];

    [ObservableProperty]
    private int _stepIndex;

    public WizardStep Step => Steps[Math.Clamp(StepIndex, 0, Steps.Count - 1)];

    public string StepNumber => $"Step {StepIndex + 1} of {Steps.Count}";

    public bool IsMachineStep => StepIndex == 0;
    public bool IsMaterialStep => StepIndex == 1;
    public bool IsArtworkStep => StepIndex == 2;
    public bool IsSettingsStep => StepIndex == 3;
    public bool IsCheckStep => StepIndex == 4;

    public bool CanGoBack => StepIndex > 0;
    public bool IsLastStep => StepIndex == Steps.Count - 1;

    /// <summary>
    /// What is missing, or empty when the step is satisfied. Shown rather than
    /// used to disable Next: a wizard that greys out its own forward button
    /// without saying why is the thing everybody hates about wizards.
    /// </summary>
    public string StepBlocker => StepIndex switch
    {
        0 when !_shell.IsConnected => "Not connected yet. Connect the machine, or choose Virtual to practise without one.",
        2 when _shell.Design.Shapes.Count == 0 => "Nothing to burn yet. Open a file, or add some text.",
        _ => "",
    };

    public bool HasBlocker => StepBlocker.Length > 0;

    partial void OnStepIndexChanged(int value) => RaiseAll();

    [RelayCommand]
    private void Next()
    {
        if (StepIndex < Steps.Count - 1) StepIndex++;
    }

    [RelayCommand]
    private void Back()
    {
        if (StepIndex > 0) StepIndex--;
    }

    // ------------------------------------------------------------ 1. machine

    public IReadOnlyList<MachineProfile> Machines => _shell.Machines.Profiles;

    private MachineProfile _selectedMachine;

    public MachineProfile SelectedMachine
    {
        get => _selectedMachine;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedMachine)) return;
            _selectedMachine = value;
            _shell.SelectedMachine = value;
            OnPropertyChanged();
            RaiseAll();
        }
    }

    public string MachineSummary =>
        $"{SelectedMachine.DisplayName} — {SelectedMachine.BedWidthMm:0} × {SelectedMachine.BedHeightMm:0} mm bed, {SelectedMachine.LaserWatts:0.#} W";

    public bool IsConnected => _shell.IsConnected;

    public string ConnectionSummary => _shell.StatusText;

    public string NetworkAddress
    {
        get => _shell.NetworkAddress;
        set { _shell.NetworkAddress = value; OnPropertyChanged(); }
    }

    [RelayCommand]
    private async Task ConnectUsbAsync()
    {
        await _shell.ConnectAsync(ConnectionKind.Serial);
        RaiseAll();
    }

    [RelayCommand]
    private async Task ConnectNetworkAsync()
    {
        await _shell.ConnectAsync(ConnectionKind.Tcp);
        RaiseAll();
    }

    [RelayCommand]
    private async Task ConnectVirtualAsync()
    {
        await _shell.ConnectAsync(ConnectionKind.Virtual);
        RaiseAll();
    }

    // ----------------------------------------------------------- 2. material

    public IReadOnlyList<WorkpiecePreset> WorkpiecePresets => _shell.WorkpiecePresets;

    public WorkpiecePreset? SelectedWorkpiecePreset
    {
        get => _shell.SelectedWorkpiecePreset;
        set { _shell.SelectedWorkpiecePreset = value; RaiseAll(); }
    }

    public bool HasWorkpiece => _shell.HasWorkpiece;

    public string WorkpieceSummary => _shell.WorkpieceSummary;

    public double WorkpieceWidthMm
    {
        get => _shell.WorkpieceWidthMm;
        set { _shell.WorkpieceWidthMm = value; RaiseAll(); }
    }

    public double WorkpieceHeightMm
    {
        get => _shell.WorkpieceHeightMm;
        set { _shell.WorkpieceHeightMm = value; RaiseAll(); }
    }

    public bool WorkpieceIsRound
    {
        get => _shell.WorkpieceIsRound;
        set { _shell.WorkpieceIsRound = value; RaiseAll(); }
    }

    [RelayCommand]
    private void UseCustomWorkpiece()
    {
        _shell.UseCustomWorkpieceCommand.Execute(null);
        RaiseAll();
    }

    // ------------------------------------------------------------ 3. artwork

    public int ShapeCount => _shell.Design.Shapes.Count;

    public string ArtworkSummary => ShapeCount switch
    {
        0 => "Nothing added yet.",
        1 => "One shape on the bed.",
        _ => $"{ShapeCount} shapes on the bed.",
    };

    [ObservableProperty]
    private string _text = "";

    [RelayCommand]
    private async Task OpenArtworkAsync()
    {
        await _shell.OpenCommand.ExecuteAsync(null);
        RaiseAll();
    }

    [RelayCommand]
    private void AddText()
    {
        if (string.IsNullOrWhiteSpace(Text)) return;
        _shell.TextInput = Text;
        _shell.AddTextCommand.Execute(null);
        Text = "";
        RaiseAll();
    }

    [RelayCommand]
    private void CentreOnWorkpiece()
    {
        _shell.CentreArtworkOnWorkpieceCommand.Execute(null);
        RaiseAll();
    }

    // ----------------------------------------------------------- 4. settings

    public IEnumerable<MaterialProfile> Materials => _shell.AvailableMaterials;

    public MaterialProfile? SelectedMaterial
    {
        get => _shell.SelectedMaterial;
        set { _shell.SelectedMaterial = value; RaiseAll(); }
    }

    public string MaterialNotes => _shell.MaterialNotes;

    public string MaterialHazard => _shell.MaterialHazard;

    public bool HasMaterialHazard => _shell.HasMaterialHazard;

    [RelayCommand]
    private void ApplyMaterial()
    {
        _shell.ApplyMaterialCommand.Execute(null);
        RaiseAll();
    }

    // -------------------------------------------------------------- 5. check

    public string EstimateText => _shell.EstimateText;

    public string JobSizeText => _shell.JobSizeText;

    public string LineCountText => _shell.LineCountText;

    public IEnumerable<string> IssueLines =>
        _shell.Issues.Select(i => $"{i.Title} — {i.Detail}");

    public bool HasIssues => _shell.Issues.Count > 0;

    public bool CanStart => _shell.CanStartJob;

    public string ReadyText => CanStart
        ? "Ready. Close this and press Frame to trace the outline at pointer power before you burn anything — it is the last chance to notice the artwork is in the wrong place."
        : _shell.StartHint;

    /// <summary>Regenerate and refresh, so the check step is looking at the real job.</summary>
    public void Refresh()
    {
        _shell.RegenerateNow();
        RaiseAll();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(StepNumber));
        OnPropertyChanged(nameof(IsMachineStep));
        OnPropertyChanged(nameof(IsMaterialStep));
        OnPropertyChanged(nameof(IsArtworkStep));
        OnPropertyChanged(nameof(IsSettingsStep));
        OnPropertyChanged(nameof(IsCheckStep));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(StepBlocker));
        OnPropertyChanged(nameof(HasBlocker));

        OnPropertyChanged(nameof(MachineSummary));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionSummary));

        OnPropertyChanged(nameof(HasWorkpiece));
        OnPropertyChanged(nameof(WorkpieceSummary));
        OnPropertyChanged(nameof(WorkpieceWidthMm));
        OnPropertyChanged(nameof(WorkpieceHeightMm));
        OnPropertyChanged(nameof(WorkpieceIsRound));
        OnPropertyChanged(nameof(SelectedWorkpiecePreset));

        OnPropertyChanged(nameof(ShapeCount));
        OnPropertyChanged(nameof(ArtworkSummary));

        OnPropertyChanged(nameof(SelectedMaterial));
        OnPropertyChanged(nameof(MaterialNotes));
        OnPropertyChanged(nameof(MaterialHazard));
        OnPropertyChanged(nameof(HasMaterialHazard));

        OnPropertyChanged(nameof(EstimateText));
        OnPropertyChanged(nameof(JobSizeText));
        OnPropertyChanged(nameof(LineCountText));
        OnPropertyChanged(nameof(IssueLines));
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ReadyText));
    }
}
