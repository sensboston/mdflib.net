using SkiaSharp;

// Renders the application icon (app.ico, app.png) and the package icon (icon.png).
// Usage: dotnet run --project apps/IconRenderer -- <output folder>
string outDir = args[0];
Directory.CreateDirectory(outDir);

SKPath Wave(float s, float x0, float x1, float yMid, float amp, int seed)
{
    var p = new SKPath();
    var rng = new Random(seed);
    int n = 64;
    for (int i = 0; i <= n; i++)
    {
        float t = i / (float)n;
        float x = x0 + (x1 - x0) * t;
        float y = yMid - amp * (float)(Math.Sin(t * 6.2 + seed) * 0.6 + Math.Sin(t * 13.1 + seed * 2) * 0.25);
        if (i == 0) p.MoveTo(x, y); else p.LineTo(x, y);
    }
    return p;
}

// Application icon: dark plot panel, two traces, a dashed cursor.
byte[] AppIcon(int size)
{
    float s = size;
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    float r = s * 0.2f;
    using (var bg = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, s), new[] { new SKColor(0x2B, 0x2F, 0x38), new SKColor(0x16, 0x18, 0x1D) }, SKShaderTileMode.Clamp) })
        c.DrawRoundRect(new SKRect(0, 0, s, s), r, r, bg);
    if (size >= 32)
    {
        using var grid = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 28), StrokeWidth = Math.Max(1, s / 128) };
        for (int i = 1; i < 4; i++) c.DrawLine(s * 0.12f, s * (0.2f + i * 0.15f), s * 0.88f, s * (0.2f + i * 0.15f), grid);
    }
    float w = Math.Max(1.5f, s * (size <= 24 ? 0.075f : 0.045f));
    using (var p1 = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = new SKColor(0x4F, 0xC3, 0xF7) })
        c.DrawPath(Wave(s, s * 0.12f, s * 0.88f, s * 0.40f, s * 0.16f, 1), p1);
    using (var p2 = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = new SKColor(0xFF, 0xB7, 0x4D) })
        c.DrawPath(Wave(s, s * 0.12f, s * 0.88f, s * 0.66f, s * 0.12f, 4), p2);
    using (var cur = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 200), StrokeWidth = Math.Max(1, s * 0.022f), PathEffect = size >= 48 ? SKPathEffect.CreateDash(new[] { s * 0.05f, s * 0.035f }, 0) : null })
        c.DrawLine(s * 0.62f, s * 0.14f, s * 0.62f, s * 0.86f, cur);
    using var img = surface.Snapshot();
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// Package icon: blue tile, "MDF" and a trace beneath.
byte[] PackageIcon(int size)
{
    float s = size;
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    float r = s * 0.18f;
    using (var bg = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(s, s), new[] { new SKColor(0x1E, 0x88, 0xE5), new SKColor(0x0D, 0x47, 0xA1) }, SKShaderTileMode.Clamp) })
        c.DrawRoundRect(new SKRect(0, 0, s, s), r, r, bg);
    using var face = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
    using var font = new SKFont(face, s * 0.34f);
    using var text = new SKPaint { IsAntialias = true, Color = SKColors.White };
    float tw = font.MeasureText("MDF");
    c.DrawText("MDF", (s - tw) / 2, s * 0.50f, font, text);
    using var wave = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = s * 0.045f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = new SKColor(0x80, 0xDE, 0xEA) };
    c.DrawPath(Wave(s, s * 0.14f, s * 0.86f, s * 0.70f, s * 0.10f, 2), wave);
    using var img = surface.Snapshot();
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

void WriteIco(string path, int[] sizes, Func<int, byte[]> render)
{
    var images = sizes.Select(render).ToArray();
    using var w = new BinaryWriter(File.Create(path));
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)0); w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)32);
        w.Write(images[i].Length); w.Write(offset);
        offset += images[i].Length;
    }
    foreach (var img in images) w.Write(img);
}

int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
WriteIco(Path.Combine(outDir, "app.ico"), icoSizes, AppIcon);
File.WriteAllBytes(Path.Combine(outDir, "app.png"), AppIcon(512));
File.WriteAllBytes(Path.Combine(outDir, "app-32.png"), AppIcon(32));
File.WriteAllBytes(Path.Combine(outDir, "icon.png"), PackageIcon(128));
File.WriteAllBytes(Path.Combine(outDir, "icon-512.png"), PackageIcon(512));
Console.WriteLine("written to " + outDir);
