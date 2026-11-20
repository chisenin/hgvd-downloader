using System;
using Avalonia.Media;
using Hgvd.Avalonia.Controls;
using Hgvd.Avalonia.ViewModels;
using Hgvd.Xqd;

namespace Hgvd.Avalonia.Views;

/// <summary>兴趣岛下载清单里的一行（包一个 <see cref="XqdJob"/>）。</summary>
public sealed class XqdRow : NotifyBase
{
    private string _state = XqdState.Pending;
    private string _message = "";
    private long _down;
    private long _total;
    private long _size;
    private double _duration;
    private double _speed;
    private double _titleWidth = 240;

    public XqdRow(XqdJob job)
    {
        Job = job;
        _state = job.State;
        _message = job.Message;
        _down = job.Down;
        _total = job.Total;
        _size = job.Size;
        _duration = job.Duration;
        _speed = job.Speed;
    }

    public XqdJob Job { get; }

    public string Title => Job.Title;
    public string Camp => Job.Camp;
    public string Display => Job.Display;

    public string State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(StateWord));
            Raise(nameof(ShowPill));
            Raise(nameof(StatePillBg));
            Raise(nameof(StatePillBorder));
            Raise(nameof(StatePillFg));
            Raise(nameof(ProgressFill));
        }
    }

    /// <summary>
    /// 状态词：**待下载也显示**（兴趣岛这边一进来全是新任务，
    /// 空着看不出是「还没开始」还是「没读到状态」）。
    /// </summary>
    public string StateWord => string.IsNullOrEmpty(State) ? XqdState.Pending : State;
    public bool ShowPill => true;

    public IBrush StatePillBg => Palette.FromHex(State switch
    {
        XqdState.Done or XqdState.Exists => "#EAF7EE",
        XqdState.Downloading or XqdState.Resolving => "#E8F0FA",
        XqdState.Muxing or XqdState.Verifying or XqdState.Queued => "#FDF6E7",
        XqdState.Paused => "#FDF1E3",
        XqdState.Failed => "#FDEEEE",
        _ => "#EEF2F8",
    });

    public IBrush StatePillBorder => Palette.FromHex(State switch
    {
        XqdState.Done or XqdState.Exists => "#B8E0C4",
        XqdState.Downloading or XqdState.Resolving => "#B9CCE4",
        XqdState.Muxing or XqdState.Verifying or XqdState.Queued => "#EBD9A8",
        XqdState.Paused => "#E8C79A",
        XqdState.Failed => "#F0C2C2",
        _ => "#DCE3EE",
    });

    public IBrush StatePillFg => Palette.FromHex(State switch
    {
        XqdState.Done or XqdState.Exists => "#1F7A3D",
        XqdState.Downloading or XqdState.Resolving => "#3B6EA5",
        XqdState.Muxing or XqdState.Verifying or XqdState.Queued => "#8A6D1A",
        XqdState.Paused => "#C07B2A",
        XqdState.Failed => "#B03030",
        _ => "#5A6B82",
    });

    public string Message
    {
        get => _message;
        set { if (Set(ref _message, value)) Raise(nameof(ToolTip)); }
    }

    public long Down
    {
        get => _down;
        set
        {
            if (!Set(ref _down, value)) return;
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

    public long Size
    {
        get => _size;
        set { if (Set(ref _size, value)) Raise(nameof(SizeText)); }
    }

    public double Duration
    {
        get => _duration;
        set { if (Set(ref _duration, value)) Raise(nameof(DurationText)); }
    }

    public double Speed
    {
        get => _speed;
        set { if (Set(ref _speed, value)) Raise(nameof(SpeedText)); }
    }

    public int Percent => Total > 0
        ? (int)Math.Min(100, Down * 100 / Total)
        : 0;

    public string ProgressText => Total > 0 ? $"{Percent}%" : "";

    /// <summary>
    /// 时长：优先用下载后探测到的真实时长；还没下时用清单里写的期望时长
    /// （链接后面加 [48:12] 或「时长=2892」就能在下载前显示）。
    /// </summary>
    public string DurationText
    {
        get
        {
            double d = Duration > 0 ? Duration : Job.ExpectDuration;
            return d > 0 ? Hgvd.Core.Util.FmtDuration(d) : "--:--";
        }
    }

    public string SizeText
    {
        get
        {
            if (Total > 0) return $"{Hgvd.Core.Util.FmtSize(Down)} / {Hgvd.Core.Util.FmtSize(Total)}";
            if (Size > 0) return Hgvd.Core.Util.FmtSize(Size);
            return "--";
        }
    }

    public string SpeedText => Speed > 0 ? Hgvd.Core.Util.FmtSize((long)Speed) + "/s" : "--";

    public IBrush ProgressFill => State == XqdState.Done
        ? Palette.Brush("ProgressFillGreen")
        : Palette.Brush("ProgressFillBlue");

    public string ToolTip
    {
        get
        {
            string tip = string.IsNullOrEmpty(Job.Url) ? Display : $"{Display}\n{Job.Url}";
            if (!string.IsNullOrEmpty(Message)) tip += "\n" + Message;
            return tip;
        }
    }

    /// <summary>标题列宽度（表头与数据行共用）。</summary>
    public double TitleWidth
    {
        get => _titleWidth;
        set => Set(ref _titleWidth, value);
    }

    /// <summary>把 Job 的最新运行期数据同步到界面属性。</summary>
    public void SyncFromJob()
    {
        State = Job.State;
        Message = Job.Message;
        Down = Job.Down;
        Total = Job.Total;
        Size = Job.Size;
        Duration = Job.Duration;
        Speed = Job.Speed;
    }
}
