namespace Emberline.Core.Documents;

/// <summary>One reversible edit.</summary>
public sealed record UndoEntry(string Name, Action Undo, Action Redo);

/// <summary>
/// Undo and redo.
///
/// Closure-based rather than a command class per operation: the operations here
/// are almost all "restore these transforms" or "put this shape back in the
/// collection", and thirty small command classes to express that would be more
/// code hiding the same two ideas.
///
/// Coalescing matters as much as the stack itself. Dragging a shape produces one
/// mouse-move event per frame, and without merging, one drag would fill the
/// history and undo would move the shape a pixel at a time.
/// </summary>
public sealed class UndoStack
{
    private readonly List<UndoEntry> _undo = [];
    private readonly List<UndoEntry> _redo = [];
    private readonly int _limit;

    /// <summary>Set while undoing or redoing, so the operation does not record itself.</summary>
    private bool _suspended;

    public UndoStack(int limit = 100) => _limit = Math.Max(1, limit);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public string? UndoName => _undo.Count > 0 ? _undo[^1].Name : null;
    public string? RedoName => _redo.Count > 0 ? _redo[^1].Name : null;

    public event Action? Changed;

    public void Push(UndoEntry entry)
    {
        if (_suspended) return;

        _undo.Add(entry);
        // Anything that was undone is unreachable once a new edit lands.
        _redo.Clear();

        while (_undo.Count > _limit) _undo.RemoveAt(0);
        Changed?.Invoke();
    }

    public void Push(string name, Action undo, Action redo) => Push(new UndoEntry(name, undo, redo));

    /// <summary>
    /// Replace the most recent entry when it has the same name and coalescing key.
    /// A drag then occupies one history slot rather than several hundred.
    /// </summary>
    public void PushOrMerge(string name, object coalesceKey, Action undo, Action redo)
    {
        if (_suspended) return;

        if (_undo.Count > 0 &&
            _lastCoalesceKey is not null &&
            Equals(_lastCoalesceKey, coalesceKey) &&
            _undo[^1].Name == name)
        {
            // Keep the *original* undo — it restores the state before the drag began
            // — and take the newest redo.
            _undo[^1] = _undo[^1] with { Redo = redo };
            _redo.Clear();
            Changed?.Invoke();
            return;
        }

        _lastCoalesceKey = coalesceKey;
        Push(name, undo, redo);
    }

    private object? _lastCoalesceKey;

    /// <summary>End the current coalescing run, so the next edit starts a new entry.</summary>
    public void EndMerge() => _lastCoalesceKey = null;

    public bool Undo()
    {
        if (_undo.Count == 0) return false;

        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        _suspended = true;
        try
        {
            entry.Undo();
        }
        finally
        {
            _suspended = false;
        }

        _redo.Add(entry);
        _lastCoalesceKey = null;
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;

        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        _suspended = true;
        try
        {
            entry.Redo();
        }
        finally
        {
            _suspended = false;
        }

        _undo.Add(entry);
        _lastCoalesceKey = null;
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _lastCoalesceKey = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// Record the transforms of a set of shapes, returning an action that puts
    /// them all back. The workhorse behind move, scale, rotate, mirror and align.
    /// </summary>
    public static Action CaptureTransforms(IReadOnlyList<Shape> shapes)
    {
        var snapshot = shapes.Select(s => (Shape: s, Transform: s.Transform)).ToArray();
        return () =>
        {
            foreach (var (shape, transform) in snapshot) shape.Transform = transform;
        };
    }
}
