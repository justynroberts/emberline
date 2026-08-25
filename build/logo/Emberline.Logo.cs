using SkiaSharp;

// The Emberline mark.
//
// A beam converging to a point on a line, and the line glowing behind it where it
// has already passed. That is literally what the machine does, and it says the
// name without spelling it: the ember, and the line.
//
// Cyan ahead of the beam, ember behind it — the same duotone the interface uses,
// carrying the same meaning it does there (cyan for motion and safety, ember for
// the beam and the heat).

const int S = 1024;

static SKColor C(string hex) => SKColor.Parse(hex);

static void Draw(SKCanvas canvas, int size, bool ground)
{
    var k = size / 1024f;
    canvas.Clear(SKColors.Transparent);

    var focusX = 512 * k;
    var lineY = 660 * k;

    if (ground)
    {
        // Dark rounded ground, warmed very slightly towards the bottom where the
        // heat is, so the tile is not flat black.
        using var bg = new SKPaint { IsAntialias = true };
        bg.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(0, size),
            [C("#1B1512"), C("#0E0B0A")], null, SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(new SKRect(0, 0, size, size), 230 * k, 230 * k, bg);
    }

    // The beam: wide at the top, converging to the focus. Brighter as it narrows.
    using (var beam = new SKPaint { IsAntialias = true })
    {
        var builder = new SKPathBuilder();
        builder.MoveTo(focusX - 150 * k, 168 * k);
        builder.LineTo(focusX + 150 * k, 168 * k);
        builder.LineTo(focusX + 7 * k, lineY);
        builder.LineTo(focusX - 7 * k, lineY);
        builder.Close();

        beam.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 168 * k), new SKPoint(0, lineY),
            [C("#FF7A3D").WithAlpha(70), C("#FF8A4D").WithAlpha(205), C("#FFC79B")],
            [0f, 0.5f, 1f], SKShaderTileMode.Clamp);

        using var path = builder.Detach();
        canvas.DrawPath(path, beam);
    }

    // The work line. Cyan where the beam has not reached, ember where it has —
    // hottest just behind the focus and cooling towards the left.
    var half = 17 * k;

    using (var ahead = new SKPaint { IsAntialias = true, Color = C("#3FD0E3") })
    {
        canvas.DrawRoundRect(
            new SKRect(focusX, lineY - half, 856 * k, lineY + half), half, half, ahead);
    }

    using (var behind = new SKPaint { IsAntialias = true })
    {
        behind.Shader = SKShader.CreateLinearGradient(
            new SKPoint(168 * k, 0), new SKPoint(focusX, 0),
            [C("#A33A14"), C("#F0601F"), C("#FFD1A8")],
            [0f, 0.6f, 1f], SKShaderTileMode.Clamp);

        canvas.DrawRoundRect(
            new SKRect(168 * k, lineY - half, focusX + half, lineY + half), half, half, behind);
    }

    // The hot spot where the beam meets the work.
    using (var glow = new SKPaint { IsAntialias = true })
    {
        glow.Shader = SKShader.CreateRadialGradient(
            new SKPoint(focusX, lineY), 132 * k,
            [C("#FFD9B0").WithAlpha(210), C("#FF7A3D").WithAlpha(90), C("#FF7A3D").WithAlpha(0)],
            [0f, 0.35f, 1f], SKShaderTileMode.Clamp);
        canvas.DrawCircle(focusX, lineY, 132 * k, glow);
    }

    using (var core = new SKPaint { IsAntialias = true, Color = C("#FFF3E6") })
    {
        canvas.DrawCircle(focusX, lineY, 26 * k, core);
    }
}

static void Write(string path, int size, bool ground)
{
    using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    using (var canvas = new SKCanvas(bitmap)) { Draw(canvas, size, ground); canvas.Flush(); }
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, data.ToArray());
    Console.WriteLine($"{path}  {size}x{size}  {data.Size / 1024} KB");
}

var dir = Path.GetDirectoryName(Environment.ProcessPath)!;
var outDir = args.Length > 0 ? args[0] : dir;
Directory.CreateDirectory(outDir);

Write(Path.Combine(outDir, "emberline.png"), 1024, ground: true);
Write(Path.Combine(outDir, "emberline-mark.png"), 1024, ground: false);
foreach (var s in new[] { 16, 32, 64, 128, 256 })
    Write(Path.Combine(outDir, $"preview-{s}.png"), s, ground: true);
