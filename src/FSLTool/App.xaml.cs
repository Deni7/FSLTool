using System.Windows;
using System.Windows.Threading;
using FSLTool.Services;

namespace FSLTool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Unhandled: " + a.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Task: " + a.Exception.Message); a.SetObserved(); };
        base.OnStartup(e);

        // FSLTool.exe scan|analyze|compact|clean … → headless run for Task Scheduler, no window
        if (Headless.IsHeadless(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code;
            try { code = Headless.Run(e.Args); }
            catch (Exception ex) { Log.Error("[CLI] " + ex); code = Headless.ExitFatal; }
            Environment.ExitCode = code;   // the generated Main ignores Run()'s return value
            Shutdown(code);
            return;
        }

        new MainWindow().Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI: " + e.Exception);
        try { FSLTool.Views.Dialog.Error("FSLTool — помилка", e.Exception.Message); }
        catch { MessageBox.Show(e.Exception.Message, "FSLTool — помилка", MessageBoxButton.OK, MessageBoxImage.Error); } // last resort
        e.Handled = true;
    }
}
