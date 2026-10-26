using SkiaSharp;

namespace GodanOverlay.Panels;

/// <summary>Material Symbols のアイコン（IconData.cs の SVG パスを Skia で描く）</summary>
public static class Icons
{
    static readonly Dictionary<string, SKPath?> Cache = new();

    static SKPath? PathFor(string name)
    {
        if (Cache.TryGetValue(name, out var p)) return p;
        p = IconData.Paths.TryGetValue(name, out var d) ? SKPath.ParseSvgPathData(d) : null;
        if (p == null) Log.Warn("アイコンがありません: " + name);
        Cache[name] = p;
        return p;
    }

    /// <summary>中心 (cx, cy)、一辺 size の正方形に描く</summary>
    public static void Draw(SKCanvas c, string name, float cx, float cy, float size, SKColor color)
    {
        if (PathFor(name) is not { } path) return;
        using var save = new SKAutoCanvasRestore(c);
        // viewBox は 0 -960 960 960
        c.Translate(cx - size / 2, cy - size / 2);
        c.Scale(size / 960f);
        c.Translate(0, 960);
        using var p = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
        c.DrawPath(path, p);
    }
}
