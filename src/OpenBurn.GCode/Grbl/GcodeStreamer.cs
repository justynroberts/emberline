namespace OpenBurn.GCode.Grbl;

public enum StreamState
{
    Idle,
    Running,
    Paused,
    Stopping,
    Completed,
    Faulted,
}

public readonly record struct StreamProgress(
    int Sent,
    int Acknowledged,
    int Total,
    int BytesInFlight,
    StreamState State)
{
    public double Fraction => Total > 0 ? (double)Acknowledged / Total : 1.0;
}

public readonly record struct StreamError(int LineIndex, int Code, string Text, GrblCodeInfo Info);

/// <summary>
/// Character-counting G-code streamer — the protocol GRBL's own stream.py uses.
///
/// GRBL has a 128-byte serial receive buffer. Send-a-line-wait-for-ok leaves it
/// empty most of the time, the motion planner starves, and a raster engrave shows
/// visible banding while taking roughly twice as long. The fix is to track the
/// byte length of every unacknowledged line and keep pushing while the total stays
/// under the buffer size.
///
/// No I/O, no timers, no threads: it emits the lines that should be written and
/// consumes the acknowledgements that come back. That makes the whole protocol
/// testable against the virtual laser, which is exactly what the test suite does.
/// </summary>
public sealed class GcodeStreamer
{
    public const int DefaultRxBufferSize = 128;

    private readonly Action<string> _write;
    private readonly int _bufferSize;
    private readonly bool _stopOnError;
    private readonly Queue<int> _inFlightBytes = new();
    private readonly List<StreamError> _errors = [];

    private IReadOnlyList<string> _lines = [];
    private int _cursor;
    private int _acknowledged;
    private int _bytesInFlight;
    private int _inFlightHead;
    private bool _pumping;
    private bool _pumpAgain;

    public GcodeStreamer(Action<string> write, int bufferSize = DefaultRxBufferSize, bool stopOnError = true)
    {
        _write = write;
        _bufferSize = bufferSize;
        _stopOnError = stopOnError;
    }

    public StreamState State { get; private set; } = StreamState.Idle;
    public int Total => _lines.Count;
    public int Acknowledged => _acknowledged;
    public int BytesInFlight => _bytesInFlight;

    /// <summary>Highest in-flight byte count seen. The tests assert this never reaches the buffer size.</summary>
    public int PeakBytesInFlight { get; private set; }

    public IReadOnlyList<StreamError> Errors => _errors;

    /// <summary>Index of the last line the controller has finished, or -1.</summary>
    public int CompletedIndex => _acknowledged - 1;

    public event Action<StreamProgress>? Progress;
    public event Action<StreamProgress>? Completed;
    public event Action<StreamError>? Error;

    /// <summary>Raised as each line is acknowledged so callers can track modal state or update the console.</summary>
    public event Action<int, string>? LineAcknowledged;

    public void Load(IReadOnlyList<string> lines)
    {
        _lines = lines;
        _cursor = 0;
        _acknowledged = 0;
        _bytesInFlight = 0;
        _inFlightHead = 0;
        PeakBytesInFlight = 0;
        _inFlightBytes.Clear();
        _errors.Clear();
        State = StreamState.Idle;
    }

    public void Start()
    {
        if (_lines.Count == 0)
        {
            State = StreamState.Completed;
            RaiseCompleted();
            return;
        }
        State = StreamState.Running;
        Pump();
    }

    /// <summary>
    /// Stop feeding new lines. Lines already in the controller's buffer will still
    /// execute — a feed hold is what stops motion, and it deliberately preserves
    /// the buffer so the job can resume exactly where it was.
    /// </summary>
    public void Pause()
    {
        if (State == StreamState.Running) State = StreamState.Paused;
    }

    public void Resume()
    {
        if (State != StreamState.Paused) return;
        State = StreamState.Running;
        Pump();
    }

    /// <summary>Stop feeding, then complete once the controller drains what it already has.</summary>
    public void Stop()
    {
        if (State is StreamState.Completed or StreamState.Idle) return;
        State = StreamState.Stopping;
        if (_inFlightBytes.Count == 0)
        {
            State = StreamState.Completed;
            RaiseCompleted();
        }
    }

    /// <summary>Abandon everything at once — for after a soft reset, which flushes the controller.</summary>
    public void Abort()
    {
        _inFlightBytes.Clear();
        _bytesInFlight = 0;
        State = StreamState.Completed;
        RaiseCompleted();
    }

