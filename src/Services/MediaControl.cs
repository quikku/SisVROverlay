using Windows.Media.Control;

namespace GodanOverlay.Services;

/// <summary>
/// 再生中の曲の情報と操作（Windows のメディア操作。Spotify・ブラウザの YouTube などが対象）。
/// 使えない環境ではメディアキーを送る。
/// </summary>
public sealed class MediaControl
{
    GlobalSystemMediaTransportControlsSessionManager? _mgr;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public bool Playing { get; private set; }
    /// <summary>情報が変わったとき（メインスレッドで呼ばれる）</summary>
    public event Action? Changed;

    public void Start()
    {
        _ = Task.Run(async () =>
        {
            try { _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
            catch (Exception e) { Log.Warn("メディア情報を取得できません: " + e.Message); return; }
            while (true)
            {
                await RefreshAsync();
                await Task.Delay(1000);
            }
        });
    }

    async Task RefreshAsync()
    {
        string title = "", artist = "";
        bool playing = false;
        try
        {
            var s = _mgr?.GetCurrentSession();
            if (s != null)
            {
                var props = await s.TryGetMediaPropertiesAsync();
                title = props?.Title ?? "";
                artist = props?.Artist ?? "";
                playing = s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
        }
        catch { }
        if (title != Title || artist != Artist || playing != Playing)
        {
            App.Post(() =>
            {
                Title = title;
                Artist = artist;
                Playing = playing;
                Changed?.Invoke();
            });
        }
    }

    public void PlayPause() => Do(s => s.TryTogglePlayPauseAsync(), 0xB3);
    public void Next() => Do(s => s.TrySkipNextAsync(), 0xB0);
    public void Previous() => Do(s => s.TrySkipPreviousAsync(), 0xB1);

    void Do(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> op, ushort mediaKey)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var s = _mgr?.GetCurrentSession();
                if (s != null && await op(s)) { await RefreshAsync(); return; }
            }
            catch { }
            Win32Input.Key(mediaKey);
        });
    }
}
