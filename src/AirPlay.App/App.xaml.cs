using System.Windows;
using System.Windows.Threading;
using AirPlay.Core;

namespace AirPlay.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private FileLog? _log;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Only one receiver per PC: a second one would fight over port 7000 and the mDNS name.
        _singleInstance = new Mutex(true, @"Local\WindowsAirplay.App", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("WindowsAirplay 已經在執行了。", "WindowsAirplay", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _log = new FileLog(FileLog.DefaultDirectory, "app");
        _log.Write($"WindowsAirplay 啟動，記錄檔: {_log.Path}");

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => _log?.Write($"未處理的例外: {ev.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, ev) => { _log?.Write($"未觀察的工作例外: {ev.Exception}"); ev.SetObserved(); };

        _window = new MainWindow(_log);
        MainWindow = _window;
        _window.Show();
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Write($"介面錯誤: {e.Exception}");
        e.Handled = true; // keep running; the error is in the log
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _log?.Write("Windows 登出/關機，停止接收");
        _window?.StopForSessionEnd();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Write("WindowsAirplay 結束");
        _log?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
