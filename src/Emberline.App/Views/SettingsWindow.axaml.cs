using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Emberline.App.ViewModels;

namespace Emberline.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow() : this(new ControllerSettingsViewModel(null, _ => { }))
    {
    }

    public SettingsWindow(ControllerSettingsViewModel model)
    {
        InitializeComponent();
        DataContext = model;

        // The confirmation lives here rather than in the view model, so the view
        // model stays testable and free of dialogs.
        model.ConfirmDangerous = ConfirmDangerousAsync;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async Task<bool> ConfirmDangerousAsync(IReadOnlyList<GrblSettingViewModel> settings)
    {
        var dialog = new ConfirmDialog(
            "Write these settings?",
            "These change how the machine physically moves. Read what each one does before agreeing — " +
            "getting one wrong can drive the gantry into the end of its travel on the next homing cycle.",
            string.Join("\n\n", settings.Select(s => $"{s.Label} {s.Name} → {s.EditedText}\n{s.DangerNote}")),
            confirmText: "Write them");

        await dialog.ShowDialog(this);
        return dialog.Confirmed;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
