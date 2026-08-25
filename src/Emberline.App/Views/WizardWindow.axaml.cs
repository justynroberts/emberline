using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Emberline.App.ViewModels;
using Emberline.Core;

namespace Emberline.App.Views;

/// <summary>
/// A guided run through a first job.
///
/// It deliberately stops short of starting one. The last step hands back to the
/// workspace with the machine ready and the artwork placed, because pressing
/// Start is a decision to take while looking at the bed, not at a dialog.
/// </summary>
public partial class WizardWindow : Window
{
    /// <summary>The XAML previewer needs a parameterless constructor.</summary>
    public WizardWindow() : this(new WizardViewModel(new MainViewModel(Core.Storage.AppSettings.Default)))
    {
    }

    public WizardWindow(WizardViewModel model)
    {
        InitializeComponent();
        DataContext = model;

        // The last step reports on the real job, so make sure there is one.
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WizardViewModel.StepIndex) && model.IsCheckStep) model.Refresh();
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
