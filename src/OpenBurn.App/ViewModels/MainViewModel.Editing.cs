using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenBurn.Cam.Text;
using OpenBurn.Core.Documents;
using OpenBurn.Core.Geometry;
using OpenBurn.Core.Units;
using ArrangeOps = OpenBurn.Core.Documents.Arrange;

namespace OpenBurn.App.ViewModels;

/// <summary>
/// Selection, direct manipulation and everything reachable from the arrange menu.
///
/// The canvas mutates shape transforms directly while a drag is in progress —
/// that is what makes dragging feel immediate — so undo is captured on
/// <see cref="BeginCanvasEdit"/> before anything moves, and committed once on
/// release. Recording per mouse-move would fill the history with a pixel at a time.
/// </summary>
public sealed partial class MainViewModel
{
    public UndoStack Undo { get; } = new();

    public ObservableCollection<Shape> Selection { get; } = [];

    [ObservableProperty]
    private bool _snapEnabled = true;

    [ObservableProperty]
    private double _snapMm = 1.0;

    public bool HasSelection => Selection.Count > 0;
    public bool HasMultipleSelected => Selection.Count > 1;

    /// <summary>The primary selection — what the inspector shows.</summary>
    public Shape? PrimarySelection => Selection.Count > 0 ? Selection[0] : null;

    public string SelectionSummary
    {
        get
        {
            if (Selection.Count == 0) return "Nothing selected";

            var bounds = ArrangeOps.Bounds(Selection);
            var size = bounds.IsEmpty
                ? string.Empty
                : $" · {Core.Units.UnitConvert.FromMm(bounds.Width, DisplayUnit):0.#} × " +
                  $"{Core.Units.UnitConvert.FromMm(bounds.Height, DisplayUnit):0.#} {DisplayUnit.Suffix()}";

            return Selection.Count == 1 ? $"{Selection[0].Name}{size}" : $"{Selection.Count} shapes{size}";
        }
    }

    public void SetSelection(IReadOnlyList<Shape> shapes, bool additive)
    {
        if (!additive) Selection.Clear();

        foreach (var shape in shapes)
        {
            if (additive && Selection.Contains(shape)) Selection.Remove(shape);
            else if (!Selection.Contains(shape)) Selection.Add(shape);
        }

        RaiseSelectionChanged();
    }

    private bool _hadSelection;

