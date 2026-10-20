using System.Windows;
using System.Windows.Threading;
using AirPlay.Core;

namespace AirPlay.App;

public partial class App : Application
{
    private const string InstanceName = @"Local\WindowsAirplay.App";
    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private FileLog? _log;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Only one receiver per PC (port 7000 and the mDNS name). A second launch just brings
        // the running one to the front.
        _singleInstance = new Mutex(true, InstanceName, out var isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        if (!isFirst)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        _log = new FileLog(FileLog.DefaultDirectory, "app");
        _log.Write($"WindowsAirplay {typeof(App).Assembly.GetName().Version} 啟動，記錄檔: {_log.Path}");

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => _log?.Write($"未處理的例外: {ev.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, ev) => { _log?.Write($"未觀察的工作例外: {ev.Exception}"); ev.SetObserved(); };

        var settings = AppSettings.Load();
        var minimized = e.Args.Contains(StartupRegistration.MinimizedArgument);
        _window = new MainWindow(_log, settings, minimized);
        MainWindow = _window;
        if (!(minimized && settings.CloseToTray)) _window.Show(); // tray mode: stay hidden until a session starts
        _ = _window.InitializeAsync();

        // Wait for "show yourself" from a second launch.
        ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(() => _window?.ShowFromBackground()), null, Timeout.Infinite, false);
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
        _showSignal?.Dispose();
        base.OnExit(e);
    }
}
