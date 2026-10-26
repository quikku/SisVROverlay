using System.Numerics;
using GodanOverlay.Input;
using GodanOverlay.Services;
using SkiaSharp;

namespace GodanOverlay.Panels;

/// <summary>GODAN配列・かなフリック・QWERTY（日本語配列）のキーボード。トリガーで押し、押したままレーザーを動かすとフリック。</summary>
public sealed class KeyboardPanel : Panel
{
    // QWERTY のときだけ横に広い（高さは同じ）
    const int NarrowW = 720, KeyU = 96, WideW = Pad * 2 + KeyU * 15;
    const int Pad = 16, Gap = 10;
    const int HeaderH = 64, PreviewH = 96, CandRowH = 58, CandH = CandRowH * 2 + Gap, RowH = 112;
    static readonly int GridTop = Pad + HeaderH + Gap + PreviewH + Gap + CandH + Gap;
    static readonly int H = GridTop + RowH * 5 + Gap * 4 + Pad;

    readonly KeyboardConfig _cfg;

    int W => WidthFor(_cfg);
    static int WidthFor(KeyboardConfig c) => c.Style == "qwerty" ? WideW : NarrowW;
    bool IsQwerty => _cfg.Style == "qwerty";
    readonly GodanEngine _engine;

    /// <summary>キーと操作ボタン（配列から作る）</summary>
    readonly List<UiButton> _keys = new();
    readonly List<UiButton> _header = new();
    readonly List<UiButton> _cands = new();

    public bool PcMode { get; private set; }
    /// <summary>PC 以外の出力先の名前（ヘッダーの「出力」ボタンに出す）</summary>
    public static string SendName => App.Config.Output.SendTo == "tts" ? "TTS" : "Chatbox";
    public override string Title => "キーボード";

    bool _placed;

    // 手ごとの操作状態
    readonly Vector2?[] _hover = new Vector2?[2];
    readonly UiButton?[] _pressed = new UiButton?[2];
    readonly Vector2[] _pressStart = new Vector2[2];
    readonly FlickDir[] _flickDir = new FlickDir[2];
    readonly double[] _repeatAt = new double[2];

    // 入力欄を押してカーソルを動かす
    SKRect PreviewRect => new(Pad, Pad + HeaderH + Gap, W - Pad, Pad + HeaderH + Gap + PreviewH);
    readonly bool[] _caretDrag = new bool[2];
    readonly List<(float x, int index)> _caretStops = new();

    /// <summary>フリック中の候補を出す別の小さいオーバーレイ（キーボードの外へはみ出せる）</summary>
    readonly FlickPopup _popup = new();

    int _focusSeg;
    string _notice = "";
    bool _noticeError;
    double _noticeUntil;
    double _now;

    // 自動候補の取得
    double _autoAt = double.MaxValue;
    CancellationTokenSource? _autoCts;
    bool _converting;

    public KeyboardPanel(KeyboardConfig cfg)
        : base("keyboard", "GODAN Keyboard", WidthFor(cfg), H, cfg.WidthMeters * WidthFor(cfg) / NarrowW, 2)
    {
        _cfg = cfg;
        PcMode = cfg.DefaultOutput == "pc";
        _engine = new GodanEngine(cfg.ActiveLayout);
        _engine.Changed += OnEngineChanged;
        BuildKeys();
        BuildHeader();
    }

    GodanLayout Layout => _cfg.ActiveLayout;

    void BuildKeys()
    {
        _keys.Clear();
        if (IsQwerty) { BuildQwertyKeys(); return; }
        var grid = Layout.Grid;
        int cols = grid.Max(r => r.Length);
        float colW = (W - Pad * 2 - Gap * (cols - 1)) / (float)cols;
        // 行が少ない配列ではキーを縦に大きくして、同じ高さに収める
        float rowH = (RowH * 5 + Gap * 4 - Gap * (grid.Length - 1)) / (float)grid.Length;
        for (int r = 0; r < grid.Length; r++)
        {
            for (int c = 0; c < grid[r].Length; c++)
            {
                var id = grid[r][c];
                if (id == null) continue;
                int span = Layout.Spans.TryGetValue(id, out var s) ? Math.Max(1, s) : 1;
                float x = Pad + c * (colW + Gap);
                float y = GridTop + r * (rowH + Gap);
                var rect = new SKRect(x, y, x + colW, y + rowH * span + Gap * (span - 1));
                _keys.Add(new UiButton { Id = id, Label = KeyLabel(id), Rect = rect, Primary = id == "#send" });
            }
        }
    }

    static string KeyLabel(string id) => id switch
    {
        "#bs" => "⌫",
        "#convert" => "変換",
        "#send" => "送信",
        "#small" => "゛゜小",
        "#clear" => "全消去",
        _ => id.ToUpperInvariant(),
    };

