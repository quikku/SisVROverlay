using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace GodanOverlay.Services;

/// <summary>既定の再生デバイスの全体音量と、アプリごとの音量</summary>
public sealed class AudioMixer
{
    public sealed class Entry
    {
        public string Name = "";
        public float Volume;
        public bool Muted;
        /// <summary>同じアプリの複数のセッション（Chrome など）をまとめて扱う</summary>
        public List<AudioSessionControl> Sessions = new();
    }

    MMDeviceEnumerator? _enumerator;
    MMDevice? _device;
    readonly Dictionary<uint, string> _names = new();

    public float MasterVolume { get; private set; }
    public bool MasterMuted { get; private set; }
    public List<Entry> Apps { get; } = new();

    /// <summary>最新の状態を読み直す（メインスレッドから呼ぶ）</summary>
    public void Refresh()
    {
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            _device?.Dispose();
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            MasterVolume = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
            MasterMuted = _device.AudioEndpointVolume.Mute;

            var byName = new Dictionary<string, Entry>();
            var sessions = _device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.IsSystemSoundsSession || s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                var name = NameOf(s);
                if (!byName.TryGetValue(name, out var e))
                {
                    e = new Entry { Name = name, Volume = s.SimpleAudioVolume.Volume, Muted = s.SimpleAudioVolume.Mute };
                    byName[name] = e;
                }
                e.Sessions.Add(s);
            }
            Apps.Clear();
            Apps.AddRange(byName.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception e)
        {
            Log.Warn("音量の取得に失敗: " + e.Message);
        }
    }

    string NameOf(AudioSessionControl s)
    {
        uint pid = s.GetProcessID;
        if (!_names.TryGetValue(pid, out var name))
        {
            try
            {
                var p = Process.GetProcessById((int)pid);
                // 「Discord」「Google Chrome」のような表示名があればそれを使う
                string? desc = null;
                try { desc = p.MainModule?.FileVersionInfo.FileDescription; } catch { }
                name = string.IsNullOrWhiteSpace(desc) ? p.ProcessName : desc!;
            }
            catch { name = string.IsNullOrEmpty(s.DisplayName) ? $"PID {pid}" : s.DisplayName; }
            _names[pid] = name;
        }
        return name;
    }

    public void SetMaster(float v)
    {
        if (_device == null) return;
        _device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(v, 0, 1);
        MasterVolume = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
    }

    public void ToggleMasterMute()
    {
        if (_device == null) return;
        _device.AudioEndpointVolume.Mute = !_device.AudioEndpointVolume.Mute;
        MasterMuted = _device.AudioEndpointVolume.Mute;
    }

    public void SetVolume(Entry e, float v)
    {
        e.Volume = Math.Clamp(v, 0, 1);
        foreach (var s in e.Sessions) { try { s.SimpleAudioVolume.Volume = e.Volume; } catch { } }
    }

    public void ToggleMute(Entry e)
    {
        e.Muted = !e.Muted;
        foreach (var s in e.Sessions) { try { s.SimpleAudioVolume.Mute = e.Muted; } catch { } }
    }
}
