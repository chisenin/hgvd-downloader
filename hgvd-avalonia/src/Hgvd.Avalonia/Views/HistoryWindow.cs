using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Hgvd.Avalonia.Controls;
using Hgvd.Core;

namespace Hgvd.Avalonia.Views;

/// <summary>
/// 下载历史窗口（两源共用 <c>history.json</c>）。
/// ★ 纯代码搭：一张只读表 + 关闭，没必要再开一个 XAML。
/// </summary>
public sealed class HistoryWindow : Window
{
    public HistoryWindow(IReadOnlyList<HistoryEntry> items)
    {
        Title = $"下载历史（{items.Count} 条）";
        Width = 860;
        Height = 560;
        MinWidth = 680;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Palette.Brush("AppBgBrush");
        Foreground = Palette.Brush("TextDarkBrush");
        FontFamily = new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
        FontSize = 13;

        var head = new TextBlock
        {
            Text = $"共 {items.Count} 条 · 最新在前 · 同一成品只保留一条",
            Margin = new Thickness(16, 12, 16, 6),
            Foreground = Palette.Brush("MutedBrush"),
            FontSize = 12,
        };

        var header = Row("时间", "来源", "标题", "大小", isHeader: true);
        header.Margin = new Thickness(16, 0, 16, 2);
        header.Background = Palette.Brush("StatusBgBrush");

        var list = new ListBox
        {
            ItemsSource = items,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(16, 0, 16, 0),
        };
        list.ItemTemplate = new FuncDataTemplate<HistoryEntry>((e, _) =>
        {
            var g = Row(e.AtText, e.SourceText, e.TitleText, e.SizeText, isHeader: false);
            ToolTip.SetTip(g, e.Path);
            return g;
        });

        var empty = new TextBlock
        {
            Text = "还没有下载记录。下载成功后会出现在这里。",
            Margin = new Thickness(16, 24, 16, 0),
            Foreground = Palette.Brush("MutedBrush"),
            IsVisible = items.Count == 0,
        };

        var close = new Button
        {
            Content = "关闭",
            Classes = { "primary" },
            MinWidth = 88,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 16, 12),
        };
        close.Click += (_, _) => Close();

        var body = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"),
        };
        Grid.SetRow(head, 0);
        Grid.SetRow(header, 1);
        Grid.SetRow(list, 3);
        Grid.SetRow(empty, 3);
        Grid.SetRow(close, 4);
        body.Children.Add(head);
        body.Children.Add(header);
        body.Children.Add(list);
        body.Children.Add(empty);
        body.Children.Add(close);
        Content = body;
    }

    private static Grid Row(string time, string source, string title, string size, bool isHeader)
    {
        var g = new Grid { Height = isHeader ? 26 : 28, ColumnDefinitions = new ColumnDefinitions("96,64,*,88") };

        var t = new TextBlock { Text = time, VerticalAlignment = VerticalAlignment.Center, FontSize = isHeader ? 12 : 12.5 };
        var s = new TextBlock { Text = source, VerticalAlignment = VerticalAlignment.Center, FontSize = isHeader ? 12 : 12.5 };
        var ti = new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = isHeader ? 12 : 12.5,
        };
        var sz = new TextBlock
        {
            Text = size,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = isHeader ? 12 : 12.5,
            Foreground = isHeader ? Palette.Brush("TextDarkBrush") : Palette.Brush("MutedBrush"),
        };
        if (isHeader)
        {
            t.FontWeight = FontWeight.SemiBold;
            s.FontWeight = FontWeight.SemiBold;
            ti.FontWeight = FontWeight.SemiBold;
            sz.FontWeight = FontWeight.SemiBold;
        }
        else
        {
            t.Foreground = Palette.Brush("MutedBrush");
            s.Foreground = Palette.Brush("MutedBrush");
        }

        Grid.SetColumn(t, 0);
        Grid.SetColumn(s, 1);
        Grid.SetColumn(ti, 2);
        Grid.SetColumn(sz, 3);
        g.Children.Add(t);
        g.Children.Add(s);
        g.Children.Add(ti);
        g.Children.Add(sz);
        return g;
    }
}
