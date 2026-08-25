using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Emberline.App.ViewModels;

/// <summary>Which group of settings the right-hand panel is showing.</summary>
public enum InspectorTab
{
    Machine,
    Design,
    Object,
    Job,
}

/// <summary>
/// The right panel, grouped by what you are doing rather than stacked in one list.
///
/// All of it used to be a single column: connection, jog, camera, rotary, material,
/// layers, operation, fill, selection, align, array, text, job. Fourteen sections
/// deep, so anything below the fold was a scroll away and nothing was ever in view
/// beside the thing it related to.
///
/// The grouping is by question — what is the machine doing, what will be burned,
/// what is selected, what is about to run — and the panel follows the work: select
/// something on the canvas and it turns to Object, deselect and it goes back. It
/// only ever moves on a real change of context, never on its own, because a panel
/// that reorganises itself while you are reaching for a control is worse than one
/// that scrolls.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty]
    private InspectorTab _inspectorTab = InspectorTab.Design;

    public bool IsMachineTab => InspectorTab == InspectorTab.Machine;
    public bool IsDesignTab => InspectorTab == InspectorTab.Design;
    public bool IsObjectTab => InspectorTab == InspectorTab.Object;
    public bool IsJobTab => InspectorTab == InspectorTab.Job;

    partial void OnInspectorTabChanged(InspectorTab value)
    {
        OnPropertyChanged(nameof(IsMachineTab));
        OnPropertyChanged(nameof(IsDesignTab));
        OnPropertyChanged(nameof(IsObjectTab));
        OnPropertyChanged(nameof(IsJobTab));
    }

    [RelayCommand]
    private void SelectInspectorTab(string? name)
    {
        if (Enum.TryParse<InspectorTab>(name, ignoreCase: true, out var tab)) InspectorTab = tab;
    }

    /// <summary>
    /// Follow the selection. Picking a shape means the next thing wanted is almost
    /// always one of its own settings; clearing the selection means it is not.
    /// Anything the operator chose by hand stays chosen.
    /// </summary>
    private void FollowSelectionContext(bool hadSelection, bool hasSelection)
    {
        if (hasSelection && !hadSelection) InspectorTab = InspectorTab.Object;
        else if (!hasSelection && hadSelection && InspectorTab == InspectorTab.Object) InspectorTab = InspectorTab.Design;
    }

    /// <summary>A job starting is a change of context too — and the one worth watching.</summary>
    public void FollowJobContext(bool running)
    {
        if (running && InspectorTab != InspectorTab.Machine) InspectorTab = InspectorTab.Job;
    }
}
