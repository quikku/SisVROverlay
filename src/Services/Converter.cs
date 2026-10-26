using System.Text.Json;
using GodanOverlay.Input;

namespace GodanOverlay.Services;

/// <summary>かな漢字変換エンジン。差し替えられるようにインターフェースにしておく</summary>
public interface IConverter
{
    /// <summary>文節に区切って変換する</summary>
    Task<List<GodanEngine.Segment>> ConvertAsync(string kana, CancellationToken ct);
    /// <summary>文全体をひとかたまりとして変換候補を出す</summary>
    Task<List<string>> WholeAsync(string kana, CancellationToken ct);
}

public sealed class NoConverter : IConverter
{
    public Task<List<GodanEngine.Segment>> ConvertAsync(string kana, CancellationToken ct) => Task.FromResult(new List<GodanEngine.Segment>());
    public Task<List<string>> WholeAsync(string kana, CancellationToken ct) => Task.FromResult(new List<string>());
}

/// <summary>Google日本語入力 Web API（www.google.com/transliterate）</summary>
public sealed class GoogleConverter : IConverter
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    static GoogleConverter()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("GodanOverlay/1.0");
    }

    public async Task<List<GodanEngine.Segment>> ConvertAsync(string kana, CancellationToken ct) => await Query(kana, ct);

    public async Task<List<string>> WholeAsync(string kana, CancellationToken ct)
    {
        // 末尾に「,」を付けると文節に区切らず、文全体の候補が返る
        var segs = await Query(kana + ",", ct);
        return segs.Count > 0 ? segs[0].Candidates : new List<string>();
    }

    static async Task<List<GodanEngine.Segment>> Query(string text, CancellationToken ct)
    {
        var url = "https://www.google.com/transliterate?langpair=ja-Hira%7Cja&text=" + Uri.EscapeDataString(text);
        var json = await Http.GetStringAsync(url, ct);
        var result = new List<GodanEngine.Segment>();
        using var doc = JsonDocument.Parse(json);
        foreach (var seg in doc.RootElement.EnumerateArray())
        {
            if (seg.GetArrayLength() < 2) continue;
            var input = seg[0].GetString() ?? "";
            var cands = seg[1].EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
            result.Add(new GodanEngine.Segment(input, cands));
        }
        return result;
    }
}
