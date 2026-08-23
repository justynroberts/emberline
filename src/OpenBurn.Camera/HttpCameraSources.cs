using SkiaSharp;

namespace OpenBurn.Camera;

/// <summary>
/// Motion JPEG over HTTP — `multipart/x-mixed-replace`, the format almost every
/// cheap IP camera and every ESP32-CAM speaks. No codec, no native dependency:
/// the stream is a sequence of complete JPEGs separated by boundary markers, so
/// decoding is Skia and a scan for the frame markers.
/// </summary>
public sealed class MjpegCameraSource : CameraSourceBase
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _url;
    private CancellationTokenSource? _loop;

    public MjpegCameraSource(string url, string? name = null)
    {
        _url = url;
        Descriptor = new CameraDescriptor(url, name ?? "MJPEG camera", CameraKind.Mjpeg, url);
    }

    public override CameraDescriptor Descriptor { get; }

    public override Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return Task.CompletedTask;

        _loop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsRunning = true;
        _ = Task.Run(() => ReadLoopAsync(_loop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken token)
    {
        try
        {
            using var response = await _http.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

            var buffer = new byte[64 * 1024];
            var frame = new List<byte>(256 * 1024);

            while (!token.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read <= 0) break;

                frame.AddRange(buffer.AsSpan(0, read).ToArray());

                // Scan for a complete JPEG: SOI (FFD8) through EOI (FFD9).
                while (true)
                {
                    var start = IndexOfMarker(frame, 0xD8, 0);
                    if (start < 0)
                    {
                        // No image started yet — do not let the buffer grow without bound.
                        if (frame.Count > 4 * 1024 * 1024) frame.Clear();
                        break;
                    }

                    var end = IndexOfMarker(frame, 0xD9, start + 2);
                    if (end < 0) break;

                    var jpeg = frame.GetRange(start, end - start + 2).ToArray();
                    frame.RemoveRange(0, end + 2);

                    var decoded = Decode(jpeg);
                    if (decoded is not null) Publish(decoded);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private static int IndexOfMarker(List<byte> data, byte marker, int from)
    {
        for (var i = Math.Max(0, from); i < data.Count - 1; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == marker) return i;
        }
        return -1;
    }

    internal static CameraFrame? Decode(byte[] jpeg)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(jpeg);
            if (bitmap is null) return null;
            return FromBitmap(bitmap);
        }
        catch (Exception)
        {
            // A partial or corrupt frame in a live stream is normal; skip it.
            return null;
        }
    }

    internal static CameraFrame FromBitmap(SKBitmap bitmap)
    {
        using var rgba = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.CopyTo(rgba, SKColorType.Rgba8888);
        return new CameraFrame(rgba.Width, rgba.Height, rgba.GetPixelSpan().ToArray());
    }

    public override Task StopAsync()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
        IsRunning = false;
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();
    }
}

/// <summary>
/// A camera that serves one still image per request. Slower than MJPEG but
/// supported by almost everything, including machine web interfaces that expose a
/// `/snapshot.jpg` endpoint.
/// </summary>
public sealed class SnapshotCameraSource : CameraSourceBase
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly string _url;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _loop;

    public SnapshotCameraSource(string url, double framesPerSecond = 2, string? name = null)
    {
        _url = url;
        _interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(framesPerSecond, 0.1, 30));
        Descriptor = new CameraDescriptor(url, name ?? "Snapshot camera", CameraKind.Snapshot, url);
    }

    public override CameraDescriptor Descriptor { get; }

    public override Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return Task.CompletedTask;

        _loop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsRunning = true;
        var token = _loop.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var bytes = await _http.GetByteArrayAsync(_url, token).ConfigureAwait(false);
                    var frame = MjpegCameraSource.Decode(bytes);
                    if (frame is not null) Publish(frame);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Fail(ex);
                    break;
                }

                await Task.Delay(_interval, token).ConfigureAwait(false);
            }
            IsRunning = false;
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public override Task StopAsync()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        _loop = null;
        IsRunning = false;
        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);
        _http.Dispose();
    }
}

/// <summary>A still image on disk, so a saved bed capture can be calibrated against.</summary>
public sealed class FileCameraSource : CameraSourceBase
{
    private readonly string _path;

    public FileCameraSource(string path)
    {
        _path = path;
        Descriptor = new CameraDescriptor(path, Path.GetFileName(path), CameraKind.File, path);
    }

    public override CameraDescriptor Descriptor { get; }

    public override Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(_path)
                ?? throw new InvalidDataException($"Could not decode {_path}.");
            Publish(MjpegCameraSource.FromBitmap(bitmap));
            IsRunning = true;
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        return Task.CompletedTask;
    }

    public override Task StopAsync()
    {
        IsRunning = false;
        return Task.CompletedTask;
    }
}
