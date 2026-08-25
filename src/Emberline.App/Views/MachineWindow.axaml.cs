using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Emberline.App.ViewModels;
using Emberline.Core.Machines;

namespace Emberline.App.Views;

/// <summary>Machine profiles: add, edit, duplicate and delete.</summary>
public partial class MachineWindow : Window
{
    public MachineWindow() : this(new MachineEditorViewModel(MachineLibrary.Load(), null, _ => { }))
    {
    }

    public MachineWindow(MachineEditorViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }

    /// <summary>The profile that was selected when the window closed.</summary>
    public MachineProfile? Result => (DataContext as MachineEditorViewModel)?.Selected;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
