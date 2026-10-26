using System.Numerics;
using GodanOverlay.Services;
using SkiaSharp;
using Valve.VR;

namespace GodanOverlay.Panels;

/// <summary>Windows の通知を、時計の上か視界の下の方に数秒だけ出す（レーザーは当たらない）</summary>
public sealed class ToastPanel : Panel
{
    const int W = 680, H = 210, Pad = 24;
    const double ShowSeconds = 5, WatchShowSeconds = 8; // 時計は腕を上げて見るので長め

    readonly Queue<NotificationWatcher.Toast> _queue = new();
    NotificationWatcher.Toast? _current;
    double _until, _now;
    uint _device = OpenVR.k_unTrackedDeviceIndexInvalid;

    static string Display => App.Config.Notifications.Display;
    bool OnWatch => Display == "watch";

    public ToastPanel() : base("toast", "GODAN Toast", W, H, 0.28f, 20) { }

    public override bool Grabbable => false;

    public void Push(NotificationWatcher.Toast t)
    {
        if (Display == "off") return;
        _queue.Enqueue(t);
        if (_queue.Count > 5) _queue.Dequeue();
    }

    public override void Update(double now, double dt)
    {
        _now = now;
        if (_current != null && now < _until) return;
        if (Display == "off") _queue.Clear();
        if (_queue.TryDequeue(out var next))
        {
            _current = next;
            _until = now + (OnWatch ? WatchShowSeconds : ShowSeconds);
            SetWidth(OnWatch ? 0.15f : 0.28f);
            Invalidate();
            Show();
            MarkTransformDirty();
            App.Input.Haptic(App.Watch.AttachedHand, 0.05f, 0.3f);
        }
        else if (_current != null)
        {
            _current = null;
            Hide();
        }
        // 時計の手のコントローラーが変わったら付け直す
        if (Visible && OnWatch && App.Watch.Device != _device) MarkTransformDirty();
    }

    protected override void ApplyTransform()
    {
        if (OnWatch)
        {
            // 時計のすぐ上（指先の側）に、時計と同じ向きで
            _device = App.Watch.Device;
            if (_device == OpenVR.k_unTrackedDeviceIndexInvalid) return;
            float up = App.Watch.HeightMeters / 2 + HeightMeters / 2 + 0.008f;
            var m = (Matrix4x4.CreateTranslation(0, up, 0.002f) * App.Watch.Offset).ToHmd();
            Ov?.SetOverlayTransformTrackedDeviceRelative(Handle, _device, ref m);
            return;
        }
        // 視界の少し下、こちらを向くように
        var rel = (Matrix4x4.CreateRotationX(-0.3f) * Matrix4x4.CreateTranslation(0, -0.2f, -0.6f)).ToHmd();
        Ov?.SetOverlayTransformTrackedDeviceRelative(Handle, OpenVR.k_unTrackedDeviceIndex_Hmd, ref rel);
    }

    protected override void Draw(SKCanvas c)
    {
        if (_current is not { } t) return;
        Ui.RoundRect(c, new SKRect(0, 0, W, H), 26, new SKColor(0x1b, 0x22, 0x2c, 235), Ui.Border, 3);
        float x = Pad, w = W - Pad * 2;
        Ui.TextLeftFit(c, t.App, x, 34, 22, w, Ui.Muted);
        Ui.TextLeftFit(c, t.Title, x, 80, 32, w, Ui.Text, bold: true);
        var lines = Ui.Wrap(t.Body, 26, w, 2);
        for (int i = 0; i < lines.Count; i++) Ui.TextLeft(c, lines[i], x, 128 + i * 38, 26, Ui.Text);
    }

    // 確認用
    public void DebugShow(NotificationWatcher.Toast t) { _current = t; Show(); Invalidate(); }
}
