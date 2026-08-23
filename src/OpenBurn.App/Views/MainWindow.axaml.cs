using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenBurn.App.Controls;
using OpenBurn.App.ViewModels;

namespace OpenBurn.App.Views;

public partial class MainWindow : Window
{
    private WorkspaceView? _workspace;

    public MainWindow()
    {
        InitializeComponent();

        _workspace = this.FindControl<WorkspaceView>("Workspace");
        if (_workspace is not null)
        {
            _workspace.CursorMoved += mm => Model?.SetCursor(mm);
            _workspace.ShapePicked += shape => Model?.PickShape(shape);
            _workspace.BedDoubleClicked += mm =>
            {
                // Double-clicking the bed sends the head there. It moves the machine,
                // so it is deliberately a two-click gesture rather than a single one.
                if (Model is { IsConnected: true }) _ = Model.MoveHeadToAsync(mm.X, mm.Y);
            };
        }

        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DragDrop.SetAllowDrop(this, true);
    }

    private MainViewModel? Model => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Model is { } model) model.TopLevel = this;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnZoomBed(object? sender, RoutedEventArgs e) => _workspace?.ZoomToFitBed();

    private void OnZoomContent(object? sender, RoutedEventArgs e) => _workspace?.ZoomToFitContent();

    private void OnShowAbout(object? sender, RoutedEventArgs e)
    {
        var dialog = new AboutDialog(Model?.SelectedMachine);
        dialog.ShowDialog(this);
    }

    private void OnConsoleKeyDown(object? sender, KeyEventArgs e)
    {
        var console = Model?.Console;
        if (console is null) return;

        switch (e.Key)
        {
            case Key.Enter:
                console.SendCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                console.HistoryBack();
                e.Handled = true;
                break;
            case Key.Down:
                console.HistoryForward();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Open the four-corner calibration window on the most recent camera frame.
    /// Captures one first if nothing has arrived yet, so the button works from a
    /// cold start rather than telling the user to press something else.
    /// </summary>
    private async void OnCalibrateCamera(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;

        if (model.LastRawFrame is null) await model.CaptureBedCommand.ExecuteAsync(null);

        if (model.LastRawFrame is not { } frame)
        {
            model.Console.AppendError("No camera frame to calibrate against. Connect a camera and capture the bed first.");
            return;
        }

        var dialog = new CalibrationWindow(frame, model.SelectedMachine.BedWidthMm, model.SelectedMachine.BedHeightMm);
        await dialog.ShowDialog(this);

        if (dialog.Result is { Count: 4 } corners) model.ApplyCalibration(corners, dialog.LensK1);
    }

    private void OnAssistantKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Model is null) return;
        Model.Assistant.SendCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// Escape is the emergency stop, from anywhere in the window. In an emergency
    /// nobody hunts for a control with a mouse.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Model is null)
        {
            base.OnKeyDown(e);
            return;
        }

        // Never fire the emergency stop while the operator is typing a command.
        var typing = FocusManager?.GetFocusedElement() is TextBox;

        if (e.Key == Key.Escape && !typing)
        {
            Model.EmergencyStopCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (typing)
        {
            base.OnKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.F when e.KeyModifiers == KeyModifiers.None:
                Model.FrameCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Space:
                if (Model.IsJobRunning) Model.PauseJobCommand.Execute(null);
                else if (Model.IsJobPaused) Model.ResumeJobCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete or Key.Back:
                Model.DeleteSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.OemTilde:
                Model.ToggleConsoleCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left: Model.JogCommand.Execute("left"); e.Handled = true; break;
            case Key.Right: Model.JogCommand.Execute("right"); e.Handled = true; break;
            case Key.Up: Model.JogCommand.Execute("up"); e.Handled = true; break;
            case Key.Down: Model.JogCommand.Execute("down"); e.Handled = true; break;
        }

        base.OnKeyDown(e);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (Model is null) return;

        foreach (var path in DroppedPaths(e))
        {
            Model.ImportFile(path);
        }
    }

    private static bool HasFiles(DragEventArgs e) =>
        e.DataTransfer?.Contains(DataFormat.File) == true;

    private static IEnumerable<string> DroppedPaths(DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles();
        if (files is null) yield break;

        foreach (var file in files)
        {
            var path = (file as IStorageFile)?.TryGetLocalPath() ?? file.Path?.LocalPath;
            if (!string.IsNullOrEmpty(path)) yield return path;
        }
    }
}
