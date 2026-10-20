using System.Windows;
using System.Windows.Controls;

namespace AirPlay.App;

/// <summary>Asks for the name shown in the iPhone's Screen Mirroring list.</summary>
public sealed class RenameDialog : Window
{
    private readonly TextBox _box;

    public string NewName => _box.Text.Trim();

    public RenameDialog(string currentName)
    {
        Title = "重新命名";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _box = new TextBox { Text = currentName, Width = 260, Margin = new Thickness(0, 8, 0, 12), MaxLength = 40 };
        var ok = new Button { Content = "確定", Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Width = 80, IsCancel = true };
        ok.Click += (_, _) =>
        {
            if (NewName.Length == 0 || NewName.IndexOfAny(['.', '@']) >= 0)
            {
                MessageBox.Show(this, "名稱不能是空的，也不能包含「.」或「@」。", Title);
                return;
            }
            DialogResult = true;
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "iPhone「螢幕鏡像輸出」清單裡顯示的名稱：" });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }
}
