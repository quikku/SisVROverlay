using System.Numerics;
using GodanOverlay.Services;
using SkiaSharp;

namespace GodanOverlay.Panels;

/// <summary>パネル一覧・ウィンドウの追加・音量ミキサー・設定の4つのタブを持つパネル</summary>
public sealed class MenuPanel : Panel
{
    const int W = 760, H = 880, Pad = 16, Gap = 10, HeaderH = 64, FooterH = 64, RowH = 80, RowGap = 8;
    const int ContentTop = Pad + HeaderH + Pad, ContentBottom = H - Pad - FooterH - Pad;
    static readonly int RowsPerPage = (ContentBottom - ContentTop + RowGap) / (RowH + RowGap);

    enum Tab { Panels, Windows, Volume, Settings }

    /// <summary>
    /// 設定タブの1行。Change(+1/-1) で値を変える。Step が true なら −／＋ のボタン、false なら値のボタン（押すと次の値）。
    /// 変えた値は設定ファイルに保存され、その場で効く
    /// </summary>
    sealed record SettingItem(string Name, Func<string> Value, Action<int> Change, Func<bool> On, bool Step);

    static SettingItem Toggle(string name, Func<bool> get, Action<bool> set) =>
        new(name, () => get() ? "オン" : "オフ", _ => set(!get()), get, false);

    static SettingItem Choice(string name, string[] values, string[] labels, Func<string> get, Action<string> set) =>
        new(name, () => labels[Math.Max(0, Array.IndexOf(values, get()))],
            _ => set(values[(Array.IndexOf(values, get()) + 1) % values.Length]), () => false, false);

    static SettingItem Number(string name, Func<float> get, Action<float> set, float step, float min, float max, Func<float, string> format) =>
        new(name, () => format(get()), d => set(Math.Clamp(MathF.Round(get() / step + d) * step, min, max)), () => false, true);

    static List<SettingItem> BuildSettings()
    {
        var c = App.Config;
        return
        [
            Toggle("時計を常に表示", () => c.Watch.AlwaysShow, v => c.Watch.AlwaysShow = v),
            Toggle("位置関係を戻す", () => c.Watch.RestoreRelativePosition, v => c.Watch.RestoreRelativePosition = v),
            Choice("時計の手", ["right", "left"], ["右", "左"], () => c.Watch.Hand, v =>
            {
                c.Watch.Hand = v;
                App.Watch.SetHand(v == "left" ? Hand.Left : Hand.Right);
            }),
            Number("2回押しの間隔", () => c.Watch.DoublePressSeconds, v => c.Watch.DoublePressSeconds = v, 0.05f, 0.2f, 1.0f, v => $"{v:0.00}秒"),
            Choice("通知", ["watch", "hud", "off"], ["時計", "視界", "オフ"], () => c.Notifications.Display, v => c.Notifications.Display = v),
            Toggle("A／X で右クリック", () => c.Input.RightClickWithButton, v => c.Input.RightClickWithButton = v),
            Toggle("タッチ操作", () => c.Desktop.TouchMode, v => c.Desktop.TouchMode = v),
            Toggle("カーソル表示", () => c.Desktop.ShowCursor, v => c.Desktop.ShowCursor = v),
            Number("カーソルの大きさ", () => c.Desktop.CursorScale, v => c.Desktop.CursorScale = v, 0.25f, 0.5f, 4f, v => $"×{v:0.##}"),
            Toggle("レーザーでマウスを動かす", () => c.Desktop.MoveCursorOnHover, v => c.Desktop.MoveCursorOnHover = v),
            Toggle("VRChatにボタンを渡さない", () => c.Input.BlockGameInputWhilePointing, v => c.Input.BlockGameInputWhilePointing = v),
            Number("ブレ補正", () => c.Input.PressStabilizeMs, v => c.Input.PressStabilizeMs = (int)v, 10, 0, 200, v => v == 0 ? "オフ" : $"{v:0}ms"),
            Number("フリックの距離", () => c.Keyboard.FlickThreshold, v => c.Keyboard.FlickThreshold = v, 0.02f, 0.1f, 0.6f, v => $"{v * 100:0}%"),
            Number("数字フリックの距離", () => c.Keyboard.NumberFlickThreshold, v => c.Keyboard.NumberFlickThreshold = v, 0.05f, 0.3f, 1.5f, v => $"{v * 100:0}%"),
            Toggle("振動", () => c.Keyboard.Haptics, v => c.Keyboard.Haptics = v),
            Toggle("アニメーション", () => c.Ui.Animations, v => c.Ui.Animations = v),
            Number("アニメーションの長さ", () => c.Ui.AnimationSeconds, v => c.Ui.AnimationSeconds = v, 0.05f, 0.05f, 0.6f, v => $"{v:0.00}秒"),
        ];
    }

