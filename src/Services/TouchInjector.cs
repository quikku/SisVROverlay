using System.Runtime.InteropServices;

namespace GodanOverlay.Services;

/// <summary>
/// PC へのタッチ入力（InjectTouchInput）。指は手ごとに1本ずつ、最大2本。
/// タップ・スクロール・長押しの右クリック・ピンチは Windows とアプリがタッチとして判定する。
/// </summary>
public static class TouchInjector
{
    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_INFO
    {
        public uint pointerType, pointerId, frameId, pointerFlags;
        public IntPtr sourceDevice, hwndTarget;
        public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
        public uint dwTime, historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags, touchMask;
        public RECT rcContact, rcContactRaw;
        public uint orientation, pressure;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool InitializeTouchInjection(uint maxCount, uint dwMode);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool InjectTouchInput(uint count, [In] POINTER_TOUCH_INFO[] contacts);

    const uint PT_TOUCH = 2, TOUCH_FEEDBACK_DEFAULT = 1;
    // POINTER_FLAG_PRIMARY を付けるとエラー87で失敗する（主の指は Windows が決める）
    const uint POINTER_FLAG_INRANGE = 0x2, POINTER_FLAG_INCONTACT = 0x4,
        POINTER_FLAG_DOWN = 0x10000, POINTER_FLAG_UPDATE = 0x20000, POINTER_FLAG_UP = 0x40000;
    const uint Touching = POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT;

    static bool? _available;
    static readonly bool[] _active = new bool[2];
    static readonly POINT[] _pos = new POINT[2];
    static bool _warned;

    /// <summary>タッチ入力が使えるか（初回に初期化する）</summary>
    public static bool Available
    {
        get
        {
            if (_available == null)
            {
                _available = InitializeTouchInjection(2, TOUCH_FEEDBACK_DEFAULT);
                if (_available == false) Log.Warn($"タッチ入力を使えません（{Marshal.GetLastWin32Error()}）。マウスで操作します");
            }
            return _available.Value;
        }
    }

    /// <summary>失敗したら false を返し、以後はタッチを使わない（呼び出し側はマウスで代わりに操作する）</summary>
    public static bool Down(int id, int x, int y)
    {
        if (_active[id]) Up(id);
        _active[id] = true;
        _pos[id] = new POINT { x = x, y = y };
        if (Inject(id, POINTER_FLAG_DOWN | Touching)) return true;
        _active[id] = false;
        _available = false;
        return false;
    }

    /// <summary>触れている間は動かなくても毎フレーム呼ぶ（途切れると長押しが取り消される）</summary>
    public static void Move(int id, int x, int y)
    {
        if (!_active[id]) return;
        _pos[id] = new POINT { x = x, y = y };
        Inject(id, POINTER_FLAG_UPDATE | Touching);
    }

    public static void Up(int id)
    {
        if (!_active[id]) return;
        Inject(id, POINTER_FLAG_UP);
        _active[id] = false;
    }

    /// <summary>1フレーム分を送る。触れている指はすべて同じフレームに入れる決まり</summary>
    static bool Inject(int changed, uint flags)
    {
        if (Win32Input.Record != null)
        {
            string kind = (flags & POINTER_FLAG_DOWN) != 0 ? "Down" : (flags & POINTER_FLAG_UP) != 0 ? "Up" : "Move";
            Win32Input.Record.Add($"Touch{kind}{changed}");
            return true;
        }
        var list = new List<POINTER_TOUCH_INFO>();
        for (int i = 0; i < 2; i++)
        {
            if (!_active[i]) continue;
            uint f = i == changed ? flags : POINTER_FLAG_UPDATE | Touching;
            list.Add(new POINTER_TOUCH_INFO
            {
                pointerInfo = new POINTER_INFO { pointerType = PT_TOUCH, pointerId = (uint)i, pointerFlags = f, ptPixelLocation = _pos[i] },
            });
        }
        if (list.Count == 0) return true;
        if (InjectTouchInput((uint)list.Count, list.ToArray())) return true;
        if (!_warned)
        {
            _warned = true;
            Log.Warn($"タッチ入力の送信に失敗（{Marshal.GetLastWin32Error()}）。マウスで操作します");
        }
        return false;
    }
}
