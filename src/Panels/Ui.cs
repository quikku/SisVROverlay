using System.Globalization;
using System.Text;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace GodanOverlay.Panels;

/// <summary>console.html と同じ配色と、日本語フォントでの文字描画</summary>
public static class Ui
{
    public static readonly SKColor Bg = SKColor.Parse("#0d1117");
    public static readonly SKColor Panel = SKColor.Parse("#161b22");
    public static readonly SKColor PanelHover = SKColor.Parse("#1f2733");
    public static readonly SKColor Border = SKColor.Parse("#30363d");
    public static readonly SKColor Text = SKColor.Parse("#e6edf3");
    public static readonly SKColor Muted = SKColor.Parse("#7d8590");
    public static readonly SKColor Accent = SKColor.Parse("#00d4ff");
    public static readonly SKColor Accent2 = SKColor.Parse("#7c3aed");
    public static readonly SKColor Danger = SKColor.Parse("#f85149");
    public static readonly SKColor Ok = SKColor.Parse("#3fb950");

    static readonly SKTypeface Regular = Pick(SKFontStyle.Normal);
    static readonly SKTypeface Bold = Pick(SKFontStyle.Bold);
    static readonly SKTypeface? Emoji = Find("Segoe UI Emoji", SKFontStyle.Normal);
    static readonly Dictionary<(int, bool), SKTypeface> Fallbacks = new();
    static readonly Dictionary<SKTypeface, SKShaper> Shapers = new();

    static SKTypeface? Find(string name, SKFontStyle style)
    {
        var tf = SKTypeface.FromFamilyName(name, style);
        return tf != null && tf.FamilyName == name ? tf : null;
    }

    static SKTypeface Pick(SKFontStyle style)
    {
        foreach (var name in new[] { "Yu Gothic UI", "Meiryo UI", "Meiryo", "MS Gothic" })
            if (Find(name, style) is { } tf) return tf;
        return SKTypeface.Default;
    }

