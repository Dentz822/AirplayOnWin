using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AirPlay.Core;
using AirPlay.Core.Identity;
using AirPlay.Core.Metadata;
using AirPlay.Core.Timing;
using AirPlay.Media;

namespace AirPlay.App;

public partial class MainWindow : Window
{
    private readonly FileLog _log;
    private readonly AppSettings _settings;
    private readonly FrameQueue _frames = new();
    private readonly TrayIcon _tray;
    private readonly VideoPlaybackHost _videoHost;
    private PlaybackClock? _activeClock;
    private AirPlayReceiver? _receiver;
    private DeviceIdentity? _identity;
    private WriteableBitmap? _bitmap;
    private (int W, int H) _videoSize;
    private bool _exitRequested;
    private bool _closingDone;
    private bool _trayHintShown;
    private bool _videoActive;
    private NowPlaying? _nowPlaying;
    private readonly System.Windows.Threading.DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    // full screen state
    private bool _fullScreen;
    private WindowState _savedState;
    private WindowStyle _savedStyle;
    private ResizeMode _savedResize;

    public MainWindow(FileLog log, AppSettings settings, bool startMinimized)
    {
        _log = log;
        _settings = settings;
        InitializeComponent();
        Topmost = settings.AlwaysOnTop;
        _tray = new TrayIcon(ShowFromBackground, RequestExit);
        _videoHost = new VideoPlaybackHost(_frames, Log, (clock, active) => Dispatcher.BeginInvoke(() =>
        {
            _activeClock = clock;
            if (active)
            {
                Status.Text = "AirPlay 影片";
                ShowFromBackground();
            }
            OnSessionActiveChanged(active);
        }));
        if (startMinimized) WindowState = WindowState.Minimized;

        Closing += OnClosing;
        _progressTimer.Tick += (_, _) => UpdateProgress();
        KeyDown += OnKeyDown;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Log(string message) => _log.Write(message);

    // ---- startup / shutdown ---------------------------------------------------------------

    /// <summary>Starts receiving. Called by App right away, also when the window starts hidden in the tray.</summary>
    public async Task InitializeAsync()
    {
        // Developer option: --play recordings\mirror-xxx.h264 replays a recording
        // through the same decode/display path, without an iPhone.
        var args = Environment.GetCommandLineArgs();
        var playIndex = Array.IndexOf(args, "--play");
        if (playIndex >= 0 && playIndex + 1 < args.Length)
        {
            Title = "WindowsAirplay — 播放錄影";
            _ = Task.Run(() => PlayRecording(args[playIndex + 1]));
            return;
        }

        Log($"FFmpeg {FFmpegLoader.EnsureLoaded()}");
        CheckFirewall();
        await StartReceiverAsync(null);
    }

    /// <summary>Starts (or restarts, e.g. after a rename) the receiver with the saved identity.</summary>
    private async Task StartReceiverAsync(string? newName)
    {
        try
        {
            if (_receiver is not null)
            {
                await _receiver.DisposeAsync();
                _receiver = null;
            }
            _identity = DeviceIdentity.LoadOrCreate(newName);
            var name = _identity.Name;
            Title = $"WindowsAirplay — {name}";
            WaitingTitle.Text = "等待 iPhone 連線";
            WaitingHint.Text = $"在 iPhone 打開「控制中心」\n點「螢幕鏡像輸出」，選擇「{name}」";
            _tray.Text = $"WindowsAirplay — {name}";

            _receiver = new AirPlayReceiver(_identity, new ReceiverOptions
            {
                MaxFps = _settings.MaxFps,
                RefreshRate = Math.Max(60, _settings.MaxFps),
                MirrorSinkFactory = clock =>
                {
                    _activeClock = clock;
                    return new VideoPresenter(_frames, clock, Log, OnSessionActiveChanged);
                },
                AudioSinkFactory = (format, clock) =>
                {
                    _activeClock ??= clock; // audio-only sessions: used to tell if music is playing
                    return new AudioPlayer(format, clock, Log);
                },
                NowPlayingChanged = np => Dispatcher.BeginInvoke(() => OnNowPlaying(np)),
                VideoPlayer = _settings.AppVideo ? _videoHost : null,
                PreferMirroring = () => _settings.KeepMirroring,
                ClientConnected = (client, model) => Dispatcher.BeginInvoke(() => OnClientConnected(client)),
            }, Log);
            await _receiver.StartAsync();
            Status.Text = $"{name} 準備好了（{_settings.MaxFps} fps）";
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

    private void OnClientConnected(string client)
    {
        Status.Text = $"已連線：{client}";
        _tray.Text = $"WindowsAirplay — {client} 投影中";
        // Bring the window forward when mirroring starts, even if it was minimized or in the tray.
        ShowFromBackground();
    }

    /// <summary>Shows the window from the tray / minimized state and brings it to the front.</summary>
    public void ShowFromBackground()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;      // reliable way to come to the front...
        Topmost = _settings.AlwaysOnTop; // ...then restore the user's choice
    }

    private void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingDone) return;

        if (_settings.CloseToTray && !_exitRequested)
        {
            // Keep receiving in the background; the tray icon brings the window back.
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _tray.ShowBalloon("WindowsAirplay 仍在執行", "在系統匣圖示按右鍵可以結束。");
                _trayHintShown = true;
            }
            return;
        }

