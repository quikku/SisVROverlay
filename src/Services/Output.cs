using System.Net.Sockets;
using System.Text;

namespace GodanOverlay.Services;

/// <summary>確定した文章の送り先</summary>
public sealed class Output
{
    readonly OutputConfig _cfg;

    public Output(OutputConfig cfg) => _cfg = cfg;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// vrc_osc.py の /send に送る（音声 + Chatbox）。
    /// サーバーに繋がらないときや Chatbox に出なかったときは、VRChat の Chatbox へ OSC で直接送る。
    /// 戻り値は通知用の短い文。例外は投げない。
    /// </summary>
    public async Task<(bool ok, string message)> SendToTtsAsync(string text)
    {
        bool ttsOk, vrcOk;
        try
        {
            var url = _cfg.TtsServerUrl.TrimEnd('/') + "/send";
            // vrc_osc.py は Content-Length の分だけ読むので、チャンク転送にならない StringContent で送る
            var json = System.Text.Json.JsonSerializer.Serialize(new { text });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(url, content);
            // 応答は {"tts": {"ok":..,"error":..}, "vrc": {"ok":..}} か {"ok": false, "error": ..}
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            ttsOk = IsOk(root, "tts");
            vrcOk = IsOk(root, "vrc");
            if (!ttsOk) Log.Warn("TTSの送信に失敗: " + ErrorOf(root));
        }
        catch (TaskCanceledException)
        {
            // 繋がったまま返事がない。向こうで Chatbox に出している途中かもしれないので、二重に送らない
            Log.Warn("TTSサーバーの応答がありません");
            return (false, "TTSの応答がありません");
        }
        catch (Exception e)
        {
            Log.Warn($"TTSサーバーに送れませんでした: {e.Message}");
            ttsOk = vrcOk = false;
        }

        if (vrcOk) return ttsOk ? (true, "送信") : (true, "Chatboxのみ送信");
        if (!_cfg.OscFallback) return ttsOk ? (true, "読み上げのみ") : (false, "送信できません");
        try
        {
            SendChatbox(text);
            return (true, ttsOk ? "送信" : "Chatboxのみ送信");
        }
        catch (Exception e)
        {
            Log.Warn("Chatboxへの送信に失敗: " + e.Message);
            return ttsOk ? (true, "読み上げのみ") : (false, "送信できません");
        }
    }

    /// <summary>root[key].ok が true か。形が違っても例外にしない</summary>
    static bool IsOk(System.Text.Json.JsonElement root, string key) =>
        root.ValueKind == System.Text.Json.JsonValueKind.Object
        && root.TryGetProperty(key, out var o) && o.ValueKind == System.Text.Json.JsonValueKind.Object
        && o.TryGetProperty("ok", out var ok) && ok.ValueKind == System.Text.Json.JsonValueKind.True;

    /// <summary>ログ用のエラー文。root.error か root.tts.error</summary>
    static string ErrorOf(System.Text.Json.JsonElement root)
    {
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return root.GetRawText();
        if (root.TryGetProperty("error", out var e)) return e.ToString();
        if (root.TryGetProperty("tts", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.Object && t.TryGetProperty("error", out var te)) return te.ToString();
        return "";
    }

    /// <summary>/chatbox/input [text, true, false] を送る</summary>
    public void SendChatbox(string text)
    {
        var msg = Osc.Message("/chatbox/input", text, true, false);
        using var udp = new UdpClient();
        udp.Send(msg, msg.Length, _cfg.OscHost, _cfg.OscPort);
    }
}

/// <summary>最小限の OSC メッセージ組み立て</summary>
public static class Osc
{
    public static byte[] Message(string address, params object[] args)
    {
        var ms = new MemoryStream();
        WriteString(ms, address);
        var tags = new StringBuilder(",");
        foreach (var a in args) tags.Append(a switch { string => 's', int => 'i', float => 'f', true => 'T', false => 'F', _ => throw new ArgumentException() });
        WriteString(ms, tags.ToString());
        foreach (var a in args)
        {
            switch (a)
            {
                case string s: WriteString(ms, s); break;
                case int i: WriteBE(ms, BitConverter.GetBytes(i)); break;
                case float f: WriteBE(ms, BitConverter.GetBytes(f)); break;
            }
        }
        return ms.ToArray();
    }

    static void WriteString(Stream s, string str)
    {
        var b = Encoding.UTF8.GetBytes(str);
        s.Write(b);
        int pad = 4 - b.Length % 4;
        for (int i = 0; i < pad; i++) s.WriteByte(0);
    }

    static void WriteBE(Stream s, byte[] b)
    {
        if (BitConverter.IsLittleEndian) Array.Reverse(b);
        s.Write(b);
    }
}