    /// <summary>
    /// Feed the controller until its buffer is as full as we dare.
    ///
    /// Guarded against re-entrancy: a synchronous transport (the in-process
    /// simulator, a loopback, a very fast USB stack) can deliver the <c>ok</c> for
    /// line N from inside the write of line N, which would otherwise recurse one
    /// stack frame per line and overflow on any real job.
    /// </summary>
    private void Pump()
    {
        if (_pumping)
        {
            _pumpAgain = true;
            return;
        }

        _pumping = true;
        try
        {
            do
            {
                _pumpAgain = false;
                PumpCore();
            }
            while (_pumpAgain && State == StreamState.Running);
        }
        finally
        {
            _pumping = false;
        }

        CheckForCompletion();
    }

    private void CheckForCompletion()
    {
        if (State != StreamState.Running && State != StreamState.Stopping) return;
        if (_cursor < _lines.Count || _inFlightBytes.Count != 0) return;

        State = StreamState.Completed;
        RaiseCompleted();
    }

    private void PumpCore()
    {
        if (State != StreamState.Running) return;

        var wrote = false;
        while (_cursor < _lines.Count)
        {
            var line = _lines[_cursor];
            var size = line.Length + 1; // the newline counts against the buffer

            if (size >= _bufferSize)
            {
                // Can never fit — GRBL would answer error:11. Fail it locally rather
                // than deadlocking the queue waiting for an ok that cannot come.
                RecordError(_cursor, 11, line);
                _cursor++;
                _acknowledged++;
                if (_stopOnError)
                {
                    State = StreamState.Faulted;
                    RaiseProgress();
                    return;
                }
                continue;
            }

            if (_bytesInFlight + size >= _bufferSize) break;

            // Record the send *before* handing the bytes to the transport. A
            // synchronous transport — the in-process simulator, a loopback, a very
            // fast USB stack — can deliver this line's `ok` from inside the write
            // call, and that acknowledgement must see a consistent cursor or the
            // job never registers as finished.
            _inFlightBytes.Enqueue(size);
            _bytesInFlight += size;
            if (_bytesInFlight > PeakBytesInFlight) PeakBytesInFlight = _bytesInFlight;
            _cursor++;
            wrote = true;

            _write(line + "\n");
        }

        if (wrote) RaiseProgress();
    }

    /// <summary>Feed one <c>ok</c> back in.</summary>
    public void Acknowledge()
    {
        if (!TryPopInFlight(out var index)) return;
        _acknowledged++;
        LineAcknowledged?.Invoke(index, _lines[index]);
        AfterAcknowledge();
    }

    /// <summary>Feed one <c>error:N</c> back in.</summary>
    public void AcknowledgeError(int code)
    {
        if (!TryPopInFlight(out var index)) return;
        _acknowledged++;
        RecordError(index, code, _lines[index]);

        if (_stopOnError)
        {
            State = StreamState.Faulted;
            RaiseProgress();
            return;
        }
        AfterAcknowledge();
    }

    private bool TryPopInFlight(out int index)
    {
        index = -1;
        if (_inFlightBytes.Count == 0) return false;
        _bytesInFlight -= _inFlightBytes.Dequeue();
        index = _inFlightHead++;
        return true;
    }

    private void AfterAcknowledge()
    {
        switch (State)
        {
            case StreamState.Running:
                Pump();
                if (_cursor >= _lines.Count && _inFlightBytes.Count == 0)
                {
                    State = StreamState.Completed;
                    RaiseCompleted();
                    return;
                }
                RaiseProgress();
                break;

            case StreamState.Stopping when _inFlightBytes.Count == 0:
                State = StreamState.Completed;
                RaiseCompleted();
                break;

            default:
                RaiseProgress();
                break;
        }
    }

    private void RecordError(int index, int code, string text)
    {
        var err = new StreamError(index, code, text, GrblCodes.DescribeError(code));
        _errors.Add(err);
        Error?.Invoke(err);
    }

    public StreamProgress Snapshot() => new(_cursor, _acknowledged, _lines.Count, _bytesInFlight, State);

    private void RaiseProgress() => Progress?.Invoke(Snapshot());

    private void RaiseCompleted()
    {
        var snapshot = Snapshot();
        Progress?.Invoke(snapshot);
        Completed?.Invoke(snapshot);
    }
}
