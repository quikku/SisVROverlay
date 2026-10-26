using System.Numerics;
using SkiaSharp;
using Valve.VR;

namespace GodanOverlay.Panels;

/// <summary>
/// 手首に付く時計。時刻、再生中の曲と操作、各パネルの出し入れ。
/// もう片方の手のグリップでつかむと位置を調整でき、離すと設定に保存される。
/// </summary>
public sealed class WatchPanel : Panel
{
    const int W = 560, H = 360, Pad = 14, Gap = 10;

    readonly WatchConfig _cfg;
    readonly List<UiButton> _buttons = new();
    readonly Vector2?[] _hover = new Vector2?[2];
    readonly UiButton?[] _pressed = new UiButton?[2];

    public Hand AttachedHand { get; private set; }
    /// <summary>取り付け先のコントローラー（通知を時計の上に出すのに使う）</summary>
    public uint Device => _device;
    public Matrix4x4 Offset => _offset;
    Matrix4x4 _offset;
    uint _device = OpenVR.k_unTrackedDeviceIndexInvalid;
    bool _deviceVisible = true;

    string _clock = "";
    string _notice = "";
    bool _noticeError;
    double _noticeUntil, _now;

    public WatchPanel(WatchConfig cfg) : base("watch", "GODAN Watch", W, H, cfg.WidthMeters, 3)
    {
        _cfg = cfg;
        SetHand(cfg.Hand == "left" ? Hand.Left : Hand.Right);

        // メディア操作の段
        string[] media = ["prev", "play", "next", "voldown", "volup"];
        float mw = (W - Pad * 2 - Gap * 4) / 5f, my = 140, mh = 66;
        for (int i = 0; i < media.Length; i++)
        {
            float x = Pad + i * (mw + Gap);
            _buttons.Add(new UiButton { Id = media[i], Label = "", Rect = new SKRect(x, my, x + mw, my + mh) });
        }

        // パネルの段
        string[] ids = ["keyboard", "desktop", "menu", "summon"];
        string[] labels = ["キーボード", "デスクトップ", "メニュー", "手前へ"];
        float bw = (W - Pad * 2 - Gap * (ids.Length - 1)) / ids.Length, by = my + mh + Gap, bh = H - by - Pad;
        for (int i = 0; i < ids.Length; i++)
        {
            float x = Pad + i * (bw + Gap);
            _buttons.Add(new UiButton { Id = ids[i], Label = labels[i], Rect = new SKRect(x, by, x + bw, by + bh) });
        }
    }

    public void SetHand(Hand hand)
    {
        AttachedHand = hand;
        _offset = (hand == Hand.Right ? _cfg.RightOffset : _cfg.LeftOffset).ToMatrix();
        _device = OpenVR.k_unTrackedDeviceIndexInvalid;
        MarkTransformDirty();
    }

    public override bool Grabbable => true;
    public override string Title => "時計";

    // 時計を消すとロックしていないパネルも消え、時計を出すと戻る
    protected override void OnHide() => HidePanels();
    protected override void OnShow() => RestorePanels();

    /// <summary>トグルボタンの2回押し。時計を消さない設定なら、ほかのパネルだけを出し入れする</summary>
    public void ButtonToggle()
    {
        if (!_cfg.AlwaysShow || !Visible) { Toggle(); return; }
        if (App.Panels.Any(p => p.HiddenByWatch && !p.Visible)) RestorePanels();
        else HidePanels();
    }

    void HidePanels()
    {
        // 頭の位置からのずれ（方角と距離）だけを覚える。消すときにどちらを向いていたかは使わない
        var fromHead = Matrix4x4.CreateTranslation(-App.Input.HmdPose.Translation);
        foreach (var p in App.Panels)
        {
            if (p == this) continue;
            if (p.Visible && !p.Locked)
            {
                p.HeadRelative = p.World * fromHead;
                p.Hide();
                p.HiddenByWatch = true;
            }
        }
    }

    void RestorePanels()
    {
        var head = App.Input.HmdPose;
        foreach (var p in App.Panels)
        {
            if (p == this) continue;
            if (p.HiddenByWatch && !p.Visible)
            {
                p.Show();
                // プレイスペースを動かしてずれていても、頭の位置から同じ方角・同じ距離の所に戻す。
                // 向きは戻さずに自分の方へ向ける（手前への傾きだけは元のまま）
                if (_cfg.RestoreRelativePosition && !p.HudLocked && p.HeadRelative is { } rel)
                {
                    var pos = rel.Translation + head.Translation;
                    float tilt = MathF.Asin(Math.Clamp(rel.M32, -1f, 1f));
                    p.SetWorld(VrMath.FacingFrom(pos, head.Translation, head.Forward(), tilt));
                }
            }
            p.HiddenByWatch = false;
            p.HeadRelative = null;
        }
    }

    public override void Update(double now, double dt)
    {
        _now = now;
        var clock = DateTime.Now.ToString("HH:mm");
        if (clock != _clock) { _clock = clock; Invalidate(); }
        if (_notice.Length > 0 && now > _noticeUntil) { _notice = ""; Invalidate(); }

        foreach (var b in _buttons)
        {
            bool active = b.Id switch
            {
                "keyboard" => App.Keyboard.Visible,
                "desktop" => App.Desktop.Visible,
                "menu" => App.Menu.Visible,
                _ => false,
            };
            if (active != b.Active) { b.Active = active; Invalidate(); }
        }

        if (Headless) return;

        // 取り付け先のコントローラーの姿勢に合わせて当たり判定用のワールド行列を更新
        var hand = App.Input.Hands[(int)AttachedHand];
        if (hand.DeviceIndex != _device) { _device = hand.DeviceIndex; MarkTransformDirty(); }
        bool ok = App.Input.TryGetDevicePose(_device, out var devicePose);
        if (ok) World = _offset * devicePose;

        // コントローラーを見失ったら隠す
        if (Visible && ok != _deviceVisible)
        {
            _deviceVisible = ok;
            if (ok) MarkTransformDirty(); else VrHide();
        }
    }

