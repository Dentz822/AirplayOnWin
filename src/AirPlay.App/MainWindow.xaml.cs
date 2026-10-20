using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AirPlay.Core;
using AirPlay.Core.Identity;
using AirPlay.Media;

namespace AirPlay.App;

public partial class MainWindow : Window
{
    private readonly FileLog _log;
    private readonly LatestFrame _latest = new();
    private AirPlayReceiver? _receiver;
    private WriteableBitmap? _bitmap;
    private long _shownVersion;
    private (int W, int H) _videoSize;
    private bool _closingDone;

    // full screen state
    private bool _fullScreen;
    private WindowState _savedState;
    private WindowStyle _savedStyle;
    private ResizeMode _savedResize;

    public MainWindow(FileLog log)
    {
        _log = log;
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        KeyDown += OnKeyDown;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Log(string message) => _log.Write(message);

    // ---- startup / shutdown ---------------------------------------------------------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Developer option: AirPlay.App --play recordings\mirror-xxx.h264 replays a recording
        // through the same decode/display path, without an iPhone.
        var args = Environment.GetCommandLineArgs();
        var playIndex = Array.IndexOf(args, "--play");
        if (playIndex >= 0 && playIndex + 1 < args.Length)
        {
            Title = "WindowsAirplay — 播放錄影";
            _ = Task.Run(() => PlayRecording(args[playIndex + 1]));
            return;
        }

        try
        {
            Log($"FFmpeg {FFmpegLoader.EnsureLoaded()}");
            var id = DeviceIdentity.LoadOrCreate();
            Title = $"WindowsAirplay — {id.Name}";
            WaitingHint.Text = $"在 iPhone 打開「控制中心」\n點「螢幕鏡像輸出」，選擇「{id.Name}」";

            _receiver = new AirPlayReceiver(id, new ReceiverOptions
            {
                MirrorSinkFactory = () => new VideoPresenter(_latest, Log, OnSessionActiveChanged),
                AudioSinkFactory = format => new AudioPlayer(format, Log),
            }, Log);
            await _receiver.StartAsync();
            Status.Text = $"{id.Name} 準備好了";
        }
        catch (Exception ex)
        {
            Log($"啟動失敗: {ex}");
            WaitingTitle.Text = "無法啟動";
            WaitingHint.Text = ex is System.Net.Sockets.SocketException
                ? "連接埠 7000 已被占用。\n請先關閉其他 WindowsAirplay（例如主控台版 AirPlay.Cli）。"
                : ex.Message;
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingDone || _receiver is null) return;
        // Withdraw mDNS before the window disappears, so iPhones stop listing this PC.
        e.Cancel = true;
        Hide();
        await _receiver.DisposeAsync();
        _closingDone = true;
        Close();
    }

    private async Task PlayRecording(string path)
    {
        try
        {
            Log($"播放錄影: {path}");
            var data = await System.IO.File.ReadAllBytesAsync(path);
            using var presenter = new VideoPresenter(_latest, Log, OnSessionActiveChanged);
            var clock = Stopwatch.StartNew();
            var n = 0;
            foreach (var au in AnnexBReader.AccessUnits(data))
            {
                presenter.OnFrame(new Core.Mirroring.VideoFrame(au.ToArray(), 0, false));
                var due = TimeSpan.FromSeconds(++n / 30.0) - clock.Elapsed; // 30 fps
                if (due > TimeSpan.Zero) await Task.Delay(due);
            }
            Log($"播放結束: {n} 幀");
        }
        catch (Exception ex)
        {
            Log($"播放失敗: {ex}");
        }
    }

    /// <summary>Logoff / shutdown: no time for async, stop synchronously.</summary>
    public void StopForSessionEnd() => _receiver?.Stop(TimeSpan.FromSeconds(4));

    // ---- video ------------------------------------------------------------------------------

    private void OnSessionActiveChanged(bool active) => Dispatcher.BeginInvoke(() =>
    {
        Waiting.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        if (!active)
        {
            Video.Source = null;
            _bitmap = null;
            _videoSize = default;
            Status.Text = "投影已結束";
        }
    });

    private int _framesThisSecond;
    private readonly Stopwatch _fpsClock = Stopwatch.StartNew();

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_latest.Version == _shownVersion || Waiting.Visibility == Visibility.Visible) return;

        _latest.Read((w, h, pixels) =>
        {
            if (w == 0) return;
            if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
            {
                _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                Video.Source = _bitmap;
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
            _shownVersion = _latest.Version;
        });

        if ((_bitmap?.PixelWidth, _bitmap?.PixelHeight) is (int bw, int bh) && (bw, bh) != _videoSize)
        {
            _videoSize = (bw, bh);
            FitWindowToVideo(bw, bh);
        }

        _framesThisSecond++;
        if (_fpsClock.ElapsedMilliseconds >= 1000)
        {
            Status.Text = $"{_videoSize.W}x{_videoSize.H}  {_framesThisSecond * 1000.0 / _fpsClock.ElapsedMilliseconds:F0} fps";
            _framesThisSecond = 0;
            _fpsClock.Restart();
        }
    }

    /// <summary>Resizes the window to the video's aspect ratio (portrait or landscape), within the screen.</summary>
    private void FitWindowToVideo(int videoW, int videoH)
    {
        if (_fullScreen || WindowState != WindowState.Normal) return;
        var work = SystemParameters.WorkArea;
        var chromeW = ActualWidth - Root.ActualWidth;
        var chromeH = ActualHeight - Root.ActualHeight;

        var maxW = work.Width * 0.9 - chromeW;
        var maxH = work.Height * 0.9 - chromeH;
        var scale = Math.Min(maxW / videoW, maxH / videoH);
        var contentW = videoW * scale;
        var contentH = videoH * scale;

        Width = contentW + chromeW;
        Height = contentH + chromeH;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }

    // ---- full screen & menu -----------------------------------------------------------------

    private void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _savedState = WindowState;
            _savedStyle = WindowStyle;
            _savedResize = ResizeMode;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal; // re-maximize so the taskbar is covered
            WindowState = WindowState.Maximized;
            _fullScreen = true;
        }
        else
        {
            WindowStyle = _savedStyle;
            ResizeMode = _savedResize;
            WindowState = _savedState;
            _fullScreen = false;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) ToggleFullScreen();
        else if (e.Key == Key.Escape && _fullScreen) ToggleFullScreen();
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleFullScreen();
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void Topmost_Click(object sender, RoutedEventArgs e) => Topmost = TopmostItem.IsChecked;

    private void OpenLogs_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{FileLog.DefaultDirectory}\"") { UseShellExecute = true });

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
}
