using System.Numerics;
using SkiaSharp;
using Valve.VR;
using Vortice.Direct3D11;

namespace GodanOverlay.Panels;

public enum Hand { Left = 0, Right = 1 }

/// <summary>
/// 空間に浮かぶ1枚の板。オーバーレイの作成、テクスチャの更新、位置、レーザーの当たり判定を受け持つ。
/// 中身の描画と操作は派生クラスで行う。
/// </summary>
public abstract class Panel : IDisposable
{
    public string Key { get; }
    public ulong Handle { get; private set; }
    public int PixW { get; private set; }
    public int PixH { get; private set; }
    public float WidthMeters { get; private set; }
    public float HeightMeters => WidthMeters * PixH / PixW;

    /// <summary>ワールド（立位基準）での位置と向き</summary>
    public Matrix4x4 World { get; protected set; } = Matrix4x4.Identity;
    public bool Visible { get; private set; }
    public virtual bool Grabbable => true;

    /// <summary>ロック中は時計を消しても表示したまま</summary>
    public bool Locked { get; set; }
    /// <summary>時計を消したときに一緒に消えた（時計を出したら戻す）</summary>
    public bool HiddenByWatch { get; set; }
    /// <summary>時計と一緒に消したときの、頭（向いている方角）から見た位置。呼び出すときにここへ戻す</summary>
    public Matrix4x4? HeadRelative { get; set; }

    /// <summary>パネル一覧に出す名前</summary>
    public virtual string Title => Key;
    /// <summary>パネル一覧から閉じて捨てられるか（ウィンドウのパネルなど）</summary>
    public virtual bool Closable => false;
    /// <summary>閉じられた。次のフレームで一覧から外して破棄される</summary>
    public bool IsClosed { get; private set; }

    /// <summary>閉じる。Closable なら破棄、そうでなければ隠すだけ</summary>
    public virtual void Close()
    {
        Hide();
        if (Closable) IsClosed = true;
    }

    /// <summary>曲面の度合い（0 = 平面、0.5 = 半円）</summary>
    public float Curvature { get; private set; }

    public void SetCurvature(float c)
    {
        Curvature = Math.Clamp(c, 0f, 0.5f);
        Ov?.SetOverlayCurvature(Handle, Curvature);
    }

    /// <summary>視界に固定中（頭についてくる）</summary>
    public bool HudLocked { get; private set; }
    Matrix4x4 _hudRel;

    /// <summary>今見えている位置のまま、視界に固定する／外す</summary>
    public void SetHud(bool on, Matrix4x4 hmdPose)
    {
        HudLocked = on;
        if (on) _hudRel = World * hmdPose.Inverse();
        _transformDirty = true;
    }

    /// <summary>視界に固定中は、毎フレーム HMD の位置から当たり判定用の位置を計算する</summary>
    public void HudUpdate(Matrix4x4 hmdPose)
    {
        if (HudLocked) World = _hudRel * hmdPose;
    }

    /// <summary>VRなしで描画だけ確かめるとき（--preview）は true。OpenVR を呼ばない</summary>
    public static bool Headless { get; set; }
    protected static CVROverlay? Ov => Headless ? null : OpenVR.Overlay;

    protected ID3D11Texture2D? Texture;
    SKBitmap? _bitmap;
    bool _dirty = true;
    bool _transformDirty = true;

    protected Panel(string key, string name, int pixW, int pixH, float widthMeters, uint sortOrder)
    {
        Key = key;
        ulong h = 0;
        var err = Ov?.CreateOverlay("vrcirodori.godan." + key, name, ref h) ?? EVROverlayError.None;
        if (err != EVROverlayError.None) throw new Exception($"CreateOverlay({key}) 失敗: {err}");
        Handle = h;
        Ov?.SetOverlaySortOrder(Handle, sortOrder);
        Resize(pixW, pixH);
        SetWidth(widthMeters);
    }

