using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using OpenBurn.Core.Storage;
using OpenBurn.Core.Documents;

namespace OpenBurn.App.ViewModels;

/// <summary>A named blank, so "100 mm tile" is one pick rather than four numbers.</summary>
public sealed record WorkpiecePreset(string Name, Workpiece Piece)
{
    public override string ToString() => Name;
}

/// <summary>
/// The material on the bed: its size, its shape and where it sits.
///
/// Mostly this exists so the canvas can draw the boundary and you can line artwork
/// up against the thing you are actually burning, rather than against a bed that
/// is four times larger than the coaster in the middle of it.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly IReadOnlyList<WorkpiecePreset> BuiltInWorkpieces =
    [
        new("No workpiece", Workpiece.None),
        new("100 mm square tile", new Workpiece { Shape = WorkpieceShape.Rectangle, WidthMm = 100, HeightMm = 100, Name = "100 mm square tile" }),
        new("150 mm square tile", new Workpiece { Shape = WorkpieceShape.Rectangle, WidthMm = 150, HeightMm = 150, Name = "150 mm square tile" }),
        new("Slate coaster, 100 mm round", new Workpiece { Shape = WorkpieceShape.Circle, WidthMm = 100, HeightMm = 100, Name = "Slate coaster, 100 mm round" }),
        new("Slate coaster, 100 mm square", new Workpiece { Shape = WorkpieceShape.Rectangle, WidthMm = 100, HeightMm = 100, CornerRadiusMm = 6, Name = "Slate coaster, 100 mm square" }),
        new("Bamboo board, 200 × 300", new Workpiece { Shape = WorkpieceShape.Rectangle, WidthMm = 200, HeightMm = 300, CornerRadiusMm = 10, Name = "Bamboo board, 200 × 300" }),
        new("A5 card, 148 × 210", new Workpiece { Shape = WorkpieceShape.Rectangle, WidthMm = 148, HeightMm = 210, Name = "A5 card, 148 × 210" }),
        new("A4 sheet, 210 × 297", new Workpiece { Shape = WorkpieceShape.Rectangle, WidthMm = 210, HeightMm = 297, Name = "A4 sheet, 210 × 297" }),
        new("Plywood offcut, 300 × 200", new Workpiece { Shape = WorkpieceShape.Rectangle, WidthMm = 300, HeightMm = 200, Name = "Plywood offcut, 300 × 200" }),
    ];

    /// <summary>Built-in blanks first, then whatever the user has saved.</summary>
    public ObservableCollection<WorkpiecePreset> WorkpiecePresets { get; } = [];

    private void RebuildWorkpiecePresets()
    {
        var keep = _selectedWorkpiecePreset?.Name;

        WorkpiecePresets.Clear();
        foreach (var preset in BuiltInWorkpieces) WorkpiecePresets.Add(preset);

        foreach (var saved in Settings.SavedWorkpieces)
        {
            WorkpiecePresets.Add(new WorkpiecePreset(saved.Name, new Workpiece
            {
                Shape = saved.Round ? WorkpieceShape.Circle : WorkpieceShape.Rectangle,
                WidthMm = saved.WidthMm,
                HeightMm = saved.HeightMm,
                CornerRadiusMm = saved.CornerRadiusMm,
                Name = saved.Name,
            }));
        }

        if (keep is not null)
        {
            _selectedWorkpiecePreset = WorkpiecePresets.FirstOrDefault(p => p.Name == keep);
            OnPropertyChanged(nameof(SelectedWorkpiecePreset));
        }
    }

    /// <summary>Can the current blank be saved — is it set, and not already a preset?</summary>
    public bool CanSaveWorkpiece =>
        Design.Workpiece.IsSet &&
        !WorkpiecePresets.Any(p => p.Piece.Shape == Design.Workpiece.Shape &&
                                   Math.Abs(p.Piece.WidthMm - Design.Workpiece.WidthMm) < 0.01 &&
                                   Math.Abs(p.Piece.HeightMm - Design.Workpiece.HeightMm) < 0.01 &&
                                   Math.Abs(p.Piece.CornerRadiusMm - Design.Workpiece.CornerRadiusMm) < 0.01);

    /// <summary>
    /// Keep this blank for next time. Named from its size, because a dialog asking
    /// for a name is a worse trade than a list that reads "80 mm circle".
    /// </summary>
    [RelayCommand]
    private void SaveWorkpiece()
    {
        var piece = Design.Workpiece;
        if (!piece.IsSet) return;

        var name = piece.Summary;
        var saved = Settings.SavedWorkpieces.Where(w => w.Name != name).ToList();
        saved.Add(new SavedWorkpiece
        {
            Name = name,
            Round = piece.Shape == WorkpieceShape.Circle,
            WidthMm = piece.WidthMm,
            HeightMm = piece.HeightMm,
            CornerRadiusMm = piece.CornerRadiusMm,
        });

        Settings = Settings with { SavedWorkpieces = saved };
        RebuildWorkpiecePresets();

        _selectedWorkpiecePreset = WorkpiecePresets.FirstOrDefault(p => p.Name == name);
        OnPropertyChanged(nameof(SelectedWorkpiecePreset));
        OnPropertyChanged(nameof(CanSaveWorkpiece));
        Console.AppendInfo($"Saved “{name}” — it will be in the list next time.");
    }

    private WorkpiecePreset? _selectedWorkpiecePreset;

    public WorkpiecePreset? SelectedWorkpiecePreset
    {
        get => _selectedWorkpiecePreset;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedWorkpiecePreset)) return;
            _selectedWorkpiecePreset = value;
            OnPropertyChanged();

            // A preset lands in the middle of the bed, which is where anybody who
            // just picked "100 mm tile" is about to put it anyway.
            Design.Workpiece = value.Piece.IsSet
                ? value.Piece.CentredOn(SelectedMachine.BedWidthMm, SelectedMachine.BedHeightMm)
                : Workpiece.None;

            AfterWorkpieceChange();
        }
    }

    public bool HasWorkpiece => Design.Workpiece.IsSet;

    public string WorkpieceSummary => Design.Workpiece.Summary;

    public bool WorkpieceIsRound
    {
        get => Design.Workpiece.Shape == WorkpieceShape.Circle;
        set => SetWorkpiece(w => w with { Shape = value ? WorkpieceShape.Circle : WorkpieceShape.Rectangle });
    }

    public double WorkpieceWidthMm
    {
        get => Design.Workpiece.WidthMm;
        set => SetWorkpiece(w => w with
        {
            WidthMm = Clamp(value),
            // A circle described by two different numbers is an ellipse nobody asked for.
            HeightMm = w.Shape == WorkpieceShape.Circle ? Clamp(value) : w.HeightMm,
        });
    }

    public double WorkpieceHeightMm
    {
        get => Design.Workpiece.HeightMm;
        set => SetWorkpiece(w => w with
        {
            HeightMm = Clamp(value),
            WidthMm = w.Shape == WorkpieceShape.Circle ? Clamp(value) : w.WidthMm,
        });
    }

    public double WorkpieceXMm
    {
        get => Design.Workpiece.XMm;
        set => SetWorkpiece(w => w with { XMm = Math.Round(value, 2) });
    }

    public double WorkpieceYMm
    {
        get => Design.Workpiece.YMm;
        set => SetWorkpiece(w => w with { YMm = Math.Round(value, 2) });
    }

    public double WorkpieceCornerMm
    {
        get => Design.Workpiece.CornerRadiusMm;
        set => SetWorkpiece(w => w with { CornerRadiusMm = Math.Clamp(Math.Round(value, 2), 0, 100) });
    }

    private static double Clamp(double mm) => Math.Clamp(Math.Round(mm, 2), 1, 5000);

    /// <summary>Describe a blank by typing its size, without picking a preset first.</summary>
    [RelayCommand]
    private void UseCustomWorkpiece()
    {
        var existing = Design.Workpiece;
        var piece = existing.IsSet
            ? existing
            : new Workpiece { Shape = WorkpieceShape.Rectangle, WidthMm = 100, HeightMm = 100 }
                .CentredOn(SelectedMachine.BedWidthMm, SelectedMachine.BedHeightMm);

        Design.Workpiece = piece with { Name = "Custom" };
        _selectedWorkpiecePreset = null;
        OnPropertyChanged(nameof(SelectedWorkpiecePreset));
        AfterWorkpieceChange();
    }

    [RelayCommand]
    private void ClearWorkpiece()
    {
        Design.Workpiece = Workpiece.None;
        _selectedWorkpiecePreset = WorkpiecePresets[0];
        OnPropertyChanged(nameof(SelectedWorkpiecePreset));
        AfterWorkpieceChange();
    }

    [RelayCommand]
    private void CentreWorkpieceOnBed()
    {
        if (!Design.Workpiece.IsSet) return;
        Design.Workpiece = Design.Workpiece.CentredOn(SelectedMachine.BedWidthMm, SelectedMachine.BedHeightMm);
        AfterWorkpieceChange();
    }

    /// <summary>Move the artwork to the middle of the material, which is the usual next wish.</summary>
    [RelayCommand]
    private void CentreArtworkOnWorkpiece()
    {
        if (!Design.Workpiece.IsSet) return;

        var targets = Selection.Count > 0 ? Selection.ToList() : Design.Shapes.ToList();
        if (targets.Count == 0)
        {
            Console.AppendError("Nothing to centre.");
            return;
        }

        EditSelectionOrDocument("Centre on the workpiece", targets, () =>
        {
            var bounds = Core.Documents.Arrange.Bounds(targets);
            if (bounds.IsEmpty) return;

            var target = Design.Workpiece.Bounds.Center;
            var delta = new Core.Geometry.Vec2(target.X - bounds.Center.X, target.Y - bounds.Center.Y);

            foreach (var shape in targets)
            {
                if (!shape.Locked) shape.Translate(delta);
            }
        });
    }

    private void SetWorkpiece(Func<Workpiece, Workpiece> change)
    {
        if (!Design.Workpiece.IsSet) return;

        var updated = change(Design.Workpiece);
        if (updated == Design.Workpiece) return;

        Design.Workpiece = updated with { Name = "Custom" };
        _selectedWorkpiecePreset = null;
        OnPropertyChanged(nameof(SelectedWorkpiecePreset));
        AfterWorkpieceChange();
    }

    private void AfterWorkpieceChange()
    {
        OnPropertyChanged(nameof(HasWorkpiece));
        OnPropertyChanged(nameof(WorkpieceSummary));
        OnPropertyChanged(nameof(WorkpieceIsRound));
        OnPropertyChanged(nameof(WorkpieceWidthMm));
        OnPropertyChanged(nameof(WorkpieceHeightMm));
        OnPropertyChanged(nameof(WorkpieceXMm));
        OnPropertyChanged(nameof(WorkpieceYMm));
        OnPropertyChanged(nameof(WorkpieceCornerMm));
        OnPropertyChanged(nameof(CanSaveWorkpiece));
        QueueRegenerate();
    }
}
