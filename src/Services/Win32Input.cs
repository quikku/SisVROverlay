using System.Runtime.InteropServices;

namespace GodanOverlay.Services;

/// <summary>PC へのマウス・キーボード入力（SendInput）</summary>
public static class Win32Input
{
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public int mouseData;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_WHEEL = 0x0800,
        MOUSEEVENTF_HWHEEL = 0x1000, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;

    [DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint code, uint mapType);

    /// <summary>矢印・Delete・Windows キーなどは拡張キーとして送らないと、テンキー側のキーと見なすアプリがある</summary>
    static bool IsExtended(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0xA3 or 0xA5;

    static INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKey(vk, 0),
                dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0),
            },
        },
    };

    /// <summary>修飾キー（Shift・Ctrl・Alt・Win）を押したまま vk を1回押す</summary>
    public static void KeyPress(ushort vk, params ushort[] mods)
    {
        if (Record != null) { Record.Add("Key(" + string.Join("+", mods.Append(vk).Select(k => $"{k:X2}")) + ")"); return; }
        var list = new List<INPUT>();
        foreach (var m in mods) list.Add(KeyInput(m, false));
        list.Add(KeyInput(vk, false));
        list.Add(KeyInput(vk, true));
        foreach (var m in mods.Reverse()) list.Add(KeyInput(m, true));
        Send(list.ToArray());
    }

    static void Send(params INPUT[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    static INPUT Mouse(uint flags, int dx = 0, int dy = 0, int data = 0) =>
        new() { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } } };

    /// <summary>仮想デスクトップ上の物理ピクセル座標へカーソルを動かす</summary>
    public static void MoveTo(int x, int y)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77); // SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN
        int vw = GetSystemMetrics(78), vh = GetSystemMetrics(79); // SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN
        if (vw <= 1 || vh <= 1) return;
        int ax = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        int ay = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        if (Record != null) return;
        Send(Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, ax, ay));
    }

    /// <summary>確認用: null でなければマウスのボタンとホイールを送らずにここへ記録する</summary>
    public static List<string>? Record;

    static void SendMouse(string name, uint flags, int data = 0)
    {
        if (Record != null) { Record.Add(data != 0 ? $"{name}({data})" : name); return; }
        Send(Mouse(flags, data: data));
    }

    public static void LeftDown() => SendMouse("LeftDown", MOUSEEVENTF_LEFTDOWN);
    public static void LeftUp() => SendMouse("LeftUp", MOUSEEVENTF_LEFTUP);
    public static void RightDown() => SendMouse("RightDown", MOUSEEVENTF_RIGHTDOWN);
    public static void RightUp() => SendMouse("RightUp", MOUSEEVENTF_RIGHTUP);
    public static void Wheel(int delta) => SendMouse("Wheel", MOUSEEVENTF_WHEEL, delta);
    public static void HWheel(int delta) => SendMouse("HWheel", MOUSEEVENTF_HWHEEL, delta);

    /// <summary>文字列をそのまま入力する（IMEを通さない）</summary>
    public static void TypeText(string text)
    {
        if (Record != null) { Record.Add($"Text({text})"); return; }
        var list = new List<INPUT>();
        foreach (char ch in text)
        {
            list.Add(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } });
            list.Add(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
        }
        if (list.Count > 0) Send(list.ToArray());
    }

    public const ushort VK_BACK = 0x08, VK_RETURN = 0x0D;

    public static void Key(ushort vk)
    {
        if (Record != null) { Record.Add($"Key({vk:X2})"); return; }
        Send(
            new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } },
            new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } });
    }
}
