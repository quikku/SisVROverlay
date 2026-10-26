using System.Numerics;
using System.Runtime.InteropServices;
using GodanOverlay.Panels;
using Valve.VR;

namespace GodanOverlay;

/// <summary>コントローラー1本ぶんのこのフレームの状態</summary>
public sealed class HandState
{
    public Hand Hand;
    public bool Tracked;
    /// <summary>レーザーの根元の姿勢（ワールド）</summary>
    public Matrix4x4 Pose = Matrix4x4.Identity;
    public uint DeviceIndex = OpenVR.k_unTrackedDeviceIndexInvalid;
    public bool Click, ClickDown, ClickUp;
    public bool Grab, GrabDown, GrabUp;
    /// <summary>右クリックのボタン（Quest の A / X）を押している。押しながらトリガーで右クリック</summary>
    public bool RightClick;
    public Vector2 Stick;

    public Vector3 RayOrigin => Pose.Translation;
    public Vector3 RayDir => Vector3.Normalize(Pose.Forward());
}

/// <summary>SteamVR Input のアクションを読む</summary>
public sealed class VrInput
{
    ulong _set, _stickSet, _click, _grab, _toggle, _rightClick, _scroll, _pointer, _haptic;
    readonly ulong[] _source = new ulong[2];
    readonly VRActiveActionSet_t[] _active = new VRActiveActionSet_t[2];
    readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];

    public HandState[] Hands { get; } = [new() { Hand = Hand.Left }, new() { Hand = Hand.Right }];
    public bool ToggleDown { get; private set; }
    public Matrix4x4 HmdPose { get; private set; } = Matrix4x4.Identity;

    /// <summary>確認用: HMD の姿勢を決める</summary>
    public void DebugSetHmd(Matrix4x4 pose) => HmdPose = pose;

    /// <summary>true の間はこのアプリのボタン入力（トリガー・グリップ・X）を優先し、VRChat には渡さない</summary>
    public bool Exclusive { get; set; }

    /// <summary>true の間はスティックをこのアプリが取る（デスクトップのスクロール・つかんだパネルの調整用）</summary>
    public bool StickExclusive { get; set; }

    bool _prevToggle;

    public void Init()
    {
        var manifest = Path.Combine(AppContext.BaseDirectory, "input", "actions.json");
        Check(OpenVR.Input.SetActionManifestPath(manifest), "SetActionManifestPath");
        Check(OpenVR.Input.GetActionSetHandle("/actions/main", ref _set), "set");
        Check(OpenVR.Input.GetActionHandle("/actions/main/in/click", ref _click), "click");
        Check(OpenVR.Input.GetActionHandle("/actions/main/in/grab", ref _grab), "grab");
        Check(OpenVR.Input.GetActionHandle("/actions/main/in/watchtoggle", ref _toggle), "watchtoggle");
        Check(OpenVR.Input.GetActionHandle("/actions/main/in/rightclick", ref _rightClick), "rightclick");
        Check(OpenVR.Input.GetActionSetHandle("/actions/stick", ref _stickSet), "stick set");
        Check(OpenVR.Input.GetActionHandle("/actions/stick/in/scroll", ref _scroll), "scroll");
        Check(OpenVR.Input.GetActionHandle("/actions/main/in/pointer", ref _pointer), "pointer");
        Check(OpenVR.Input.GetActionHandle("/actions/main/out/haptic", ref _haptic), "haptic");
        Check(OpenVR.Input.GetInputSourceHandle("/user/hand/left", ref _source[0]), "left");
        Check(OpenVR.Input.GetInputSourceHandle("/user/hand/right", ref _source[1]), "right");
    }

    static void Check(EVRInputError err, string what)
    {
        if (err != EVRInputError.None) Log.Warn($"SteamVR Input {what}: {err}");
    }

    /// <summary>今フレームの姿勢とボタンを読む</summary>
    public void Update()
    {
        // 優先度を上げたアクションセットに割り当てたボタンは VRChat に届かなくなる
        const int high = OpenVR.k_nActionSetOverlayGlobalPriorityMin + 0x100;
        _active[0] = new VRActiveActionSet_t
        {
            ulActionSet = _set,
            ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle,
            nPriority = Exclusive ? high : 0,
        };
        _active[1] = new VRActiveActionSet_t
        {
            ulActionSet = _stickSet,
            ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle,
            nPriority = StickExclusive ? high : 0,
        };
        OpenVR.Input.UpdateActionState(_active, (uint)Marshal.SizeOf<VRActiveActionSet_t>());

        // 実デバイスの姿勢（時計の位置合わせとHMD用）
        OpenVR.System.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, PredictSeconds(), _poses);
        if (_poses[0].bPoseIsValid) HmdPose = _poses[0].mDeviceToAbsoluteTracking.ToMatrix();

        for (int i = 0; i < 2; i++)
        {
            var h = Hands[i];
            h.DeviceIndex = OpenVR.System.GetTrackedDeviceIndexForControllerRole(i == 0 ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand);

            var pose = new InputPoseActionData_t();
            var perr = OpenVR.Input.GetPoseActionDataForNextFrame(_pointer, ETrackingUniverseOrigin.TrackingUniverseStanding,
                ref pose, (uint)Marshal.SizeOf<InputPoseActionData_t>(), _source[i]);
            if (perr == EVRInputError.None && pose.bActive && pose.pose.bPoseIsValid)
            {
                h.Pose = pose.pose.mDeviceToAbsoluteTracking.ToMatrix();
                h.Tracked = true;
            }
            else if (h.DeviceIndex < _poses.Length && _poses[h.DeviceIndex].bPoseIsValid)
            {
                h.Pose = _poses[h.DeviceIndex].mDeviceToAbsoluteTracking.ToMatrix();
                h.Tracked = true;
            }
            else h.Tracked = false;

            ReadDigital(_click, _source[i], out h.Click, out h.ClickDown, out h.ClickUp);
            ReadDigital(_grab, _source[i], out h.Grab, out h.GrabDown, out h.GrabUp);
            ReadDigital(_rightClick, _source[i], out h.RightClick, out _, out _);

            var analog = new InputAnalogActionData_t();
            OpenVR.Input.GetAnalogActionData(_scroll, ref analog, (uint)Marshal.SizeOf<InputAnalogActionData_t>(), _source[i]);
            h.Stick = analog.bActive ? new Vector2(analog.x, analog.y) : Vector2.Zero;
        }

        var t = new InputDigitalActionData_t();
        OpenVR.Input.GetDigitalActionData(_toggle, ref t, (uint)Marshal.SizeOf<InputDigitalActionData_t>(), OpenVR.k_ulInvalidInputValueHandle);
        bool toggle = t.bActive && t.bState;
        ToggleDown = toggle && !_prevToggle;
        _prevToggle = toggle;
    }

    void ReadDigital(ulong action, ulong source, out bool state, out bool down, out bool up)
    {
        var d = new InputDigitalActionData_t();
        OpenVR.Input.GetDigitalActionData(action, ref d, (uint)Marshal.SizeOf<InputDigitalActionData_t>(), source);
        state = d.bActive && d.bState;
        down = d.bActive && d.bState && d.bChanged;
        up = d.bActive && !d.bState && d.bChanged;
    }

    /// <summary>実デバイスの姿勢（時計の取り付け先）</summary>
    public bool TryGetDevicePose(uint index, out Matrix4x4 pose)
    {
        pose = Matrix4x4.Identity;
        if (index >= _poses.Length || !_poses[index].bPoseIsValid) return false;
        pose = _poses[index].mDeviceToAbsoluteTracking.ToMatrix();
        return true;
    }

    public void Haptic(Hand hand, float seconds = 0.012f, float amplitude = 0.35f)
    {
        if (Panel.Headless) return;
        OpenVR.Input.TriggerHapticVibrationAction(_haptic, 0, seconds, 160, amplitude, _source[(int)hand]);
    }

    static float PredictSeconds()
    {
        float sinceVsync = 0;
        ulong frame = 0;
        OpenVR.System.GetTimeSinceLastVsync(ref sinceVsync, ref frame);
        var err = ETrackedPropertyError.TrackedProp_Success;
        float freq = OpenVR.System.GetFloatTrackedDeviceProperty(0, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref err);
        float toPhotons = OpenVR.System.GetFloatTrackedDeviceProperty(0, ETrackedDeviceProperty.Prop_SecondsFromVsyncToPhotons_Float, ref err);
        if (freq <= 0) freq = 90;
        return 1f / freq - sinceVsync + toPhotons;
    }
}
