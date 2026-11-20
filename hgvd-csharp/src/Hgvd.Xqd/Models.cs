using System;
using System.Collections.Generic;

namespace Hgvd.Xqd;

/// <summary>兴趣岛下载的状态词 —— 与红果侧共用同一套中文习惯，别另起一套。</summary>
public static class XqdState
{
    public const string Pending = "待下载";
    public const string Queued = "排队中";
    public const string Resolving = "解析中";
    public const string Downloading = "下载中";
    public const string Muxing = "封装中";
    public const string Verifying = "校验中";
    public const string Paused = "已暂停";
    public const string Done = "已完成";
    public const string Exists = "已存在";
    public const string Failed = "失败";

    /// <summary>进行中：可以被阶段名自由覆盖。</summary>
    public static bool IsRunning(string word) =>
        word is Queued or Resolving or Downloading or Muxing or Verifying;

    /// <summary>终态：不会被后续进度回调覆盖。</summary>
    public static bool IsTerminal(string word) => word is Done or Exists or Failed;

    public static string PhaseWord(string phase) => phase switch
    {
        "解析" => Resolving,
        "下载" => Downloading,
        "封装" => Muxing,
        "校验" => Verifying,
        _ => phase ?? "",
    };
}

/// <summary>一条待下载的课程。由链接清单解析而来，也是界面上的一行。</summary>
public sealed class XqdJob
{
    /// <summary>稳定标识：标题 + URL（URL 变了就是新任务）。</summary>
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Camp { get; set; } = "";

    /// <summary>成品路径（加入队列时算好，下载时按需改扩展名）。</summary>
    public string OutPath { get; set; } = "";

    /// <summary>期望时长（秒）；0 = 不做时长校验。链接清单里可写「时长=mm:ss」。</summary>
    public double ExpectDuration { get; set; }

    // ── 运行期状态（会随队列落盘，重启后非终态一律回到「待下载」）──
    public string State { get; set; } = XqdState.Pending;
    public string Message { get; set; } = "";
    public long Size { get; set; }
    public double Duration { get; set; }
    public double AddedAt { get; set; }

    // ── 仅运行期（进度 / 速度），不落盘 ──
    [System.Text.Json.Serialization.JsonIgnore] public long Down { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public long Total { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public double Speed { get; set; }

    /// <summary>「营 · 课名」，界面显示与日志用。</summary>
    public string Display => string.IsNullOrEmpty(Camp) ? Title : Camp + " · " + Title;

    public static string MakeId(string title, string url) => XqdUtil.Md5Hex16(title + "\n" + url);
}

/// <summary>下载进度快照。</summary>
public sealed class XqdProgress
{
    public long DoneBytes { get; set; }
    public long TotalBytes { get; set; }
    public double DoneDuration { get; set; }
    public double TotalDuration { get; set; }
    /// <summary>"dur"（分片视频，按内容时长算）/ "byte"（直链音频）。</summary>
    public string Unit { get; set; } = "dur";

    public int Percent => TotalDuration > 0
        ? (int)Math.Max(0, Math.Min(100, DoneDuration * 100.0 / TotalDuration))
        : TotalBytes > 0
            ? (int)Math.Max(0, Math.Min(100, DoneBytes * 100.0 / TotalBytes))
            : 0;
}

public sealed class XqdCallbacks
{
    public Action<string> Phase { get; set; }
    public Action<XqdProgress> Progress { get; set; }
    public Action<string> Log { get; set; }

    public void OnPhase(string p) => Phase?.Invoke(p);
    public void OnProgress(XqdProgress p) => Progress?.Invoke(p);
    public void OnLog(string m) => Log?.Invoke(m);
}

/// <summary>暂停 / 取消。暂停时冻结、分片保留；取消抛 OperationCanceledException。</summary>
public sealed class XqdControl
{
    private volatile bool _paused;
    private volatile bool _cancelled;

    public bool Paused { get => _paused; set => _paused = value; }
    public bool Cancelled { get => _cancelled; set => _cancelled = value; }

    public async System.Threading.Tasks.Task WaitIfPausedAsync(
        System.Threading.CancellationToken ct = default)
    {
        while (_paused && !_cancelled && !ct.IsCancellationRequested)
            await System.Threading.Tasks.Task.Delay(120, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (_cancelled) throw new OperationCanceledException("已取消");
    }
}

public sealed class XqdResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string OutPath { get; set; } = "";
    public long Size { get; set; }
    public double Duration { get; set; }
    /// <summary>true = 磁盘上已有完整成品，没下。</summary>
    public bool Existed { get; set; }
}

/// <summary>一次下载的选项。</summary>
public sealed class XqdOptions
{
    /// <summary>下载根目录；实际成品落在 &lt;OutRoot&gt;\兴趣岛\。</summary>
    public string OutRoot { get; set; } = "";
    /// <summary>max / 1080 / 720 / 480（按短边挑流，竖屏课也适用）。</summary>
    public string Quality { get; set; } = "max";
    /// <summary>单节课内部的分片并发。</summary>
    public int SegmentWorkers { get; set; } = 12;
    /// <summary>同时下几节课（网络槽位）。</summary>
    public int LessonWorkers { get; set; } = 2;
    /// <summary>成功后也保留分片（排障用）。</summary>
    public bool KeepParts { get; set; }
}
