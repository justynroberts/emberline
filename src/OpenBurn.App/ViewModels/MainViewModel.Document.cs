using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenBurn.Devices;
using CommunityToolkit.Mvvm.Input;
using OpenBurn.Cam.Import;
using OpenBurn.Cam.Trace;
using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;
using OpenBurn.Materials;

namespace OpenBurn.App.ViewModels;

/// <summary>Document commands: files, shapes, layers, materials.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Set by the window so file dialogs have somewhere to attach.</summary>
    public TopLevel? TopLevel { get; set; }

    // ------------------------------------------------------------------ file

    [RelayCommand]
    private void NewDocument()
    {
        Design = Core.Documents.Design.CreateDefault();
        Selection.Clear();
        Undo.Clear();
        RebuildLayers();
        QueueRegenerate();
        Console.AppendInfo("New document.");
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (TopLevel?.StorageProvider is not { } storage) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open artwork",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("All supported")
                {
                    Patterns = ["*.svg", "*.dxf", "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp",
                                "*.nc", "*.gcode", "*.gc", "*.tap", "*.ngc"],
                },
                new FilePickerFileType("Vector") { Patterns = ["*.svg", "*.dxf"] },
                new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"] },
                new FilePickerFileType("G-code") { Patterns = ["*.nc", "*.gcode", "*.gc", "*.tap", "*.ngc"] },
            ],
        }).ConfigureAwait(true);

        foreach (var file in files)
        {
            var path = file.TryGetLocalPath();
            if (path is not null) ImportFile(path);
        }
    }

    /// <summary>Import one file, choosing the right pipeline from its extension.</summary>
    public void ImportFile(string path)
    {
        try
        {
            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var result = SvgImporter.Import(File.ReadAllText(path));
                var shape = new PathShape(result.Paths) { Name = Path.GetFileNameWithoutExtension(path) };
                PlaceOnBed(shape);
                Design.AddShape(shape, SelectedLayer?.Layer);

                foreach (var warning in result.Warnings) Console.AppendInfo(warning);
                Console.AppendInfo($"Imported {result.Paths.Count} path(s) from {Path.GetFileName(path)} " +
                                   $"at {result.WidthMm:0.#} × {result.HeightMm:0.#} mm.");
            }
            else if (DxfImporter.IsSupported(path))
            {
                var result = DxfImporter.Load(path);
                var shape = new PathShape(result.Paths) { Name = Path.GetFileNameWithoutExtension(path) };
                PlaceOnBed(shape);
                Design.AddShape(shape, SelectedLayer?.Layer);

                foreach (var warning in result.Warnings) Console.AppendInfo(warning);
                Console.AppendInfo($"Imported {result.Paths.Count} entit{(result.Paths.Count == 1 ? "y" : "ies")} " +
                                   $"from {Path.GetFileName(path)} — {result.WidthMm:0.#} × {result.HeightMm:0.#} mm ({result.Units}).");
            }
            else if (ImageImporter.IsSupported(path))
            {
                var shape = ImageImporter.LoadAsShape(path);
                PlaceOnBed(shape);
                Design.AddShape(shape, SelectedLayer?.Layer);
                Console.AppendInfo($"Imported {Path.GetFileName(path)} " +
                                   $"({shape.Source.Width}×{shape.Source.Height} px) at {shape.WidthMm:0.#} × {shape.HeightMm:0.#} mm.");
            }
            else if (GcodeImporter.IsSupported(path))
            {
                var result = GcodeImporter.Load(path, SelectedMachine.MaxSpindleValue);
                var shape = GcodeImporter.ToPreviewShape(result.Toolpath, Path.GetFileNameWithoutExtension(path));
                Design.AddShape(shape, SelectedLayer?.Layer);

                foreach (var warning in result.Warnings) Console.AppendInfo(warning);
                Console.AppendInfo($"Imported {result.Lines.Count:N0} lines of G-code. " +
                                   "It is shown for reference; regenerating the job will not reproduce it byte for byte.");
            }
            else if (!TryPluginImport(path))
            {
                Console.AppendError($"OpenBurn does not know how to open {Path.GetExtension(path)} files.");
                return;
            }

            Settings = Settings.WithRecentFile(path);
            Selection.Clear();
            if (Design.Shapes.LastOrDefault() is { } added) Selection.Add(added);
            RaiseSelectionChanged();
            QueueRegenerate();
        }
        catch (Exception ex)
        {
            Console.AppendError($"Could not import {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>Centre new artwork on the bed rather than dropping it at the origin.</summary>
    private void PlaceOnBed(Shape shape)
    {
        var b = shape.Bounds;
        if (b.IsEmpty) return;

        var x = (SelectedMachine.BedWidthMm - b.Width) / 2;
        var y = (SelectedMachine.BedHeightMm - b.Height) / 2;
        shape.MoveTo(new Vec2(Math.Max(0, x), Math.Max(0, y)));
    }

    [RelayCommand]
    private async Task ExportGcodeAsync()
    {
        RegenerateNow();
        if (TopLevel?.StorageProvider is not { } storage || _cam is null) return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export G-code",
            SuggestedFileName = $"{Design.Name}.nc",
            DefaultExtension = "nc",
            FileTypeChoices = [new FilePickerFileType("G-code") { Patterns = ["*.nc", "*.gcode"] }],
        }).ConfigureAwait(true);

        var path = file?.TryGetLocalPath();
        if (path is null) return;

        try
        {
            await File.WriteAllLinesAsync(path, _cam.Job.Lines).ConfigureAwait(true);
            Console.AppendInfo($"Exported {_cam.Job.LineCount:N0} lines to {Path.GetFileName(path)}.");
        }
        catch (Exception ex)
        {
            Console.AppendError($"Could not export: {ex.Message}");
        }
    }

    // ----------------------------------------------------------------- shapes

    [RelayCommand]
    private void AddRectangle()
    {
        var shape = PathShape.Rectangle(60, 40);
        PlaceOnBed(shape);

        EditDocument("Add rectangle", () =>
        {
            Design.AddShape(shape, SelectedLayer?.Layer);
            Selection.Clear();
            Selection.Add(shape);
        });
    }

    [RelayCommand]
    private void AddCircle()
    {
        var shape = PathShape.Ellipse(25, 25);
        PlaceOnBed(shape);

        EditDocument("Add circle", () =>
        {
            Design.AddShape(shape, SelectedLayer?.Layer);
            Selection.Clear();
            Selection.Add(shape);
        });
    }

    [RelayCommand]
    private void AddPolygon()
    {
        var shape = PathShape.Polygon(6, 25, 90);
        PlaceOnBed(shape);

        EditDocument("Add polygon", () =>
        {
            Design.AddShape(shape, SelectedLayer?.Layer);
            Selection.Clear();
            Selection.Add(shape);
        });
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (Selection.Count == 0) return;

        var doomed = Selection.Where(s => !s.Locked).ToList();
        if (doomed.Count == 0)
        {
            Console.AppendInfo("Everything selected is locked. Unlock it first.");
            return;
        }

        EditDocument($"Delete {doomed.Count} shape(s)", () =>
        {
            foreach (var shape in doomed) Design.RemoveShape(shape);
            Selection.Clear();
        });
    }

    [RelayCommand]
    private void DuplicateSelected()
    {
        if (Selection.Count == 0) return;

        var copies = Selection.Select(s =>
        {
            var copy = s.Clone();
            copy.Translate(new Vec2(5, -5));
            return copy;
        }).ToList();

        EditDocument($"Duplicate {copies.Count} shape(s)", () =>
        {
            foreach (var copy in copies) Design.Shapes.Add(copy);
            Selection.Clear();
            foreach (var copy in copies) Selection.Add(copy);
        });
    }

    [RelayCommand]
    private void CentreSelected()
    {
        if (Selection.Count == 0) return;

        EditSelection("Centre", () =>
        {
            var bounds = Core.Documents.Arrange.Bounds(Selection);
            if (bounds.IsEmpty) return;

            var delta = new Vec2(
                (SelectedMachine.BedWidthMm - bounds.Width) / 2 - bounds.MinX,
                (SelectedMachine.BedHeightMm - bounds.Height) / 2 - bounds.MinY);

            foreach (var shape in Selection)
            {
                if (!shape.Locked) shape.Translate(delta);
            }
        });
    }

    [RelayCommand]
    private void RotateSelected(string? degrees)
    {
        if (Selection.Count == 0) return;
        var angle = double.TryParse(degrees, out var d) ? d : 90;

        EditSelection($"Rotate {angle:0.#} degrees", () =>
            Core.Documents.Arrange.RotateSelection(Selection, angle, Core.Documents.Arrange.Bounds(Selection).Center));
    }

    [RelayCommand]
    private void MirrorSelected(string? axis)
    {
        if (Selection.Count == 0) return;

        var horizontal = axis != "vertical";
        EditSelection(horizontal ? "Mirror horizontally" : "Mirror vertically", () =>
        {
            // Mirror about the whole selection rather than each shape individually,
            // or a row of shapes flips in place and keeps the same arrangement.
            var pivot = Core.Documents.Arrange.Bounds(Selection).Center;
            foreach (var shape in Selection)
            {
                if (!shape.Locked) shape.Mirror(horizontal, pivot);
            }
        });
    }

    // ----------------------------------------------------------------- trace

    /// <summary>
    /// A trace editor for the selected image, or null if nothing traceable is
    /// selected. The window drives it; the view model only supplies and applies.
    /// </summary>
    public TraceViewModel? CreateTraceEditor()
    {
        if (PrimarySelection is not ImageShape image) return null;

        return new TraceViewModel(image.Source, image.Name, image.WidthMm, image.HeightMm)
        {
            SourceTransform = image.Transform,
        };
    }

    /// <summary>A trace editor for an image file that has not been imported.</summary>
    public TraceViewModel? CreateTraceEditor(string path)
    {
        try
        {
            var loaded = ImageImporter.Load(path);
            foreach (var warning in loaded.Warnings) Console.AppendInfo(warning);

            var (widthMm, heightMm) = FitToBed(loaded.SuggestedWidthMm, loaded.SuggestedHeightMm);
            return new TraceViewModel(loaded.Image, Path.GetFileNameWithoutExtension(path), widthMm, heightMm);
        }
        catch (Exception ex)
        {
            Console.AppendError($"Could not open {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Shrink to fit the bed, keeping the aspect ratio. Growing is the operator's call.</summary>
    private (double Width, double Height) FitToBed(double widthMm, double heightMm)
    {
        var margin = 0.9;
        var scale = Math.Min(
            SelectedMachine.BedWidthMm * margin / Math.Max(0.1, widthMm),
            SelectedMachine.BedHeightMm * margin / Math.Max(0.1, heightMm));

        return scale >= 1 ? (widthMm, heightMm) : (widthMm * scale, heightMm * scale);
    }

    /// <summary>Add a traced shape to the design, as one undoable step.</summary>
    public void ApplyTrace(TraceViewModel editor, PathShape traced)
    {
        traced.Transform = editor.SourceTransform ?? Matrix2D.Identity;
        if (editor.SourceTransform is null) PlaceOnBed(traced);

        EditDocument("Trace image", () =>
        {
            Design.AddShape(traced, SelectedLayer?.Layer);
            Selection.Clear();
            Selection.Add(traced);
        });

        var kind = editor.Mode == Cam.Trace.TraceMode.Centreline ? "centreline" : "outline";
        Console.AppendInfo($"Traced {traced.Paths.Count:N0} {kind} path(s) from {editor.SourceName}. " +
                           "The image is untouched, so you can trace it again with different settings.");
    }

    // ----------------------------------------------------------------- layers

    [RelayCommand]
    private void AddLayer()
    {
        var layer = Layer.CreateDefault(OperationKind.Engrave, Design.Layers.Count);
        Design.Layers.Add(layer);
        RebuildLayers();
        SelectedLayer = Layers.LastOrDefault();
        QueueRegenerate();
    }

    [RelayCommand]
    private void RemoveLayer()
    {
        if (SelectedLayer is null || Design.Layers.Count <= 1) return;
        Design.RemoveLayer(SelectedLayer.Layer);
        RebuildLayers();
        QueueRegenerate();
    }

    [RelayCommand]
    private void MoveLayerUp()
    {
        if (SelectedLayer is null) return;
        var index = Design.Layers.IndexOf(SelectedLayer.Layer);
        if (index <= 0) return;

        (Design.Layers[index - 1].Order, SelectedLayer.Layer.Order) =
            (SelectedLayer.Layer.Order, Design.Layers[index - 1].Order);

        var keep = SelectedLayer.Layer;
        RebuildLayers();
        SelectedLayer = Layers.FirstOrDefault(l => l.Layer == keep);
        QueueRegenerate();
    }

    [RelayCommand]
    private void MoveLayerDown()
    {
        if (SelectedLayer is null) return;
        var index = Design.Layers.IndexOf(SelectedLayer.Layer);
        if (index < 0 || index >= Design.Layers.Count - 1) return;

        (Design.Layers[index + 1].Order, SelectedLayer.Layer.Order) =
            (SelectedLayer.Layer.Order, Design.Layers[index + 1].Order);

        var keep = SelectedLayer.Layer;
        RebuildLayers();
        SelectedLayer = Layers.FirstOrDefault(l => l.Layer == keep);
        QueueRegenerate();
    }

    // --------------------------------------------------------------- materials

    public IEnumerable<MaterialProfile> AvailableMaterials =>
        MaterialLibrary.Profiles
            .OrderBy(m => m.Category)
            .ThenBy(m => m.Name)
            .ThenBy(m => m.ThicknessMm);

    [ObservableProperty]
    private MaterialProfile? _selectedMaterial;

    partial void OnSelectedMaterialChanged(MaterialProfile? value)
    {
        OnPropertyChanged(nameof(MaterialNotes));
        OnPropertyChanged(nameof(MaterialHazard));
        OnPropertyChanged(nameof(HasMaterialHazard));
    }

    public string MaterialNotes => SelectedMaterial?.Notes ?? string.Empty;
    public string MaterialHazard => SelectedMaterial?.Hazard ?? string.Empty;
    public bool HasMaterialHazard => !string.IsNullOrEmpty(SelectedMaterial?.Hazard);

    /// <summary>Apply the selected material's settings to every layer that has an entry for its operation.</summary>
    [RelayCommand]
    private void ApplyMaterial()
    {
        if (SelectedMaterial is null) return;

        var material = SelectedMaterial;
        if (Math.Abs(material.LaserWatts - SelectedMachine.LaserWatts) > 0.51)
        {
            material = material.ScaleTo(SelectedMachine.LaserWatts);
            Console.Append(new ConsoleEntry(DateTimeOffset.UtcNow, ConsoleDirection.Warning,
                $"These settings were measured on a {SelectedMaterial.LaserWatts:0.#} W machine and have been rescaled for " +
                $"{SelectedMachine.LaserWatts:0.#} W. Burn a test grid before committing a workpiece."));
        }

        var applied = 0;
        foreach (var layer in Layers)
        {
            if (material.For(layer.Operation) is null) continue;
            MaterialLibrary.ApplyTo(layer.Layer, material);
            applied++;
        }

        RebuildLayers();
        QueueRegenerate();

        Console.AppendInfo(applied == 0
            ? $"{material.DisplayName} has no settings for the operations on your layers."
            : $"Applied {material.DisplayName} to {applied} layer(s).");

        if (material.Hazard is { Length: > 0 } hazard) Console.AppendError($"Safety: {hazard}");
    }
}

/// <summary>View toggles and small conveniences the window binds to directly.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Jog step sizes. The classic 0.1/1/10/100 ladder every sender uses.</summary>
    public IReadOnlyList<double> JogSteps { get; } = [0.1, 0.5, 1, 5, 10, 50, 100];

    public IReadOnlyList<double> JogFeeds { get; } = [500, 1000, 2000, 3000, 6000, 12000];

    [RelayCommand]
    private void ToggleGrid() => ShowGrid = !ShowGrid;

    [RelayCommand]
    private void ToggleTravel() => ShowTravel = !ShowTravel;

    [RelayCommand]
    private void ToggleConsole() => ShowConsole = !ShowConsole;

    /// <summary>Light, dark, follow the system — in that order, as the house style requires.</summary>
    [RelayCommand]
    private void CycleTheme() => Theme = Theme switch
    {
        Core.Storage.ThemeMode.System => Core.Storage.ThemeMode.Light,
        Core.Storage.ThemeMode.Light => Core.Storage.ThemeMode.Dark,
        _ => Core.Storage.ThemeMode.System,
    };

    [RelayCommand]
    private void ToggleUnits() =>
        DisplayUnit = DisplayUnit == Core.Units.LengthUnit.Millimetres
            ? Core.Units.LengthUnit.Inches
            : Core.Units.LengthUnit.Millimetres;
}