    public override bool Flush() => _deviceVisible && base.Flush();

    protected override void ApplyTransform()
    {
        if (_device == OpenVR.k_unTrackedDeviceIndexInvalid) return;
        var m = _offset.ToHmd();
        Ov?.SetOverlayTransformTrackedDeviceRelative(Handle, _device, ref m);
    }

    /// <summary>つかんで動かしている間は、ワールド位置からコントローラー基準のずれを計算し直す</summary>
    public override void GrabMoveTo(Matrix4x4 world)
    {
        if (!App.Input.TryGetDevicePose(_device, out var devicePose)) return;
        _offset = world * devicePose.Inverse();
        World = world;
        MarkTransformDirty();
    }

    public override void OnGrabEnd()
    {
        var p = Placement.FromMatrix(_offset);
        if (AttachedHand == Hand.Right) _cfg.RightOffset = p; else _cfg.LeftOffset = p;
        _cfg.WidthMeters = WidthMeters;
        App.Config.Save();
        App.Notify("位置を保存");
    }

    public void ShowNotice(string msg, bool error)
    {
        _notice = msg;
        _noticeError = error;
        _noticeUntil = _now + 4;
        Invalidate();
    }

    UiButton? Find(Vector2 p) => _buttons.FirstOrDefault(b => b.Contains(p));

    public override void OnHover(Hand hand, Vector2 px)
    {
        var before = _hover[(int)hand] is { } p ? Find(p) : null;
        _hover[(int)hand] = px;
        if (Find(px) != before) Invalidate();
    }

    public override void OnLeave(Hand hand) { _hover[(int)hand] = null; Invalidate(); }

    public override void OnPress(Hand hand, Vector2 px)
    {
        _pressed[(int)hand] = Find(px);
        if (_pressed[(int)hand] != null) App.Input.Haptic(hand);
        Invalidate();
    }

    public override void OnRelease(Hand hand, Vector2 px)
    {
        var b = _pressed[(int)hand];
        _pressed[(int)hand] = null;
        Invalidate();
        if (b == null || !b.Contains(px)) return;
        switch (b.Id)
        {
            case "prev": App.Media.Previous(); break;
            case "play": App.Media.PlayPause(); break;
            case "next": App.Media.Next(); break;
            case "voldown": StepMaster(-0.05f); break;
            case "volup": StepMaster(0.05f); break;
            case "keyboard": App.Keyboard.Toggle(); break;
            case "desktop": App.Desktop.Toggle(); break;
            case "menu": App.Menu.Toggle(); break;
            case "summon":
                foreach (var p in App.Panels)
                    if (p != this && p.Visible) p.PlaceInFront();
                break;
        }
    }

    static void StepMaster(float delta)
    {
        App.Mixer.Refresh();
        App.Mixer.SetMaster(App.Mixer.MasterVolume + delta);
        App.Notify($"音量 {MathF.Round(App.Mixer.MasterVolume * 100)}%");
    }

    protected override void Draw(SKCanvas c)
    {
        Ui.RoundRect(c, new SKRect(0, 0, W, H), 36, new SKColor(0x1b, 0x22, 0x2c, 240), Ui.Border, 3);

        // 時刻と日付
        Ui.TextLeft(c, _clock, Pad + 14, 62, 76, Ui.Text, bold: true);
        var now = DateTime.Now;
        string[] wd = ["日", "月", "火", "水", "木", "金", "土"];
        Ui.TextLeft(c, $"{wd[(int)now.DayOfWeek]}  {now:yyyy/MM/dd}", Pad + 18, 116, 26, Ui.Muted);

        // 右側：通知があればそれ、なければ再生中の曲
        float rx = 290, rw = W - Pad - 8 - rx;
        if (_notice.Length > 0)
        {
            Ui.TextLeftFit(c, _notice, rx, 70, 26, rw, _noticeError ? Ui.Danger : Ui.Ok);
        }
        else if (App.Media.Title.Length > 0)
        {
            Ui.TextLeftFit(c, App.Media.Title, rx, 52, 26, rw, Ui.Text, bold: true);
            Ui.TextLeftFit(c, App.Media.Artist, rx, 88, 22, rw, Ui.Muted);
        }

        foreach (var b in _buttons)
        {
            bool hover = (_hover[0] is { } a && b.Contains(a)) || (_hover[1] is { } d && b.Contains(d));
            bool pressed = _pressed[0] == b || _pressed[1] == b;
            b.Draw(c, hover, pressed, 24, 18);
            switch (b.Id)
            {
                case "prev": Icons.Previous(c, b.Rect, Ui.Text); break;
                case "play": if (App.Media.Playing) Icons.Pause(c, b.Rect, Ui.Text); else Icons.Play(c, b.Rect, Ui.Text); break;
                case "next": Icons.Next(c, b.Rect, Ui.Text); break;
                case "voldown": Icons.Speaker(c, b.Rect, Ui.Text, plus: false); break;
                case "volup": Icons.Speaker(c, b.Rect, Ui.Text, plus: true); break;
            }
        }
    }
}
