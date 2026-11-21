using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hgvd.Avalonia.Controls;

namespace Hgvd.Avalonia.Views;

/// <summary>
/// 「自动抓剧」输入对话框：让用户输入剧 ID（series_id）和可选剧名。
/// ★ 纯代码搭（与 MsgBox 同风格），无需 XAML。
/// </summary>
public sealed class AutoGrabDialog : Window
{
    private readonly TextBox _series;
    private readonly TextBox _title;
    private bool _ok;

    /// <summary>用户点了确定。Callers read SeriesId/DramaTitle after ShowDialog.</summary>
    public bool Confirmed => _ok;
    public string SeriesId => (_series.Text ?? "").Trim();
    public string DramaTitle => (_title.Text ?? "").Trim();

    public AutoGrabDialog(Window owner, string hint = "")
    {
        Title = "自动抓剧（frida hook 雷电）";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        ShowInTaskbar = false;
        Background = Palette.Brush("AppBgBrush");
        Foreground = Palette.Brush("TextDarkBrush");
        FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
        FontSize = 13;

        _series = new TextBox
        {
            Watermark = "剧 ID（series_id），如 7705413803088825369",
            Margin = new Thickness(0, 4, 0, 0),
        };
        _title = new TextBox
        {
            Watermark = "剧名（可留空，抓到后自动填）",
            Margin = new Thickness(0, 4, 0, 0),
        };

        var info = new TextBlock
        {
            Text = "从红果 App 打开一部剧的详情页，地址栏/分享链接里的 series_id 就是它。"
                 + "\n也可直接粘贴形如 dragon8662://detail?series_id=数字 的深链（会自动提取）。"
                 + "\n\n前提：雷电 14 已开 root、frida-server 已启动、红果已登录并停在播放页。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Palette.Brush("HintBrush"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 10),
        };
        if (!string.IsNullOrEmpty(hint))
        {
            info.Text += "\n" + hint;
        }

        var grid = new Grid { RowDefinitions = { }, Margin = new Thickness(20, 18, 20, 6) };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // info
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // 剧ID label
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // 剧ID box
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // 剧名 label
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); // 剧名 box

        grid.Children.Add(info);

        var lblSeries = new TextBlock { Text = "剧 ID / 深链", FontWeight = FontWeight.SemiBold };
        grid.Children.Add(lblSeries); Grid.SetRow(lblSeries, 1);
        grid.Children.Add(_series); Grid.SetRow(_series, 2);

        var lblTitle = new TextBlock { Text = "剧名（可选）", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) };
        grid.Children.Add(lblTitle); Grid.SetRow(lblTitle, 3);
        grid.Children.Add(_title); Grid.SetRow(_title, 4);

        var ok = new Button { Content = "开始抓剧", Classes = { "primary" }, MinWidth = 100 };
        var cancel = new Button { Content = "取消", Classes = { "modern" }, MinWidth = 88 };
        ok.Click += (_, _) => { if (NormalizeSeries(SeriesId).Length == 0) return; _ok = true; Close(); };
        cancel.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var footer = new Border
        {
            Background = Palette.Brush("StatusBgBrush"),
            BorderBrush = Palette.Brush("CardBorderBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 10),
            Child = buttons,
        };

        var root = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) } };
        root.Children.Add(grid);
        root.Children.Add(footer);
        Grid.SetRow(footer, 1);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && NormalizeSeries(SeriesId).Length > 0)
            { _ok = true; Close(); e.Handled = true; }
        };
    }

    /// <summary>把深链里的 series_id 抠出来（支持纯数字与 dragon8662://detail?series_id=...）。</summary>
    public static string NormalizeSeries(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0) return "";
        var m = System.Text.RegularExpressions.Regex.Match(raw, @"series_id[=%3D]+(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        return System.Text.RegularExpressions.Regex.IsMatch(raw, @"^\d+$") ? raw : "";
    }
}
