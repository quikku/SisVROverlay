using System.Collections.Concurrent;
using GodanOverlay.Panels;
using GodanOverlay.Services;

namespace GodanOverlay;

/// <summary>アプリ全体で共有するもの</summary>
public static class App
{
    public static Config Config { get; set; } = null!;
    public static VrInput Input { get; set; } = null!;
    public static Output Output { get; set; } = null!;
    public static IConverter Converter { get; set; } = null!;
    public static MediaControl Media { get; set; } = new();
    public static AudioMixer Mixer { get; set; } = new();

    public static WatchPanel Watch { get; set; } = null!;
    public static KeyboardPanel Keyboard { get; set; } = null!;
    public static DesktopPanel Desktop { get; set; } = null!;
    public static MenuPanel Menu { get; set; } = null!;
    public static ToastPanel? Toast { get; set; }

    /// <summary>レーザーで操作できるパネル全部（ウィンドウのパネルは増えたり減ったりする）</summary>
    public static List<Panel> Panels { get; } = new();

    static int _windowSlot;

    /// <summary>ウィンドウを映すパネルを追加して目の前に出す</summary>
    public static WindowPanel AddWindow(WinApi.WindowInfo info)
    {
        var p = new WindowPanel(info, Config.Desktop, _windowSlot++);
        Panels.Add(p);
        p.Show();
        return p;
    }

    static readonly ConcurrentQueue<Action> Queue = new();

    /// <summary>別スレッド（HTTPの結果など）からメインループへ処理を渡す</summary>
    public static void Post(Action a) => Queue.Enqueue(a);

    public static void RunPosted()
    {
        while (Queue.TryDequeue(out var a))
        {
            try { a(); }
            catch (Exception e) { Log.Error("メインループ内の処理で例外: " + e); }
        }
    }

    /// <summary>VR内に短い通知を出す（キーボードと時計に表示）</summary>
    public static void Notify(string message, bool error = false)
    {
        if (error) Log.Warn(message); else Log.Info(message);
        Keyboard?.ShowNotice(message, error);
        Watch?.ShowNotice(message, error);
    }
}
