using GodanOverlay.Input;

namespace GodanOverlay.Services;

/// <summary>
/// 文字を日本語（JIS）キーボードのキー操作にして PC へ送る。ひらがなはローマ字のキーにして送るので、PC の IME がそのまま変換する。
/// </summary>
public static class JisKeys
{
    public const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0xA0, VK_CONTROL = 0xA2, VK_MENU = 0xA4,
        VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_LWIN = 0x5B, VK_DELETE = 0x2E,
        VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28,
        VK_KANJI = 0x19, VK_CONVERT = 0x1C, VK_NONCONVERT = 0x1D,
        VK_OEM_1 = 0xBA, VK_OEM_PLUS = 0xBB, VK_OEM_COMMA = 0xBC, VK_OEM_MINUS = 0xBD, VK_OEM_PERIOD = 0xBE, VK_OEM_2 = 0xBF,
        VK_OEM_3 = 0xC0, VK_OEM_4 = 0xDB, VK_OEM_5 = 0xDC, VK_OEM_6 = 0xDD, VK_OEM_7 = 0xDE, VK_OEM_102 = 0xE2;

    /// <summary>記号と、それを打つ JIS キーボードのキー（Shift が要るか）。全角の記号は IME の日本語入力中に同じキーで出るもの</summary>
    static readonly Dictionary<char, (ushort vk, bool shift)> Symbols = new()
    {
        ['-'] = (VK_OEM_MINUS, false), ['ー'] = (VK_OEM_MINUS, false), ['='] = (VK_OEM_MINUS, true),
        ['^'] = (VK_OEM_7, false), ['~'] = (VK_OEM_7, true),
        ['\\'] = (VK_OEM_5, false), ['¥'] = (VK_OEM_5, false), ['|'] = (VK_OEM_5, true),
        ['@'] = (VK_OEM_3, false), ['`'] = (VK_OEM_3, true),
        ['['] = (VK_OEM_4, false), ['「'] = (VK_OEM_4, false), ['{'] = (VK_OEM_4, true),
        [']'] = (VK_OEM_6, false), ['」'] = (VK_OEM_6, false), ['}'] = (VK_OEM_6, true),
        [';'] = (VK_OEM_PLUS, false), ['+'] = (VK_OEM_PLUS, true),
        [':'] = (VK_OEM_1, false), ['*'] = (VK_OEM_1, true),
        [','] = (VK_OEM_COMMA, false), ['、'] = (VK_OEM_COMMA, false), ['<'] = (VK_OEM_COMMA, true),
        ['.'] = (VK_OEM_PERIOD, false), ['。'] = (VK_OEM_PERIOD, false), ['>'] = (VK_OEM_PERIOD, true),
        ['/'] = (VK_OEM_2, false), ['・'] = (VK_OEM_2, false), ['?'] = (VK_OEM_2, true), ['？'] = (VK_OEM_2, true),
        ['_'] = (VK_OEM_102, true),
        ['!'] = ((ushort)'1', true), ['！'] = ((ushort)'1', true), ['"'] = ((ushort)'2', true), ['#'] = ((ushort)'3', true),
        ['$'] = ((ushort)'4', true), ['%'] = ((ushort)'5', true), ['&'] = ((ushort)'6', true), ['\''] = ((ushort)'7', true),
        ['('] = ((ushort)'8', true), ['（'] = ((ushort)'8', true), [')'] = ((ushort)'9', true), ['）'] = ((ushort)'9', true),
        [' '] = (VK_SPACE, false),
    };

    /// <summary>ひらがな1文字 → ローマ字（ローマ字の表を逆に引く。いちばん短い綴りを使う）</summary>
    static readonly Dictionary<string, string> KanaToRomaji = BuildReverse();

    static Dictionary<string, string> BuildReverse()
    {
        var r = new Dictionary<string, string>();
        foreach (var (romaji, kana) in RomajiTable.Map)
        {
            if (kana.Length != 1) continue;
            if (!r.TryGetValue(kana, out var cur) || romaji.Length < cur.Length) r[kana] = romaji;
        }
        r["ん"] = "nn";
        return r;
    }

    /// <summary>文字列をキー操作にして送る。キーで打てない文字（…など）はそのまま入力する</summary>
    public static void Type(string text)
    {
        foreach (char ch in text)
        {
            if (ch is >= 'a' and <= 'z') Win32Input.KeyPress(char.ToUpperInvariant(ch));
            else if (ch is >= 'A' and <= 'Z') Win32Input.KeyPress(ch); // GODAN の大文字は子音のキー
            else if (ch is >= '0' and <= '9') Win32Input.KeyPress(ch);
            else if (Symbols.TryGetValue(ch, out var k))
            {
                if (k.shift) Win32Input.KeyPress(k.vk, VK_SHIFT); else Win32Input.KeyPress(k.vk);
            }
            else if (KanaToRomaji.TryGetValue(ch.ToString(), out var romaji)) Type(romaji);
            else Win32Input.TypeText(ch.ToString());
        }
    }
}
