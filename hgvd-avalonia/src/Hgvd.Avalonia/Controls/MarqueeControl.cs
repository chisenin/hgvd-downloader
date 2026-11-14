using Avalonia;
using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Hgvd.Avalonia.Controls;

/// <summary>
/// 横向滚动的跑马灯（显示当前正在处理的长标题）。
///
/// ★ 用「TextBlock + 位移」而不是自己 DrawText ——
///   字体、缩放、避头尾都交给 Avalonia 的排版引擎，省心也好看。
/// </summary>
public sealed class MarqueeControl : Panel
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<MarqueeControl, string>(nameof(Text), "");

    public static readonly StyledProperty<double> SpeedProperty =
        AvaloniaProperty.Register<MarqueeControl, double>(nameof(Speed), 1.2);

    public static readonly StyledProperty<IBrush> ForegroundProperty =
        AvaloniaProperty.Register<MarqueeControl, IBrush>(nameof(Foreground),
            new SolidColorBrush(Color.Parse("#8494A8")));

    private readonly TextBlock _label;
    private readonly DispatcherTimer _timer;
    private double _offset;

    public MarqueeControl()
    {
        ClipToBounds = true;

        _label = new TextBlock
        {
            FontSize = 11,
            Foreground = Foreground,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            TextWrapping = TextWrapping.NoWrap,
        };
        Children.Add(_label);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => Advance();
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>每帧位移（像素）。</summary>
    public double Speed
    {
        get => GetValue(SpeedProperty);
        set => SetValue(SpeedProperty, value);
    }

    public IBrush Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            _label.Text = Text ?? "";
            _offset = 0;
            InvalidateArrange();
        }
        else if (change.Property == ForegroundProperty)
        {
            _label.Foreground = Foreground;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    private void Advance()
    {
        double w = _label.Bounds.Width;
        double avail = Bounds.Width;
        if (w <= 0 || avail <= 0) return;

        if (w <= avail)
        {
            if (_offset != 0) { _offset = 0; InvalidateArrange(); }
            return;
        }

        _offset += Speed;
        if (_offset > w) _offset = -w;
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _label.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        var d = _label.DesiredSize;
        return new Size(Math.Min(availableSize.Width, d.Width), d.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var d = _label.DesiredSize;
        _label.Arrange(new Rect(_offset, 0, d.Width, finalSize.Height));
        return finalSize;
    }
}
