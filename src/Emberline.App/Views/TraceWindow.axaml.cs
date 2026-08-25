using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Emberline.App.ViewModels;
using Emberline.Core.Documents;

namespace Emberline.App.Views;

/// <summary>Bitmap to paths, with a live preview of what the settings produce.</summary>
public partial class TraceWindow : Window
{
    public TraceWindow() : this(new TraceViewModel(RasterImage.Create(64, 64), "Untitled", 64, 64))
    {
    }

    public TraceWindow(TraceViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }

    /// <summary>The traced shape, if the operator accepted it.</summary>
    public PathShape? Result { get; private set; }

    private TraceViewModel? Model => DataContext as TraceViewModel;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        // Settle any pending debounce first, so what is added is what is on screen.
        Model?.Retrace();
        Result = Model?.BuildShape();
        Close();
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
