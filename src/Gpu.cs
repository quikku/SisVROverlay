using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GodanOverlay;

/// <summary>全パネルで共有する D3D11 デバイス。SteamVR と同じGPUに作る。</summary>
public static class Gpu
{
    public static IDXGIFactory1 Factory { get; private set; } = null!;
    public static IDXGIAdapter1 Adapter { get; private set; } = null!;
    public static ID3D11Device Device { get; private set; } = null!;
    public static ID3D11DeviceContext Context { get; private set; } = null!;

    public static void Init(int adapterIndex)
    {
        Factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (adapterIndex < 0 || Factory.EnumAdapters1((uint)adapterIndex, out var adapter).Failure)
        {
            Factory.EnumAdapters1(0, out adapter).CheckError();
        }
        Adapter = adapter;
        Log.Info($"GPU: {Adapter.Description1.Description}");

        D3D11.D3D11CreateDevice(
            Adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        Device = device!;
        Context = context!;

        // ウィンドウの取り込み（Windows.Graphics.Capture）が別スレッドからこのデバイスを使うので保護する
        using var mt = Device.QueryInterface<ID3D11Multithread>();
        mt.SetMultithreadProtected(true);
    }

    public static ID3D11Texture2D CreateOverlayTexture(int w, int h, bool renderTarget = false)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | (renderTarget ? BindFlags.RenderTarget : 0),
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.Shared,
        };
        return Device.CreateTexture2D(desc);
    }

    public static void Shutdown()
    {
        Context?.ClearState();
        Context?.Flush();
        Context?.Dispose();
        Device?.Dispose();
        Adapter?.Dispose();
        Factory?.Dispose();
    }
}
