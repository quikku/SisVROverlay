using System.Numerics;
using Valve.VR;

namespace GodanOverlay;

/// <summary>
/// OpenVR の 3x4 行列（列ベクトル形式）と System.Numerics の Matrix4x4（行ベクトル形式）の変換。
/// Numerics 側では「子 * 親」でワールド行列になる。
/// </summary>
public static class VrMath
{
    public static Matrix4x4 ToMatrix(this HmdMatrix34_t m) => new(
        m.m0, m.m4, m.m8, 0,
        m.m1, m.m5, m.m9, 0,
        m.m2, m.m6, m.m10, 0,
        m.m3, m.m7, m.m11, 1);

    public static HmdMatrix34_t ToHmd(this Matrix4x4 m) => new()
    {
        m0 = m.M11, m1 = m.M21, m2 = m.M31, m3 = m.M41,
        m4 = m.M12, m5 = m.M22, m6 = m.M32, m7 = m.M42,
        m8 = m.M13, m9 = m.M23, m10 = m.M33, m11 = m.M43,
    };

    public static Vector3 Forward(this Matrix4x4 m) => -new Vector3(m.M31, m.M32, m.M33);
    public static Vector3 Up(this Matrix4x4 m) => new(m.M21, m.M22, m.M23);
    public static Vector3 Right(this Matrix4x4 m) => new(m.M11, m.M12, m.M13);

    public static Matrix4x4 FromBasis(Vector3 x, Vector3 y, Vector3 z, Vector3 t) => new(
        x.X, x.Y, x.Z, 0,
        y.X, y.Y, y.Z, 0,
        z.X, z.Y, z.Z, 0,
        t.X, t.Y, t.Z, 1);

    public static Matrix4x4 Inverse(this Matrix4x4 m) =>
        Matrix4x4.Invert(m, out var inv) ? inv : Matrix4x4.Identity;

    /// <summary>HMD の位置で、向いている方角（水平）だけを取った座標系。頭の傾きは無視する</summary>
    public static Matrix4x4 YawFrame(Matrix4x4 hmd)
    {
        var fwd = hmd.Forward();
        fwd.Y = 0;
        if (fwd.LengthSquared() < 1e-4f) fwd = new Vector3(0, 0, -1);
        var z = -Vector3.Normalize(fwd);
        var y = Vector3.UnitY;
        return FromBasis(Vector3.Cross(y, z), y, z, hmd.Translation);
    }

    /// <summary>
    /// pos に置いて、水平方向は head の方を向くパネルの行列。tiltRad だけ上向きに傾ける（InFrontOf の傾きと同じ向き）
    /// </summary>
    public static Matrix4x4 FacingFrom(Vector3 pos, Vector3 head, Vector3 fallbackForward, float tiltRad)
    {
        var z = head - pos; // パネルの表面は利用者側
        z.Y = 0;
        if (z.LengthSquared() < 1e-4f) { z = -fallbackForward; z.Y = 0; }
        if (z.LengthSquared() < 1e-4f) z = Vector3.UnitZ;
        z = Vector3.Normalize(z);
        var up = Vector3.UnitY;
        return Matrix4x4.CreateRotationX(-tiltRad) * FromBasis(Vector3.Cross(up, z), up, z, pos);
    }

    /// <summary>HMDの正面（水平方向）に、利用者の方を向いたパネルを置く行列</summary>
    public static Matrix4x4 InFrontOf(Matrix4x4 hmd, float distance, float down, float tiltDeg)
    {
        var fwd = hmd.Forward();
        fwd.Y = 0;
        if (fwd.LengthSquared() < 1e-4f) fwd = new Vector3(0, 0, -1);
        fwd = Vector3.Normalize(fwd);
        var up = Vector3.UnitY;
        var z = -fwd; // パネルの表面は利用者側
        var x = Vector3.Cross(up, z);
        var pos = hmd.Translation + fwd * distance - up * down;
        var basis = FromBasis(x, up, z, pos);
        return Matrix4x4.CreateRotationX(-tiltDeg * MathF.PI / 180f) * basis;
    }
}