    /// <summary>文字列を、同じフォントで描ける部分ごとに分ける（日本語と絵文字が混ざっても化けないように）</summary>
    static List<(string text, SKTypeface face)> Runs(string text, bool bold)
    {
        var main = bold ? Bold : Regular;
        var runs = new List<(string, SKTypeface)>();
        var sb = new StringBuilder();
        SKTypeface? cur = null;
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var el = (string)e.Current;
            var face = FaceFor(el, main, bold);
            if (cur != null && face != cur)
            {
                runs.Add((sb.ToString(), cur));
                sb.Clear();
            }
            cur = face;
            sb.Append(el);
        }
        if (cur != null && sb.Length > 0) runs.Add((sb.ToString(), cur));
        return runs;
    }

    static SKTypeface FaceFor(string element, SKTypeface main, bool bold)
    {
        int cp = Rune.GetRuneAt(element, 0).Value;
        // 絵文字（異体字セレクタや ZWJ でつながったものを含む）は絵文字のフォントで描く
        bool emoji = cp >= 0x1F000 || element.Contains('\uFE0F') || element.Contains('\u200D')
                     || (cp >= 0x2600 && cp <= 0x27BF && !main.ContainsGlyph(cp));
        if (emoji && Emoji != null) return Emoji;
        if (main.ContainsGlyph(cp)) return main;
        if (!Fallbacks.TryGetValue((cp, bold), out var fb))
        {
            fb = SKFontManager.Default.MatchCharacter(main.FamilyName, bold ? SKFontStyle.Bold : SKFontStyle.Normal, null, cp) ?? main;
            Fallbacks[(cp, bold)] = fb;
        }
        return fb;
    }

    static SKShaper ShaperFor(SKTypeface face)
    {
        if (!Shapers.TryGetValue(face, out var s)) Shapers[face] = s = new SKShaper(face);
        return s;
    }

    static SKFont MakeFont(SKTypeface face, float size) =>
        new(face, size) { Edging = SKFontEdging.Antialias, Subpixel = true };

    /// <summary>描いたときの幅</summary>
    public static float Measure(string text, float size, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        float w = 0;
        foreach (var (t, face) in Runs(text, bold))
        {
            using var f = MakeFont(face, size);
            w += ShaperFor(face).Shape(t, f).Width;
        }
        return w;
    }

    /// <summary>左端 x、縦の中心 midY に描き、描いた幅を返す</summary>
    static float DrawRuns(SKCanvas c, string text, float x, float midY, float size, SKColor color, bool bold)
    {
        using var mainFont = MakeFont(bold ? Bold : Regular, size);
        mainFont.GetFontMetrics(out var fm);
        float baseline = midY - (fm.Ascent + fm.Descent) / 2;
        using var p = new SKPaint { Color = color, IsAntialias = true };
        float start = x;
        foreach (var (t, face) in Runs(text, bold))
        {
            using var f = MakeFont(face, size);
            var shaper = ShaperFor(face);
            c.DrawShapedText(shaper, t, x, baseline, SKTextAlign.Left, f, p);
            x += shaper.Shape(t, f).Width;
        }
        return x - start;
    }

    /// <summary>矩形の中央に1行で描く。はみ出すときは縮める</summary>
    public static void TextCenter(SKCanvas c, string text, SKRect r, float size, SKColor color, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        float w = Measure(text, size, bold);
        if (w > r.Width * 0.92f)
        {
            size *= r.Width * 0.92f / w;
            w = Measure(text, size, bold);
        }
        DrawRuns(c, text, r.MidX - w / 2, r.MidY, size, color, bold);
    }

    /// <summary>左寄せで描き、描いた幅を返す</summary>
    public static float TextLeft(SKCanvas c, string text, float x, float midY, float size, SKColor color, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return DrawRuns(c, text, x, midY, size, color, bold);
    }

    /// <summary>左寄せで幅に収まるように描く（はみ出す分は末尾を「…」にする）</summary>
    public static void TextLeftFit(SKCanvas c, string text, float x, float midY, float size, float maxWidth, SKColor color, bool bold = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        // 改行は1行にまとめる
        text = text.Replace("\r", "").Replace('\n', ' ');
        if (Measure(text, size, bold) > maxWidth)
        {
            var els = new List<string>();
            var e = StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext()) els.Add((string)e.Current);
            while (els.Count > 1 && Measure(string.Concat(els) + "…", size, bold) > maxWidth) els.RemoveAt(els.Count - 1);
            text = string.Concat(els) + "…";
        }
        TextLeft(c, text, x, midY, size, color, bold);
    }

    /// <summary>幅に収まるように折り返す。maxLines を超える分は最後の行の末尾を「…」にする</summary>
    public static List<string> Wrap(string text, float size, float maxWidth, int maxLines, bool bold = false)
    {
        var lines = new List<string>();
        var cur = new StringBuilder();
        var e = StringInfo.GetTextElementEnumerator(text.Replace("\r", ""));
        while (e.MoveNext())
        {
            var el = (string)e.Current;
            if (el == "\n" || Measure(cur + el, size, bold) > maxWidth)
            {
                lines.Add(cur.ToString());
                cur.Clear();
                if (el == "\n") continue;
            }
            cur.Append(el);
        }
        if (cur.Length > 0) lines.Add(cur.ToString());
        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            var last = lines[^1];
            while (last.Length > 1 && Measure(last + "…", size, bold) > maxWidth) last = last[..^1];
            lines[^1] = last + "…";
        }
        return lines;
    }

    public static void RoundRect(SKCanvas c, SKRect r, float radius, SKColor fill, SKColor? stroke = null, float strokeWidth = 2)
    {
        using var p = new SKPaint { Color = fill, IsAntialias = true, Style = SKPaintStyle.Fill };
        c.DrawRoundRect(r, radius, radius, p);
        if (stroke is { } s)
        {
            using var sp = new SKPaint { Color = s, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = strokeWidth };
            var inset = r;
            inset.Inflate(-strokeWidth / 2, -strokeWidth / 2);
            c.DrawRoundRect(inset, radius, radius, sp);
        }
    }
}