    List<SettingItem> _settings = [];

    /// <summary>押し続けると繰り返すボタン（音量と設定の −／＋）</summary>
    static bool Repeats(string id) =>
        id.StartsWith("v:down:") || id.StartsWith("v:up:") || (id.StartsWith("s:") && (id.EndsWith(":-") || id.EndsWith(":+")));
    Tab _tab = Tab.Panels;
    int _page;

    readonly List<UiButton> _buttons = new();
    readonly List<(SKRect rect, Action<SKCanvas> draw)> _rows = new();
    readonly Vector2?[] _hover = new Vector2?[2];
    readonly UiButton?[] _pressed = new UiButton?[2];
    readonly double[] _repeatAt = new double[2];

    List<WinApi.WindowInfo> _windows = new();
    Panel[] _panelRows = [];
    double _now, _nextRefresh;
    bool _placed;

    public MenuPanel() : base("menu", "GODAN Menu", W, H, 0.36f, 2) { }

    public override string Title => "メニュー";

    protected override void OnShow()
    {
        if (!_placed || Vector3.Distance(World.Translation, App.Input.HmdPose.Translation) > 2.5f) PlaceInFront();
        Refresh();
    }

    public override void PlaceInFront()
    {
        if (HudLocked) return;
        var m = VrMath.InFrontOf(App.Input.HmdPose, 0.6f, 0.15f, 10);
        var right = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
        m.Translation -= right * 0.38f;
        SetWorld(m);
        _placed = true;
    }

    void SetTab(Tab t)
    {
        _tab = t;
        _page = 0;
        Refresh();
    }

    void Refresh()
    {
        if (_tab == Tab.Windows) _windows = WinApi.ListWindows();
        if (_tab == Tab.Volume) App.Mixer.Refresh();
        Invalidate();
    }

    public override void Update(double now, double dt)
    {
        _now = now;
        if (!Visible) return;
        // パネルの状態や音量は他の場所でも変わるので、ときどき描き直す
        if (now >= _nextRefresh)
        {
            _nextRefresh = now + (_tab == Tab.Volume ? 1.0 : 0.5);
            if (_tab == Tab.Volume) App.Mixer.Refresh();
            if (_tab != Tab.Windows) Invalidate();
        }
        // ＋／−の長押し
        for (int i = 0; i < 2; i++)
        {
            if (_pressed[i] is { } b && Repeats(b.Id) && now >= _repeatAt[i])
            {
                Activate(b);
                _repeatAt[i] = now + 0.12;
            }
        }
    }

    // ── 並べ方 ──

