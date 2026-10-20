using System.IO;
using Forms = System.Windows.Forms;

namespace AirPlay.App;

/// <summary>Notification-area icon: double-click shows the window; menu to show or exit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(Action show, Action exit)
    {
        using var stream = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("顯示視窗", null, (_, _) => show());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("結束 WindowsAirplay", null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = "WindowsAirplay",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => show();
    }

    /// <summary>Tooltip (max 63 characters).</summary>
    public string Text
    {
        set => _icon.Text = value.Length > 63 ? value[..63] : value;
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(3000, title, text, Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
