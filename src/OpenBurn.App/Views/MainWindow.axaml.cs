using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenBurn.App.Controls;
using OpenBurn.App.ViewModels;
using OpenBurn.Core.Machines;

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
            _workspace.SelectionRequested += (shapes, additive) => Model?.SetSelection(shapes, additive);

            // The canvas mutates shapes directly during a drag; these three hooks
            // are how the view model learns to snapshot for undo and to regenerate
            // the toolpath afterwards.
            _workspace.EditBegan += name => Model?.BeginCanvasEdit(name);
            _workspace.EditChanged += () => Model?.CanvasEditChanged();
            _workspace.EditEnded += () => Model?.EndCanvasEdit();
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
        var dialog = new AboutDialog(Model?.SelectedMachine, Model?.LoadedPlugins);
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

    private async void OnShowSettings(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;

        // Read fresh values first if the machine is connected but nothing has been
        // read yet, so the editor never opens showing an empty table.
        if (model.IsConnected && model.MachineSettingCount == 0)
        {
            await model.ReadSettingsCommand.ExecuteAsync(null);
        }

        var dialog = new SettingsWindow(model.CreateSettingsEditor());
        await dialog.ShowDialog(this);
    }

    private async void OnEditMachines(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;

        var editor = new MachineEditorViewModel(model.Machines, model.SelectedMachine, text => model.Console.AppendInfo(text));
        var dialog = new MachineWindow(editor);
        await dialog.ShowDialog(this);

        model.ReloadMachines(dialog.Result?.Id);
    }

    /// <summary>
    /// Trace the selected image, or — if nothing traceable is selected — ask for a
    /// file and trace that. Wanting to trace something is not the same as wanting
    /// it on the bed as a raster first, and making people import before they can
    /// trace is a step with no purpose.
    /// </summary>
    private async void OnTraceImage(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;

        var editor = model.CreateTraceEditor();

        if (editor is null)
        {
            var path = await PickImageAsync();
            if (path is null)
            {
                model.Console.AppendInfo("Nothing to trace. Select an imported image, or pick an image file.");
                return;
            }

            editor = model.CreateTraceEditor(path);
            if (editor is null) return;
        }

        var dialog = new TraceWindow(editor);
        await dialog.ShowDialog(this);

        if (dialog.Result is { } traced) model.ApplyTrace(editor, traced);
    }

    private async Task<string?> PickImageAsync()
    {
        if (StorageProvider is not { } storage) return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Trace an image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"],
                },
            ],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async void OnShowJobLibrary(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        var dialog = new JobLibraryWindow(model.JobLibrary);
        await dialog.ShowDialog(this);
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

        // Command on macOS, Control elsewhere. Avalonia reports the platform's own
        // modifier, so checking both keeps one code path.
        var accel = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (accel)
        {
            switch (e.Key)
            {
                case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    Model.RedoEditCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Z:
                    Model.UndoEditCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Y:
                    Model.RedoEditCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.A:
                    Model.SelectAllCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.D:
                    Model.DuplicateSelectedCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.G when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    Model.UngroupSelectionCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.G:
                    Model.GroupSelectionCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.O:
                    Model.OpenCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.N:
                    Model.NewDocumentCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }

        // Arrow keys nudge the selection when something is selected, and jog the
        // machine when nothing is. Shift makes both coarse.
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down && Model.HasSelection)
        {
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10.0 : 1.0;
            var (dx, dy) = e.Key switch
            {
                Key.Left => (-step, 0.0),
                Key.Right => (step, 0.0),
                Key.Up => (0.0, step),
                _ => (0.0, -step),
            };
            Model.NudgeSelection(dx, dy);
            e.Handled = true;
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