    void Layout()
    {
        _buttons.Clear();
        _rows.Clear();

        // ヘッダー：タブ・ロック・閉じる
        float x = Pad, y = Pad;
        foreach (var (tab, label, icon) in new[] { (Tab.Panels, "パネル", "layers"), (Tab.Windows, "ウィンドウ", "select_window"), (Tab.Volume, "音量", "volume_up"), (Tab.Settings, "設定", "settings") })
        {
            _buttons.Add(new UiButton { Id = "tab:" + tab, Label = label, Icon = icon, Rect = new SKRect(x, y, x + 128, y + HeaderH), Active = _tab == tab });
            x += 128 + Gap;
        }
        _buttons.Add(new UiButton { Id = "close", Label = "", Icon = "close", Rect = new SKRect(W - Pad - 64, y, W - Pad, y + HeaderH) });
        _buttons.Add(new UiButton { Id = "lock", Label = "ロック", Icon = Locked ? "lock" : "lock_open", Rect = new SKRect(W - Pad - 64 - Gap - 110, y, W - Pad - 64 - Gap, y + HeaderH), Active = Locked });

        int count = _tab switch
        {
            Tab.Panels => (_panelRows = App.Panels.Where(p => p is not WatchPanel and not MenuPanel).ToArray()).Length,
            Tab.Windows => _windows.Count,
            Tab.Settings => (_settings = BuildSettings()).Count,
            _ => App.Mixer.Apps.Count + 1,
        };
        int pages = Math.Max(1, (count + RowsPerPage - 1) / RowsPerPage);
        _page = Math.Clamp(_page, 0, pages - 1);

        for (int r = 0; r < RowsPerPage; r++)
        {
            int i = _page * RowsPerPage + r;
            if (i >= count) break;
            var row = new SKRect(Pad, ContentTop + r * (RowH + RowGap), W - Pad, ContentTop + r * (RowH + RowGap) + RowH);
            switch (_tab)
            {
                case Tab.Panels: LayoutPanelRow(row, i); break;
                case Tab.Windows: LayoutWindowRow(row, i); break;
                case Tab.Volume: LayoutVolumeRow(row, i - 1); break;
                case Tab.Settings: LayoutSettingRow(row, i); break;
            }
        }

        // フッター：ページ送り（ウィンドウのタブは更新も）
        float fy = H - Pad - FooterH;
        if (pages > 1)
        {
            _buttons.Add(new UiButton { Id = "prev", Label = "", Icon = "chevron_left", Rect = new SKRect(W / 2f - 200, fy, W / 2f - 90, fy + FooterH) });
            _buttons.Add(new UiButton { Id = "next", Label = "", Icon = "chevron_right", Rect = new SKRect(W / 2f + 90, fy, W / 2f + 200, fy + FooterH) });
            _rows.Add((new SKRect(W / 2f - 90, fy, W / 2f + 90, fy + FooterH),
                c => Ui.TextCenter(c, $"{_page + 1} / {pages}", new SKRect(W / 2f - 90, fy, W / 2f + 90, fy + FooterH), 26, Ui.Muted)));
        }
        if (_tab == Tab.Windows)
            _buttons.Add(new UiButton { Id = "refresh", Label = "更新", Icon = "refresh", Rect = new SKRect(Pad, fy, Pad + 140, fy + FooterH) });
        if (count == 0)
        {
            var empty = _tab == Tab.Windows ? "ウィンドウなし" : _tab == Tab.Volume ? "再生中のアプリなし" : "";
            _rows.Add((new SKRect(Pad, ContentTop, W - Pad, ContentTop + RowH), c => Ui.TextCenter(c, empty, new SKRect(Pad, ContentTop, W - Pad, ContentTop + RowH), 26, Ui.Muted)));
        }
    }

    // 右から順にボタンを置き、残りの左側を名前の欄にする
    float PlaceRight(ref float right, SKRect row, string id, string label, float w, bool active = false, string? icon = null)
    {
        var r = new SKRect(right - w, row.Top + 8, right, row.Bottom - 8);
        _buttons.Add(new UiButton { Id = id, Label = label, Icon = icon, Rect = r, Active = active });
        right -= w + Gap;
        return right;
    }

    void LayoutPanelRow(SKRect row, int i)
    {
        var p = _panelRows[i];
        float right = row.Right - 10;
        if (p.Closable) PlaceRight(ref right, row, $"p:close:{i}", "", 64, icon: "close");
        int level = (int)MathF.Round(p.Curvature / 0.1f);
        PlaceRight(ref right, row, $"p:curve:{i}", level == 0 ? "平面" : $"曲面{level}", 120, level > 0, level == 0 ? "crop_landscape" : "panorama_horizontal");
        PlaceRight(ref right, row, $"p:hud:{i}", "HUD", 105, p.HudLocked, "head_mounted_device");
        PlaceRight(ref right, row, $"p:show:{i}", "表示", 110, p.Visible, p.Visible ? "visibility" : "visibility_off");
        float nameW = right - row.Left - 20;
        _rows.Add((row, c =>
        {
            Ui.RoundRect(c, row, 14, Ui.Panel, Ui.Border, 2);
            Ui.TextLeftFit(c, p.Title, row.Left + 18, row.MidY, 26, nameW, p.Visible ? Ui.Text : Ui.Muted);
        }));
    }

    void LayoutWindowRow(SKRect row, int i)
    {
        var w = _windows[i];
        bool open = App.Panels.OfType<WindowPanel>().Any(p => p.Hwnd == w.Hwnd);
        _buttons.Add(new UiButton { Id = $"w:{i}", Label = "", Rect = row, Active = open });
        _rows.Add((row, c =>
        {
            Ui.TextLeftFit(c, w.Title, row.Left + 18, row.MidY - 12, 26, row.Width - 36, open ? Ui.Accent : Ui.Text);
            Ui.TextLeftFit(c, w.Process, row.Left + 18, row.MidY + 20, 18, row.Width - 36, Ui.Muted);
        }));
    }

