using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace AirPlay.App.PcAudio;

/// <summary>Settings window for "listen to the PC on the iPhone": switch, QR code and address.</summary>
public sealed class ShareAudioWindow : Window
{
    private readonly PcAudioShare _share;
    private readonly AppSettings _settings;
    private readonly CheckBox _enabled;
    private readonly Image _qr;
    private readonly TextBox _url;
    private readonly TextBlock _listeners;
    private readonly StackPanel _details;

    public ShareAudioWindow(PcAudioShare share, AppSettings settings)
    {
        _share = share;
        _settings = settings;
        Title = "讓手機收聽電腦的聲音";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _enabled = new CheckBox
        {
            Content = "允許手機收聽電腦的聲音",
            IsChecked = share.Enabled,
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 12),
        };
        _enabled.Click += (_, _) => SetEnabled(_enabled.IsChecked == true);

        _qr = new Image { Width = 220, Height = 220, Margin = new Thickness(0, 4, 0, 8) };
        RenderOptions.SetBitmapScalingMode(_qr, BitmapScalingMode.NearestNeighbor);
        _url = new TextBox { IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                             HorizontalContentAlignment = HorizontalAlignment.Center, FontSize = 13 };
        _listeners = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.DimGray };
        var renew = new Button { Content = "更換網址（舊的連結會失效）", Padding = new Thickness(10, 3, 10, 3),
                                 HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        renew.Click += (_, _) => Renew();

        var steps = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Width = 300,
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = Brushes.DimGray,
            Text = "1. iPhone 和電腦連同一個 Wi-Fi\n" +
                   "2. 用 iPhone 相機掃描 QR code（或在 Safari 輸入上面的網址）\n" +
                   "3. 在網頁按「開始收聽」\n\n" +
                   "電腦本身照樣會出聲；只想從手機聽的話，可以把電腦喇叭關小。\n" +
                   "iPhone 投影到電腦的聲音不會被送回手機（不會回音）。",
        };

        _details = new StackPanel();
        _details.Children.Add(_qr);
        _details.Children.Add(_url);
        _details.Children.Add(_listeners);
        _details.Children.Add(renew);
        _details.Children.Add(steps);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(_enabled);
        root.Children.Add(_details);
        Content = root;

        share.ListenersChanged += OnListenersChanged;
        Closed += (_, _) => share.ListenersChanged -= OnListenersChanged;
        Refresh();
    }

    private void SetEnabled(bool enabled)
    {
        _share.Enabled = enabled;
        _settings.ShareAudio = enabled;
        _settings.Save();
        Refresh();
    }

    private void Renew()
    {
        _share.Token = PcAudioShare.NewToken();
        _settings.ShareToken = _share.Token;
        _settings.Save();
        Refresh();
    }

    private void OnListenersChanged() => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        _details.Visibility = _share.Enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!_share.Enabled) return;
        var url = _share.Url;
        _url.Text = url;
        _qr.Source = QrImage(url);
        var n = _share.ListenerCount;
        _listeners.Text = n == 0 ? "目前沒有手機在收聽" : $"{n} 支手機正在收聽";
    }

    private static BitmapImage QrImage(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
