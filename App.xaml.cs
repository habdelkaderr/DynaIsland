using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DynaIsland;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Used by build.ps1 to check Windows will run a new build before replacing the installed one.
        if (e.Args.Contains("--self-test"))
        {
            Shutdown(0);
            return;
        }

        _singleInstance = new Mutex(true, @"Local\DynaIsland.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("unobserved task", args.Exception);
            args.SetObserved();
        };

        var settings = Settings.Load();
        if (settings.LowMemoryMode) RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        var window = new MainWindow(settings);
        MainWindow = window;
        window.Show();
    }

    /// <summary>Starts a fresh copy of the app and exits this one (for settings applied at start-up).</summary>
    public void Restart()
    {
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        _singleInstance = null;
        if (Environment.ProcessPath is { } exe) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("dispatcher", e.Exception);
        e.Handled = true;
    }
}
