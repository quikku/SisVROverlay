using System.Numerics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GodanOverlay.Panels;

/// <summary>
/// モニター全体を映すパネル。Desktop Duplication で取り込む。
/// 1枚目（時計の「デスクトップ」）は映すモニターを設定に保存し、「モニター追加」で足したパネルは閉じられる
/// </summary>
public sealed class DesktopPanel : CapturePanel
{
    IDXGIOutputDuplication? _dup;
    Vortice.RawRect _monitor;
    int _monitorCount;
    bool _placed;
    static int _serial;

    /// <summary>「モニター追加」で足したパネルか（1枚目は false）</summary>
    readonly bool _extra;
    int _extraMonitor;

    /// <summary>映しているモニター（0 から）</summary>
    public int Monitor
    {
        get => _extra ? _extraMonitor : Cfg.MonitorIndex;
        private set { if (_extra) _extraMonitor = value; else Cfg.MonitorIndex = value; }
    }

    public DesktopPanel(DesktopConfig cfg) : base("desktop", "GODAN Desktop", cfg.WidthMeters, cfg) { }

    /// <summary>「モニター追加」で足すパネル</summary>
    DesktopPanel(DesktopConfig cfg, int monitor)
        : base("desktop." + (++_serial), "GODAN Desktop " + _serial, cfg.WidthMeters, cfg)
    {
        _extra = true;
        _extraMonitor = monitor;
    }

    public override string Title => $"デスクトップ（モニター{Monitor + 1}）";
    public override bool Closable => _extra;

    protected override Vortice.RawRect ScreenRect => _monitor;

    protected override bool OpenSource()
    {
        CloseSource();
        _monitorCount = 0;
        while (Gpu.Adapter.EnumOutputs((uint)_monitorCount, out var o).Success) { o.Dispose(); _monitorCount++; }
        if (_monitorCount == 0)
        {
            App.Notify("モニターなし", true);
            return false;
        }
        if (Monitor >= _monitorCount || Monitor < 0) Monitor = 0;

        try
        {
            Gpu.Adapter.EnumOutputs((uint)Monitor, out var output).CheckError();
            using (output)
            using (var output1 = output.QueryInterface<IDXGIOutput1>())
            {
                _monitor = output.Description.DesktopCoordinates;
                _dup = output1.DuplicateOutput(Gpu.Device);
            }
        }
        catch (Exception e)
        {
            Log.Error("画面の取り込みを開始できません: " + e.Message);
            App.Notify("取り込めません", true);
            return false;
        }

        int w = _monitor.Right - _monitor.Left, h = _monitor.Bottom - _monitor.Top;
        SetupOutput(w, h);
        BuildToolbar();
        Log.Info($"モニター{Monitor + 1}: {w}x{h}");
        return true;
    }

    protected override void CloseSource()
    {
        _dup?.Dispose();
        _dup = null;
    }

    double _retryAt;

    protected override void PollSource()
    {
        if (_dup == null)
        {
            // 解像度変更などで途切れたら少し待って作り直す
            if (Environment.TickCount64 / 1000.0 >= _retryAt)
            {
                _retryAt = Environment.TickCount64 / 1000.0 + 2;
                OpenSource();
            }
            return;
        }
        var hr = _dup.AcquireNextFrame(0, out var info, out var resource);
        if (hr == Vortice.DXGI.ResultCode.WaitTimeout) return;
        if (hr.Failure)
        {
            // 解像度変更・UAC画面・フルスクリーン切り替えなどで失われる
            Log.Warn($"画面の取り込みが途切れました ({hr}). 作り直します");
            CloseSource();
            _retryAt = 0;
            return;
        }
        try
        {
            if (info.LastPresentTime != 0 && Source != null)
            {
                using var tex = resource.QueryInterface<ID3D11Texture2D>();
                Gpu.Context.CopyResource(Source, tex);
                MarkFrame();
            }
        }
        finally
        {
            resource.Dispose();
            _dup.ReleaseFrame();
        }
    }

    protected override void AddToolbarButtons(Func<string, string, UiButton> add)
    {
        // モニターが1枚なら、切り替えも追加も出さない
        if (_monitorCount < 2) return;
        add("monitor", $"モニター {Monitor + 1}/{_monitorCount}");
        // まだ出していないモニターがあれば、そのパネルを足せる
        if (NextFreeMonitor() >= 0) add("addmonitor", "モニター追加");
    }

    protected override bool OnToolbarButton(string id)
    {
        if (id == "addmonitor")
        {
            int m = NextFreeMonitor();
            if (m < 0) return true;
            var p = new DesktopPanel(Cfg, m);
            App.Panels.Add(p);
            p.Show();
            // 今のパネルのすぐ右に、同じ向きで並べる
            var right = Vector3.Normalize(new Vector3(World.M11, World.M12, World.M13));
            var w = World;
            w.Translation += right * (WidthMeters + p.WidthMeters) / 2 * 1.04f;
            p.SetWorld(w);
            p._placed = true;
            RefreshAllToolbars();
            return true;
        }
        if (id != "monitor") return false;
        int next = (Monitor + 1) % Math.Max(1, _monitorCount);
        // 切り替え先を別のパネルが映していたら、そのパネルと映すモニターを入れ替える（同じモニターを2枚に出さない）
        var other = App.Panels.OfType<DesktopPanel>().FirstOrDefault(p => p != this && !p.IsClosed && p.Monitor == next);
        // 同じモニターを同時に2つ取り込むことはできないので、先にこちらの取り込みを止めてから渡す
        CloseSource();
        if (other != null) other.SetMonitor(Monitor);
        SetMonitor(next);
        RefreshAllToolbars();
        return true;
    }

    void SetMonitor(int m)
    {
        Monitor = m;
        if (!_extra && !Headless) App.Config.Save();
        if (IsOpen || Visible) OpenSource();
    }

    /// <summary>まだどのパネルにも映していないモニター（なければ -1）</summary>
    int NextFreeMonitor()
    {
        var shown = App.Panels.OfType<DesktopPanel>().Where(p => !p.IsClosed).Select(p => p.Monitor).ToHashSet();
        for (int i = 0; i < _monitorCount; i++) if (!shown.Contains(i)) return i;
        return -1;
    }

    /// <summary>モニターを足したり閉じたりしたら、どのパネルの「モニター追加」も出し直す</summary>
    static void RefreshAllToolbars()
    {
        foreach (var p in App.Panels.OfType<DesktopPanel>()) p.BuildToolbar();
    }

    public override void Close()
    {
        base.Close();
        RefreshAllToolbars();
    }

    // 確認用
    public void DebugToolbar(string id) => OnToolbarButton(id);

    protected override void PlaceDefault()
    {
        if (HudLocked) return;
        var hmd = App.Input.HmdPose;
        if (!_placed || Vector3.Distance(World.Translation, hmd.Translation) > 4f) PlaceInFront();
    }

    public override void PlaceInFront()
    {
        if (HudLocked) return;
        SetWorld(VrMath.InFrontOf(App.Input.HmdPose, 0.9f, 0.1f, 0));
        _placed = true;
    }
}
