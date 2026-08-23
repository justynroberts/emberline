using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenBurn.Camera;

namespace OpenBurn.App.Services;

/// <summary>Bridges a <see cref="CameraFrame"/> into something Avalonia can draw.</summary>
public static class FrameConverter
{
    public static WriteableBitmap ToBitmap(CameraFrame frame)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(frame.Width, frame.Height),
            new Vector(96, 96),
            PixelFormat.Rgba8888,
            AlphaFormat.Unpremul);

        using var buffer = bitmap.Lock();

        // Avalonia's row stride is not necessarily width × 4, so copy row by row
        // rather than assuming a contiguous block.
        var rowBytes = frame.Width * 4;
        for (var y = 0; y < frame.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(
                frame.Pixels,
                y * rowBytes,
                buffer.Address + y * buffer.RowBytes,
                rowBytes);
        }

        return bitmap;
    }
}
