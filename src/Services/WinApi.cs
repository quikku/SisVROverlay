using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GodanOverlay.Services;

/// <summary>ウィンドウの一覧・位置・前面化</summary>
public static class WinApi
{
    public sealed record WindowInfo(IntPtr Hwnd, string Title, string Process);

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] static extern IntPtr ChildWindowFromPointEx(IntPtr parent, POINT p, uint flags);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    public const uint WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205;
    public const int MK_LBUTTON = 1, MK_RBUTTON = 2;

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] static extern long GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    const uint GW_OWNER = 4;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

    /// <summary>タスクバーに出るような普通のウィンドウの一覧</summary>
    public static List<WindowInfo> ListWindows()
    {
        var result = new List<WindowInfo>();
        int self = Environment.ProcessId;
        var names = new Dictionary<uint, string>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
            int len = GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == self) return true;
            if (!names.TryGetValue(pid, out var name))
            {
                try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = "?"; }
                names[pid] = name;
            }
            result.Add(new WindowInfo(hwnd, sb.ToString(), name));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static string GetTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>見た目どおりの枠（影を含まない）。取り込まれる範囲とほぼ同じ</summary>
    public static RECT GetFrameBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0) return r;
        GetWindowRect(hwnd, out r);
        return r;
    }

    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint to, bool on);

    /// <summary>
    /// ウィンドウを選んだ状態にして一番手前に出す。別のアプリが前面にあると Windows は前面の切り替えを断るので、
    /// その間だけ前面のアプリと入力の担当をつないでから切り替える
    /// </summary>
    public static void Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        var fg = GetForegroundWindow();
        if (fg == hwnd) { BringWindowToTop(hwnd); return; }
        uint me = GetCurrentThreadId(), fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try
        {
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(me, fgThread, false);
        }
    }

    public static bool IsMinimized(IntPtr hwnd) => IsIconic(hwnd);

    /// <summary>画面の (x, y) で一番手前に見えているのが hwnd（とその子）か</summary>
    public static bool IsTopAt(IntPtr hwnd, int x, int y)
    {
        var w = WindowFromPoint(new POINT { X = x, Y = y });
        return w != IntPtr.Zero && GetAncestor(w, 2 /* GA_ROOT */) == hwnd;
    }

    /// <summary>
    /// (x, y) をクリックすれば hwnd に届く状態にする。届かなければ false（「常に手前」の別のウィンドウが重なっているなど）。
    /// Alt キーを押したことにして前に出す方法は、Alt でウィンドウがメニュー操作の状態になり、次のクリックが消えるので使わない
    /// </summary>
    public static bool EnsureTopAt(IntPtr hwnd, int x, int y)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        if (IsTopAt(hwnd, x, y)) return true;
        Activate(hwnd);
        if (IsTopAt(hwnd, x, y)) return true;
        // いったん「常に手前」にしてから戻すと、普通のウィンドウの中で一番上に残る
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
        SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        SetWindowPos(hwnd, new IntPtr(-2) /* HWND_NOTOPMOST */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        return IsTopAt(hwnd, x, y);
    }

    /// <summary>hwnd の中で、画面の (x, y) にある一番奥の子ウィンドウ（ボタンなど）</summary>
    public static IntPtr DeepestChildAt(IntPtr hwnd, int x, int y)
    {
        const uint CWP_SKIPINVISIBLE = 1, CWP_SKIPDISABLED = 2, CWP_SKIPTRANSPARENT = 4;
        var cur = hwnd;
        for (int i = 0; i < 16; i++)
        {
            var p = new POINT { X = x, Y = y };
            ScreenToClient(cur, ref p);
            var child = ChildWindowFromPointEx(cur, p, CWP_SKIPINVISIBLE | CWP_SKIPDISABLED | CWP_SKIPTRANSPARENT);
            if (child == IntPtr.Zero || child == cur) break;
            cur = child;
        }
        return cur;
    }

    /// <summary>マウスのメッセージをウィンドウへ直接送る（手前に出せないウィンドウをクリックするとき）</summary>
    public static void PostMouse(IntPtr target, uint msg, int x, int y, int keys)
    {
        if (Win32Input.Record != null) { Win32Input.Record.Add($"Post({msg:X3})"); return; }
        var p = new POINT { X = x, Y = y };
        ScreenToClient(target, ref p);
        PostMessage(target, msg, (IntPtr)keys, (IntPtr)((p.Y << 16) | (p.X & 0xFFFF)));
    }
}
