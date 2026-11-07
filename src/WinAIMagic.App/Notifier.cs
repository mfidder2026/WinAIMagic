using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace WinAIMagic.App;

/// <summary>Windows notifications: a task is done, needs approval, or went wrong. A click opens its conversation. When Windows
/// will not take the app's notifications (an unusual setup), the tray icon shows a balloon instead.</summary>
public static class Notifier
{
    private static AppState? app;
    private static bool registered;

    public static void SetUp(AppState state)
    {
        app = state;
        if (registered || Program.TestData) return;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, e) =>
            {
                var id = e.Arguments.TryGetValue("conversation", out var s) && Guid.TryParse(s, out var g) ? g : (Guid?)null;
                app?.Ui.TryEnqueue(() =>
                {
                    if (app.Window is ShellWindow w) w.ShowFromTray();
                    if (id is { } conversation) app.Open(conversation);
                });
            };
            AppNotificationManager.Default.Register();
            registered = true;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            App.Log(e);
        }
    }

    public static void Post(string title, string body, Guid? conversation)
    {
        body = body.Length > 240 ? body[..240] + "…" : body;
        if (registered)
        {
            try
            {
                var b = new AppNotificationBuilder().AddText(title).AddText(body);
                if (conversation is { } id) b.AddArgument("conversation", id.ToString());
                AppNotificationManager.Default.Show(b.BuildNotification());
                return;
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { App.Log(e); }
        }
        (app?.Window as ShellWindow)?.Tray.Balloon(title, body);
    }

    public static void Shutdown()
    {
        if (!registered) return;
        try { AppNotificationManager.Default.Unregister(); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
        registered = false;
    }
}
