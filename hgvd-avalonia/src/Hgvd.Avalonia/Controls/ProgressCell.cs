using Avalonia;
using System;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Hgvd.Avalonia.Controls;

/// <summary>
/// 进度单元格：轨道 + 渐变填充 + **居中**的百分比文字。
///
/// ★ 不用 Avalonia 自带的 ProgressBar ——
///   它的模板部件（PART_Track / PART_Indicator）随版本变动，
///   自己排版最稳：填充宽度 = 百分比 × 实际宽度，文字直接压在上面。
/// </summary>
public sealed class ProgressCell : Panel
{
    public static readonly StyledProperty<double> PercentProperty =
        AvaloniaProperty.Register<ProgressCell, double>(nameof(Percent));

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<ProgressCell, string>(nameof(Text), "");

    public static readonly StyledProperty<IBrush> FillProperty =
        AvaloniaProperty.Register<ProgressCell, IBrush>(nameof(Fill), Brushes.Transparent);

    public static readonly StyledProperty<IBrush> TrackBrushProperty =
        AvaloniaProperty.Register<ProgressCell, IBrush>(nameof(TrackBrush),
            new SolidColorBrush(Color.Parse("#E4EAF3")));

    public static readonly StyledProperty<IBrush> TextBrushProperty =
        AvaloniaProperty.Register<ProgressCell, IBrush>(nameof(TextBrush),
            new SolidColorBrush(Color.Parse("#22364F")));

    public static readonly StyledProperty<double> BarHeightProperty =
        AvaloniaProperty.Register<ProgressCell, double>(nameof(BarHeight), 16);

    private readonly Border _track;
    private readonly Border _indicator;
    private readonly TextBlock _label;
    private static int _diagCount;

    public ProgressCell()
    {
        _track = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = TrackBrush,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _indicator = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Fill,
            // ★★★ 必须是 Stretch，不能是 Left ——
            //   Border 没有子内容，DesiredSize 恒为 0；
            //   Avalonia 排版时 Left 对齐会把宽度收缩到 DesiredSize.Width(=0)
            //   ⇒ 整块渐变填充被压成 0 宽（轨道正常、填充消失，就是这个坑）。
            //   Stretch 则直接吃满 Arrange 给的矩形宽度（= 百分比宽度）。
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _label = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        Children.Add(_track);
        Children.Add(_indicator);
        Children.Add(_label);
    }

    public double Percent
    {
        get => GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IBrush Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public double BarHeight
    {
        get => GetValue(BarHeightProperty);
        set => SetValue(BarHeightProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FillProperty)
        {
            _indicator.Background = Fill;
        }
        else if (change.Property == TrackBrushProperty) _track.Background = TrackBrush;
        else if (change.Property == TextBrushProperty) _label.Foreground = TextBrush;
        else if (change.Property == TextProperty) _label.Text = Text ?? "";
        else if (change.Property == PercentProperty) InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double h = Math.Min(BarHeight, double.IsInfinity(availableSize.Height) ? BarHeight : availableSize.Height);
        double w = Math.Max(0, Math.Min(width, width * Percent / 100.0));

        // ★ 子控件都要显式 Measure：Panel 不会替我们做，
        //   没量过的控件 DesiredSize 为 0 → 对齐时宽度会被压成 0。
        _label.Measure(availableSize);
        _track.Measure(new Size(width, h));
        _indicator.Measure(new Size(w, h));
        return new Size(width, Math.Max(h, _label.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double h = Math.Min(BarHeight, finalSize.Height);
        double top = (finalSize.Height - h) / 2.0;

        _track.Arrange(new Rect(0, top, finalSize.Width, h));
        double w = Math.Max(0, Math.Min(finalSize.Width, finalSize.Width * Percent / 100.0));
        _indicator.Arrange(new Rect(0, top, w, h));
        _label.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
        return finalSize;
    }
}
