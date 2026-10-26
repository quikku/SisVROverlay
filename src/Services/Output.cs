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
    /// サーバーに繋がらなければ VRChat の Chatbox へ OSC で直接送る。
    /// 戻り値は通知用の短い文。
    /// </summary>
    public async Task<(bool ok, string message)> SendToTtsAsync(string text)
    {
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
            if (root.TryGetProperty("error", out var topErr))
            {
                Log.Warn("TTSの送信に失敗: " + topErr.GetString());
                return (false, "送信できません");
            }
            bool ttsOk = root.TryGetProperty("tts", out var tts) && tts.TryGetProperty("ok", out var t) && t.GetBoolean();
            bool vrcOk = root.TryGetProperty("vrc", out var vrc) && vrc.TryGetProperty("ok", out var v) && v.GetBoolean();
            if (ttsOk) return (true, "送信");
            var why = tts.ValueKind == System.Text.Json.JsonValueKind.Object && tts.TryGetProperty("error", out var e) ? e.GetString() : "";
            Log.Warn("TTSの送信に失敗: " + why);
            return (vrcOk, vrcOk ? "Chatboxのみ送信" : "送信できません");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Log.Warn($"TTSサーバーに送れませんでした: {e.Message}");
            if (_cfg.OscFallback)
            {
                SendChatbox(text);
                return (true, "Chatboxのみ送信");
            }
            return (false, "TTSに接続できません");
        }
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
