using System;
using System.Collections.Generic;
using Hgvd.Core;

namespace Hgvd.Xqd;

/// <summary>
/// 兴趣岛的状态词 —— 只是 <see cref="JobState"/> 的转发，**不要在这里加词**。
///
/// ★ 两源共用一套状态词：红果、兴趣岛的行在同一张清单、同一套筛选里，
///   各自定义就会出现「红果的『下载』不算下载中」这种同表不同语义。
/// ★ 保留这个类是为了既有调用（XqdQueue / 界面 / 回归）不必改名。
/// </summary>
public static class XqdState
{
    public const string Pending = JobState.Pending;
    public const string Queued = JobState.Queued;
    public const string Resolving = JobState.Resolving;
    public const string Downloading = JobState.Downloading;
    public const string Muxing = JobState.Muxing;
    public const string Decrypting = JobState.Decrypting;
    public const string Verifying = JobState.Verifying;
    public const string Paused = JobState.Paused;
    public const string Done = JobState.Done;
    public const string Exists = JobState.Exists;
    public const string Failed = JobState.Failed;

    /// <summary>进行中：可以被阶段名自由覆盖。</summary>
    public static bool IsRunning(string word) => JobState.IsRunning(word);

    /// <summary>终态：不会被后续进度回调覆盖。</summary>
    public static bool IsTerminal(string word) => JobState.IsTerminal(word);

    public static string PhaseWord(string phase) => JobState.PhaseWord(phase);
}

/// <summary>一个营（我的课程里的一个合集）。对应兴趣岛 myCourseListV2 的一项。</summary>
public sealed class Camp
{
    public string Name { get; set; } = "";
    public string CampId { get; set; } = "";
    /// <summary>公开课没有 campId，只有 periodId（走 openCourse 端点）。</summary>
    public string PeriodId { get; set; } = "";
    public string SkuType { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Count { get; set; }
    public string Badge => Count > 0 ? $"{Kind} · {Count} 节" : Kind;
    public override string ToString() => $"<Camp {Name} ({Count})>";
}

/// <summary>一节课（课表里的一行）。</summary>
public sealed class Lesson
{
    /// <summary>营内顺序，0 起；成品名里的序号 = Index + 1。</summary>
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public string When { get; set; } = "";
    public string WhenDate { get; set; } = "";
    public double Duration { get; set; }
    /// <summary>VIDEO / INTELLIGENT_LIVE / AUDIO / OPEN_COURSE。</summary>
    public string Type { get; set; } = "";
    public string TopicId { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string CampName { get; set; } = "";
    public string CampId { get; set; } = "";
    public string PeriodId { get; set; } = "";
}

/// <summary>一条待下载的课程。来源可以是粘贴的链接，也可以是自动发现的课表。</summary>
public sealed class XqdJob
{
    /// <summary>稳定标识：标题 + 营 + topicId + URL。</summary>
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>直链 / 清单地址。课表来的任务这里为空，下载时再解析。</summary>
    public string Url { get; set; } = "";
    public string Camp { get; set; } = "";

    // ── 课表来源（有 TopicId 或 CampId/PeriodId 即视为课表任务）──
    public string CampId { get; set; } = "";
    public string PeriodId { get; set; } = "";
    public string TopicId { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string LessonType { get; set; } = "";
    /// <summary>营内序号（1 起）；0 = 链接任务。</summary>
    public int Seq { get; set; }
    public string WhenDate { get; set; } = "";

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

    /// <summary>true = 课表任务（需要先用 API 解析播放地址）。</summary>
    public bool FromCatalog => !string.IsNullOrEmpty(TopicId) || !string.IsNullOrEmpty(CampId)
                               || !string.IsNullOrEmpty(PeriodId);

    /// <summary>「营 · 课名」，界面显示与日志用。</summary>
    public string Display => string.IsNullOrEmpty(Camp) ? Title : Camp + " · " + Title;

    public static string MakeId(string title, string url, string camp = "", string topicId = "")
        => XqdUtil.Md5Hex16((title ?? "") + "\n" + (url ?? "") + "\n" + (camp ?? "") + "\n" + (topicId ?? ""));
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
public sealed class XqdControl : IPauseToken
{
    private readonly IPauseToken _shared;
    private volatile bool _paused;
    private volatile bool _cancelled;

    /// <summary>
    /// <paramref name="shared"/> 不为空时，暂停状态以它为准（两源共用一个 PauseGate）；
    /// 为空时退回自己的布尔闸门（老的独立运行路径 / 回归）。
    /// </summary>
    public XqdControl(IPauseToken shared = null)
    {
        _shared = shared;
        if (shared is Hgvd.Core.Engine.PauseGate) _paused = shared.IsPaused;
    }

    public bool Paused
    {
        get => _shared?.IsPaused ?? _paused;
        set
        {
            _paused = value;
            if (_shared is Hgvd.Core.Engine.PauseGate gate)
            {
                if (value) gate.Pause(); else gate.Resume();
            }
        }
    }
    public bool Cancelled { get => _cancelled; set => _cancelled = value; }
    public bool IsPaused => Paused;

    /// <summary>Core 单文件引擎用的接口；与 <see cref="WaitIfPausedAsync"/> 等价。</summary>
    public System.Threading.Tasks.Task WaitAsync(
        System.Threading.CancellationToken ct = default) => WaitIfPausedAsync(ct);

    public async System.Threading.Tasks.Task WaitIfPausedAsync(
        System.Threading.CancellationToken ct = default)
    {
        if (_shared != null)
        {
            await _shared.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (_cancelled) throw new OperationCanceledException("已取消");
            return;
        }
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