    void LayoutSettingRow(SKRect row, int i)
    {
        var s = _settings[i];
        float top = row.Top + 8, bottom = row.Bottom - 8, right = row.Right - 10;
        float nameW;
        if (s.Step)
        {
            // 右から ＋ / 値 / −
            _buttons.Add(new UiButton { Id = $"s:{i}:+", Label = "", Icon = "add", Rect = new SKRect(right - 64, top, right, bottom) });
            var value = new SKRect(right - 64 - Gap - 130, top, right - 64 - Gap, bottom);
            float minus = value.Left - Gap;
            _buttons.Add(new UiButton { Id = $"s:{i}:-", Label = "", Icon = "remove", Rect = new SKRect(minus - 64, top, minus, bottom) });
            nameW = minus - 64 - row.Left - 30;
            _rows.Add((row, c =>
            {
                Ui.RoundRect(c, row, 14, Ui.Panel, Ui.Border, 2);
                Ui.TextLeftFit(c, s.Name, row.Left + 18, row.MidY, 26, nameW, Ui.Text);
                Ui.TextCenter(c, s.Value(), value, 26, Ui.Text, bold: true);
            }));
            return;
        }
        _buttons.Add(new UiButton { Id = $"s:{i}:t", Label = s.Value(), Rect = new SKRect(right - 150, top, right, bottom), Active = s.On() });
        nameW = right - 150 - row.Left - 30;
        _rows.Add((row, c =>
        {
            Ui.RoundRect(c, row, 14, Ui.Panel, Ui.Border, 2);
            Ui.TextLeftFit(c, s.Name, row.Left + 18, row.MidY, 26, nameW, Ui.Text);
        }));
    }

    void LayoutVolumeRow(SKRect row, int i)
    {
        // i = -1 は全体の音量
        var mixer = App.Mixer;
        string name = i < 0 ? "全体" : mixer.Apps[i].Name;
        float vol = i < 0 ? mixer.MasterVolume : mixer.Apps[i].Volume;
        bool muted = i < 0 ? mixer.MasterMuted : mixer.Apps[i].Muted;

        // 左から 名前 / ミュート / − / バー / ＋
        float top = row.Top + 8, bottom = row.Bottom - 8;
        float nameW = 200;
        float x = row.Left + 18 + nameW + Gap;
        _buttons.Add(new UiButton { Id = $"v:mute:{i}", Label = "", Icon = muted ? "volume_off_fill1" : "volume_up_fill1", Rect = new SKRect(x, top, x + 110, bottom), Active = muted });
        x += 110 + Gap;
        _buttons.Add(new UiButton { Id = $"v:down:{i}", Label = "", Icon = "remove", Rect = new SKRect(x, top, x + 64, bottom) });
        x += 64 + Gap;
        _buttons.Add(new UiButton { Id = $"v:up:{i}", Label = "", Icon = "add", Rect = new SKRect(row.Right - 10 - 64, top, row.Right - 10, bottom) });
        var bar = new SKRect(x, row.Top + 22, row.Right - 10 - 64 - Gap, row.Bottom - 22);
        _rows.Add((row, c =>
        {
            Ui.RoundRect(c, row, 14, Ui.Panel, Ui.Border, 2);
            Ui.TextLeftFit(c, name, row.Left + 18, row.MidY, 24, nameW, muted ? Ui.Muted : Ui.Text, bold: i < 0);
            Ui.RoundRect(c, bar, bar.Height / 2, Ui.Bg, Ui.Border, 2);
            var fill = new SKRect(bar.Left, bar.Top, bar.Left + bar.Width * vol, bar.Bottom);
            if (fill.Width > 2) Ui.RoundRect(c, fill, bar.Height / 2, muted ? Ui.Muted : Ui.Accent);
            Ui.TextCenter(c, $"{MathF.Round(vol * 100)}%", bar, 22, Ui.Text, bold: true);
        }));
    }

    // ── 操作 ──

    UiButton? Find(Vector2 p) => _buttons.LastOrDefault(b => b.Contains(p));

    public override void OnHover(Hand hand, Vector2 px)
    {
        var before = _hover[(int)hand] is { } p ? Find(p) : null;
        _hover[(int)hand] = px;
        if (Find(px) != before) Invalidate();
    }

    public override void OnLeave(Hand hand) { _hover[(int)hand] = null; Invalidate(); }

