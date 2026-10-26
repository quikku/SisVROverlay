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
    public SKRect Rect;
    public bool Active;   // ON状態など
    public bool Primary;  // 送信ボタンなど強調するもの

    public bool Contains(System.Numerics.Vector2 p) => Rect.Contains(p.X, p.Y);

    public void Draw(SKCanvas c, bool hover, bool pressed, float fontSize, float radius = 14)
    {
        SKColor fill = Ui.Panel, border = Ui.Border, text = Ui.Text;
        if (Primary) { fill = Ui.Accent; border = Ui.Accent; text = SKColors.Black; }
        if (Active) { fill = new SKColor(0, 212, 255, 40); border = Ui.Accent; text = Ui.Accent; }
        if (hover && !Primary) { fill = Ui.PanelHover; border = Ui.Accent; }
        if (pressed) { fill = Primary ? SKColor.Parse("#7fe9ff") : new SKColor(0, 212, 255, 90); border = Ui.Accent; }
        Ui.RoundRect(c, Rect, radius, fill, border, 3);
        Ui.TextCenter(c, Label, Rect, fontSize, text, bold: true);
    }
}
