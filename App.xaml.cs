using System.Windows;
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

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
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
