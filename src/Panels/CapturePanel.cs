using System.Numerics;
using GodanOverlay.Services;
using SkiaSharp;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace GodanOverlay.Panels;

/// <summary>
/// PC の画面（モニター全体やウィンドウ）を映すパネルの共通部分。
/// 取り込んだ画像を GPU 上で縮小してオーバーレイに渡し、下端にツールバーを描く。
/// レーザーの位置がマウス、スティックがホイール。
/// トリガーは Windows のタッチとして送るので、タップ・スクロール・長押しの右クリックはタッチ画面と同じになる。
/// </summary>
public abstract class CapturePanel : Panel
{
    const string Shader = @"
Texture2D tex : register(t0);
SamplerState smp : register(s0);
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
VSOut VS(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.uv = uv;
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
float4 PS(VSOut i) : SV_Target
{
    return float4(tex.Sample(smp, i.uv).rgb, 1);
}
cbuffer Quad : register(b0) { float4 rc; };
VSOut VSQ(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2(id & 1, id >> 1);
    o.uv = uv;
    o.pos = float4(lerp(rc.x, rc.z, uv.x), lerp(rc.y, rc.w, uv.y), 0, 1);
    return o;
}
float4 PSQ(VSOut i) : SV_Target
{
    return tex.Sample(smp, i.uv);
}";
    // ↑ 取り込んだ画像はアルファが不定なので不透明にしている（HLSL内に日本語を書くとコンパイラが誤読する）
    //   VSQ/PSQ はマウスカーソルを rc（NDC の左上と右下）の位置にアルファ付きで重ねる

    static ID3D11VertexShader? _vs;
    static ID3D11PixelShader? _ps;
    static ID3D11SamplerState? _sampler;
    static ID3D11VertexShader? _vsQuad;
    static ID3D11PixelShader? _psQuad;
    static ID3D11Buffer? _quadCb;
    static ID3D11BlendState? _blend;

    // 重ねて描くマウスカーソル
    ID3D11Texture2D? _cursorTex;
    ID3D11ShaderResourceView? _cursorView;
    Services.CursorImage.Shape? _cursorShape;
    (bool visible, int x, int y, IntPtr handle) _cursorDrawn;

    protected readonly DesktopConfig Cfg;

    /// <summary>取り込んだ画像を置くテクスチャ（取り込み元と同じ大きさ）</summary>
    protected ID3D11Texture2D? Source;
    ID3D11ShaderResourceView? _srcView;
    ID3D11RenderTargetView? _rtv;
    protected int SrcW, SrcH;
    int _outW, _outH, _toolbarH;

    SKBitmap? _toolbar;
    readonly List<UiButton> _buttons = new();
    bool _toolbarDirty = true;
    bool _frameDirty;
    double _nextCapture;

    readonly Vector2?[] _hover = new Vector2?[2];
    readonly UiButton?[] _pressedButton = new UiButton?[2];
    readonly bool[] _mouseDown = new bool[2];
    readonly bool[] _rightDown = new bool[2];
    (int x, int y) _lastCursor = (-1, -1);
    readonly Vector2[] _wheelAcc = new Vector2[2];
    Hand? _mouseHand;

    readonly bool[] _touchDown = new bool[2];
    // 手前に出せないウィンドウへ、クリックを直接送っている間の送り先と、右クリックか
    readonly IntPtr[] _direct = new IntPtr[2];
    readonly bool[] _directRight = new bool[2];

    protected CapturePanel(string key, string name, float widthMeters, DesktopConfig cfg) : base(key, name, 16, 16, widthMeters, 1)
    {
        Cfg = cfg;
        if (_vs == null)
        {
            var vsCode = Compiler.Compile(Shader, "VS", "capture.hlsl", "vs_5_0");
            var psCode = Compiler.Compile(Shader, "PS", "capture.hlsl", "ps_5_0");
            _vs = Gpu.Device.CreateVertexShader(vsCode.Span);
            _ps = Gpu.Device.CreatePixelShader(psCode.Span);
            _sampler = Gpu.Device.CreateSamplerState(SamplerDescription.LinearClamp);
            _vsQuad = Gpu.Device.CreateVertexShader(Compiler.Compile(Shader, "VSQ", "capture.hlsl", "vs_5_0").Span);
            _psQuad = Gpu.Device.CreatePixelShader(Compiler.Compile(Shader, "PSQ", "capture.hlsl", "ps_5_0").Span);
            _quadCb = Gpu.Device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Default));
            _blend = Gpu.Device.CreateBlendState(BlendDescription.NonPremultiplied);
        }
    }

    protected override ID3D11Texture2D CreateTexture(int w, int h) => Gpu.CreateOverlayTexture(w, h, renderTarget: true);

    // ── 派生クラスが用意するもの ──

    /// <summary>取り込みを始める。成功したら SetupOutput を呼んでおくこと</summary>
    protected abstract bool OpenSource();
    protected abstract void CloseSource();
    /// <summary>新しい画像があれば Source にコピーして MarkFrame を呼ぶ</summary>
    protected abstract void PollSource();
    /// <summary>映している範囲の、PC の画面上の位置（マウス操作用）</summary>
    protected abstract Vortice.RawRect ScreenRect { get; }
    protected abstract void PlaceDefault();
    /// <summary>ツールバーの左端に足すボタン</summary>
    protected virtual void AddToolbarButtons(Func<string, string, UiButton> add) { }
    protected virtual bool OnToolbarButton(string id) => false;
    /// <summary>
    /// クリックの直前（ウィンドウを前面に出すなど）。画面の (x, y) をクリックして届くなら true。
    /// false なら DirectTarget へマウスのメッセージを直接送る
    /// </summary>
    protected virtual bool BeforeClick(int x, int y) => true;
    /// <summary>手前に出せないときにクリックを直接送る先のウィンドウ（なければ 0）</summary>
    protected virtual IntPtr DirectTarget => IntPtr.Zero;

    protected bool IsOpen => _rtv != null;

    /// <summary>取り込み元の大きさが決まったら呼ぶ。テクスチャとツールバーを作り直す</summary>
    protected void SetupOutput(int srcW, int srcH)
    {
        srcW = Math.Max(1, srcW);
        srcH = Math.Max(1, srcH);
        if (srcW == SrcW && srcH == SrcH && _rtv != null) return;
        ReleaseOutput();
        SrcW = srcW;
        SrcH = srcH;
        float scale = Math.Min(1f, Cfg.MaxTextureWidth / (float)srcW);
        _outW = Math.Max(64, (int)(srcW * scale));
        _outH = Math.Max(64, (int)(srcH * scale));
        _toolbarH = Math.Clamp(_outW / 22, 48, 96);

        Source = Gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)srcW, Height = (uint)srcH, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        });
        _srcView = Gpu.Device.CreateShaderResourceView(Source);

        // パネルの物理的な幅は変えずに、縦横比だけ取り込み元に合わせる
        Resize(_outW, _outH + _toolbarH, createBitmap: false);
        _rtv = Gpu.Device.CreateRenderTargetView(Texture!);
        Gpu.Context.ClearRenderTargetView(_rtv, new Color4(0.05f, 0.07f, 0.09f, 1));

        _toolbar?.Dispose();
        _toolbar = new SKBitmap(new SKImageInfo(_outW, _toolbarH, SKColorType.Bgra8888, SKAlphaType.Premul));
        BuildToolbar();
        _frameDirty = true;
    }

    void ReleaseOutput()
    {
        _srcView?.Dispose(); _srcView = null;
        Source?.Dispose(); Source = null;
        _rtv?.Dispose(); _rtv = null;
        SrcW = SrcH = 0;
    }

    /// <summary>Source に新しい画像を入れた</summary>
    protected void MarkFrame() => _frameDirty = true;

    protected void BuildToolbar()
    {
        if (_toolbarH == 0) return;
        _buttons.Clear();
        float pad = _toolbarH * 0.12f, h = _toolbarH - pad * 2, w = h * 3.4f;
        float y = _outH + pad;
        float x = pad;
        UiButton Add(string id, string label)
        {
            var b = new UiButton { Id = id, Label = label, Rect = new SKRect(x, y, x + w, y + h) };
            _buttons.Add(b);
            x += w + pad;
            return b;
        }
        AddToolbarButtons(Add);
        x = _outW - (pad + w) * 2;
        Add("lock", "ロック").Active = Locked;
        Add("close", "閉じる");
        _toolbarDirty = true;
    }

    protected override void OnShow()
    {
        if (!IsOpen && !OpenSource()) { Hide(); return; }
        PlaceDefault();
    }

    protected override void OnHide()
    {
        ReleaseMouse();
        CloseSource(); // 見えていない間は取り込みを止める
        ReleaseOutput();
    }

    // ── 毎フレーム ──

    public override void Update(double now, double dt)
    {
        if (!Visible || !IsOpen) return;
        if (now < _nextCapture) return;
        _nextCapture = now + 1.0 / Math.Max(1, Cfg.MaxFps);
        PollSource();
        PollCursor();
    }

    /// <summary>カーソルが動いたり形が変わったりしたら描き直す</summary>
    void PollCursor()
    {
        (bool, int, int, IntPtr) now = (false, 0, 0, IntPtr.Zero);
        if (Cfg.ShowCursor && Services.CursorImage.TryGet(out int x, out int y, out var shape))
        {
            var r = ScreenRect;
            // 映している範囲から大きく外れているときは描かない
            if (x >= r.Left - 64 && x < r.Right + 64 && y >= r.Top - 64 && y < r.Bottom + 64)
            {
                now = (true, x, y, shape!.Handle);
                if (_cursorShape != shape) SetCursorShape(shape);
            }
        }
        if (now != _cursorDrawn)
        {
            _cursorDrawn = now;
            _frameDirty = true;
        }
    }

    void SetCursorShape(Services.CursorImage.Shape shape)
    {
        _cursorView?.Dispose();
        _cursorTex?.Dispose();
        _cursorShape = shape;
        _cursorTex = Gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)shape.Width, Height = (uint)shape.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        });
        unsafe
        {
            fixed (byte* p = shape.Bgra)
                Gpu.Context.UpdateSubresource(_cursorTex, 0, null, (IntPtr)p, (uint)shape.Width * 4, 0);
        }
        _cursorView = Gpu.Device.CreateShaderResourceView(_cursorTex);
    }

    void DrawCursor(ID3D11DeviceContext ctx)
    {
        var (visible, x, y, _) = _cursorDrawn;
        if (!visible || _cursorShape is not { } s || _cursorView == null) return;
        var r = ScreenRect;
        float toOut = _outW / (float)Math.Max(1, r.Right - r.Left);
        float scale = Cfg.CursorScale;
        float px0 = (x - r.Left - s.HotX * scale) * toOut, py0 = (y - r.Top - s.HotY * scale) * toOut;
        float px1 = px0 + s.Width * scale * toOut, py1 = py0 + s.Height * scale * toOut;
        var rc = new Vector4(px0 / _outW * 2 - 1, 1 - py0 / _outH * 2, px1 / _outW * 2 - 1, 1 - py1 / _outH * 2);
        ctx.UpdateSubresource(in rc, _quadCb!);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.VSSetShader(_vsQuad);
        ctx.VSSetConstantBuffer(0, _quadCb);
        ctx.PSSetShader(_psQuad);
        ctx.PSSetShaderResource(0, _cursorView);
        ctx.OMSetBlendState(_blend);
        ctx.Draw(4, 0);
        ctx.OMSetBlendState(null);
    }

    public override bool Flush()
    {
        if (!Visible || Texture == null || _rtv == null) return base.Flush();
        bool changed = false;
        var ctx = Gpu.Context;
        if (_frameDirty)
        {
            _frameDirty = false;
            ctx.OMSetRenderTargets(_rtv);
            ctx.RSSetViewport(new Viewport(0, 0, _outW, _outH));
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.IASetInputLayout(null);
            ctx.VSSetShader(_vs);
            ctx.PSSetShader(_ps);
            ctx.PSSetShaderResource(0, _srcView);
            ctx.PSSetSampler(0, _sampler);
            ctx.Draw(3, 0);
            DrawCursor(ctx);
            ctx.PSSetShaderResource(0, null!);
            ctx.OMSetRenderTargets((ID3D11RenderTargetView)null!);
            changed = true;
        }
        if (_toolbarDirty && _toolbar != null)
        {
            _toolbarDirty = false;
            using (var c = new SKCanvas(_toolbar))
            {
                c.Clear(Ui.Bg);
                using var c2 = new SKAutoCanvasRestore(c);
                c.Translate(0, -_outH);
                foreach (var b in _buttons)
                    b.Draw(c, IsHover(b), _pressedButton[0] == b || _pressedButton[1] == b, _toolbarH * 0.36f, _toolbarH * 0.2f);
            }
            UploadBitmap(_toolbar, _outH);
            changed = true;
        }
        if (changed) SubmitTexture();
        base.Flush();
        return changed;
    }

    protected override void Draw(SKCanvas c) { } // 使わない（Flush で直接描く）

    // ── 操作 ──

    bool InScreen(Vector2 px) => px.Y < _outH;

    (int x, int y) ToScreen(Vector2 px)
    {
        var r = ScreenRect;
        int x = r.Left + (int)(Math.Clamp(px.X / _outW, 0, 0.9999f) * (r.Right - r.Left));
        int y = r.Top + (int)(Math.Clamp(px.Y / _outH, 0, 0.9999f) * (r.Bottom - r.Top));
        return (x, y);
    }

    void MoveCursor(Vector2 px)
    {
        var p = ToScreen(px);
        if (p == _lastCursor) return;
        _lastCursor = p;
        Win32Input.MoveTo(p.x, p.y);
    }

    UiButton? Find(Vector2 p) => _buttons.FirstOrDefault(b => b.Contains(p));
    bool IsHover(UiButton b) => (_hover[0] is { } a && b.Contains(a)) || (_hover[1] is { } d && b.Contains(d));

    public override void OnHover(Hand hand, Vector2 px)
    {
        var before = _hover[(int)hand] is { } p ? Find(p) : null;
        _hover[(int)hand] = px;
        if (Find(px) != before) _toolbarDirty = true;
        // レーザーが2本当たっているとカーソルが行ったり来たりするので、
        // マウスは最後にクリックした方の手（まだなら先に当てた方）にだけ付いていく
        _mouseHand ??= hand;
        if (hand == _mouseHand && InScreen(px) && Cfg.MoveCursorOnHover) MoveCursor(px);
    }

    public override void OnLeave(Hand hand)
    {
        _hover[(int)hand] = null;
        _toolbarDirty = true;
        // マウスを持っていた手が離れたら、もう片方が当たっていればそちらに渡す
        if (_mouseHand == hand) _mouseHand = _hover[1 - (int)hand] != null ? (Hand)(1 - (int)hand) : null;
    }

    public override void OnPress(Hand hand, Vector2 px)
    {
        int i = (int)hand;
        if (!InScreen(px))
        {
            _pressedButton[i] = Find(px);
            if (_pressedButton[i] != null) { App.Input.Haptic(hand); _toolbarDirty = true; }
            return;
        }
        _mouseHand = hand;
        var sp = ToScreen(px);
        bool reachable = Win32Input.Record != null || BeforeClick(sp.x, sp.y);
        _lastCursor = (-1, -1);
        MoveCursor(px);
        // 右クリックのボタン（A / X）を押しながらなら、マウスの右ボタン
        bool right = App.Config.Input.RightClickWithButton && App.Input.Hands.Any(h => h.RightClick);
        if (!reachable && DirectTarget != IntPtr.Zero)
        {
            // 他のウィンドウが手前にあって画面の位置では届かないので、そのウィンドウへ直接送る
            _direct[i] = WinApi.DeepestChildAt(DirectTarget, sp.x, sp.y);
            _directRight[i] = right;
            WinApi.PostMouse(_direct[i], right ? WinApi.WM_RBUTTONDOWN : WinApi.WM_LBUTTONDOWN, sp.x, sp.y, right ? WinApi.MK_RBUTTON : WinApi.MK_LBUTTON);
            return;
        }
        if (Cfg.TouchMode && !right && TouchInjector.Available)
        {
            var p = ToScreen(px);
            if (TouchInjector.Down(i, p.x, p.y))
            {
                _touchDown[i] = true;
                return;
            }
        }
        if (right)
        {
            Win32Input.RightDown();
            _rightDown[i] = true;
        }
        else
        {
            Win32Input.LeftDown();
            _mouseDown[i] = true;
        }
    }

    public override void OnDrag(Hand hand, Vector2 px)
    {
        int i = (int)hand;
        if (_direct[i] != IntPtr.Zero)
        {
            var dp = ToScreen(px);
            WinApi.PostMouse(_direct[i], WinApi.WM_MOUSEMOVE, dp.x, dp.y, _directRight[i] ? WinApi.MK_RBUTTON : WinApi.MK_LBUTTON);
            return;
        }
        if (_touchDown[i])
        {
            var p = ToScreen(px); // 画面の外へはみ出したら端で止める
            TouchInjector.Move(i, p.x, p.y);
            return;
        }
        if (_mouseDown[i] || _rightDown[i])
        {
            px = new Vector2(Math.Clamp(px.X, 0, _outW - 1), Math.Clamp(px.Y, 0, _outH - 1));
            MoveCursor(px);
        }
    }

    public override void OnRelease(Hand hand, Vector2 px)
    {
        int i = (int)hand;
        if (_direct[i] != IntPtr.Zero)
        {
            var up = ToScreen(px);
            WinApi.PostMouse(_direct[i], _directRight[i] ? WinApi.WM_RBUTTONUP : WinApi.WM_LBUTTONUP, up.x, up.y, 0);
            _direct[i] = IntPtr.Zero;
        }
        if (_touchDown[i]) { TouchInjector.Up(i); _touchDown[i] = false; }
        if (_mouseDown[i]) { Win32Input.LeftUp(); _mouseDown[i] = false; }
        if (_rightDown[i])
        {
            Win32Input.RightUp();
            _rightDown[i] = false;
        }
        var b = _pressedButton[i];
        _pressedButton[i] = null;
        _toolbarDirty = true;
        if (b == null || !b.Contains(px)) return;
        switch (b.Id)
        {
            case "lock":
                Locked = !Locked;
                BuildToolbar();
                break;
            case "close":
                Close();
                break;
            default:
                OnToolbarButton(b.Id);
                break;
        }
    }

    public override void OnScroll(Hand hand, Vector2 stick, double dt)
    {
        const float dead = 0.3f, notchesPerSec = 12f;
        ref var acc = ref _wheelAcc[(int)hand];
        float y = MathF.Abs(stick.Y) > dead ? stick.Y : 0;
        float x = MathF.Abs(stick.X) > dead ? stick.X : 0;
        if (y == 0) acc.Y = 0;
        if (x == 0) acc.X = 0;
        acc += new Vector2(x, y) * notchesPerSec * (float)dt;
        while (acc.Y >= 1) { Win32Input.Wheel(120); acc.Y -= 1; }
        while (acc.Y <= -1) { Win32Input.Wheel(-120); acc.Y += 1; }
        while (acc.X >= 1) { Win32Input.HWheel(120); acc.X -= 1; }
        while (acc.X <= -1) { Win32Input.HWheel(-120); acc.X += 1; }
    }

    void ReleaseMouse()
    {
        for (int i = 0; i < 2; i++)
        {
            if (_touchDown[i]) { TouchInjector.Up(i); _touchDown[i] = false; }
            if (_direct[i] != IntPtr.Zero)
            {
                WinApi.PostMouse(_direct[i], _directRight[i] ? WinApi.WM_RBUTTONUP : WinApi.WM_LBUTTONUP, 0, 0, 0);
                _direct[i] = IntPtr.Zero;
            }
            if (_mouseDown[i]) { Win32Input.LeftUp(); _mouseDown[i] = false; }
            if (_rightDown[i]) { Win32Input.RightUp(); _rightDown[i] = false; }
        }
    }

    public override void Dispose()
    {
        ReleaseMouse();
        CloseSource();
        ReleaseOutput();
        _toolbar?.Dispose();
        _cursorView?.Dispose();
        _cursorTex?.Dispose();
        base.Dispose();
    }
}
