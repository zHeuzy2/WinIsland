using SkiaSharp;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace WinIsland.Services;

/// <summary>
/// Surfaces incoming Windows toast notifications as island alerts via
/// UserNotificationListener. Degrades gracefully if access is denied
/// (common for unpackaged dev builds).
/// </summary>
public sealed class NotificationService
{
    private readonly AppState _state;
    private readonly Action _notify;
    private readonly HashSet<uint> _seen = new();

    public NotificationService(AppState state, Action notify)
    {
        _state = state;
        _notify = notify;
    }

    public void Start() => _ = LoopAsync();

    private async Task LoopAsync()
    {
        UserNotificationListener listener;
        try
        {
            listener = UserNotificationListener.Current;
            var status = await listener.RequestAccessAsync();
            if (status != UserNotificationListenerAccessStatus.Allowed)
                return; // graceful fallback: music/timer alerts still work
        }
        catch { return; }

        while (true)
        {
            try { await PollAsync(listener); }
            catch { }
            await Task.Delay(1500);
        }
    }

    private async Task PollAsync(UserNotificationListener listener)
    {
        var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
        var current = new HashSet<uint>();

        foreach (var un in notifications)
        {
            uint id = un.Id;
            current.Add(id);
            if (_seen.Contains(id)) continue;

            string app = "";
            try { app = un.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }

            string title = "", body = "";
            try
            {
                var binding = un.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (binding != null)
                {
                    var texts = binding.GetTextElements();
                    if (texts.Count > 0) title = texts[0].Text ?? "";
                    if (texts.Count > 1)
                        body = string.Join(" ", texts.Skip(1).Select(t => t.Text));
                }
            }
            catch { }

            _state.SetAlert(new AlertInfo
            {
                Icon = "\uE7ED", // ringer / bell
                Title = string.IsNullOrEmpty(title) ? (string.IsNullOrEmpty(app) ? "Notificação" : app) : title,
                Subtitle = string.IsNullOrEmpty(body) ? app : body,
                Accent = new SKColor(255, 159, 10),
            }, 5000);
            _notify();
        }

        _seen.RemoveWhere(id => !current.Contains(id));
        foreach (var id in current) _seen.Add(id);
    }
}
