using System;

namespace Hgvd.Avalonia.ViewModels;

/// <summary>收藏夹里的一项（左栏一行）。</summary>
public sealed class ShelfRow : NotifyBase
{
    private bool _isChecked;
    private string _title;
    private int _episodeCnt;
    private bool _unavailable;
    private double _titleWidth = 200;

    public ShelfRow(Core.ShelfItem item)
    {
        SeriesId = item.SeriesId;
        Title = item.Title;
        EpisodeCnt = item.EpisodeCnt;
        TypeLabel = item.TypeLabel;
        IsShortDrama = item.IsShortDrama;
        BookType = item.BookType;
        IsChecked = true;
    }

    public string SeriesId { get; }
    public int BookType { get; }
    public string TypeLabel { get; }

    /// <summary>真短剧（bytevc 加密，解码受限）—— 淡化显示并默认不参与下载。</summary>
    public bool IsShortDrama { get; }

    /// <summary>剧名（渐进式读取时是**后补**的，补到就通知界面刷新）。</summary>
    public string Title
    {
        get => _title;
        set
        {
            if (Set(ref _title, value))
            {
                Raise(nameof(Display));
                Raise(nameof(MetaText));
                Raise(nameof(ToolTip));
            }
        }
    }

    /// <summary>集数（点开该剧时随分集一起回填）。</summary>
    public int EpisodeCnt
    {
        get => _episodeCnt;
        set
        {
            if (Set(ref _episodeCnt, value))
            {
                Raise(nameof(MetaText));
                Raise(nameof(ToolTip));
            }
        }
    }

    /// <summary>主标题：标题还没取到时先显示 id，别留白。</summary>
    public string Display => string.IsNullOrEmpty(Title) ? SeriesId : Title;

    /// <summary>
    /// 服务端什么都给不出来（已下架 / 被删）—— 标出来，
    /// 免得用户以为界面坏了。
    /// </summary>
    public bool Unavailable
    {
        get => _unavailable;
        set
        {
            if (Set(ref _unavailable, value))
            {
                Raise(nameof(MetaText));
                Raise(nameof(ToolTip));
            }
        }
    }

    /// <summary>右侧的类型 + 集数（类型用短名，别把剧名挤没了）。</summary>
    public string MetaText
    {
        get
        {
            if (Unavailable) return "已下架";
            string t = IsShortDrama ? "短剧"
                : BookType == 4 ? "单视频"
                : BookType == 3 ? "教学" : "其他";
            return EpisodeCnt > 0 ? $"{t} · {EpisodeCnt} 集" : t;
        }
    }

    public string ToolTip =>
        $"{Display}\n{TypeLabel} · {EpisodeCnt} 集\nid={SeriesId}"
        + (IsShortDrama ? "\n★ 短剧：bytevc 加密，下载后可能无法播放" : "");

    public bool IsChecked
    {
        get => _isChecked;
        set { if (Set(ref _isChecked, value)) Raise(); }
    }

    /// <summary>
    /// 剧名列的宽度 —— 由窗口按实际宽度算好后统一写进每一行。
    /// ★ Avalonia 的 ListBoxItem 默认不拉伸内容，靠算宽度才不会把右侧标签挤出面板。
    /// </summary>
    public double TitleWidth
    {
        get => _titleWidth;
        set => Set(ref _titleWidth, value);
    }

    /// <summary>右侧类型标签的固定宽度（"教学 · 786 集" 差不多就这么宽）。</summary>
    public const double MetaWidth = 66;
}
