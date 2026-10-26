using System.Numerics;
using GodanOverlay.Services;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace GodanOverlay.Panels;

/// <summary>1つのウィンドウを映すパネル。Windows.Graphics.Capture で取り込む</summary>
public sealed class WindowPanel : CapturePanel
{
    static int _serial;

    readonly IntPtr _hwnd;
    readonly string _process;
    WindowCapture? _capture;
    Vortice.RawRect _rect;
    string _title;
    bool _placed;
    int _slot;

    public WindowPanel(WinApi.WindowInfo info, DesktopConfig cfg, int slot)
        : base("window." + (++_serial), "GODAN Window " + _serial, 0.6f, cfg)
    {
        _hwnd = info.Hwnd;
        _title = info.Title;
        _process = info.Process;
        _slot = slot;
    }

    public IntPtr Hwnd => _hwnd;
    public override string Title => $"{_title}（{_process}）";
    public override bool Closable => true;

    protected override Vortice.RawRect ScreenRect => _rect;

    protected override bool OpenSource()
    {
        CloseSource();
        if (!WinApi.IsWindow(_hwnd))
        {
            App.Notify("ウィンドウなし", true);
            Close();
            return false;
        }
        try
        {
            _capture = new WindowCapture(_hwnd);
            _capture.Closed += () => App.Post(() =>
            {
                App.Notify("閉じられました");
                Close();
            });
        }
        catch (Exception e)
        {
            Log.Error("ウィンドウを取り込めません: " + e.Message);
            App.Notify("取り込めません", true);
            return false;
        }
        UpdateRect();
        SetupOutput(_capture.Size.Width, _capture.Size.Height);
        return true;
    }

    protected override void CloseSource()
    {
        _capture?.Dispose();
        _capture = null;
    }

    void UpdateRect()
    {
        var r = WinApi.GetFrameBounds(_hwnd);
        _rect = new Vortice.RawRect(r.Left, r.Top, r.Right, r.Bottom);
    }

    protected override void PollSource()
    {
        if (_capture == null) return;
        _capture.TryGetFrame((tex, w, h) =>
        {
            if (w != SrcW || h != SrcH) SetupOutput(w, h); // ウィンドウの大きさが変わった
            if (Source == null) return;
            // 取り込みバッファの方が大きいことがあるので、有効な範囲だけコピーする
            var desc = tex.Description;
            int cw = Math.Min(w, (int)desc.Width), ch = Math.Min(h, (int)desc.Height);
            Gpu.Context.CopySubresourceRegion(Source, 0, 0, 0, 0, tex, 0, new Box(0, 0, 0, cw, ch, 1));
            MarkFrame();
        });
        UpdateRect();
        var t = WinApi.GetTitle(_hwnd);
        if (t.Length > 0) _title = t;
    }

    bool _warnedCovered;

    protected override bool BeforeClick(int x, int y)
    {
        if (WinApi.EnsureTopAt(_hwnd, x, y)) return true;
        if (!_warnedCovered)
        {
            _warnedCovered = true;
            Log.Info($"「{_title}」を一番手前に出せないので、クリックをウィンドウへ直接送ります");
        }
        return false;
    }

    protected override IntPtr DirectTarget => _hwnd;

    protected override void PlaceDefault()
    {
        if (_placed || HudLocked) return;
        PlaceInFront();
    }

    /// <summary>目の前の少し右に、開いた順にずらして置く</summary>
    public override void PlaceInFront()
    {
        if (HudLocked) return;
        var m = VrMath.InFrontOf(App.Input.HmdPose, 0.8f, 0.05f, 0);
        var right = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
        m.Translation += right * (0.25f + 0.08f * (_slot % 4)) + new Vector3(0, -0.05f * (_slot % 4), 0);
        SetWorld(m);
        _placed = true;
    }
}