/// <summary>パネル上の押せる矩形</summary>
public sealed class UiButton
{
    public string Id = "";
    public string Label = "";
    /// <summary>Material Symbols の名前（Icons）。Label もあれば並べて描く</summary>
    public string? Icon;
    public SKRect Rect;
    public bool Active;   // ON状態など
    public bool Primary;  // 送信ボタンなど強調するもの

    public bool Contains(System.Numerics.Vector2 p) => Rect.Contains(p.X, p.Y);

    public void Draw(SKCanvas c, bool hover, bool pressed, float fontSize, float radius = 14)
    {
        var s = ButtonFx.Style(this, hover, pressed);
        var r = ButtonFx.Shrink(Rect, s.Press);
        ButtonFx.Glow(c, r, radius, s);
        Ui.RoundRect(c, r, radius, s.Fill, s.Border, 3);
        DrawContent(c, r, fontSize * (1 - 0.04f * s.Press), s.Text);
    }

    void DrawContent(SKCanvas c, SKRect r, float fontSize, SKColor color)
    {
        if (Icon == null) { Ui.TextCenter(c, Label, r, fontSize, color, bold: true); return; }
        if (Label.Length == 0)
        {
            Icons.Draw(c, Icon, r.MidX, r.MidY, Math.Min(r.Width, r.Height) * 0.56f, color);
            return;
        }
        if (r.Height >= fontSize * 3.6f)
        {
            // 縦に長いボタンは、アイコンの下に名前
            float size = Math.Min(r.Height * 0.4f, r.Width * 0.42f), gap = fontSize * 0.25f;
            float top = r.MidY - (size + gap + fontSize) / 2;
            Icons.Draw(c, Icon, r.MidX, top + size / 2, size, color);
            Ui.TextCenter(c, Label, new SKRect(r.Left, top + size + gap, r.Right, top + size + gap + fontSize), fontSize, color, bold: true);
            return;
        }
        // 横に並べる。入りきらなければアイコンと文字をまとめて縮める
        float icon = fontSize * 1.25f, space = fontSize * 0.3f;
        float textW = Ui.Measure(Label, fontSize, bold: true);
        float k = Math.Min(1, r.Width * 0.88f / (icon + space + textW));
        icon *= k; space *= k; textW *= k;
        float x = r.MidX - (icon + space + textW) / 2;
        Icons.Draw(c, Icon, x + icon / 2, r.MidY, icon, color);
        Ui.TextLeft(c, Label, x + icon + space, r.MidY, fontSize * k, color, bold: true);
    }
}

/// <summary>
/// ボタンを指したり押したりしたときの色や大きさの変化を、なめらかにつなぐ。
/// 描くたびにボタンを作り直すパネルもあるので、状態はパネルとボタンの Id ごとに覚えておく。
/// 変化の途中は Panel.Flush が次のフレームも描き直す。
/// </summary>
public static class ButtonFx
{
    sealed class State { public float Hover, Press, Active; public double Last; }

    public readonly record struct Look(SKColor Fill, SKColor Border, SKColor Text, float Hover, float Press);

    static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    static readonly Dictionary<(object?, string), State> States = new();
    static object? _owner;
    static bool _moving;
    static double _nextPrune;

    // 変化にかける秒数。押した瞬間は速く、離したあとは少し残光が残る
    const float HoverIn = 0.09f, HoverOut = 0.22f, PressIn = 0.04f, PressOut = 0.3f, ActiveTime = 0.16f;

    static readonly SKColor PressFill = new(0, 212, 255, 90);
    static readonly SKColor ActiveFill = new(0, 212, 255, 40);
    static readonly SKColor ActiveHoverFill = new(0, 212, 255, 64);
    static readonly SKColor PrimaryHover = SKColor.Parse("#5ce3ff");
    static readonly SKColor PrimaryPress = SKColor.Parse("#a6f1ff");

    /// <summary>パネルを描き始める前に呼ぶ。owner ごとに状態を分ける</summary>
    public static void Begin(object owner)
    {
        _owner = owner;
        _moving = false;
    }

