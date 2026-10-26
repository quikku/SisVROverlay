using System.Net.Sockets;
using System.Text;

namespace GodanOverlay.Services;

/// <summary>確定した文章の送り先</summary>
public sealed class Output
{
    readonly OutputConfig _cfg;

    public Output(OutputConfig cfg) => _cfg = cfg;

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
