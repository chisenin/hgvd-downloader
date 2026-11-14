using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Hgvd.Avalonia.Controls;

namespace Hgvd.Avalonia.Views;

/// <summary>
/// 轻量对话框（Avalonia 没有内置 MessageBox）。
/// ★ 纯代码搭，不走 XAML —— 就两个按钮，没必要再开一个文件。
/// </summary>
public static class MsgBox
{
    /// <summary>确认框：返回 true = 确定，false = 取消。</summary>
    public static async Task<bool> ConfirmAsync(Window owner, string title, string message)
    {
        bool result = false;
        var win = BuildDialog(owner, title, message, out var okBtn);
        okBtn.Click += (_, _) => { result = true; win.Close(); };
        await win.ShowDialog(owner);
        return result;
    }

    /// <summary>提示框（只有一个「确定」）。</summary>
    public static async Task InfoAsync(Window owner, string title, string message)
    {
        var win = BuildDialog(owner, title, message, out var okBtn);
        okBtn.Click += (_, _) => win.Close();
        await win.ShowDialog(owner);
    }

    private static Window BuildDialog(Window owner, string title, string message, out Button okButton)
    {
        var win = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            CanResize = false,
            ShowInTaskbar = false,
            Background = Palette.Brush("AppBgBrush"),
            Foreground = Palette.Brush("TextDarkBrush"),
            FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI"),
            FontSize = 13,
        };

        var msg = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20, 18, 20, 14),
        };

        okButton = new Button { Content = "确定", Classes = { "primary" }, MinWidth = 88 };
        var cancel = new Button { Content = "取消", Classes = { "modern" }, MinWidth = 88 };
        cancel.Click += (_, _) => win.Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(okButton);

        var footer = new Border
        {
            Background = Palette.Brush("StatusBgBrush"),
            BorderBrush = Palette.Brush("CardBorderBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 10),
            Child = buttons,
        };

        var root = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) } };
        root.Children.Add(msg);
        root.Children.Add(footer);
        Grid.SetRow(footer, 1);
        win.Content = root;

        // 回车 = 确定，Esc = 取消
        win.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { win.Close(); e.Handled = true; }
        };
        return win;
    }
}