    public override void OnPress(Hand hand, Vector2 px)
    {
        int i = (int)hand;
        _pressed[i] = Find(px);
        if (_pressed[i] is not { } b) return;
        App.Input.Haptic(hand);
        // 音量の＋／−は押した瞬間に効かせて、長押しで続ける
        if (Repeats(b.Id))
        {
            Activate(b);
            _repeatAt[i] = _now + 0.45;
        }
        Invalidate();
    }

    public override void OnRelease(Hand hand, Vector2 px)
    {
        var b = _pressed[(int)hand];
        _pressed[(int)hand] = null;
        Invalidate();
        if (b == null || !b.Contains(px)) return;
        if (Repeats(b.Id)) return; // 押した時点で処理済み
        Activate(b);
    }

    void Activate(UiButton b)
    {
        var parts = b.Id.Split(':');
        switch (parts[0])
        {
            case "tab": SetTab(Enum.Parse<Tab>(parts[1])); return;
            case "close": Hide(); return;
            case "lock": Locked = !Locked; break;
            case "prev": _page--; break;
            case "next": _page++; break;
            case "refresh": Refresh(); return;
            case "s":
            {
                int i = int.Parse(parts[1]);
                if (i >= _settings.Count) return;
                _settings[i].Change(parts[2] == "-" ? -1 : 1);
                if (!Headless) App.Config.Save();
                break;
            }
            case "p":
            {
                int i = int.Parse(parts[2]);
                if (i >= _panelRows.Length) return;
                var p = _panelRows[i];
                switch (parts[1])
                {
                    case "show": p.Toggle(); break;
                    case "hud": p.SetHud(!p.HudLocked, App.Input.HmdPose); break;
                    case "curve":
                        int level = ((int)MathF.Round(p.Curvature / 0.1f) + 1) % 4;
                        p.SetCurvature(level * 0.1f);
                        break;
                    case "close": p.Close(); break;
                }
                break;
            }
            case "w":
            {
                int i = int.Parse(parts[1]);
                if (i >= _windows.Count) return;
                var w = _windows[i];
                var open = App.Panels.OfType<WindowPanel>().FirstOrDefault(p => p.Hwnd == w.Hwnd);
                if (open != null) { open.Show(); open.PlaceInFront(); }
                else App.AddWindow(w);
                break;
            }
            case "v":
            {
                int i = int.Parse(parts[2]);
                var mixer = App.Mixer;
                if (i >= mixer.Apps.Count) return;
                float step = parts[1] == "up" ? 0.05f : -0.05f;
                switch (parts[1])
                {
                    case "mute": if (i < 0) mixer.ToggleMasterMute(); else mixer.ToggleMute(mixer.Apps[i]); break;
                    default:
                        if (i < 0) mixer.SetMaster(mixer.MasterVolume + step);
                        else mixer.SetVolume(mixer.Apps[i], mixer.Apps[i].Volume + step);
                        break;
                }
                break;
            }
        }
        Invalidate();
    }

    protected override void Draw(SKCanvas c)
    {
        Layout();
        Ui.RoundRect(c, new SKRect(0, 0, W, H), 28, Ui.Bg.WithAlpha(245), Ui.Border, 3);
        foreach (var (_, draw) in _rows) draw(c);
        foreach (var b in _buttons)
        {
            bool hover = (_hover[0] is { } a && b.Contains(a)) || (_hover[1] is { } d && b.Contains(d));
            // 描くたびにボタンを作り直すので、押しているボタンは Id で見る
            bool pressed = _pressed[0]?.Id == b.Id || _pressed[1]?.Id == b.Id;
            if (b.Id.StartsWith("w:"))
            {
                // ウィンドウの行は文字を自前で描くので枠だけ
                var look = ButtonFx.Style(b, hover, pressed);
                ButtonFx.Glow(c, b.Rect, 14, look);
                Ui.RoundRect(c, b.Rect, 14, look.Fill, look.Border, 2);
                continue;
            }
            b.Draw(c, hover, pressed, 26, 14);
        }
        // ウィンドウの行の文字は枠の上に描き直す
        if (_tab == Tab.Windows) foreach (var (_, draw) in _rows) draw(c);
    }

    // 確認用
    public void DebugSetTab(string tab) => SetTab(Enum.Parse<Tab>(tab));
    public void DebugActivate(string id)
    {
        Layout();
        if (_buttons.FirstOrDefault(b => b.Id == id) is { } b) Activate(b);
    }
}