        // Withdraw mDNS before the window disappears, so iPhones stop listing this PC.
        e.Cancel = true;
        Hide();
        _tray.Dispose();
        _videoHost.Stop();
        if (_receiver is not null) await _receiver.DisposeAsync();
        _closingDone = true;
        Close();
    }

    private async Task PlayRecording(string path)
    {
        try
        {
            Log($"播放錄影: {path}");
            var data = await System.IO.File.ReadAllBytesAsync(path);
            using var presenter = new VideoPresenter(_frames, null, Log, OnSessionActiveChanged);
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
    public void StopForSessionEnd()
    {
        _tray.Dispose();
        _receiver?.Stop(TimeSpan.FromSeconds(4));
    }

    // ---- firewall ----------------------------------------------------------------------------

    private void CheckFirewall()
    {
        var rules = FirewallSetup.RulesPresent();
        var publicNets = FirewallSetup.PublicNetworks();
        Log($"防火牆規則: {(rules switch { true => "已設定", false => "未設定", null => "無法檢查" })}" +
            (publicNets.Count > 0 ? $"，公用網路: {string.Join(", ", publicNets)}" : ""));

        if (rules == false)
        {
            FirewallText.Text = "防火牆還沒設定，iPhone 可能連不進來。";
            FirewallButton.Visibility = Visibility.Visible;
            FirewallBanner.Visibility = Visibility.Visible;
        }
        else if (publicNets.Count > 0)
        {
            FirewallText.Text = $"網路「{string.Join("、", publicNets)}」是「公用」，iPhone 會連不進來。\n" +
                                "請到 設定 → 網路和網際網路 把它改成「私人網路」。";
            FirewallButton.Visibility = Visibility.Collapsed;
            FirewallBanner.Visibility = Visibility.Visible;
        }
        else
        {
            FirewallBanner.Visibility = Visibility.Collapsed;
        }
    }

    private async void Firewall_Click(object sender, RoutedEventArgs e)
    {
        Log("設定防火牆（UAC）");
        var ok = await Task.Run(FirewallSetup.CreateRules);
        Log(ok ? "防火牆規則已建立" : "防火牆設定取消或失敗");
        if (!ok) MessageBox.Show(this, "防火牆沒有設定成功（可能按了「否」）。", "WindowsAirplay");
        CheckFirewall();
    }

    // ---- video ------------------------------------------------------------------------------

    private void OnSessionActiveChanged(bool active) => Dispatcher.BeginInvoke(() =>
    {
        _videoActive = active;
        UpdatePanels();
        if (!active)
        {
            _frames.Clear();
            Video.Source = null;
            _bitmap = null;
            _videoSize = default;
            Status.Text = "投影已結束";
            _tray.Text = $"WindowsAirplay — {_identity?.Name}";
        }
    });

    // ---- audio-only "now playing" ------------------------------------------------------------

    private void OnNowPlaying(NowPlaying? np)
    {
        var artworkChanged = !ReferenceEquals(np?.Artwork, _nowPlaying?.Artwork);
        _nowPlaying = np;
        if (np is null)
        {
            _activeClock = null;
            Status.Text = "投影已結束";
            UpdatePanels();
            return;
        }

        TrackTitle.Text = string.IsNullOrEmpty(np.Title) ? "正在播放" : np.Title;
        TrackArtist.Text = string.Join(" — ", new[] { np.Artist, np.Album }.Where(s => !string.IsNullOrEmpty(s)));
        if (artworkChanged) Artwork.Source = LoadImage(np.Artwork);
        _tray.Text = $"♪ {np.Title} — {np.Artist}";
        UpdateProgress();
        UpdatePanels();
    }

    /// <summary>Video, the music panel or the waiting screen — exactly one is visible.</summary>
    private void UpdatePanels()
    {
        var music = !_videoActive && _nowPlaying is not null;
        MusicPanel.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        Waiting.Visibility = !_videoActive && !music ? Visibility.Visible : Visibility.Collapsed;
        if (music) _progressTimer.Start(); else _progressTimer.Stop();
    }

    private void UpdateProgress()
    {
        if (_nowPlaying is not { } np || np.Duration <= TimeSpan.Zero)
        {
            TrackProgress.Value = 0;
            TrackPosition.Text = TrackDuration.Text = "";
            return;
        }
        var playing = _activeClock?.AudibleRemoteNanosNow() is not null;
        var position = np.CurrentPosition(playing);
        TrackProgress.Value = position.TotalSeconds / np.Duration.TotalSeconds;
        TrackPosition.Text = Format(position);
        TrackDuration.Text = Format(np.Duration);
        Status.Text = playing ? "播放中" : "暫停";

        static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private static BitmapImage? LoadImage(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new System.IO.MemoryStream(data);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null; // not a decodable image
        }
    }

    private int _framesThisSecond;
    private readonly Stopwatch _fpsClock = Stopwatch.StartNew();
    // A/V sync statistics: how far each shown picture is from the audio being heard.
    private readonly Stopwatch _syncReportClock = Stopwatch.StartNew();
    private double _skewSumMs;
    private int _skewCount;

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_videoActive) return;

        // Video follows audio: with live audio, show the newest picture whose time has been reached.
        var audibleNow = _activeClock?.AudibleRemoteNanosNow();
        var frame = _frames.TakeDue(audibleNow, _activeClock?.IsPaused ?? false);
        if (frame is null) return;

        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
            Video.Source = _bitmap;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
        _frames.Recycle(frame.Pixels);

        if (audibleNow is long heard && frame.RemoteNs is long due)
        {
            _skewSumMs += (heard - due) / 1e6;
            _skewCount++;
        }

        if ((frame.Width, frame.Height) != _videoSize)
        {
            _videoSize = (frame.Width, frame.Height);
            FitWindowToVideo(frame.Width, frame.Height);
        }

        _framesThisSecond++;
        if (_fpsClock.ElapsedMilliseconds >= 1000)
        {
            var sync = audibleNow is null ? "" : "  影音同步";
            Status.Text = $"{_videoSize.W}x{_videoSize.H}  {_framesThisSecond * 1000.0 / _fpsClock.ElapsedMilliseconds:F0} fps{sync}";
            _framesThisSecond = 0;
            _fpsClock.Restart();
        }

        if (_syncReportClock.Elapsed.TotalSeconds >= 10)
        {
            if (_skewCount > 0)
                Log($"     影音同步: 畫面比聲音晚 {_skewSumMs / _skewCount:F0} ms（平均，正值=畫面較晚），等待中 {_frames.Count} 幀，逾時強制顯示 {_frames.ForcedByTimeout}");
            else
                Log("     影音同步: 沒有聲音，畫面即時顯示");
            _skewSumMs = 0;
            _skewCount = 0;
            _syncReportClock.Restart();
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

        Width = videoW * scale + chromeW;
        Height = videoH * scale + chromeH;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
    }

    // ---- full screen ----------------------------------------------------------------------

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

    // ---- menu -----------------------------------------------------------------------------

    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        TopmostItem.IsChecked = _settings.AlwaysOnTop;
        Fps30Item.IsChecked = _settings.MaxFps == 30;
        Fps60Item.IsChecked = _settings.MaxFps == 60;
        AutoStartItem.IsChecked = StartupRegistration.IsEnabled;
        CloseToTrayItem.IsChecked = _settings.CloseToTray;
        AppVideoItem.IsChecked = _settings.AppVideo;
        KeepMirroringItem.IsChecked = _settings.KeepMirroring;
        KeepMirroringItem.IsEnabled = _settings.AppVideo;
    }

    private async void AppVideo_Click(object sender, RoutedEventArgs e)
    {
        _settings.AppVideo = AppVideoItem.IsChecked;
        _settings.Save();
        Log($"App 影片直接播放: {(_settings.AppVideo ? "開" : "關")}");
        _videoHost.Stop();
        await StartReceiverAsync(null); // re-advertise with or without video support
    }

    private void KeepMirroring_Click(object sender, RoutedEventArgs e)
    {
        _settings.KeepMirroring = KeepMirroringItem.IsChecked;
        _settings.Save();
        Log($"螢幕鏡像時維持鏡像: {(_settings.KeepMirroring ? "開" : "關")}");
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void Topmost_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = TopmostItem.IsChecked;
        Topmost = _settings.AlwaysOnTop;
        _settings.Save();
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new RenameDialog(_identity?.Name ?? Environment.MachineName) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.NewName == _identity?.Name) return;
        Log($"重新命名為「{dialog.NewName}」");
        await StartReceiverAsync(dialog.NewName); // re-advertise under the new name
    }

    private async void Fps_Click(object sender, RoutedEventArgs e)
    {
        var fps = int.Parse((string)((MenuItem)sender).Tag);
        if (fps == _settings.MaxFps) return;
        _settings.MaxFps = fps;
        _settings.Save();
        Log($"畫面更新率改為 {fps} fps");
        await StartReceiverAsync(null); // applies to the next connection
    }

    private void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        StartupRegistration.Set(AutoStartItem.IsChecked);
        Log($"開機自動啟動: {(AutoStartItem.IsChecked ? "開" : "關")}");
    }

    private void CloseToTray_Click(object sender, RoutedEventArgs e)
    {
        _settings.CloseToTray = CloseToTrayItem.IsChecked;
        _settings.Save();
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{FileLog.DefaultDirectory}\"") { UseShellExecute = true });

    private void Exit_Click(object sender, RoutedEventArgs e) => RequestExit();
}
