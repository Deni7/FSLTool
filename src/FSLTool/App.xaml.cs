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
        // UI language: --lang (headless) → choice saved for this Windows user → Windows display language → English
        Loc.Init(LangArg(e.Args));

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

        // light / dark palette and the embedded Manrope font (the headless mode above needs neither)
        FSLTool.Views.ThemeManager.Init(this);
        new MainWindow().Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI: " + e.Exception);
        try { FSLTool.Views.Dialog.Error(Loc.T("App.ErrorTitle"), e.Exception.Message); }
        catch { MessageBox.Show(e.Exception.Message, Loc.T("App.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error); } // last resort
        e.Handled = true;
    }

    /// <summary>Value of --lang xx / --lang=xx, if given and translated.</summary>
    private static string? LangArg(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase)) return a[7..];
            if (a.Equals("--lang", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) return args[i + 1];
        }
        return null;
    }
}
