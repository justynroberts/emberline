namespace Emberline.Camera;

public enum CameraKind
{
    /// <summary>A UVC webcam attached to this computer.</summary>
    Usb,

    /// <summary>An IP camera streaming motion JPEG over HTTP.</summary>
    Mjpeg,

    /// <summary>An IP camera serving a still image per request.</summary>
    Snapshot,

    /// <summary>RTSP, via an external decoder.</summary>
    Rtsp,

    /// <summary>A still image on disk. Useful for calibrating from a saved capture.</summary>
    File,

    /// <summary>A generated test pattern. Lets the whole camera path run in CI.</summary>
    Synthetic,
}

public sealed record CameraDescriptor(
    string Id,
    string Name,
    CameraKind Kind,
    string? Address = null)
{
    public override string ToString() => Address is null ? Name : $"{Name} · {Address}";
}

/// <summary>
/// A source of frames.
///
/// Camera support is a first-class subsystem in the PRD, and the reason it is an
/// interface rather than a class is that the useful sources differ per platform:
/// UVC capture needs a native path on each operating system, while MJPEG and
/// snapshot cameras are plain HTTP and work identically everywhere. Everything
/// above this interface — calibration, rectification, overlay, detection — is
/// platform-independent and testable.
/// </summary>
public interface ICameraSource : IAsyncDisposable
{
    CameraDescriptor Descriptor { get; }
    bool IsRunning { get; }

    event Action<CameraFrame>? FrameReceived;
    event Action<Exception>? Failed;

    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();

    /// <summary>Grab one frame. Starts the source if it is not already running.</summary>
    Task<CameraFrame?> CaptureAsync(CancellationToken cancellationToken = default);
}

public abstract class CameraSourceBase : ICameraSource
{
    public abstract CameraDescriptor Descriptor { get; }
    public bool IsRunning { get; protected set; }

    public event Action<CameraFrame>? FrameReceived;
    public event Action<Exception>? Failed;

    protected CameraFrame? LastFrame { get; private set; }

    protected void Publish(CameraFrame frame)
    {
        LastFrame = frame;
        FrameReceived?.Invoke(frame);
    }

    protected void Fail(Exception ex) => Failed?.Invoke(ex);

    public abstract Task StartAsync(CancellationToken cancellationToken = default);
    public abstract Task StopAsync();

    public virtual async Task<CameraFrame?> CaptureAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRunning) await StartAsync(cancellationToken).ConfigureAwait(false);

        // Wait briefly for the first frame rather than returning null on a source
        // that is about to produce one.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (LastFrame is null && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return LastFrame?.Clone();
    }

    public virtual async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
