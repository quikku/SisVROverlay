using System.Numerics;
using SkiaSharp;
using Valve.VR;

namespace GodanOverlay.Panels;

/// <summary>
/// フリック中の候補（押したキーと上下左右の文字）を出す小さいオーバーレイ。
/// キーボードとは別の板なので、端のキーでもキーボードの外へはみ出して出せる。レーザーは当たらない。
/// </summary>
public sealed class FlickPopup : Panel
{
    protected override bool Animated => false;

    const int W = 360, H = 320;

    (string center, string? up, string? down, string? left, string? right, FlickDir current, float w, float h) _content;
    bool _hud;

    public FlickPopup() : base("flickpop", "GODAN Flick", W, H, 0.1f, 6) { }

    public override bool Grabbable => false;

    /// <summary>出す文字と、今選んでいる方向。w, h は候補1つの大きさ（キーボードのピクセル）</summary>
    public void SetContent(string center, FlickDef? f, FlickDir current, float w, float h)
    {
        var c = (center, f?.Up, f?.Down, f?.Left, f?.Right, current, w, h);
        if (c == _content) return;
        _content = c;
        Invalidate();
    }

    /// <summary>キーボードの keyRect（キーボードのピクセル座標）の真ん中に、キーボードと同じ大きさの比率で重ねる</summary>
    public void Place(Panel keyboard, SKRect keyRect)
    {
        float metersPerPixel = keyboard.WidthMeters / keyboard.PixW;
        float width = metersPerPixel * W;
        if (MathF.Abs(width - WidthMeters) > 1e-5f) SetWidth(width);
        // キーボードの表面から少し手前（パネルの +Z が利用者側）
        var local = Matrix4x4.CreateTranslation(
            (keyRect.MidX - keyboard.PixW / 2f) * metersPerPixel,
            (keyboard.PixH / 2f - keyRect.MidY) * metersPerPixel,
            0.004f);
        var world = local * keyboard.World;
        if (world != World || _hud != keyboard.HudLocked)
        {
            _hud = keyboard.HudLocked;
            SetWorld(world);
        }
    }

    protected override void ApplyTransform()
    {
        if (_hud)
        {
            // 視界に固定したキーボードは頭についてくるので、こちらも頭を基準に置く
            var rel = (World * App.Input.HmdPose.Inverse()).ToHmd();
            Ov?.SetOverlayTransformTrackedDeviceRelative(Handle, OpenVR.k_unTrackedDeviceIndex_Hmd, ref rel);
            return;
        }
        base.ApplyTransform();
    }

    protected override void Draw(SKCanvas c)
    {
        var (center, up, down, left, right, current, w, h) = _content;
        float cx = W / 2f, cy = H / 2f;
        void Item(FlickDir d, string? label, float x, float y)
        {
            if (label == null) return;
            var r = new SKRect(x - w / 2, y - h / 2, x + w / 2, y + h / 2);
            bool on = d == current;
            var fill = on ? Ui.Accent : d == FlickDir.Center ? Ui.Accent2 : Ui.Panel;
            Ui.RoundRect(c, r, 14, fill, on ? Ui.Accent : Ui.Border, 3);
            Ui.TextCenter(c, label, r, 40, on ? SKColors.Black : Ui.Text, bold: true);
        }
        Item(FlickDir.Center, center, cx, cy);
        Item(FlickDir.Up, up, cx, cy - h);
        Item(FlickDir.Down, down, cx, cy + h);
        Item(FlickDir.Left, left, cx - w, cy);
        Item(FlickDir.Right, right, cx + w, cy);
    }
}
