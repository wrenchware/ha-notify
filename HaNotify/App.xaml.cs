using Microsoft.UI.Xaml;

namespace HaNotify;

public partial class App : Application
{
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    private Mutex? instance;
    private MainWindow? window;
    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        var preview = arguments.Contains("--preview");
        if (!preview)
        {
            instance = new Mutex(true, "Local\\Personal.HaNotify", out var first);
            if (!first) { instance.Dispose(); instance = null; Exit(); return; }
        }
        try
        {
            if (!preview)
                System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID("Personal.HaNotify.Notifications"));
            window = new MainWindow(preview);
            window.Closed += (_, _) => { instance?.ReleaseMutex(); instance?.Dispose(); instance = null; };
            if (!arguments.Contains("--tray") || !window.HasConnection || preview) window.Activate();
        }
        catch (Exception ex)
        {
            if (preview) File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "preview-error.txt"), ex.ToString());
            else System.Windows.Forms.MessageBox.Show(ex.Message, "HA Notify could not start");
            instance?.ReleaseMutex();
            instance?.Dispose();
            Exit();
        }
    }
}
