namespace GodanOverlay.Input;

/// <summary>
/// console.html の GODAN 入力ロジックを移植したもの。
/// 打鍵（キーID + フリック方向）を受けて、入力中の文字列と子音待ちの状態を持つ。
/// 入力欄は「確定済み（Committed）」と「未確定のかな（Composing）」に分かれていて、
/// 変換するのは未確定の部分だけ（普通の日本語入力と同じ）。
/// </summary>
public sealed class GodanEngine
{
    /// <summary>確定済みの文字列</summary>
    public string Committed { get; private set; } = "";
    /// <summary>まだ変換していない部分（変換中は変換結果）</summary>
    public string Composing { get; private set; } = "";
    /// <summary>カーソルより後ろの文字（入力欄の途中を直しているとき）</summary>
    public string After { get; private set; } = "";
    /// <summary>入力欄の全体</summary>
    public string Buffer => Committed + Composing + After;
    /// <summary>母音待ちの子音（ローマ字）。なければ null</summary>
    public string? Pending { get; private set; }

    // ── 変換状態 ──
    public bool Converting { get; private set; }
    public List<Segment> Segments { get; } = new();
    public List<int> SegmentChoice { get; } = new();
    public List<string> AutoCandidates { get; } = new();
    string _convertSource = "";

    /// <summary>文字列が変わったとき（自動候補の取得に使う）</summary>
    public event Action? Changed;

    GodanLayout _layout;
    public GodanEngine(GodanLayout layout) => _layout = layout;

    /// <summary>配列を切り替える。打ちかけの子音は捨てる</summary>
    public void SetLayout(GodanLayout layout)
    {
        FlushPendingN();
        _layout = layout;
        Changed?.Invoke();
    }

    public sealed record Segment(string Input, List<string> Candidates);

    static readonly HashSet<string> Vowels = ["a", "i", "u", "e", "o"];

    public void Press(string keyId, FlickDir dir)
    {
        string? val = dir == FlickDir.Center
            ? keyId
            : _layout.Flicks.TryGetValue(keyId, out var f) ? f.Get(dir) : null;
        // 割り当てのない方向へ動いてしまったときは、キーをそのまま押したことにする
        if (val == null) { dir = FlickDir.Center; val = keyId; }

        // 変換中に続きを打ったら、変換結果を確定してから続ける
        if (Converting) CommitConversion();

        bool isVowelKey = Vowels.Contains(keyId) && dir == FlickDir.Center;

        // 「n」の後に母音・n以外が来たら「ん」を確定（ny のように続けられる場合は除く）
        if (Pending == "n" && !isVowelKey && !(keyId == "n" && dir == FlickDir.Center))
        {
            string tryAppend = "n" + (val != null ? val.ToLowerInvariant() : keyId);
            if (!IsValidConsPrefix(tryAppend))
            {
                Composing += "ん";
                Pending = null;
            }
        }

        if (keyId == "n" && dir == FlickDir.Center)
        {
            if (Pending == "n") { Composing += "ん"; Pending = null; }
            else if (Pending != null) Pending = IsValidConsPrefix(Pending + "n") ? Pending + "n" : "n";
            else Pending = "n";
        }
        else if (isVowelKey)
        {
            Vowel(keyId);
            return;
        }
        else if (val == null)
        {
            return; // 何も割り当てのないフリック
        }
        else if (!IsAsciiLetters(val))
        {
            Composing += val; // 記号・長音などはそのまま
            Pending = null;
        }
        else
        {
            string cons = val.ToLowerInvariant();
            if (Pending != null)
            {
                string tryAppend = Pending + cons;
                if (IsValidConsPrefix(tryAppend)) Pending = tryAppend;
                else
                {
                    if (Pending == cons) Composing += "っ";
                    Pending = cons;
                }
            }
            else Pending = cons;
        }
        Changed?.Invoke();
    }

    void Vowel(string v)
    {
        if (Pending == null) Composing += RomajiTable.Map.GetValueOrDefault(v, v);
        else
        {
            string key = Pending + v;
            Composing += RomajiTable.Map.GetValueOrDefault(key, key);
            Pending = null;
        }
        Changed?.Invoke();
    }

    public void Backspace()
    {
        if (Converting)
        {
            Composing = _convertSource; // 変換前のひらがなに戻す
            ClearConversion();
        }
        else if (Pending != null) Pending = null;
        else if (Composing.Length > 0) Composing = DropLast(Composing);
        else if (Committed.Length > 0) Committed = DropLast(Committed);
        Changed?.Invoke();
    }

    static string DropLast(string s)
    {
        var runes = s.EnumerateRunes().ToList();
        runes.RemoveAt(runes.Count - 1);
        return string.Concat(runes.Select(r => r.ToString()));
    }

    /// <summary>小文字・濁点・半濁点の切り替え（未確定の最後の文字）</summary>
    public void CycleSmall()
    {
        if (Converting || Composing.Length == 0) return;
        string last = Composing[^1].ToString();
        if (RomajiTable.SmallCycle.TryGetValue(last, out var next))
        {
            Composing = Composing[..^1] + next;
            Changed?.Invoke();
        }
    }