    void BuildHeader()
    {
        _header.Clear();
        float y = Pad, h = HeaderH;
        float right = W - Pad;
        _header.Add(new UiButton { Id = "close", Label = "×", Rect = new SKRect(right - 64, y, right, y + h) });
        right -= 64 + Gap;
        _header.Add(new UiButton { Id = "clear", Label = PcMode ? "半/全" : "全消去", Rect = new SKRect(right - 120, y, right, y + h) });
        right -= 120 + Gap;
        _header.Add(new UiButton { Id = "mode", Label = PcMode ? "出力: PC" : "出力: " + SendName, Rect = new SKRect(right - 220, y, right, y + h), Active = PcMode });
        right -= 220 + Gap;
        _header.Add(new UiButton { Id = "lock", Label = "ロック", Rect = new SKRect(right - 100, y, right, y + h), Active = Locked });
        string style = _cfg.Style switch { "flick" => "フリック", "qwerty" => "QWERTY", _ => "GODAN" };
        _header.Add(new UiButton { Id = "style", Label = style, Rect = new SKRect(Pad, y, Math.Min(right - 100 - Gap, Pad + 220), y + h) });
    }

    void ToggleStyle()
    {
        _cfg.Style = _cfg.Style switch { "godan" => "flick", "flick" => "qwerty", _ => "godan" };
        _mods.Clear();
        // QWERTY は横に広いので作り直す。1ピクセルあたりの大きさは変えない
        float metersPerPixel = WidthMeters / PixW;
        if (PixW != W)
        {
            Resize(W, H);
            SetWidth(metersPerPixel * W);
        }
        _engine.SetLayout(Layout);
        BuildKeys();
        LayoutCandidates();
        BuildHeader();
        if (!Headless) App.Config.Save();
        Invalidate();
    }

    public void SetPcMode(bool pc)
    {
        if (pc != PcMode) _engine.Clear();
        PcMode = pc;
        BuildHeader();
        Invalidate();
    }

    // ── 表示位置 ──

    protected override void OnShow()
    {
        var hmd = App.Input.HmdPose;
        if (!_placed || Vector3.Distance(World.Translation, hmd.Translation) > 2.5f)
        {
            SetWorld(VrMath.InFrontOf(hmd, 0.45f, 0.28f, 30));
            _placed = true;
        }
    }

    public override void PlaceInFront()
    {
        if (HudLocked) return;
        SetWorld(VrMath.InFrontOf(App.Input.HmdPose, 0.45f, 0.28f, 30));
        _placed = true;
    }

    // ── 通知 ──

    public void ShowNotice(string msg, bool error)
    {
        _notice = msg;
        _noticeError = error;
        _noticeUntil = _now + 4;
        Invalidate();
    }

    // ── 毎フレーム ──

    public override void Update(double now, double dt)
    {
        _now = now;
        if (_notice.Length > 0 && now > _noticeUntil) { _notice = ""; Invalidate(); }

        // Backspace の長押しリピート
        for (int i = 0; i < 2; i++)
        {
            if (_pressed[i] is { } pb && now >= _repeatAt[i])
            {
                if (pb.Id == "#bs") { DoBackspace(); _repeatAt[i] = now + 0.07; }
                else if (_qkeys.TryGetValue(pb.Id, out var qk)) { QwertyPress(qk); _repeatAt[i] = now + 0.05; }
            }
        }

        if (now >= _autoAt)
        {
            _autoAt = double.MaxValue;
            RequestAutoCandidates();
        }
    }

    void OnEngineChanged()
    {
        if (PcMode)
        {
            // PC へ直接入力しているときは、入力欄も候補も使わない
            _engine.ClearAutoCandidates();
            _cands.Clear();
            Invalidate();
            return;
        }
        _focusSeg = Math.Min(_focusSeg, Math.Max(0, _engine.Segments.Count - 1));
        if (!_engine.Converting)
        {
            _engine.ClearAutoCandidates();
            _autoCts?.Cancel();
            _autoAt = _engine.Composing.Trim().Length > 0 ? _now + App.Config.Converter.AutoCandidateDelayMs / 1000.0 : double.MaxValue;
        }
        LayoutCandidates();
        Invalidate();
    }

