using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using System.Diagnostics;
using System.Security;

namespace HaNotify;

public sealed partial class MainWindow
{
    private string? launchClick;
    private long launchClickTime;
    private Task receivedNotifications = Task.CompletedTask;

    // Handles the click that started this process. Returns true if the click needs no settings window.
    internal bool HandleLaunchActivation()
    {
        if (!notificationsRegistered) return false;
        try
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind != ExtendedActivationKind.AppNotification || activation.Data is not AppNotificationActivatedEventArgs args) return false;
            launchClick = ClickArgument(args);
            launchClickTime = Environment.TickCount64;
            if (NotificationClick.Decode(launchClick) is not { } click) return false;
            HandleClick(click);
            return true;
        }
        catch (Exception ex)
        {
            ShowDelivery("Could not read the notification click: " + ex.Message);
            return false;
        }
    }

    // Windows raises this on a background thread.
    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var argument = ClickArgument(args);
        Ui(() =>
        {
            // The click that launched the app can also arrive here. Handling it twice would fire the HA event twice.
            if (launchClick != null && argument == launchClick && Environment.TickCount64 - launchClickTime < 10000) { launchClick = null; return; }
            HandleClick(NotificationClick.Decode(argument));
        });
    }

    private static string? ClickArgument(AppNotificationActivatedEventArgs args) =>
        args.Arguments.TryGetValue(NotificationClick.ArgumentName, out var value) ? value : null;

    private void HandleClick(NotificationClick? click)
    {
        if (click?.Uri != null) OpenInBrowser(click.Uri);
        else if (click?.Action != null) SendAction(click);
        else Restore();
    }

    private void OpenInBrowser(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception) { ShowDelivery("Could not open the link in your browser."); }
    }

    private async void SendAction(NotificationClick click)
    {
        if (saved is not { } settings) { ShowDelivery("Could not send the action. Connect to Home Assistant first."); Restore(); return; }
        try
        {
            await Task.Run(() => HaConnection.SendActionAsync(settings, click.Action!, click.Tag, click.Title, click.Message));
            if (!closed) ShowDelivery($"Action sent to Home Assistant at {DateTime.Now:t}.");
        }
        catch (Exception ex)
        {
            if (!closed) ShowDelivery(ex is IOException ? ex.Message : "Could not send the action to Home Assistant.");
        }
    }

    private void Test_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        ShowNotification(new HaNotification(HaNotification.DefaultTitle, "Your Windows notifications are ready.", null, null, null, []), true);

    private void ReceiveNotification(HaNotification notification) => receivedNotifications = ProcessAfterAsync(receivedNotifications, notification);

    // Runs in arrival order, so a toast sent straight after a clear can't be removed by that clear. Never faults.
    private async Task ProcessAfterAsync(Task previous, HaNotification notification)
    {
        await previous;
        if (closed) return;
        if (!notification.IsClear) ShowNotification(notification);
        else if (notification.ToastIdentity() != null)
        {
            try { await ClearNotificationAsync(notification); }
            catch (Exception ex) { if (!closed) ShowDelivery("Could not clear the notification: " + ex.Message); }
        }
    }

    private void ShowNotification(HaNotification content, bool test = false)
    {
        try
        {
            if (!notificationsRegistered) throw new InvalidOperationException("Restart the app to initialize notifications.");
            var notification = BuildToast(content);
            if (content.ToastIdentity() is { } identity)
            {
                notification.Tag = identity.Tag;
                notification.Group = identity.Group;
            }
            AppNotificationManager.Default.Show(notification);
            ShowDelivery(notification.Id == 0 ? "Windows did not accept the notification. Check notification settings." :
                test ? "Test sent to Windows. This checks Windows notifications only." : $"Last notification received at {DateTime.Now:t}.");
        }
        catch (Exception ex) { ShowDelivery("Windows notification failed: " + ex.Message); }
    }

    // Each custom button repeats the title and message, which can exceed Windows' size limit. Only the action id is required, so drop the text if so.
    private static AppNotification BuildToast(HaNotification content)
    {
        try { return BuildToast(content, true); }
        catch (Exception) when (content.Actions.Any(action => action.Uri == null)) { return BuildToast(content, false); }
    }

    private static AppNotification BuildToast(HaNotification content, bool includeText)
    {
        var builder = new AppNotificationBuilder().AddText(content.Title).AddText(content.Message);
        if (content.ClickUri != null)
            builder.AddArgument(NotificationClick.ArgumentName, new NotificationClick(null, content.ClickUri, null, null, null).Encode());
        // Unlike text and arguments, the builder writes button titles into the XML unescaped.
        foreach (var action in content.Actions)
            builder.AddButton(new AppNotificationButton(SecurityElement.Escape(action.Title)).AddArgument(NotificationClick.ArgumentName, content.ClickFor(action, includeText).Encode()));
        return builder.BuildNotification();
    }

    private static async Task ClearNotificationAsync(HaNotification clear)
    {
        var (tag, group) = clear.ToastIdentity()!.Value;
        var manager = AppNotificationManager.Default;
        await manager.RemoveByTagAndGroupAsync(tag, group);
        await manager.RemoveByTagAsync(tag);
        if (clear.Group != null) return;
        // RemoveByTagAsync only matches toasts with no group, and does nothing otherwise without raising an error.
        // Android clears by tag alone, so find a toast shown with an explicit group and remove it by its real group.
        foreach (var shown in await manager.GetAllAsync())
            if (shown.Tag == tag) await manager.RemoveByTagAndGroupAsync(shown.Tag, shown.Group);
    }
}
