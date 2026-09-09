using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;

namespace HaNotify;

public sealed partial class MainWindow
{
    private readonly CancellationTokenSource updateCancellation = new();
    private DispatcherTimer? updateTimer;
    private AppRelease? availableRelease;
    private bool updateBusy;
    private EventWaitHandle? updateShutdown;
    private RegisteredWaitHandle? updateShutdownWait;

    private void InitializeUpdates()
    {
        VersionText.Text = "Version " + AppUpdates.VersionLabel;
        if (preview) return;
        updateShutdown = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Personal.HaNotify.UpdateShutdown." + Environment.ProcessId);
        updateShutdownWait = ThreadPool.RegisterWaitForSingleObject(updateShutdown, (_, _) => Ui(() => { exiting = true; Close(); }), null, Timeout.Infinite, true);
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(false);
        updateTimer.Start();
        Closed += (_, _) =>
        {
            updateCancellation.Cancel();
            updateTimer.Stop();
            updateShutdownWait.Unregister(null);
            updateShutdown.Dispose();
        };
        _ = CheckForUpdatesAsync(false);
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (updateBusy) return;
        updateBusy = true;
        UpdateButton.IsEnabled = false;
        if (manual) UpdateStatus.Text = "Checking for updates…";
        try
        {
            availableRelease = await AppUpdates.CheckAsync(updateCancellation.Token);
            UpdateButton.Content = availableRelease == null ? "Check for updates" : "Update available";
            UpdateStatus.Text = availableRelease == null ? "You're up to date." : $"Update available: {AppUpdates.VersionLabel} → {availableRelease.Version.ToString(3)}";
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception)
        {
            if (manual) UpdateStatus.Text = "Could not check for updates. Try again later.";
        }
        finally
        {
            updateBusy = false;
            if (!closed) UpdateButton.IsEnabled = true;
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (preview || updateBusy) return;
        if (availableRelease == null) { await CheckForUpdatesAsync(true); return; }
        var release = availableRelease;
        updateBusy = true;
        UpdateButton.IsEnabled = false;
        try
        {
            var prompt = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = $"Update HA Notify {AppUpdates.VersionLabel} → {release.Version.ToString(3)}",
                Content = "Download and open the installer? It will close HA Notify when installation starts and reopen it afterward. Your connection settings will be kept.",
                PrimaryButtonText = "Download & install",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await prompt.ShowAsync() != ContentDialogResult.Primary) return;
            var progress = new Progress<int>(percent => UpdateStatus.Text = $"Downloading update… {percent}%");
            var installer = await AppUpdates.DownloadAsync(release, progress, updateCancellation.Token);
            var launch = new ProcessStartInfo(installer) { UseShellExecute = true };
            launch.ArgumentList.Add("/DIR=" + AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            Process.Start(launch);
            UpdateStatus.Text = "Installer opened. Follow its steps to finish the update.";
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception) { if (!closed) UpdateStatus.Text = "Could not install the update. Try again later."; }
        finally { updateBusy = false; if (!closed) UpdateButton.IsEnabled = true; }
    }
}