    void RequestAutoCandidates()
    {
        var text = _engine.Composing.Trim();
        if (text.Length == 0 || _engine.Converting) return;
        _autoCts?.Cancel();
        var cts = _autoCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                var cands = await App.Converter.WholeAsync(text, cts.Token);
                App.Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    _engine.SetAutoCandidates(text, cands);
                    LayoutCandidates();
                    Invalidate();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Log.Warn("自動候補の取得に失敗: " + e.Message); }
        });
    }

    void StartConvert()
    {
        if (_engine.Converting) { _engine.CommitConversion(); return; }
        var text = _engine.BeginConvert();
        if (text.Length == 0 || _converting) return;
        _converting = true;
        _autoCts?.Cancel();
        Invalidate();
        _ = Task.Run(async () =>
        {
            try
            {
                var segs = await App.Converter.ConvertAsync(text, CancellationToken.None);
                App.Post(() =>
                {
                    _converting = false;
                    _focusSeg = 0;
                    _engine.ApplyConversion(text, segs);
                    LayoutCandidates();
                    Invalidate();
                });
            }
            catch (Exception e)
            {
                App.Post(() => { _converting = false; Log.Warn("変換に失敗: " + e.Message); App.Notify("変換できません", true); Invalidate(); });
            }
        });
    }

    void DoBackspace()
    {
        if (PcMode)
        {
            Win32Input.KeyPress(JisKeys.VK_BACK);
            _engine.Backspace();
            return;
        }
        _engine.Backspace();
    }

    // ── QWERTY（日本語配列） ──
    // 出力が PC なら、キーをそのまま PC へ送る（変換は PC の IME）。
    // それ以外ならローマ字入力として入力欄に打ち込み、Space で変換、Enter で確定と送信。

    /// <summary>キー1つ。Chat / ChatShift は入力欄へ打ち込む文字（null なら Label をそのまま使う）</summary>
    sealed record QKey(string Id, string Label, string? ShiftLabel, ushort Vk, string? Chat = null, string? ChatShift = null);

    readonly Dictionary<string, QKey> _qkeys = new();
    readonly HashSet<string> _mods = new(); // 押したままにしている修飾キー（shift / ctrl / alt / win）

    void BuildQwertyKeys()
    {
        _qkeys.Clear();
        // 一番上の段（Esc・Del）は低く、残りの5段でキーボードの高さを分ける
        const float slim = 56;
        float area = RowH * 5 + Gap * 4;
        float rowH = (area - slim - Gap * 5) / 5f;
        float RowTop(int r) => GridTop + (r == 0 ? 0 : slim + Gap + (r - 1) * (rowH + Gap));
        float RowBottom(int r) => RowTop(r) + (r == 0 ? slim : rowH);
        float x = 0;
        int row = 0;
        void Add(string id, string label, string? shift, ushort vk, float w = 1, string? chat = null, string? chatShift = null, int rows = 1)
        {
            float left = Pad + x * KeyU + Gap / 2f;
            var rect = new SKRect(left, RowTop(row), left + w * KeyU - Gap, RowBottom(row + rows - 1));
            x += w;
            var k = new QKey("q:" + id, label, shift, vk, chat, chatShift);
            _qkeys[k.Id] = k;
            _keys.Add(new UiButton { Id = k.Id, Label = label, Rect = rect });
        }
        void Next() { x = 0; row++; }

        Add("esc", "Esc", null, JisKeys.VK_ESCAPE, 1.5f);
        x += 12;
        Add("del", "Del", null, JisKeys.VK_DELETE, 1.5f);
        Next();

        Add("kanji", "半/全", null, JisKeys.VK_KANJI);
        string[] shifted = ["!", "\"", "#", "$", "%", "&", "'", "(", ")", ""];
        for (int i = 0; i < 10; i++)
        {
            char d = i == 9 ? '0' : (char)('1' + i);
            Add("k" + d, d.ToString(), shifted[i].Length > 0 ? shifted[i] : null, d);
        }
        Add("minus", "-", "=", JisKeys.VK_OEM_MINUS, chat: "ー");
        Add("caret", "^", "~", JisKeys.VK_OEM_7, chatShift: "～");
        Add("yen", "¥", "|", JisKeys.VK_OEM_5);
        Add("bs", "⌫", null, JisKeys.VK_BACK);
        Next();

        Add("tab", "Tab", null, JisKeys.VK_TAB, 1.5f);
        foreach (var ch in "QWERTYUIOP") Add("k" + ch, ch.ToString(), null, ch, chat: char.ToLowerInvariant(ch).ToString());
        Add("at", "@", "`", JisKeys.VK_OEM_3);
        Add("lbracket", "[", "{", JisKeys.VK_OEM_4, chat: "「");
        Add("enter", "Enter", null, JisKeys.VK_RETURN, 1.5f);
        Next();

        // CapsLock の位置は Tab の下半分にする（Tab を大きく）。A の段は Q の段より少し右（実物と同じ）
        _keys.Add(new UiButton { Id = "q:tab", Label = "", Rect = new SKRect(Pad + Gap / 2f, RowTop(row), Pad + 1.75f * KeyU - Gap / 2f, RowBottom(row)) });
        x += 1.75f;
        foreach (var ch in "ASDFGHJKL") Add("k" + ch, ch.ToString(), null, ch, chat: char.ToLowerInvariant(ch).ToString());
        Add("semicolon", ";", "+", JisKeys.VK_OEM_PLUS);
        Add("colon", ":", "*", JisKeys.VK_OEM_1);
        Add("rbracket", "]", "}", JisKeys.VK_OEM_6, chat: "」");
        // Enter の下半分（実物と同じ L 字。押すと上半分と同じ Enter）
        {
            float left = Pad + x * KeyU + Gap / 2f;
            _keys.Add(new UiButton { Id = "q:enter", Label = "", Rect = new SKRect(left, RowTop(row), Pad + 15 * KeyU - Gap / 2f, RowBottom(row)) });
        }
        Next();

        Add("shift", "Shift", null, JisKeys.VK_SHIFT, 2);
        foreach (var ch in "ZXCVBNM") Add("k" + ch, ch.ToString(), null, ch, chat: char.ToLowerInvariant(ch).ToString());
        Add("comma", ",", "<", JisKeys.VK_OEM_COMMA, chat: "、");
        Add("period", ".", ">", JisKeys.VK_OEM_PERIOD, chat: "。");
        Add("slash", "/", "?", JisKeys.VK_OEM_2, chat: "・", chatShift: "？");
        Add("backslash", "＼", "_", JisKeys.VK_OEM_102, chat: "\\"); // 半角の \ は日本語のフォントだと ¥ に見える
        Add("up", "▲", null, JisKeys.VK_UP);
        Add("shift2", "Shift", null, JisKeys.VK_SHIFT);
        Next();

        Add("ctrl", "Ctrl", null, JisKeys.VK_CONTROL, 1.5f);
        Add("win", "Win", null, JisKeys.VK_LWIN, 1.25f);
        Add("alt", "Alt", null, JisKeys.VK_MENU, 1.25f);
        Add("nonconvert", "無変換", null, JisKeys.VK_NONCONVERT, 1.25f);
        Add("space", "Space", null, JisKeys.VK_SPACE, 4.5f);
        Add("convert", "変換", null, JisKeys.VK_CONVERT, 1.25f);
        Add("ctrl2", "Ctrl", null, JisKeys.VK_CONTROL);
        Add("left", "◀", null, JisKeys.VK_LEFT);
        Add("down", "▼", null, JisKeys.VK_DOWN);
        Add("right", "▶", null, JisKeys.VK_RIGHT);
    }

    static string ModOf(string id) => id switch
    {
        "q:shift" or "q:shift2" => "shift",
        "q:ctrl" or "q:ctrl2" => "ctrl",
        "q:alt" => "alt",
        "q:win" => "win",
        _ => "",
    };

    static bool QwertyRepeats(string id) => id is "q:bs" or "q:del" or "q:left" or "q:right" or "q:up" or "q:down";

    IEnumerable<ushort> ModKeys()
    {
        if (_mods.Contains("ctrl")) yield return JisKeys.VK_CONTROL;
        if (_mods.Contains("alt")) yield return JisKeys.VK_MENU;
        if (_mods.Contains("shift")) yield return JisKeys.VK_SHIFT;
        if (_mods.Contains("win")) yield return JisKeys.VK_LWIN;
    }

    /// <summary>
    /// Shift・Ctrl・Alt・Win は押すと次のキーまで押したままになる（Ctrl→C で Ctrl+C）。
    /// Win をもう一度押すと Win だけを押したことになる（スタートメニュー）
    /// </summary>
    void QwertyPress(QKey k)
    {
        var mod = ModOf(k.Id);
        if (mod.Length > 0)
        {
            if (mod == "win" && _mods.Contains("win") && PcMode)
            {
                _mods.Remove("win");
                Win32Input.KeyPress(JisKeys.VK_LWIN, ModKeys().ToArray());
                _mods.Clear();
            }
            else if (!_mods.Remove(mod)) _mods.Add(mod);
            Invalidate();
            return;
        }
        if (PcMode) Win32Input.KeyPress(k.Vk, ModKeys().ToArray());
        else QwertyToInput(k);
        _mods.Clear();
        Invalidate();
    }

    void QwertyToInput(QKey k)
    {
        switch (k.Id)
        {
            case "q:space" or "q:convert": StartConvert(); return;
            case "q:enter":
                // 変換中や未確定の文字があれば確定、なければ送信
                if (_engine.Converting || _engine.Composing.Length > 0 || _engine.Pending != null) _engine.CommitComposing();
                else DoSend();
                return;
            case "q:bs": DoBackspace(); return;
            case "q:esc": if (_engine.Converting) _engine.Backspace(); return; // 変換前のひらがなに戻す
            case "q:left": _engine.MoveCaret(_engine.Caret - 1); return;
            case "q:right": _engine.MoveCaret(_engine.Caret + 1); return;
            case "q:tab" or "q:del" or "q:up" or "q:down" or "q:kanji" or "q:nonconvert": return;
        }
        string text = _mods.Contains("shift") ? k.ChatShift ?? ToFullWidth(k.ShiftLabel ?? k.Label) : k.Chat ?? k.Label;
        if (text.Length == 1 && char.IsAsciiLetterUpper(text[0])) text = text.ToLowerInvariant();
        _engine.Press(text, FlickDir.Center);
    }

    /// <summary>2段にまたがるキー（L 字の Enter、CapsLock の分まで伸ばした Tab）を、上下2つのボタンをつないだ形で1回だけ描く</summary>
    void DrawTwoRowKey(SKCanvas c, UiButton b, string label)
    {
        var parts = _keys.Where(x => x.Id == b.Id).ToList();
        if (parts.Count < 2 || b != parts[0]) return;
        var top = parts[0].Rect;
        var bottom = parts[1].Rect;
        bool hover = parts.Any(IsHover);
        bool pressed = _pressed[0]?.Id == b.Id || _pressed[1]?.Id == b.Id;
        SKColor fill = pressed ? new SKColor(0, 212, 255, 90) : hover ? Ui.PanelHover : Ui.Panel;
        SKColor border = hover || pressed ? Ui.Accent : Ui.Border;
        using var a = new SKPath();
        a.AddRoundRect(top, 12, 12);
        using var bpath = new SKPath();
        bpath.AddRoundRect(bottom, 12, 12);
        // 上下のすき間は、上下が重なっている幅だけでつなぐ（広い方をそのまま伸ばすと隣のキーにかぶる）
        using var joint = new SKPath();
        joint.AddRect(new SKRect(Math.Max(top.Left, bottom.Left), top.Bottom - 12, Math.Min(top.Right, bottom.Right), bottom.Top + 12));
        using var both = a.Op(bpath, SKPathOp.Union);
        using var shape = both.Op(joint, SKPathOp.Union);
        using (var f = new SKPaint { Color = fill, IsAntialias = true }) c.DrawPath(shape, f);
        using (var st = new SKPaint { Color = border, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 }) c.DrawPath(shape, st);
        // 文字は上下が重なっている幅の真ん中に
        Ui.TextCenter(c, label, new SKRect(Math.Max(top.Left, bottom.Left), top.Top, Math.Min(top.Right, bottom.Right), bottom.Bottom), 24, Ui.Text, bold: true);
    }

    /// <summary>IME の日本語入力と同じく、よく使う記号は全角にする</summary>
    static string ToFullWidth(string s) => s switch { "!" => "！", "?" => "？", "(" => "（", ")" => "）", _ => s };

    void DrawQwertyKey(SKCanvas c, UiButton b, QKey k)
    {
        if (k.Id is "q:enter" or "q:tab") { DrawTwoRowKey(c, b, k.Label); return; }
        bool shift = _mods.Contains("shift");
        var mod = ModOf(k.Id);
        b.Label = shift && k.ShiftLabel != null ? k.ShiftLabel : k.Label;
        b.Active = mod.Length > 0 && _mods.Contains(mod);
        float size = k.Label.Length > 2 || k.Label.Any(ch => ch > 0x7F && ch is not ('¥' or '＼' or '▲' or '▼' or '◀' or '▶' or '⌫')) ? 24 : 34;
        b.Draw(c, IsHover(b), IsPressed(b), size, 12);
        // Shift で出る文字を左上に小さく
        if (!shift && k.ShiftLabel != null)
            Ui.TextCenter(c, k.ShiftLabel, new SKRect(b.Rect.Left + 4, b.Rect.Top + 4, b.Rect.Left + 34, b.Rect.Top + 30), 18, Ui.Muted);
    }

    // ── PC へ直接入力 ──
    // 出力が PC のときは、こちらでは変換せずにキーをそのまま送り、変換は PC の IME に任せる。
    // エンジンには同じ入力を流しておき、「゛゜小」で直前の字を知るのにだけ使う（表示はしない）。

    void PcType(string id, FlickDir dir)
    {
        string val = dir == FlickDir.Center ? id
            : Layout.Flicks.TryGetValue(id, out var f) && f.Get(dir) is { } v ? v : id;
        JisKeys.Type(val);
        _engine.Press(id, dir);
    }

    /// <summary>直前の字を小文字・濁点・半濁点にする。PC には Backspace で消してから打ち直す</summary>
    void PcCycleSmall()
    {
        if (_engine.Pending != null || _engine.Composing.Length == 0) return;
        char before = _engine.Composing[^1];
        _engine.CycleSmall();
        char after = _engine.Composing[^1];
        if (after == before) return;
        Win32Input.KeyPress(JisKeys.VK_BACK);
        JisKeys.Type(after.ToString());
    }

    void DoSend()
    {
        var text = _engine.Take();
        if (PcMode)
        {
            Win32Input.KeyPress(JisKeys.VK_RETURN);
            return;
        }
        if (text.Length == 0) return;
        if (App.Config.Output.SendTo == "tts")
        {
            // vrc_osc.py へ送って読み上げる（届かなければ Chatbox だけ）
            ShowNotice("送信中…", false);
            _ = Task.Run(async () =>
            {
                var (ok, msg) = await App.Output.SendToTtsAsync(text);
                App.Post(() => App.Notify(msg, !ok));
            });
            return;
        }
        try
        {
            App.Output.SendChatbox(text);
            App.Notify("送信", false);
        }
        catch (Exception e)
        {
            Log.Warn("Chatboxへの送信に失敗: " + e.Message);
            App.Notify("送信できません", true);
        }
    }

    // ── 候補の並べ方 ──

    void LayoutCandidates()
    {
        _cands.Clear();
        float top = Pad + HeaderH + Gap + PreviewH + Gap;
        if (_engine.Converting)
        {
            // 1段目: 文節（選択中の候補）、2段目: 選んだ文節の候補
            float x = Pad;
            for (int s = 0; s < _engine.Segments.Count; s++)
            {
                var seg = _engine.Segments[s];
                var label = seg.Candidates.Count > _engine.SegmentChoice[s] ? seg.Candidates[_engine.SegmentChoice[s]] : seg.Input;
                if (!Place(ref x, top, label, "seg:" + s, s == _focusSeg)) break;
            }
            x = Pad;
            if (_focusSeg < _engine.Segments.Count)
            {
                var cands = _engine.Segments[_focusSeg].Candidates;
                for (int c = 0; c < cands.Count; c++)
                    if (!Place(ref x, top + CandRowH + Gap, cands[c], $"cand:{_focusSeg}:{c}", c == _engine.SegmentChoice[_focusSeg])) break;
            }
        }
        else
        {
            float x = Pad, y = top;
            for (int i = 0; i < _engine.AutoCandidates.Count; i++)
            {
                if (!Place(ref x, y, _engine.AutoCandidates[i], "auto:" + i, false))
                {
                    if (y > top) break;
                    x = Pad; y = top + CandRowH + Gap;
                    if (!Place(ref x, y, _engine.AutoCandidates[i], "auto:" + i, false)) break;
                }
            }
        }
    }

    bool Place(ref float x, float y, string label, string id, bool active)
    {
        float w = Math.Max(72, Ui.Measure(label, 30, true) + 36);
        w = Math.Min(w, W - Pad * 2);
        if (x + w > W - Pad) return false;
        _cands.Add(new UiButton { Id = id, Label = label, Rect = new SKRect(x, y, x + w, y + CandRowH), Active = active });
        x += w + 8;
        return true;
    }

    // ── 操作 ──

    UiButton? Find(Vector2 p) =>
        _keys.FirstOrDefault(b => b.Contains(p)) ?? _header.FirstOrDefault(b => b.Contains(p)) ?? _cands.FirstOrDefault(b => b.Contains(p));

    public override void OnHover(Hand hand, Vector2 px)
    {
        var before = _hover[(int)hand] is { } p ? Find(p) : null;
        _hover[(int)hand] = px;
        if (Find(px) != before) Invalidate();
    }

    public override void OnLeave(Hand hand)
    {
        _hover[(int)hand] = null;
        Invalidate();
    }

    public override void OnPress(Hand hand, Vector2 px)
    {
        int i = (int)hand;
        if (PreviewRect.Contains(px.X, px.Y) && _engine.Buffer.Length > 0 && !PcMode)
        {
            _caretDrag[i] = true;
            MoveCaretTo(px.X, hand);
            return;
        }
        var b = Find(px);
        _pressed[i] = b;
        _pressStart[i] = px;
        _flickDir[i] = FlickDir.Center;
        if (b == null) return;
        if (_cfg.Haptics) App.Input.Haptic(hand);
        _repeatAt[i] = double.MaxValue;
        if (b.Id == "#bs")
        {
            DoBackspace();
            _repeatAt[i] = _now + 0.45;
        }
        else if (_qkeys.TryGetValue(b.Id, out var qk))
        {
            // QWERTY のキーは押した瞬間に入る（本物のキーボードと同じ）。⌫・Del・矢印は押し続けると繰り返す
            QwertyPress(qk);
            if (QwertyRepeats(qk.Id)) _repeatAt[i] = _now + 0.45;
        }
        Invalidate();
    }

    public override void OnDrag(Hand hand, Vector2 px)
    {
        int i = (int)hand;
        if (_caretDrag[i]) { MoveCaretTo(px.X, hand); return; }
        var b = _pressed[i];
        if (b == null || !IsCharKey(b.Id)) return;
        var d = px - _pressStart[i];
        float threshold = _cfg.FlickThreshold * b.Rect.Width;
        FlickDir dir = FlickDir.Center;
        if (d.Length() >= threshold)
        {
            float a = MathF.Atan2(d.Y, d.X) * 180 / MathF.PI;
            dir = a > -135 && a <= -45 ? FlickDir.Up
                : a > -45 && a <= 45 ? FlickDir.Right
                : a > 45 && a <= 135 ? FlickDir.Down
                : FlickDir.Left;
            // 数字は強めに引いたときだけ（届かない間はキーをそのまま押したことになる）
            if (Layout.Flicks.TryGetValue(b.Id, out var f) && f.Get(dir) is { Length: > 0 } v && v.All(char.IsAsciiDigit)
                && d.Length() < _cfg.NumberFlickThreshold * b.Rect.Width)
                dir = FlickDir.Center;
        }
        if (dir != _flickDir[i])
        {
            _flickDir[i] = dir;
            if (_cfg.Haptics && dir != FlickDir.Center) App.Input.Haptic(hand, 0.008f, 0.2f);
            Invalidate();
        }
    }

    public override void OnRelease(Hand hand, Vector2 px)
    {
        int i = (int)hand;
        if (_caretDrag[i]) { _caretDrag[i] = false; return; }
        var b = _pressed[i];
        _pressed[i] = null;
        Invalidate();
        if (b == null || _qkeys.ContainsKey(b.Id)) return;

        if (IsCharKey(b.Id))
        {
            if (PcMode) PcType(b.Id, _flickDir[i]);
            else _engine.Press(b.Id, _flickDir[i]);
            return;
        }
        // 操作ボタンは離した位置がボタン上のときだけ
        if (!b.Contains(px)) return;
        switch (b.Id)
        {
            case "#bs": break; // 押した時点で処理済み
            case "#convert":
                if (PcMode) { Win32Input.KeyPress(JisKeys.VK_SPACE); _engine.Clear(); }
                else StartConvert();
                break;
            case "#send": DoSend(); break;
            case "#small":
                if (PcMode) PcCycleSmall(); else _engine.CycleSmall();
                break;
            case "#clear":
            case "clear":
                if (PcMode) Win32Input.KeyPress(JisKeys.VK_KANJI); else _engine.Clear();
                break;
            case "mode": SetPcMode(!PcMode); break;
            case "lock": Locked = !Locked; BuildHeader(); break;
            case "style": ToggleStyle(); break;
            case "close": Hide(); break;
            default:
                if (b.Id.StartsWith("auto:")) _engine.ChooseAutoCandidate(int.Parse(b.Id[5..]));
                else if (b.Id.StartsWith("seg:")) { _focusSeg = int.Parse(b.Id[4..]); LayoutCandidates(); }
                else if (b.Id.StartsWith("cand:"))
                {
                    var parts = b.Id.Split(':');
                    int s = int.Parse(parts[1]), c = int.Parse(parts[2]);
                    _engine.ChooseSegmentCandidate(s, c);
                    // 次の文節へ進む
                    if (s + 1 < _engine.Segments.Count) _focusSeg = s + 1;
                    LayoutCandidates();
                }
                break;
        }
        Invalidate();
    }

    /// <summary>入力欄の x の位置に一番近い文字の境目へカーソルを移す</summary>
    void MoveCaretTo(float x, Hand hand)
    {
        if (_caretStops.Count == 0) return;
        var stop = _caretStops.MinBy(s => MathF.Abs(s.x - x));
        if (stop.index == _engine.Caret && _engine.Composing.Length == 0) return;
        _engine.MoveCaret(stop.index);
        if (_cfg.Haptics) App.Input.Haptic(hand, 0.006f, 0.2f);
        Invalidate();
    }

    static bool IsCharKey(string id) => !id.StartsWith('#') && !id.Contains(':') && id is not ("mode" or "clear" or "close" or "lock" or "style");

    // ── 描画 ──

    protected override void Draw(SKCanvas c)
    {
        Ui.RoundRect(c, new SKRect(0, 0, W, H), 28, Ui.Bg.WithAlpha(245), Ui.Border, 3);

        // ヘッダー
        foreach (var b in _header) b.Draw(c, IsHover(b), IsPressed(b), 26);

        // 入力中の文字列
        var pr = new SKRect(Pad, Pad + HeaderH + Gap, W - Pad, Pad + HeaderH + Gap + PreviewH);
        Ui.RoundRect(c, pr, 16, Ui.Panel, _engine.Converting ? Ui.Accent2 : Ui.Border, 3);
        DrawPreview(c, pr);

        // 候補
        foreach (var b in _cands) b.Draw(c, IsHover(b), IsPressed(b), 30, 12);
        if (_cands.Count == 0 && _converting)
            Ui.TextLeft(c, "…", Pad + 8, Pad + HeaderH + Gap + PreviewH + Gap + CandRowH / 2f, 26, Ui.Muted);

        // キー
        foreach (var k in _keys)
        {
            if (k.Id == "#convert") k.Label = _engine.Converting ? "確定" : _converting ? "…" : "変換";
            if (k.Id == "#send") k.Label = PcMode ? "Enter" : "送信";
            if (_qkeys.TryGetValue(k.Id, out var qk)) { DrawQwertyKey(c, k, qk); continue; }
            k.Draw(c, IsHover(k), IsPressed(k) && !IsCharKey(k.Id), k.Id.StartsWith('#') ? 34 : 44, 16);
            if (IsCharKey(k.Id)) DrawFlickHints(c, k);
        }

    }

    void DrawPreview(SKCanvas c, SKRect r)
    {
        float size = 40;
        string text = _engine.Buffer;
        string pending = _engine.Pending != null ? $"[{_engine.Pending.ToUpperInvariant()}]" : "";

        _caretStops.Clear();
        if (PcMode) { text = ""; pending = ""; }
        if (_notice.Length > 0 && text.Length == 0 && pending.Length == 0)
        {
            Ui.TextLeft(c, _notice, r.Left + 20, r.MidY, 28, _noticeError ? Ui.Danger : Ui.Ok);
            return;
        }
        if (text.Length == 0 && pending.Length == 0) return;
        // 長いときはカーソルが見えるように前を削る（確定済みの方から）
        string committed = _engine.Committed, composing = _engine.Composing, after = _engine.After;
        float maxW = r.Width - 40 - Ui.Measure(pending, size, true);
        int skip = 0;
        while (Ui.Measure(committed + composing, size) > maxW && committed.Length + composing.Length > 1)
        {
            if (committed.Length > 0) committed = committed[1..];
            else composing = composing[1..];
            skip++;
        }
        float x0 = r.Left + 20, x = x0;
        using var clip = new SKAutoCanvasRestore(c);
        c.ClipRect(new SKRect(r.Left + 4, r.Top, r.Right - 8, r.Bottom));
        x += Ui.TextLeft(c, committed, x, r.MidY, size, Ui.Text);
        if (composing.Length > 0)
        {
            // 未確定（変換の対象）は下線付き。変換中は色を変える
            float w = Ui.TextLeft(c, composing, x, r.MidY, size, _engine.Converting ? Ui.Accent2 : Ui.Text);
            using var ul = new SKPaint { Color = _engine.Converting ? Ui.Accent2 : Ui.Accent, StrokeWidth = 3 };
            c.DrawLine(x, r.MidY + 26, x + w, r.MidY + 26, ul);
            x += w;
        }
        if (pending.Length > 0) x += Ui.TextLeft(c, pending, x + 4, r.MidY, size, Ui.Accent, bold: true) + 4;
        // カーソル
        float caretX = x + 4;
        using var p = new SKPaint { Color = Ui.Accent, StrokeWidth = 3 };
        c.DrawLine(caretX, r.MidY - 22, caretX, r.MidY + 22, p);
        // カーソルより後ろ（途中を直しているとき）
        if (after.Length > 0) Ui.TextLeft(c, after, caretX + 4, r.MidY, size, Ui.Text);

        // 押してカーソルを移せる位置（文字の境目）
        string before = committed + composing;
        for (int k = 0; k <= before.Length; k++)
            _caretStops.Add((x0 + Ui.Measure(before[..k], size), skip + k));
        for (int k = 1; k <= after.Length; k++)
        {
            float ax = caretX + 4 + Ui.Measure(after[..k], size);
            if (ax > r.Right) break;
            _caretStops.Add((ax, skip + before.Length + k));
        }

        if (_notice.Length > 0)
            Ui.TextLeft(c, _notice, r.Left + 20, r.Bottom - 14, 18, _noticeError ? Ui.Danger : Ui.Ok);
    }

    void DrawFlickHints(SKCanvas c, UiButton k)
    {
        if (!Layout.Flicks.TryGetValue(k.Id, out var f)) return;
        var r = k.Rect;
        void Hint(string? s, float x, float y)
        {
            if (s == null) return;
            Ui.TextCenter(c, s, new SKRect(x - 20, y - 14, x + 20, y + 14), 20, Ui.Muted);
        }
        Hint(f.Up, r.MidX, r.Top + 16);
        Hint(f.Down, r.MidX, r.Bottom - 16);
        Hint(f.Left, r.Left + 18, r.MidY);
        Hint(f.Right, r.Right - 18, r.MidY);
    }

    public override bool Flush()
    {
        bool sent = base.Flush();
        UpdatePopup();
        return sent;
    }

    /// <summary>文字のキーを押している間だけ、そのキーの上にフリックの候補を出す</summary>
    void UpdatePopup()
    {
        int hand = _pressed[1] is { } r && IsCharKey(r.Id) ? 1 : _pressed[0] is { } l && IsCharKey(l.Id) ? 0 : -1;
        if (hand < 0 || !Visible)
        {
            if (_popup.Visible) _popup.Hide();
            return;
        }
        var k = _pressed[hand]!;
        Layout.Flicks.TryGetValue(k.Id, out var f);
        _popup.SetContent(k.Label, f, _flickDir[hand], k.Rect.Width * 0.62f, k.Rect.Height * 0.62f);
        _popup.Place(this, k.Rect);
        if (!_popup.Visible) _popup.Show();
        _popup.Flush();
    }

    protected override void OnHide() => _popup.Hide();

    public override void Dispose()
    {
        _popup.Dispose();
        base.Dispose();
    }

    // 確認用
    public void DebugSavePopup(string path) => _popup.SaveSnapshot(path);

    /// <summary>確認用: キーを押す。hold=true なら離さずにフリック中の状態で止める。reach はフリックする距離（キー幅に対する割合）</summary>
    public void DebugTap(string id, FlickDir dir = FlickDir.Center, bool hold = false, float reach = 0.5f)
    {
        var b = _keys.Concat(_header).Concat(_cands).FirstOrDefault(k => k.Id == id);
        if (b == null) return;
        var c = new Vector2(b.Rect.MidX, b.Rect.MidY);
        float off = b.Rect.Width * reach;
        var end = dir switch
        {
            FlickDir.Up => c + new Vector2(0, -off),
            FlickDir.Down => c + new Vector2(0, off),
            FlickDir.Left => c + new Vector2(-off, 0),
            FlickDir.Right => c + new Vector2(off, 0),
            _ => c,
        };
        OnPress(Hand.Right, c);
        OnDrag(Hand.Right, end);
        if (!hold) OnRelease(Hand.Right, end);
    }

    public string DebugText => $"確定「{_engine.Committed}」未確定「{_engine.Composing}」" + (_engine.Pending != null ? $"[{_engine.Pending}]" : "") + (_engine.After.Length > 0 ? $"後ろ「{_engine.After}」" : "");

    /// <summary>確認用: 入力欄の index 文字目の前を押す</summary>
    public void DebugTapPreview(int index)
    {
        var stop = _caretStops.FirstOrDefault(s => s.index == index);
        var px = new Vector2(stop.x, PreviewRect.MidY);
        OnPress(Hand.Right, px);
        OnRelease(Hand.Right, px);
    }

    bool IsHover(UiButton b) => (_hover[0] is { } a && b.Contains(a)) || (_hover[1] is { } d && b.Contains(d));
    bool IsPressed(UiButton b) => _pressed[0] == b || _pressed[1] == b;
}