    public void Clear()
    {
        Committed = "";
        Composing = "";
        After = "";
        Pending = null;
        ClearConversion();
        Changed?.Invoke();
    }

    /// <summary>送信用に文字列を取り出して空にする</summary>
    public string Take()
    {
        FlushPendingN();
        var text = Buffer.Trim();
        Committed = "";
        Composing = "";
        After = "";
        Pending = null;
        ClearConversion();
        Changed?.Invoke();
        return text;
    }

    /// <summary>送れなかった文字列を入力欄に戻す。もう次を打ち始めていたら戻さない</summary>
    public void Restore(string text)
    {
        if (Buffer.Length > 0 || Pending != null || Converting) return;
        Committed = text;
        Changed?.Invoke();
    }

    /// <summary>
    /// カーソルを入力欄の index 文字目の前へ移す。未確定の文字はそのまま確定する（スマホと同じ）。
    /// </summary>
    public void MoveCaret(int index)
    {
        if (Converting) CommitConversion();
        FlushPendingN();
        var full = Committed + Composing + After;
        index = Math.Clamp(index, 0, full.Length);
        if (index > 0 && index < full.Length && char.IsLowSurrogate(full[index])) index--; // 絵文字の途中で切らない
        if (index == Committed.Length + Composing.Length && Composing.Length == 0) return;
        Committed = full[..index];
        Composing = "";
        After = full[index..];
        AutoCandidates.Clear();
        Changed?.Invoke();
    }

    /// <summary>未確定の文字を、変換せずにそのまま確定する（IME の Enter と同じ）</summary>
    public void CommitComposing()
    {
        if (Converting) { CommitConversion(); return; }
        FlushPendingN();
        Committed += Composing;
        Composing = "";
        AutoCandidates.Clear();
        Changed?.Invoke();
    }

    /// <summary>カーソルの位置（入力欄の先頭からの文字数）</summary>
    public int Caret => Committed.Length + Composing.Length;

    public void FlushPendingN()
    {
        if (Pending == "n") Composing += "ん";
        Pending = null;
    }

    // ── 変換 ──

    /// <summary>
    /// 変換キー。未確定の部分を返す（これを文節ごとに変換する）。
    /// 未確定がなければ全角スペースを入れて空を返す。
    /// </summary>
    public string BeginConvert()
    {
        FlushPendingN();
        if (Composing.Trim().Length == 0)
        {
            Committed += Composing + "　";
            Composing = "";
        }
        Changed?.Invoke();
        return Composing.Trim();
    }

    public void ApplyConversion(string source, List<Segment> segments)
    {
        if (Composing.Trim() != source || segments.Count == 0) return; // 待っている間に打たれた
        _convertSource = Composing;
        Segments.Clear();
        Segments.AddRange(segments);
        SegmentChoice.Clear();
        SegmentChoice.AddRange(segments.Select(_ => 0));
        Converting = true;
        AutoCandidates.Clear();
        RebuildFromSegments();
    }

    public void ChooseSegmentCandidate(int seg, int cand)
    {
        if (!Converting || seg >= Segments.Count || cand >= Segments[seg].Candidates.Count) return;
        SegmentChoice[seg] = cand;
        RebuildFromSegments();
    }

    void RebuildFromSegments()
    {
        Composing = string.Concat(Segments.Select((s, i) =>
            s.Candidates.Count > SegmentChoice[i] ? s.Candidates[SegmentChoice[i]] : s.Input));
    }

    /// <summary>変換結果を確定する</summary>
    public void CommitConversion()
    {
        Committed += Composing;
        Composing = "";
        ClearConversion();
        Changed?.Invoke();
    }

    void ClearConversion()
    {
        Converting = false;
        _convertSource = "";
        Segments.Clear();
        SegmentChoice.Clear();
        AutoCandidates.Clear();
    }

    /// <summary>自動候補（未確定部分をひとかたまりで変換した結果）を受け取る</summary>
    public void SetAutoCandidates(string source, List<string> cands)
    {
        if (Converting || Composing.Trim() != source) return;
        AutoCandidates.Clear();
        AutoCandidates.AddRange(cands.Where(c => c != source).Distinct().Take(10));
    }

    /// <summary>自動候補を選ぶと、未確定の部分がその候補で確定する</summary>
    public void ChooseAutoCandidate(int i)
    {
        if (i < 0 || i >= AutoCandidates.Count) return;
        Committed += AutoCandidates[i];
        Composing = "";
        // 打ちかけの子音（候補を選ぶ前に押していたもの）は次の未確定に残す
        AutoCandidates.Clear();
        Changed?.Invoke();
    }

    public void ClearAutoCandidates() => AutoCandidates.Clear();

    static bool IsValidConsPrefix(string cons) =>
        Vowels.Contains(cons) || Vowels.Any(v => RomajiTable.Map.ContainsKey(cons + v));

    static bool IsAsciiLetters(string s) => s.All(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
}
