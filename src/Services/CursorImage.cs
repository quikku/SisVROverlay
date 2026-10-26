using System.Runtime.InteropServices;

namespace GodanOverlay.Services;

/// <summary>
/// PC のマウスカーソルの位置と絵。画面の取り込みにはカーソルが写らないので、パネル側で重ねて描くのに使う。
/// </summary>
public static class CursorImage
{
    /// <summary>カーソルの絵（BGRA、アルファは乗算していない）</summary>
    public sealed record Shape(IntPtr Handle, int Width, int Height, int HotX, int HotY, byte[] Bgra);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public int fIcon, xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
    [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int w, int h, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bmp);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    const int CURSOR_SHOWING = 1, CURSOR_SUPPRESSED = 2, DI_NORMAL = 3;

    static readonly Dictionary<IntPtr, Shape?> Cache = new();

    /// <summary>
    /// 今のカーソル。アプリが隠している（ゲーム中など）ときは false。
    /// タッチの直後に Windows が一時的に隠している間は、位置が分かるように出す。
    /// </summary>
    public static bool TryGet(out int x, out int y, out Shape? shape)
    {
        x = y = 0;
        shape = null;
        if (DebugPosition is { } d)
        {
            (x, y) = d;
            shape = _debugArrow ??= DebugStandard(32512);
            return shape != null;
        }
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & (CURSOR_SHOWING | CURSOR_SUPPRESSED)) == 0 || ci.hCursor == IntPtr.Zero) return false;
        x = ci.pt.x;
        y = ci.pt.y;
        if (!Cache.TryGetValue(ci.hCursor, out shape))
        {
            try { shape = Render(ci.hCursor); }
            catch (Exception e) { Log.Warn("カーソルの絵を作れません: " + e.Message); }
            Cache[ci.hCursor] = shape;
        }
        return shape != null;
    }

    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr name);

    /// <summary>確認用: 本物のマウスの代わりに、この位置に矢印があることにする</summary>
    public static (int x, int y)? DebugPosition;
    static Shape? _debugArrow;

    /// <summary>確認用: 標準のカーソル（IDC_ARROW=32512 など）の絵</summary>
    public static Shape? DebugStandard(int id) => Render(LoadCursor(IntPtr.Zero, id));

    static Shape? Render(IntPtr hCursor)
    {
        if (!GetIconInfo(hCursor, out var ii)) return null;
        int w, h;
        try
        {
            if (ii.hbmColor != IntPtr.Zero && GetObject(ii.hbmColor, Marshal.SizeOf<BITMAP>(), out var bc) != 0) { w = bc.bmWidth; h = bc.bmHeight; }
            else if (GetObject(ii.hbmMask, Marshal.SizeOf<BITMAP>(), out var bm) != 0) { w = bm.bmWidth; h = bm.bmHeight / 2; } // 白黒のカーソルは AND と XOR が縦に並ぶ
            else return null;
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
        }
        if (w <= 0 || h <= 0 || w > 256 || h > 256) return null;

        // 黒と白の背景に描いた差から、色とアルファを求める（白黒反転のカーソルにも対応するため）
        var black = DrawOn(hCursor, w, h, 0x00);
        var white = DrawOn(hCursor, w, h, 0xFF);
        var outPx = new byte[w * h * 4];
        var inverted = new bool[w * h];
        for (int i = 0; i < w * h; i++)
        {
            int o = i * 4;
            int diff = 0;
            bool inv = false;
            for (int c = 0; c < 3; c++)
            {
                int d = white[o + c] - black[o + c];
                if (d < 0) inv = true;
                diff = Math.Max(diff, d);
            }
            if (inv)
            {
                // 背景を反転する部分（Iビームなど）は白にして、あとで黒い縁を付ける
                outPx[o] = outPx[o + 1] = outPx[o + 2] = outPx[o + 3] = 255;
                inverted[i] = true;
                continue;
            }
            int a = 255 - diff;
            if (a <= 0) continue;
            for (int c = 0; c < 3; c++) outPx[o + c] = (byte)Math.Min(255, black[o + c] * 255 / a);
            outPx[o + 3] = (byte)a;
        }
        for (int yy = 0; yy < h; yy++)
        {
            for (int xx = 0; xx < w; xx++)
            {
                int i = yy * w + xx;
                if (outPx[i * 4 + 3] != 0) continue;
                bool near = (xx > 0 && inverted[i - 1]) || (xx < w - 1 && inverted[i + 1]) || (yy > 0 && inverted[i - w]) || (yy < h - 1 && inverted[i + w]);
                if (near) outPx[i * 4 + 3] = 255; // 色は 0（黒）のまま
            }
        }
        return new Shape(hCursor, w, h, ii.xHotspot, ii.yHotspot, outPx);
    }

    static byte[] DrawOn(IntPtr hCursor, int w, int h, byte background)
    {
        var bmi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var dc = CreateCompatibleDC(IntPtr.Zero);
        var dib = CreateDIBSection(dc, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            var old = SelectObject(dc, dib);
            var buf = new byte[w * h * 4];
            if (background != 0) Array.Fill(buf, background);
            Marshal.Copy(buf, 0, bits, buf.Length);
            DrawIconEx(dc, 0, 0, hCursor, w, h, 0, IntPtr.Zero, DI_NORMAL);
            Marshal.Copy(bits, buf, 0, buf.Length);
            SelectObject(dc, old);
            return buf;
        }
        finally
        {
            DeleteObject(dib);
            DeleteDC(dc);
        }
    }
}
