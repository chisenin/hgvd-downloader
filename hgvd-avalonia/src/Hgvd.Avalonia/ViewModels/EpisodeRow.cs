using System;
using Avalonia.Media;
using Hgvd.Avalonia.Controls;

namespace Hgvd.Avalonia.ViewModels;

/// <summary>一集（右栏一行）。</summary>
public sealed class EpisodeRow : NotifyBase
{
    private string _state = "待下载";
    private long _down;
    private long _total;
    private long _planSize;
    private double _speed;
    private string _error = "";
    private double _titleWidth = 240;
    private readonly int _groupCount;

    public EpisodeRow(Core.Api.Episode ep)
    {
        Vid = ep.Vid;
        Index = ep.Index;
        Title = ep.Title;
        Duration = ep.Duration;
        SeriesId = ep.SeriesId;
        SeriesTitle = ep.SeriesTitle;
        VideoIdType = ep.VideoIdType;
    }

    private EpisodeRow(string seriesId, string title, int count)
    {
        IsGroupHeader = true;
        Vid = "";
        Index = 0;
        Title = title;
        Duration = 0;
        SeriesId = seriesId;
        SeriesTitle = title;
        VideoIdType = 0;
        _groupCount = count;
    }

    /// <summary>
    /// 分组标题行（「某剧 · 共 N 集」）。
    /// ★ 勾选多部剧时，右栏是**累计清单**，靠这行把每部剧分开，
    ///   免得几部剧的分集混在一起分不清序号属于谁。
    /// </summary>
    public static EpisodeRow Header(string seriesId, string title, int count)
        => new EpisodeRow(seriesId, title, count);

    public string Vid { get; }
    public int Index { get; }
    public string Title { get; }
    public double Duration { get; }
    public string SeriesId { get; }
    public string SeriesTitle { get; }
    public int VideoIdType { get; }

    /// <summary>true = 分组标题行（不是真实分集，下载时要跳过）。</summary>
    public bool IsGroupHeader { get; }
    public bool IsEpisode => !IsGroupHeader;

    public string HeaderText => $"{SeriesTitle} · 共 {_groupCount} 集";

    public string State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                Raise(nameof(StateWord));
                Raise(nameof(ShowPill));
                Raise(nameof(StatePillBg));
                Raise(nameof(StatePillBorder));
                Raise(nameof(ProgressFill));
            }
        }
    }

    public string StateWord => State == "待下载" ? "" : State;

    /// <summary>待下载时状态词是空串 —— 把整个胶囊收起来，别留一个空的小圆角块。</summary>
    public bool ShowPill => !string.IsNullOrEmpty(StateWord);

    public IBrush StatePillBg => Palette.FromHex(State switch
    {
        "已完成" => "#EAF7EE",
        "下载中" => "#E8F0FA",
        "失败" => "#FDEEEE",
        _ => "#EEF2F8",
    });

    public IBrush StatePillBorder => Palette.FromHex(State switch
    {
        "已完成" => "#B8E0C4",
        "下载中" => "#B9CCE4",
        "失败" => "#F0C2C2",
        _ => "#DCE3EE",
    });

    public long Down
    {
        get => _down;
        set
        {
            if (!Set(ref _down, value)) return;
            // ★★ 必须把**依赖属性**一起通知 —— 界面绑的是 SizeText / ProgressText，
            //   只通知 "Down" 的话永远不会重新求值，列就一直是空的。
            Raise(nameof(ProgressText));
            Raise(nameof(Percent));
            Raise(nameof(SizeText));
        }
    }

    public long Total
    {
        get => _total;
        set
        {
            if (!Set(ref _total, value)) return;
            Raise(nameof(ProgressText));
            Raise(nameof(Percent));
            Raise(nameof(SizeText));
        }
    }

    /// <summary>
    /// 预计总大小（播放地址解析后已知，下载前就能显示「≈ 29.1 MB」）。
    /// </summary>
    public long PlanSize
    {
        get => _planSize;
        set { if (Set(ref _planSize, value)) Raise(nameof(SizeText)); }
    }

    public double Speed
    {
        get => _speed;
        set { if (Set(ref _speed, value)) Raise(nameof(SpeedText)); }
    }

    public string Error
    {
        get => _error;
        set { if (Set(ref _error, value)) Raise(); }
    }

    public int Percent => Total > 0 ? (int)Math.Min(100, Down * 100 / Total) : 0;

    public string ProgressText => Total > 0 ? $"{Percent}%" : "";

    public string DurationText => Duration > 0
        ? TimeSpan.FromSeconds(Duration).ToString(@"mm\:ss") : "--:--";

    /// <summary>
    /// 大小列：下载中显示「已下 / 总计」，下载前显示预计大小，都拿不到就显示占位符
    /// ★★ 不要返回空串 —— 空白会被当成「没取到数」，占位符才看得出是正常状态。
    /// </summary>
    public string SizeText
    {
        get
        {
            if (Total > 0) return $"{Core.Util.FmtSize(Down)} / {Core.Util.FmtSize(Total)}";
            if (PlanSize > 0) return $"≈ {Core.Util.FmtSize(PlanSize)}";
            return "--";
        }
    }

    public string SpeedText => Speed > 0 ? $"{Core.Util.FmtSize((long)Speed)}/s" : "--";

    /// <summary>进度条填充：已完成走绿色渐变，其余走蓝色渐变。</summary>
    public IBrush ProgressFill => State == "已完成"
        ? Palette.Brush("ProgressFillGreen")
        : Palette.Brush("ProgressFillBlue");

    /// <summary>
    /// 标题列的宽度。
    /// ★ 由窗口按实际宽度算好后统一写进每一行 ——
    ///   表头和数据行用同样的列定义 + 同样的宽度 → 两边永远对齐。
    /// </summary>
    public double TitleWidth
    {
        get => _titleWidth;
        set => Set(ref _titleWidth, value);
    }
}