    protected void Resize(int pixW, int pixH, bool createBitmap = true)
    {
        PixW = pixW;
        PixH = pixH;
        Texture?.Dispose();
        Texture = CreateTexture(pixW, pixH);
        _bitmap?.Dispose();
        _bitmap = createBitmap ? new SKBitmap(new SKImageInfo(pixW, pixH, SKColorType.Bgra8888, SKAlphaType.Premul)) : null;
        _dirty = true;
    }

    protected virtual ID3D11Texture2D CreateTexture(int w, int h) => Gpu.CreateOverlayTexture(w, h);

    public void SetWidth(float meters)
    {
        WidthMeters = Math.Clamp(meters, 0.0005f, 5f);
        Ov?.SetOverlayWidthInMeters(Handle, WidthMeters);
    }

    public void SetWorld(Matrix4x4 world)
    {
        World = world;
        _transformDirty = true;
    }

    public void Show()
    {
        if (Visible) return;
        Visible = true;
        _dirty = true;
        _transformDirty = true;
        if (AnimOn)
        {
            if (!_shownInVr) _alpha = 0; // 消えるフェードの途中なら、その濃さから戻す
            StartAnim(+1);
        }
        else
        {
            _animDir = 0;
            _alpha = 1;
            Ov?.SetOverlayAlpha(Handle, 1);
            Ov?.SetOverlayWidthInMeters(Handle, WidthMeters);
        }
        OnShow();
    }

    public void Hide()
    {
        if (!Visible) return;
        Visible = false;
        if (AnimOn && _shownInVr) StartAnim(-1);
        else { _animDir = 0; VrHide(); }
        OnHide();
    }

    // ── 出る・消えるときのフェード ──

    /// <summary>出し入れでフェードするか。レーザーやフリックの候補のように、すぐ出し入れするものは false</summary>
    protected virtual bool Animated => true;

    static readonly System.Diagnostics.Stopwatch AnimClock = System.Diagnostics.Stopwatch.StartNew();
    float _alpha = 1;
    float _animFrom;
    double _animStart;
    int _animDir; // +1 出る途中、-1 消える途中、0 なし

    bool AnimOn => Animated && (!Headless || DebugAnimate) && App.Config?.Ui is { Animations: true, AnimationSeconds: > 0.01f };

    /// <summary>確認用: VR なしでもフェードを動かす</summary>
    public static bool DebugAnimate { get; set; }
    public float DebugAlpha => _alpha;
    public bool DebugShownInVr => _shownInVr;

    void StartAnim(int dir)
    {
        _animFrom = _alpha;
        _animStart = AnimClock.Elapsed.TotalSeconds;
        _animDir = dir;
    }

    /// <summary>フェードを1コマ進める。少し小さい所から大きくなりながら出て、少し縮みながら消える</summary>
    void StepAnim()
    {
        if (_animDir == 0) return;
        float seconds = App.Config.Ui.AnimationSeconds * (_animDir > 0 ? 1f : 0.75f);
        float t = Math.Clamp((float)((AnimClock.Elapsed.TotalSeconds - _animStart) / seconds), 0f, 1f);
        float eased = 1 - MathF.Pow(1 - t, 3); // 最初に速く動いて、最後はゆっくり止まる
        _alpha = _animFrom + ((_animDir > 0 ? 1f : 0f) - _animFrom) * eased;
        Ov?.SetOverlayAlpha(Handle, _alpha);
        Ov?.SetOverlayWidthInMeters(Handle, WidthMeters * (0.96f + 0.04f * _alpha));
        if (t < 1) return;
        Ov?.SetOverlayWidthInMeters(Handle, WidthMeters);
        if (_animDir < 0)
        {
            _shownInVr = false;
            Ov?.HideOverlay(Handle);
        }
        _animDir = 0;
    }

    bool _shownInVr;

    /// <summary>Visible のまま一時的に見えなくする（次の Flush でまた出る）</summary>
    protected void VrHide()
    {
        _shownInVr = false;
        Ov?.HideOverlay(Handle);
    }

    /// <summary>つかんで動かしているときに呼ばれる</summary>
    public virtual void GrabMoveTo(Matrix4x4 world)
    {
        if (HudLocked)
        {
            _hudRel = world * App.Input.HmdPose.Inverse();
            World = world;
            _transformDirty = true;
        }
        else SetWorld(world);
    }

