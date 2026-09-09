using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace HaNotify;

public sealed partial class MainWindow : Window
{
    private readonly bool preview;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private Settings? saved;
    private CancellationTokenSource? cancellation;
    private Task? listener;
    private bool ready;
    private bool exiting;
    private bool closed;
    private bool notificationsRegistered;
    internal bool HasConnection => saved != null;

    public MainWindow(bool preview)
    {
        this.preview = preview;
        InitializeComponent();
        Root.SizeChanged += (_, _) =>
        {
            PageContent.Width = Math.Max(0, Math.Min(560, Root.ActualWidth - 64));
            var side = Math.Max(20, (Root.ActualWidth - PageContent.Width) / 2);
            PageContent.Margin = new Thickness(side, 18, side, 28);
        };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleRegion);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ha-notify.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(680, 880));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + Math.Max(0, (area.Width - 680) / 2), area.Y + Math.Max(0, (area.Height - 880) / 2)));
        if (MicaController.IsSupported()) SystemBackdrop = new MicaBackdrop();
        Root.ActualThemeChanged += (_, _) => ApplyTheme();
        ApplyTheme();
        Device.Text = Environment.MachineName;
        if (preview)
        {
            Device.Text = "Office PC";
            Root.Loaded += async (_, _) => await CapturePreviewsAsync();
            return;
        }

        trayIcon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "ha-notify.ico"), 32, 32);
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "HA Notify", Visible = true, ContextMenuStrip = new Forms.ContextMenuStrip() };
        tray.ContextMenuStrip.Items.Add("Open HA Notify", null, (_, _) => Ui(Restore));
        tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Ui(() => { exiting = true; Close(); }));
        tray.DoubleClick += (_, _) => Ui(Restore);
        ApplyTheme();
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register("Home Assistant", new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "home-assistant.png")));
            notificationsRegistered = true;
        }
        catch (Exception ex) { ShowDelivery("Windows notifications could not initialize: " + ex.Message); }

        try
        {
            saved = Settings.Load();
            if (saved != null) { Address.Text = saved.Url; Token.Password = saved.Token; Device.Text = saved.Name; }
        }
        catch { SetStatus("Saved connection could not be read. Enter your connection again."); }
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            Startup.IsOn = key?.GetValue("HaNotify") != null;
        ready = true;
        AppWindow.Closing += (_, args) => { if (!exiting) { args.Cancel = true; AppWindow.Hide(); } };
        Closed += (_, _) =>
        {
            closed = true;
            cancellation?.Cancel();
            tray?.Dispose();
            trayIcon?.Dispose();
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            if (notificationsRegistered) AppNotificationManager.Default.Unregister();
        };
        if (saved != null) StartListener();
    }

    private void ApplyTheme()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        if (tray?.ContextMenuStrip is { } menu)
        {
            menu.BackColor = dark ? System.Drawing.Color.FromArgb(32, 32, 32) : System.Drawing.SystemColors.Menu;
            menu.ForeColor = dark ? System.Drawing.Color.White : System.Drawing.Color.Black;
        }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => Ui(Restore);
    private void Restore() { AppWindow.Show(); if (AppWindow.Presenter is OverlappedPresenter p) p.Restore(); Activate(); }
    private void Ui(Action action) => DispatcherQueue.TryEnqueue(() => { if (!closed) action(); });
    private void SetStatus(string message)
    {
        var connected = message.StartsWith("Connected", StringComparison.Ordinal);
        ConnectionStatus.Severity = connected ? InfoBarSeverity.Success : message.Contains("Retrying", StringComparison.Ordinal) ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        ConnectionStatus.Title = connected ? "Connected to Home Assistant" : "Connection status";
        ConnectionStatus.Message = connected ? "Listening for notifications." : message;
        if (tray != null) tray.Text = connected ? "HA Notify • Connected" : "HA Notify • " + (message.Length > 45 ? message[..45] : message);
    }
    private void ShowDelivery(string message) { DeliveryStatus.Text = message; DeliveryStatus.Visibility = Visibility.Visible; }
    private void Test_Click(object sender, RoutedEventArgs e) => ShowNotification("Home Assistant", "Your Windows notifications are ready.", true);
    private void ShowNotification(string title, string message, bool test = false)
    {
        try
        {
            if (!notificationsRegistered) throw new InvalidOperationException("Restart the app to initialize notifications.");
            var notification = new AppNotificationBuilder().AddText(title).AddText(message).BuildNotification();
            AppNotificationManager.Default.Show(notification);
            ShowDelivery(notification.Id == 0 ? "Windows did not accept the notification. Check notification settings." :
                test ? "Test sent to Windows. This checks Windows notifications only." : $"Last notification received at {DateTime.Now:t}.");
        }
        catch (Exception ex) { ShowDelivery("Windows notification failed: " + ex.Message); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (preview) return;
        ConnectButton.IsEnabled = false;
        try
        {
            SetStatus("Saving connection…");
            var url = Address.Text.Trim().TrimEnd('/') + "/";
            var newToken = Token.Password.Trim();
            var name = Device.Text.Trim();
            var next = saved != null && saved.Url == url && saved.Name == name
                ? saved with { Token = !string.IsNullOrWhiteSpace(newToken) ? newToken : throw new InvalidOperationException("Enter an access token.") }
                : await HaConnection.RegisterAsync(url, newToken, name);
            next.Save();
            cancellation?.Cancel();
            if (listener != null) await listener;
            cancellation?.Dispose();
            saved = next;
            if (!closed) StartListener();
        }
        catch (Exception ex) { if (!closed) { SetStatus(ex.Message); ConnectionStatus.Severity = InfoBarSeverity.Error; } }
        finally { if (!closed) ConnectButton.IsEnabled = true; }
    }
    private void StartListener()
    {
        cancellation = new CancellationTokenSource();
        listener = HaConnection.RunAsync(saved!, s => Ui(() => SetStatus(s)), (t, m) => Ui(() => ShowNotification(t, m)), cancellation.Token);
    }
    private void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (!ready || preview) return;
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (Startup.IsOn) run.SetValue("HaNotify", $"\"{Environment.ProcessPath}\" --tray");
            else run.DeleteValue("HaNotify", false);
        }
        catch (Exception ex)
        {
            ready = false; Startup.IsOn = !Startup.IsOn; ready = true;
            ShowDelivery("Could not change startup setting: " + ex.Message);
        }
    }

    private async Task CapturePreviewsAsync()
    {
        try
        {
            foreach (var sample in new[] { (Dark: false, Width: 680, Name: "light"), (Dark: true, Width: 680, Name: "dark"), (Dark: true, Width: 520, Name: "narrow") })
            {
                AppWindow.Resize(new Windows.Graphics.SizeInt32(sample.Width, 880));
                Root.RequestedTheme = sample.Dark ? ElementTheme.Dark : ElementTheme.Light;
                await Task.Delay(350);
                var position = PageContent.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0));
                if (position.X < 0 || position.X + PageContent.ActualWidth > Root.ActualWidth + 1)
                    throw new InvalidOperationException("Preview content extends outside the window.");
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                await bitmap.RenderAsync(Root);
                var pixels = await bitmap.GetPixelsAsync();
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(CreatePreviewFile(sample.Name));
                using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
                await encoder.FlushAsync();
            }
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "preview-error.txt"), ex.ToString()); }
        finally { Close(); Application.Current.Exit(); }
    }
    private static string CreatePreviewFile(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"preview-{name}.png");
        File.WriteAllBytes(path, []);
        return path;
    }
}
