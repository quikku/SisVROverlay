using SkiaSharp;

namespace GodanOverlay.Panels;

/// <summary>メディア操作のアイコン（フォントに頼らず図形で描く）</summary>
public static class Icons
{
    static SKPaint Fill(SKColor c) => new() { Color = c, IsAntialias = true, Style = SKPaintStyle.Fill };
    static SKPaint Stroke(SKColor c, float w) => new() { Color = c, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w, StrokeCap = SKStrokeCap.Round };

    static void Triangle(SKCanvas c, float x, float y, float w, float h, bool right, SKColor color)
    {
        using var path = new SKPath();
        if (right) { path.MoveTo(x, y); path.LineTo(x + w, y + h / 2); path.LineTo(x, y + h); }
        else { path.MoveTo(x + w, y); path.LineTo(x, y + h / 2); path.LineTo(x + w, y + h); }
        path.Close();
        using var p = Fill(color);
        c.DrawPath(path, p);
    }

    public static void Play(SKCanvas c, SKRect r, SKColor color)
    {
        float s = r.Height * 0.42f;
        Triangle(c, r.MidX - s * 0.4f, r.MidY - s / 2, s * 0.9f, s, true, color);
    }

    public static void Pause(SKCanvas c, SKRect r, SKColor color)
    {
        float s = r.Height * 0.4f, bw = s * 0.28f;
        using var p = Fill(color);
        c.DrawRect(r.MidX - s * 0.38f, r.MidY - s / 2, bw, s, p);
        c.DrawRect(r.MidX + s * 0.38f - bw, r.MidY - s / 2, bw, s, p);
    }

    public static void Next(SKCanvas c, SKRect r, SKColor color)
    {
        float s = r.Height * 0.36f;
        Triangle(c, r.MidX - s * 0.75f, r.MidY - s / 2, s * 0.75f, s, true, color);
        Triangle(c, r.MidX, r.MidY - s / 2, s * 0.75f, s, true, color);
        using var p = Fill(color);
        c.DrawRect(r.MidX + s * 0.75f, r.MidY - s / 2, s * 0.16f, s, p);
    }

    public static void Previous(SKCanvas c, SKRect r, SKColor color)
    {
        float s = r.Height * 0.36f;
        using var p = Fill(color);
        c.DrawRect(r.MidX - s * 0.91f, r.MidY - s / 2, s * 0.16f, s, p);
        Triangle(c, r.MidX - s * 0.75f, r.MidY - s / 2, s * 0.75f, s, false, color);
        Triangle(c, r.MidX, r.MidY - s / 2, s * 0.75f, s, false, color);
    }

    public static void Speaker(SKCanvas c, SKRect r, SKColor color, bool plus)
    {
        float s = r.Height * 0.42f;
        float x = r.MidX - s * 0.85f, y = r.MidY;
        using var path = new SKPath();
        path.MoveTo(x, y - s * 0.22f);
        path.LineTo(x + s * 0.3f, y - s * 0.22f);
        path.LineTo(x + s * 0.7f, y - s * 0.5f);
        path.LineTo(x + s * 0.7f, y + s * 0.5f);
        path.LineTo(x + s * 0.3f, y + s * 0.22f);
        path.LineTo(x, y + s * 0.22f);
        path.Close();
        using var f = Fill(color);
        c.DrawPath(path, f);
        using var st = Stroke(color, s * 0.14f);
        float sx = x + s * 1.05f, half = s * 0.28f;
        c.DrawLine(sx, y, sx + half * 2, y, st);
        if (plus) c.DrawLine(sx + half, y - half, sx + half, y + half, st);
    }
}