    /// <summary>描き終えたら呼ぶ。変化の途中のボタンがあれば true（次のフレームも描き直す）</summary>
    public static bool End()
    {
        _owner = null;
        double now = Clock.Elapsed.TotalSeconds;
        if (now > _nextPrune)
        {
            _nextPrune = now + 10;
            foreach (var key in States.Where(kv => now - kv.Value.Last > 30).Select(kv => kv.Key).ToList()) States.Remove(key);
        }
        return _moving;
    }

    static float Approach(float v, float target, float dt, float upSeconds, float downSeconds)
    {
        if (v < target) v = Math.Min(target, v + dt / upSeconds);
        else if (v > target) v = Math.Max(target, v - dt / downSeconds);
        if (v != target) _moving = true;
        return v;
    }

    static float Ease(float t) => t * t * (3 - 2 * t);

    static SKColor Mix(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t),
        (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t),
        (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));

    /// <summary>
    /// 今のフレームでの色と、指している度合い・押している度合い（0〜1）。
    /// 同じ Id のボタンは同じ状態を使う（2段にまたがるキーなど）。
    /// </summary>
    public static Look Style(UiButton b, bool hover, bool pressed)
    {
        double now = Clock.Elapsed.TotalSeconds;
        var key = (_owner, b.Id.Length > 0 ? b.Id : b.Rect.ToString());
        if (!States.TryGetValue(key, out var st))
        {
            // 初めて描くボタンは、いきなり今の状態で出す
            st = new State { Hover = hover ? 1 : 0, Press = pressed ? 1 : 0, Active = b.Active ? 1 : 0, Last = now };
            States[key] = st;
        }
        float dt = (float)Math.Clamp(now - st.Last, 0, 0.05); // 久しぶりに描くときに飛ばない
        st.Last = now;
        st.Hover = Approach(st.Hover, hover ? 1 : 0, dt, HoverIn, HoverOut);
        st.Press = Approach(st.Press, pressed ? 1 : 0, dt, PressIn, PressOut);
        st.Active = Approach(st.Active, b.Active ? 1 : 0, dt, ActiveTime, ActiveTime);
        float h = Ease(st.Hover), p = Ease(st.Press), a = Ease(st.Active);

        SKColor fill, border, text;
        if (b.Primary)
        {
            fill = Mix(Mix(Ui.Accent, PrimaryHover, h), PrimaryPress, p);
            border = Ui.Accent;
            text = SKColors.Black;
        }
        else
        {
            fill = Mix(Mix(Ui.Panel, ActiveFill, a), Mix(Ui.PanelHover, ActiveHoverFill, a), h);
            fill = Mix(fill, PressFill, p);
            border = Mix(Mix(Ui.Border, Ui.Accent, a), Ui.Accent, Math.Max(h, p));
            text = Mix(Mix(Ui.Text, Ui.Accent, a), SKColors.White, p * 0.6f);
        }
        return new Look(fill, border, text, h, p);
    }

    /// <summary>押している間は少し沈んだように縮める</summary>
    public static SKRect Shrink(SKRect r, float press)
    {
        float k = 0.035f * press;
        r.Inflate(-r.Width * k / 2, -r.Height * k / 2);
        return r;
    }

    /// <summary>指している・押しているときに、ボタンのまわりをふわっと光らせる</summary>
    public static void Glow(SKCanvas c, SKRect r, float radius, Look s) =>
        Glow(c, path => path.AddRoundRect(r, radius, radius), s);

    public static void Glow(SKCanvas c, Action<SKPath> shape, Look s)
    {
        float strength = Math.Max(s.Hover * 0.35f, s.Press);
        if (strength < 0.01f) return;
        using var path = new SKPath();
        shape(path);
        using var p = new SKPaint
        {
            Color = Ui.Accent.WithAlpha((byte)(110 * strength)),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Outer, 4 + 6 * strength),
        };
        c.DrawPath(path, p);
    }
}
