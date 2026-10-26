using System.Numerics;
using SkiaSharp;

namespace GodanOverlay.Panels;

/// <summary>レーザーの線。縦長の板を光線方向に引き伸ばして使う</summary>
public sealed class LaserVisual : Panel
{
    protected override bool Animated => false;

    const int TW = 8, TH = 128;
    readonly float _naturalH;

    public LaserVisual(Hand hand, float widthMeters) : base("laser." + hand, "GODAN Laser " + hand, TW, TH, widthMeters, 10)
    {
        _naturalH = WidthMeters * TH / TW;
    }

    public override bool Grabbable => false;

    /// <summary>origin から length メートルぶん dir に伸ばし、目の方へ面を向ける</summary>
    public void Aim(Vector3 origin, Vector3 dir, float length, Vector3 eye)
    {
        length = Math.Max(0.01f, length);
        var center = origin + dir * (length / 2);
        var toEye = eye - center;
        var z = toEye - dir * Vector3.Dot(toEye, dir);
        if (z.LengthSquared() < 1e-8f) z = Vector3.UnitY;
        z = Vector3.Normalize(z);
        var x = Vector3.Cross(dir, z);
        SetWorld(VrMath.FromBasis(x, dir * (length / _naturalH), z, center));
    }

    protected override void Draw(SKCanvas c)
    {
        // 上（当たった側）が明るく、手元に向かって薄くなる
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(0, TH),
            [Ui.Accent.WithAlpha(230), Ui.Accent.WithAlpha(40)], SKShaderTileMode.Clamp);
        using var p = new SKPaint { Shader = shader };
        c.DrawRect(0, 0, TW, TH, p);
    }
}

/// <summary>レーザーが当たっている位置の印</summary>
public sealed class CursorVisual : Panel
{
    protected override bool Animated => false;

    const int S = 64;

    public CursorVisual(Hand hand) : base("cursor." + hand, "GODAN Cursor " + hand, S, S, 0.014f, 11) { }

    public override bool Grabbable => false;

    /// <summary>当たった位置に、その面の向き（曲面なら曲がった面の向き）で置く</summary>
    public void Place(Vector3 hit, Vector3 normal, Matrix4x4 targetWorld, Vector3 rayOrigin, bool pressed)
    {
        var z = normal;
        if (Vector3.Dot(rayOrigin - hit, z) < 0) z = -z;
        var y = Vector3.Normalize(new Vector3(targetWorld.M21, targetWorld.M22, targetWorld.M23));
        var x = Vector3.Normalize(Vector3.Cross(y, z));
        y = Vector3.Cross(z, x);
        SetWorld(VrMath.FromBasis(x, y, z, hit + z * 0.003f));
        if (pressed != _pressed) { _pressed = pressed; Invalidate(); }
    }

    bool _pressed;

    protected override void Draw(SKCanvas c)
    {
        using var ring = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6 };
        using var dot = new SKPaint { Color = _pressed ? Ui.Accent : SKColors.White, IsAntialias = true };
        using var shade = new SKPaint { Color = new SKColor(0, 0, 0, 120), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 10 };
        c.DrawCircle(S / 2f, S / 2f, 24, shade);
        c.DrawCircle(S / 2f, S / 2f, 24, ring);
        c.DrawCircle(S / 2f, S / 2f, _pressed ? 14 : 8, dot);
    }
}
