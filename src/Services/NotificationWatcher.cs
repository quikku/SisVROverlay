using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace GodanOverlay.Services;

/// <summary>Windows の通知（トースト）を見張って、新しいものを VR 内に出す</summary>
public sealed class NotificationWatcher
{
    public sealed record Toast(string App, string Title, string Body);

    /// <summary>新しい通知が来た（メインスレッドで呼ばれる）</summary>
    public event Action<Toast>? Received;

    readonly HashSet<uint> _seen = new();
    readonly HashSet<string> _exclude;

    public NotificationWatcher(IEnumerable<string> excludeApps)
    {
        _exclude = new HashSet<string>(excludeApps, StringComparer.OrdinalIgnoreCase);
    }

    public void Start()
    {
        _ = Task.Run(async () =>
        {
            UserNotificationListener listener;
            try
            {
                listener = UserNotificationListener.Current;
                var st = await listener.RequestAccessAsync();
                if (st != UserNotificationListenerAccessStatus.Allowed)
                {
                    Log.Warn("通知を読む権限がありません（Windows の設定 → プライバシー → 通知 で許可）: " + st);
                    return;
                }
                // 起動前からある通知は出さない
                foreach (var n in await listener.GetNotificationsAsync(NotificationKinds.Toast)) _seen.Add(n.Id);
            }
            catch (Exception e)
            {
                Log.Warn("通知を読めません: " + e.Message);
                return;
            }

            while (true)
            {
                await Task.Delay(1500);
                try
                {
                    foreach (var n in await listener.GetNotificationsAsync(NotificationKinds.Toast))
                    {
                        if (!_seen.Add(n.Id)) continue;
                        var toast = Parse(n);
                        if (toast == null || _exclude.Contains(toast.App)) continue;
                        App.Post(() => Received?.Invoke(toast));
                    }
                }
                catch (Exception e) { Log.Warn("通知の取得に失敗: " + e.Message); }
            }
        });
    }

    static Toast? Parse(UserNotification n)
    {
        string app = "";
        try { app = n.AppInfo.DisplayInfo.DisplayName; } catch { }
        var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (binding == null) return null;
        var texts = binding.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (texts.Count == 0) return null;
        return new Toast(app, texts[0], string.Join(" ", texts.Skip(1)));
    }
}
