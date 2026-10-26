using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GodanOverlay.Services;

/// <summary>Windows.Graphics.Capture で1つのウィンドウを取り込む</summary>
public sealed class WindowCapture : IDisposable
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    static readonly Guid ItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid Texture2DIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    static IDirect3DDevice? _device;

    readonly GraphicsCaptureItem _item;
    readonly Direct3D11CaptureFramePool _pool;
    readonly GraphicsCaptureSession _session;

    public SizeInt32 Size { get; private set; }
    /// <summary>ウィンドウが閉じられた（別スレッドから呼ばれる）</summary>
    public event Action? Closed;

    public WindowCapture(IntPtr hwnd)
    {
        _device ??= CreateDevice();
        _item = CreateItem(hwnd);
        _item.Closed += (_, _) => Closed?.Invoke();
        Size = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, Size);
        _session = _pool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = false; } catch { }
        try { _session.IsBorderRequired = false; } catch { } // 黄色い枠を出さない（Windows 11）
        _session.StartCapture();
    }

    static IDirect3DDevice CreateDevice()
    {
        using var dxgi = Gpu.Device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var ptr));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    static GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = ItemIid;
        var ptr = interop.CreateForWindow(hwnd, ref iid);
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    /// <summary>新しいフレームがあれば、その内容（テクスチャと有効な大きさ）を渡す</summary>
    public bool TryGetFrame(Action<ID3D11Texture2D, int, int> use)
    {
        Direct3D11CaptureFrame? latest = null;
        while (_pool.TryGetNextFrame() is { } f)
        {
            latest?.Dispose();
            latest = f;
        }
        if (latest == null) return false;
        using (latest)
        {
            var cs = latest.ContentSize;
            var access = latest.Surface.As<IDirect3DDxgiInterfaceAccess>();
            var iid = Texture2DIid;
            using var tex = new ID3D11Texture2D(access.GetInterface(ref iid));
            use(tex, cs.Width, cs.Height);
            if (cs.Width != Size.Width || cs.Height != Size.Height)
            {
                // ウィンドウの大きさが変わったので、受け取るバッファも作り直す
                Size = cs;
                _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, cs);
            }
        }
        return true;
    }

    public void Dispose()
    {
        try { _session.Dispose(); } catch { }
        try { _pool.Dispose(); } catch { }
    }
}