    private void RaiseSelectionChanged()
    {
        var has = Selection.Count > 0;
        FollowSelectionContext(_hadSelection, has);
        _hadSelection = has;

        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasMultipleSelected));
        OnPropertyChanged(nameof(PrimarySelection));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(SelectedText));
        OnPropertyChanged(nameof(IsTextSelected));
        OnPropertyChanged(nameof(SelectedWidthMm));
        OnPropertyChanged(nameof(SelectedHeightMm));
        OnPropertyChanged(nameof(SelectedXMm));
        OnPropertyChanged(nameof(SelectedYMm));
        RaiseImageSelection();
    }

    // ------------------------------------------------------- canvas dragging

    private Action? _pendingUndo;

    /// <summary>Snapshot the selection before the canvas starts moving it.</summary>
    public void BeginCanvasEdit(string name)
    {
        _pendingUndo = UndoStack.CaptureTransforms(Selection);
        _pendingEditName = name;
    }

    private string _pendingEditName = "Edit";

    public void CanvasEditChanged()
    {
        // Live feedback while dragging, without regenerating on every frame — the
        // debounce in QueueRegenerate absorbs the rate.
        QueueRegenerate();
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(SelectedWidthMm));
        OnPropertyChanged(nameof(SelectedHeightMm));
        OnPropertyChanged(nameof(SelectedXMm));
        OnPropertyChanged(nameof(SelectedYMm));
    }

    public void EndCanvasEdit()
    {
        if (_pendingUndo is null) return;

        var undo = _pendingUndo;
        var redo = UndoStack.CaptureTransforms(Selection);
        _pendingUndo = null;

        Undo.Push(_pendingEditName, () => { undo(); AfterUndo(); }, () => { redo(); AfterUndo(); });
        QueueRegenerate();
    }

    private void AfterUndo()
    {
        QueueRegenerate();
        RaiseSelectionChanged();
        RaiseUndoState();
    }

    private void RaiseUndoState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoLabel));
        OnPropertyChanged(nameof(RedoLabel));
        UndoEditCommand.NotifyCanExecuteChanged();
        RedoEditCommand.NotifyCanExecuteChanged();
    }

    public bool CanUndo => Undo.CanUndo;
    public bool CanRedo => Undo.CanRedo;
    public string UndoLabel => Undo.UndoName is { } n ? $"Undo {n}" : "Undo";
    public string RedoLabel => Undo.RedoName is { } n ? $"Redo {n}" : "Redo";

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void UndoEdit()
    {
        if (Undo.Undo()) AfterUndo();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void RedoEdit()
    {
        if (Undo.Redo()) AfterUndo();
    }

    /// <summary>Run an edit that changes transforms, recording it for undo.</summary>
    private void EditSelection(string name, Action change)
    {
        if (Selection.Count == 0) return;

        var before = UndoStack.CaptureTransforms(Selection);
        change();
        var after = UndoStack.CaptureTransforms(Selection);

        Undo.Push(name, () => { before(); AfterUndo(); }, () => { after(); AfterUndo(); });
        QueueRegenerate();
        RaiseSelectionChanged();
        RaiseUndoState();
    }

    /// <summary>Run an edit that adds or removes shapes, recording it for undo.</summary>
    private void EditDocument(string name, Action change)
    {
        var before = Design.Shapes.ToList();
        var beforeSelection = Selection.ToList();

        change();

        var after = Design.Shapes.ToList();
        var afterSelection = Selection.ToList();

        void Restore(List<Shape> shapes, List<Shape> selection)
        {
            Design.Shapes.Clear();
            foreach (var s in shapes) Design.Shapes.Add(s);
            Selection.Clear();
            foreach (var s in selection) Selection.Add(s);
            AfterUndo();
        }

        Undo.Push(name, () => Restore(before, beforeSelection), () => Restore(after, afterSelection));
        QueueRegenerate();
        RaiseSelectionChanged();
        RaiseUndoState();
    }

    // ------------------------------------------------------------- selection

    [RelayCommand]
    private void SelectAll()
    {
        Selection.Clear();
        foreach (var shape in Design.Shapes)
        {
            if (shape.Visible && !shape.Locked) Selection.Add(shape);
        }
        RaiseSelectionChanged();
    }

    [RelayCommand]
    private void SelectNone()
    {
        Selection.Clear();
        RaiseSelectionChanged();
    }

    // --------------------------------------------------------------- arrange

    [RelayCommand]
    private void AlignSelection(string? edge)
    {
        if (!Enum.TryParse<AlignEdge>(edge, ignoreCase: true, out var parsed)) return;

        // One shape has nothing to align to but the bed, which is what people
        // actually mean by "align left" with a single object selected.
        var relativeTo = Selection.Count == 1
            ? new Rect2(0, 0, SelectedMachine.BedWidthMm, SelectedMachine.BedHeightMm)
            : (Rect2?)null;

        EditSelection($"Align {parsed}", () => ArrangeOps.Align(Selection, parsed, relativeTo));
    }

    [RelayCommand]
    private void DistributeSelection(string? axis)
    {
        if (!Enum.TryParse<DistributeAxis>(axis, ignoreCase: true, out var parsed)) return;
        if (Selection.Count < 3)
        {
            Console.AppendInfo("Distributing needs at least three shapes selected.");
            return;
        }
        EditSelection($"Distribute {parsed}", () => ArrangeOps.Distribute(Selection, parsed));
    }

    [RelayCommand]
    private void GroupSelection()
    {
        if (Selection.Count < 2) return;

        EditDocument("Group", () =>
        {
            var members = Selection.ToList();
            var group = new GroupShape(members) { Name = $"Group of {members.Count}" };

            // The group takes the layer of the first member, which is the least
            // surprising choice when they disagree.
            group.LayerId = members[0].LayerId;

            foreach (var member in members) Design.Shapes.Remove(member);
            Design.Shapes.Add(group);

            Selection.Clear();
            Selection.Add(group);
        });
    }

    [RelayCommand]
    private void UngroupSelection()
    {
        var groups = Selection.OfType<GroupShape>().ToList();
        if (groups.Count == 0) return;

        EditDocument("Ungroup", () =>
        {
            foreach (var group in groups)
            {
                var children = group.Ungroup();
                Design.Shapes.Remove(group);
                Selection.Remove(group);

                foreach (var child in children)
                {
                    if (string.IsNullOrEmpty(child.LayerId)) child.LayerId = group.LayerId;
                    Design.Shapes.Add(child);
                    Selection.Add(child);
                }
            }
        });
    }

    [RelayCommand]
    private void ToggleLockSelection()
    {
        if (Selection.Count == 0) return;

        var locking = Selection.Any(s => !s.Locked);
        foreach (var shape in Selection) shape.Locked = locking;

        Console.AppendInfo(locking ? $"Locked {Selection.Count} shape(s)." : $"Unlocked {Selection.Count} shape(s).");
        RaiseSelectionChanged();
    }

    [RelayCommand]
    private void ToggleVisibilitySelection()
    {
        if (Selection.Count == 0) return;

        var hiding = Selection.Any(s => s.Visible);
        foreach (var shape in Selection) shape.Visible = !hiding;

        QueueRegenerate();
        RaiseSelectionChanged();
    }

    // --------------------------------------------------------- numeric entry

    /// <summary>Selection width in display units. Setting it resizes about the bottom-left.</summary>
    public double SelectedWidthMm
    {
        get => Core.Units.UnitConvert.FromMm(ArrangeOps.Bounds(Selection).Width, DisplayUnit);
        set
        {
            var bounds = ArrangeOps.Bounds(Selection);
            var target = Core.Units.UnitConvert.ToMm(value, DisplayUnit);
            if (bounds.IsEmpty || bounds.Width < 1e-9 || target <= 0) return;

            var factor = target / bounds.Width;
            EditSelection("Resize", () => ArrangeOps.ScaleSelection(Selection, factor, 1, bounds.Min));
        }
    }

    public double SelectedHeightMm
    {
        get => Core.Units.UnitConvert.FromMm(ArrangeOps.Bounds(Selection).Height, DisplayUnit);
        set
        {
            var bounds = ArrangeOps.Bounds(Selection);
            var target = Core.Units.UnitConvert.ToMm(value, DisplayUnit);
            if (bounds.IsEmpty || bounds.Height < 1e-9 || target <= 0) return;

            var factor = target / bounds.Height;
            EditSelection("Resize", () => ArrangeOps.ScaleSelection(Selection, 1, factor, bounds.Min));
        }
    }

    public double SelectedXMm
    {
        get => Core.Units.UnitConvert.FromMm(ArrangeOps.Bounds(Selection).MinX, DisplayUnit);
        set
        {
            var bounds = ArrangeOps.Bounds(Selection);
            if (bounds.IsEmpty) return;

            var delta = Core.Units.UnitConvert.ToMm(value, DisplayUnit) - bounds.MinX;
            if (Math.Abs(delta) < 1e-9) return;

            EditSelection("Move", () =>
            {
                foreach (var s in Selection)
                {
                    if (!s.Locked) s.Translate(new Vec2(delta, 0));
                }
            });
        }
    }

    public double SelectedYMm
    {
        get => Core.Units.UnitConvert.FromMm(ArrangeOps.Bounds(Selection).MinY, DisplayUnit);
        set
        {
            var bounds = ArrangeOps.Bounds(Selection);
            if (bounds.IsEmpty) return;

            var delta = Core.Units.UnitConvert.ToMm(value, DisplayUnit) - bounds.MinY;
            if (Math.Abs(delta) < 1e-9) return;

            EditSelection("Move", () =>
            {
                foreach (var s in Selection)
                {
                    if (!s.Locked) s.Translate(new Vec2(0, delta));
                }
            });
        }
    }

    /// <summary>Nudge the selection with the arrow keys.</summary>
    public void NudgeSelection(double dx, double dy)
    {
        if (Selection.Count == 0) return;
        EditSelection("Nudge", () =>
        {
            foreach (var s in Selection)
            {
                if (!s.Locked) s.Translate(new Vec2(dx, dy));
            }
        });
    }

    // ------------------------------------------------------------------ text

    [ObservableProperty]
    private string _textInput = "OpenBurn";

    [ObservableProperty]
    private double _textSizeMm = 20;

    [ObservableProperty]
    private string _textFont = "Bricolage Grotesque";

    public IReadOnlyList<string> AvailableFonts { get; } = TextOutliner.AvailableFamilies();

    public bool IsTextSelected => PrimarySelection is TextShape;

    public TextShape? SelectedText => PrimarySelection as TextShape;

    [RelayCommand]
    private void AddText()
    {
        var text = string.IsNullOrWhiteSpace(TextInput) ? "Text" : TextInput;

        var shape = new TextShape
        {
            Text = text,
            FontFamily = TextFont,
            FontSizeMm = TextSizeMm,
            Name = text.Length > 20 ? text[..20] : text,
        };

        var result = TextOutliner.Apply(shape);

        if (result.Outlines.Count == 0)
        {
            Console.AppendError($"'{TextFont}' produced no outlines for that text. Try a different font.");
            return;
        }

        if (result.FontWasSubstituted)
        {
            Console.Append(new Devices.ConsoleEntry(DateTimeOffset.UtcNow, Devices.ConsoleDirection.Warning,
                $"'{TextFont}' is not installed — '{result.ResolvedFamily}' was used instead. The engraved shapes will differ."));
        }

        PlaceOnBed(shape);

        EditDocument("Add text", () =>
        {
            Design.AddShape(shape, SelectedLayer?.Layer);
            Selection.Clear();
            Selection.Add(shape);
        });

        Console.AppendInfo($"Added text in {result.ResolvedFamily}, {result.WidthMm:0.#} × {result.HeightMm:0.#} mm.");
    }

    /// <summary>Re-run the font engine after the selected text's properties change.</summary>
    [RelayCommand]
    private void RefreshText()
    {
        if (SelectedText is not { } shape) return;

        shape.Text = string.IsNullOrWhiteSpace(TextInput) ? shape.Text : TextInput;
        shape.FontFamily = TextFont;
        shape.FontSizeMm = TextSizeMm;

        TextOutliner.Apply(shape);
        QueueRegenerate();
        RaiseSelectionChanged();
    }

    /// <summary>Convert live text to plain paths so it can be node-edited or shared.</summary>
    [RelayCommand]
    private void ConvertTextToPaths()
    {
        var texts = Selection.OfType<TextShape>().ToList();
        if (texts.Count == 0) return;

        EditDocument("Convert text to paths", () =>
        {
            foreach (var text in texts)
            {
                var index = Design.Shapes.IndexOf(text);
                if (index < 0) continue;

                var paths = text.ToPathShape();
                Design.Shapes[index] = paths;

                Selection.Remove(text);
                Selection.Add(paths);
            }
        });
    }

    // ----------------------------------------------------------------- array

    [ObservableProperty]
    private int _arrayColumns = 3;

    [ObservableProperty]
    private int _arrayRows = 2;

    [ObservableProperty]
    private double _arraySpacingMm = 5;

    /// <summary>
    /// Duplicate the selection into a grid. The batch-production case: one keyring
    /// becomes twenty-four, spaced to fit the bed.
    /// </summary>
    [RelayCommand]
    private void CreateArray()
    {
        if (PrimarySelection is not { } source) return;

        var copies = ArrangeOps.Array(source, ArrayColumns, ArrayRows, ArraySpacingMm, ArraySpacingMm);
        if (copies.Count == 0) return;

        EditDocument($"Array {ArrayColumns}×{ArrayRows}", () =>
        {
            foreach (var copy in copies) Design.Shapes.Add(copy);
            foreach (var copy in copies) Selection.Add(copy);
        });

        var bounds = ArrangeOps.Bounds(Design.Shapes);
        var fits = bounds.MaxX <= SelectedMachine.BedWidthMm && bounds.MaxY <= SelectedMachine.BedHeightMm;

        Console.AppendInfo($"Created {copies.Count} cop{(copies.Count == 1 ? "y" : "ies")}. " +
                           (fits ? "The array fits the bed." : "The array runs off the bed — reduce the count or the spacing."));
    }
}
