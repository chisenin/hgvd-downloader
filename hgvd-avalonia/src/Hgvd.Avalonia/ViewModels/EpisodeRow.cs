using System;
using Avalonia.Media;
using Hgvd.Avalonia.Controls;
using Hgvd.Xqd;

namespace Hgvd.Avalonia.ViewModels;

/// <summary>下载清单里的一行：红果的一集，或兴趣岛的一节课（两源共用一张表）。</summary>
public sealed class EpisodeRow : NotifyBase
{
    private string _state = "待下载";
    private long _down;
    private long _total;
    private long _planSize;
    private double _speed;
    private string _error = "";
    private double _titleWidth = 240;
    private double _duration;
    private readonly int _groupCount;

    public EpisodeRow(Core.Api.Episode ep)
    {
        Vid = ep.Vid;
        Index = ep.Index;
        Title = ep.Title;
        _duration = ep.Duration;
        SeriesId = ep.SeriesId;
        SeriesTitle = ep.SeriesTitle;
        VideoIdType = ep.VideoIdType;
    }

    /// <summary>兴趣岛课表 / 链接任务。</summary>
    public EpisodeRow(XqdJob job)
    {
        Xqd = job;
        Vid = "";
        Index = job.Seq;
        Title = job.Title;
        _duration = job.ExpectDuration;
        SeriesId = "xqd:" + (job.Camp ?? "");
        SeriesTitle = job.Camp ?? "";
        VideoIdType = 0;
        _state = job.State;
        if (job.Size > 0) _planSize = job.Size;
    }

    private EpisodeRow(string seriesId, string title, int count)
    {
        IsGroupHeader = true;
        Vid = "";
        Index = 0;
        Title = title;
        _duration = 0;
        SeriesId = seriesId;
        SeriesTitle = title;
        VideoIdType = 0;
        _groupCount = count;
    }

    /// <summary>
    /// 分组标题行（「某剧 · 共 N 集」/「某营 · 共 N 节」）。
    /// ★ 勾选多部剧时，右栏是**累计清单**，靠这行把每部剧分开，
    ///   免得几部剧的分集混在一起分不清序号属于谁。
    /// </summary>
    public static EpisodeRow Header(string seriesId, string title, int count)
        => new EpisodeRow(seriesId, title, count) { IsCampHeader = seriesId.StartsWith("xqd:", StringComparison.Ordinal) };

    public static EpisodeRow FromXqd(XqdJob job) => new EpisodeRow(job);

    public string Vid { get; }
    public int Index { get; }
    public string Title { get; }
    public double Duration => _duration;
    public string SeriesId { get; }
    public string SeriesTitle { get; }
    public int VideoIdType { get; }

    /// <summary>非空 = 这一行来自兴趣岛（下载走 XqdQueue）。</summary>
    public XqdJob Xqd { get; }
    public bool IsXqd => Xqd != null;

    /// <summary>来源列：红果 / 兴趣岛（分组标题行留空）。</summary>
    public string SourceText => IsGroupHeader ? "" : (IsXqd ? "兴趣岛" : "红果");

    /// <summary>true = 分组标题行（不是真实分集，下载时要跳过）。</summary>
    public bool IsGroupHeader { get; }
    public bool IsEpisode => !IsGroupHeader;
    public bool IsCampHeader { get; private set; }

    public string HeaderText => $"{SeriesTitle} · 共 {_groupCount} {(IsCampHeader ? "节" : "集")}";

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
                Raise(nameof(EtaText));
            }
        }
    }

    /// <summary>
    /// 待下载时状态词是空串 —— 把整个胶囊收起来，别留一个空的小圆角块。
    /// ★ 兴趣岛任务例外：新加的课全是「待下载」，空着看不出是没开始还是没读到状态。
    /// </summary>
    public string StateWord => State == "待下载" && !IsXqd ? "" : State;

    public bool ShowPill => !string.IsNullOrEmpty(StateWord);

    public IBrush StatePillBg => Palette.FromHex(State switch
    {
        "已完成" or "已存在" => "#EAF7EE",
        "下载中" or "解析中" => "#E8F0FA",
        "封装中" or "校验中" or "排队中" => "#FDF6E7",
        "已暂停" => "#FDF1E3",
        "失败" => "#FDEEEE",
        _ => "#EEF2F8",
    });

    public IBrush StatePillBorder => Palette.FromHex(State switch
    {
        "已完成" or "已存在" => "#B8E0C4",
        "下载中" or "解析中" => "#B9CCE4",
        "封装中" or "校验中" or "排队中" => "#EBD9A8",
        "已暂停" => "#E8C79A",
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
            Raise(nameof(EtaText));
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
            Raise(nameof(EtaText));
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
        set
        {
            if (!Set(ref _speed, value)) return;
            Raise(nameof(SpeedText));
            Raise(nameof(EtaText));
        }
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

    /// <summary>
    /// 剩余时间列：按「剩余字节 / 当前速度」估算；没速度或已结束就显示占位符。
    /// ★ 只作参考 —— 速度会波动，别拿它当承诺。
    /// </summary>
    public string EtaText
    {
        get
        {
            if (State is "已完成" or "已存在" or "失败" or "待下载") return "--";
            if (Total <= Down || Speed < 1024) return "--";
            double sec = (Total - Down) / Speed;
            if (sec <= 0 || sec > 359999) return "--";
            var ts = TimeSpan.FromSeconds(sec);
            return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
        }
    }

    /// <summary>进度条填充：已完成走绿色渐变，其余走蓝色渐变。</summary>
    public IBrush ProgressFill => State is "已完成" or "已存在"
        ? Palette.Brush("ProgressFillGreen")
        : Palette.Brush("ProgressFillBlue");

    /// <summary>把兴趣岛任务的最新运行期数据同步到界面（状态机在 XqdQueue 里）。</summary>
    public void SyncFromXqd()
    {
        if (Xqd == null) return;
        if (State != Xqd.State) State = Xqd.State;
        if (Down != Xqd.Down) Down = Xqd.Down;
        if (Total != Xqd.Total) Total = Xqd.Total;
        if (Math.Abs(Speed - Xqd.Speed) > 0.5) Speed = Xqd.Speed;
        Error = Xqd.Message;
        if (Xqd.Size > 0 && PlanSize != Xqd.Size) PlanSize = Xqd.Size;
        if (Xqd.Duration > 0 && Math.Abs(_duration - Xqd.Duration) > 0.5)
        {
            _duration = Xqd.Duration;
            Raise(nameof(DurationText));
        }
    }

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