    public void Toggle() { if (Visible) Hide(); else Show(); }

    /// <summary>目の前に呼び寄せる（時計の「手前へ」）</summary>
    public virtual void PlaceInFront() { }

    protected virtual void OnShow() { }
    protected virtual void OnHide() { }

    /// <summary>次のフレームで描き直す</summary>
    public void Invalidate() => _dirty = true;

    /// <summary>毎フレーム呼ばれる</summary>
    public virtual void Update(double now, double dt) { }

    /// <summary>描き直しが必要なら Skia で描いて GPU に送る。送ったら true</summary>
    public virtual bool Flush()
    {
        if (!Visible)
        {
            StepAnim(); // 消えるフェードの途中
            return false;
        }
        bool sent = false;
        if (_dirty && _bitmap != null && Texture != null)
        {
            _dirty = false;
            using (var canvas = new SKCanvas(_bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                Draw(canvas);
            }
            UploadBitmap(_bitmap, 0);
            SubmitTexture();
            sent = true;
        }
        if (_transformDirty)
        {
            _transformDirty = false;
            ApplyTransform();
        }
        StepAnim();
        if (!_shownInVr)
        {
            _shownInVr = true;
            Ov?.SetOverlayAlpha(Handle, _alpha);
            Ov?.ShowOverlay(Handle);
        }
        return sent;
    }

    protected unsafe void UploadBitmap(SKBitmap bmp, int top)
    {
        var box = new Vortice.Mathematics.Box(0, top, 0, bmp.Width, top + bmp.Height, 1);
        Gpu.Context.UpdateSubresource(Texture!, 0, box, bmp.GetPixels(), (uint)bmp.RowBytes, 0);
    }

    protected void SubmitTexture()
    {
        var tex = new Texture_t
        {
            handle = Texture!.NativePointer,
            eType = ETextureType.DirectX,
            eColorSpace = EColorSpace.Auto,
        };
        var err = Ov?.SetOverlayTexture(Handle, ref tex) ?? EVROverlayError.None;
        if (err != EVROverlayError.None) Log.Warn($"SetOverlayTexture({Key}): {err}");
    }

    protected virtual void ApplyTransform()
    {
        if (HudLocked)
        {
            var rel = _hudRel.ToHmd();
            Ov?.SetOverlayTransformTrackedDeviceRelative(Handle, OpenVR.k_unTrackedDeviceIndex_Hmd, ref rel);
            return;
        }
        var m = World.ToHmd();
        Ov?.SetOverlayTransformAbsolute(Handle, ETrackingUniverseOrigin.TrackingUniverseStanding, ref m);
    }

    protected void MarkTransformDirty() => _transformDirty = true;

    protected abstract void Draw(SKCanvas c);

    // ── 当たり判定 ──

    /// <summary>レーザーとパネル平面の交点をピクセル座標で返す。板の外でも平面上なら座標を返す。</summary>
    public bool Project(Vector3 origin, Vector3 dir, out Vector2 px, out float distance, out Vector3 hitWorld) =>
        Project(origin, dir, out px, out distance, out hitWorld, out _);

    /// <summary>
    /// レーザーとパネルの交点。曲面のときは円柱との交点を求める。
    /// SteamVR の曲面は、パネルの幅を弧の長さとして、見ている側（+Z）を中心に曲がる。
    /// </summary>
    public bool Project(Vector3 origin, Vector3 dir, out Vector2 px, out float distance, out Vector3 hitWorld, out Vector3 normalWorld)
    {
        px = default; distance = 0; hitWorld = default; normalWorld = default;
        var inv = World.Inverse();
        var o = Vector3.Transform(origin, inv);
        var d = Vector3.TransformNormal(dir, inv);
        float w = WidthMeters, h = HeightMeters;
        Vector3 hit, n;
        float u;

        if (Curvature > 0.001f)
        {
            float theta = 2 * MathF.PI * Curvature; // 弧の角度
            float r = w / theta;                    // 円柱の半径。中心軸は (0, *, r)
            float ox = o.X, oz = o.Z - r;
            float a = d.X * d.X + d.Z * d.Z;
            if (a < 1e-9f) return false;
            float b = 2 * (ox * d.X + oz * d.Z);
            float c = ox * ox + oz * oz - r * r;
            float disc = b * b - 4 * a * c;
            if (disc < 0) return false;
            float sq = MathF.Sqrt(disc);
            float t = float.NaN;
            foreach (var tc in new[] { (-b - sq) / (2 * a), (-b + sq) / (2 * a) })
            {
                if (tc <= 0) continue;
                var p = o + d * tc;
                if (r - p.Z <= 0) continue; // 円柱の奥側の面は使わない
                t = tc;
                break;
            }
            if (float.IsNaN(t)) return false;
            hit = o + d * t;
            float phi = MathF.Atan2(hit.X, r - hit.Z);
            u = phi / theta + 0.5f;
            n = new Vector3(-MathF.Sin(phi), 0, MathF.Cos(phi));
        }
        else
        {
            if (MathF.Abs(d.Z) < 1e-6f) return false;
            float t = -o.Z / d.Z;
            if (t <= 0) return false;
            hit = o + d * t;
            hit.Z = 0;
            u = hit.X / w + 0.5f;
            n = Vector3.UnitZ;
        }

        float v = 0.5f - hit.Y / h;
        px = new Vector2(u * PixW, v * PixH);
        hitWorld = Vector3.Transform(hit, World);
        normalWorld = Vector3.Normalize(Vector3.TransformNormal(n, World));
        distance = Vector3.Distance(origin, hitWorld);
        return true;
    }

    public bool HitTest(Vector3 origin, Vector3 dir, out Vector2 px, out float distance, out Vector3 hitWorld)
    {
        if (!Visible) { px = default; distance = 0; hitWorld = default; return false; }
        return Project(origin, dir, out px, out distance, out hitWorld)
               && px.X >= 0 && px.Y >= 0 && px.X < PixW && px.Y < PixH
               && AcceptsPoint(px);
    }

    /// <summary>透明な部分などを当たり判定から外したいときに上書きする</summary>
    protected virtual bool AcceptsPoint(Vector2 px) => true;

    // ── レーザー操作。px はパネルのピクセル座標 ──
    public virtual void OnHover(Hand hand, Vector2 px) { }
    public virtual void OnLeave(Hand hand) { }
    public virtual void OnPress(Hand hand, Vector2 px) { }
    public virtual void OnDrag(Hand hand, Vector2 px) { }
    public virtual void OnRelease(Hand hand, Vector2 px) { }
    public virtual void OnScroll(Hand hand, Vector2 stick, double dt) { }

    /// <summary>つかんで動かし終えたとき</summary>
    public virtual void OnGrabEnd() { }

    /// <summary>今のテクスチャの中身をPNGに書き出す（確認用）</summary>
    public unsafe void SaveSnapshot(string path)
    {
        if (Texture == null) return;
        var desc = Texture.Description;
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.MiscFlags = ResourceOptionFlags.None;
        using var staging = Gpu.Device.CreateTexture2D(desc);
        Gpu.Context.CopyResource(staging, Texture);
        var map = Gpu.Context.Map(staging, 0, MapMode.Read);
        try
        {
            using var bmp = new SKBitmap(new SKImageInfo((int)desc.Width, (int)desc.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            for (int y = 0; y < desc.Height; y++)
                Buffer.MemoryCopy((byte*)map.DataPointer + y * map.RowPitch, (byte*)bmp.GetPixels() + y * bmp.RowBytes, bmp.RowBytes, desc.Width * 4);
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, data.ToArray());
        }
        finally { Gpu.Context.Unmap(staging, 0); }
    }

    public virtual void Dispose()
    {
        if (Handle != 0) Ov?.DestroyOverlay(Handle);
        Handle = 0;
        Texture?.Dispose();
        _bitmap?.Dispose();
    }
}
