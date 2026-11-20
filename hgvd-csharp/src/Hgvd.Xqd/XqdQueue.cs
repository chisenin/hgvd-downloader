using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hgvd.Xqd.Engine;

namespace Hgvd.Xqd;

/// <summary>
/// 兴趣岛下载队列 —— 链接清单 → 排队 → 并发下载 → 落盘。
///
/// ★ 课与课之间并发 <see cref="XqdOptions.LessonWorkers"/>（默认 2），
///   课内分片并发由引擎管（默认 12）。
/// ★ 进度回调来自多个线程，这里统一**限流到约 10 次/秒**，避免把 UI 线程灌满。
/// ★ 只有状态变化才落盘；进度不落盘（分片本身在磁盘上，重启后靠文件存在即跳过）。
/// </summary>
public sealed class XqdQueue
{
    private const int MinExistingBytes = 10 * 1024;

    private readonly XqdStore _store;
    private readonly List<XqdJob> _jobs = new();
    private readonly object _gate = new();
    // ★ 多节课并发下载时 OnProgress 来自不同线程，普通 Dictionary 会写坏 → 必须并发容器
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _lastEmitTicks = new();

    private XqdControl _control;
    private bool _running;

    /// <summary>兴趣岛 API（课表任务解析播放地址用）；未授权时为 null。</summary>
    private Api.QianliaoPlatform _platform;

    public XqdQueue(XqdOptions options, XqdStore store = null)
    {
        Options = options ?? new XqdOptions();
        _store = store ?? new XqdStore();
    }

    public XqdOptions Options { get; }
    public XqdStore Store => _store;

    /// <summary>
    /// 装配兴趣岛授权（Cookie）。课表任务靠它解析播放地址；
    /// 只粘链接的任务不需要。
    /// </summary>
    public void Configure(string cookie)
    {
        _platform = string.IsNullOrEmpty(cookie) ? null : new Api.QianliaoPlatform(cookie);
    }

    public bool HasAuth => _platform != null;

    /// <summary>已装配的 API（营列表 / 课表用；未授权时为 null）。</summary>
    public Api.QianliaoPlatform Platform => _platform;

    /// <summary>兴趣岛成品的根目录（&lt;下载目录&gt;\兴趣岛）。</summary>
    public string OutDir => Path.Combine(Options.OutRoot ?? "", "兴趣岛");

    public IReadOnlyList<XqdJob> Jobs
    {
        get { lock (_gate) return _jobs.ToList(); }
    }

    public bool Running => _running;

    public event Action<XqdJob> JobChanged;
    public event Action<XqdJob, XqdProgress> JobProgress;
    public event Action<string> Log;

    // ══════════════════════════════════════════════════════════════
    //  清单维护
    // ══════════════════════════════════════════════════════════════

    /// <summary>加入清单，返回真正新增的条数（重复的按 Id 忽略）。</summary>
    public int Add(IEnumerable<LinkItem> items)
    {
        int added = 0;
        lock (_gate)
        {
            foreach (var it in items)
            {
                if (!it.HasUrl) continue;
                string id = XqdJob.MakeId(it.Title, it.Url, it.Camp, "");
                if (_jobs.Any(j => j.Id == id)) continue;

                var job = new XqdJob
                {
                    Id = id,
                    Title = string.IsNullOrWhiteSpace(it.Title) ? "未命名" : it.Title.Trim(),
                    Url = it.Url.Trim(),
                    Camp = (it.Camp ?? "").Trim(),
                    ExpectDuration = it.Duration,
                    AddedAt = XqdStore.Now(),
                    State = XqdState.Pending,
                };
                job.OutPath = OutPathFor(job);
                _jobs.Add(job);
                added++;
            }
        }
        if (added > 0) Save();
        return added;
    }

    public void Remove(XqdJob job)
    {
        lock (_gate) _jobs.Remove(job);
        Save();
    }

    /// <summary>
    /// 把一个营的课表加入清单（自动发现路径）。
    /// 按「营 + topicId」去重，重复勾选同一个营不会翻倍。
    /// </summary>
    public int AddLessons(Camp camp, IEnumerable<Lesson> lessons)
    {
        int added = 0;
        lock (_gate)
        {
            foreach (var l in lessons)
            {
                if (string.IsNullOrEmpty(l.TopicId) && string.IsNullOrEmpty(l.RoomId)) continue;
                string campName = camp?.Name ?? l.CampName ?? "";
                string id = XqdJob.MakeId(l.Title, "", campName, l.TopicId);
                if (_jobs.Any(j => j.Id == id)) continue;

                var job = new XqdJob
                {
                    Id = id,
                    Title = l.Title,
                    Camp = campName,
                    CampId = !string.IsNullOrEmpty(l.CampId) ? l.CampId : camp?.CampId ?? "",
                    PeriodId = !string.IsNullOrEmpty(l.PeriodId) ? l.PeriodId : camp?.PeriodId ?? "",
                    TopicId = l.TopicId,
                    RoomId = l.RoomId,
                    LessonType = l.Type,
                    Seq = l.Index + 1,
                    WhenDate = l.WhenDate,
                    ExpectDuration = l.Duration,
                    AddedAt = XqdStore.Now(),
                    State = XqdState.Pending,
                };
                job.OutPath = OutPathFor(job);
                _jobs.Add(job);
                added++;
            }
        }
        if (added > 0) Save();
        return added;
    }

    /// <summary>某个营当前有多少任务在清单里（界面上取消勾选营时用）。</summary>
    public List<XqdJob> JobsOfCamp(string campName)
    {
        lock (_gate)
            return _jobs.Where(j => string.Equals(j.Camp, campName, StringComparison.Ordinal)).ToList();
    }

    /// <summary>清掉已完成 / 已存在的行（不动磁盘文件）。</summary>
    public int ClearFinished()
    {
        int n;
        lock (_gate)
        {
            n = _jobs.RemoveAll(j => j.State == XqdState.Done || j.State == XqdState.Exists);
        }
        if (n > 0) Save();
        return n;
    }

    public void ClearAll()
    {
        lock (_gate) _jobs.Clear();
        Save();
    }

    /// <summary>只保留一个行内修改入口：把任务的标题/链接更新后重算路径。</summary>
    public void UpdateUrl(XqdJob job, string url)
    {
        if (!XqdUtil.IsHttp(url)) return;
        job.Url = url.Trim();
        job.Id = XqdJob.MakeId(job.Title, job.Url);
        job.OutPath = OutPathFor(job);
        job.State = XqdState.Pending;
        job.Message = "";
        Save();
    }

    /// <summary>
    /// 下载目录变了以后重算成品路径。
    /// ★ 已完成的路径不动（那是既成事实），其余按当前 <see cref="OutDir"/> 重排。
    /// </summary>
    public void RebasePaths()
    {
        lock (_gate)
        {
            foreach (var j in _jobs)
                if (j.State != XqdState.Done) j.OutPath = OutPathFor(j);
        }
        Save();
    }

    /// <summary>按磁盘上已有的成品重判状态：够大就算「已存在」。</summary>
    public int Rescan()    {
        int n = 0;
        lock (_gate)
        {
            foreach (var j in _jobs)
            {
                bool exists = false;
                long size = 0;
                try
                {
                    if (File.Exists(j.OutPath))
                    {
                        size = new FileInfo(j.OutPath).Length;
                        exists = size >= MinExistingBytes;
                    }
                }
                catch { }

                if (exists)
                {
                    if (j.State != XqdState.Done && j.State != XqdState.Exists) n++;
                    j.State = XqdState.Exists;
                    j.Size = size;
                    j.Message = "";
                }
                else if (j.State == XqdState.Exists)
                {
                    j.State = XqdState.Pending;
                    j.Size = 0;
                }
            }
        }
        if (n > 0) Save();
        return n;
    }

    public void Load()
    {
        var list = _store.Load();
        lock (_gate)
        {
            _jobs.Clear();
            _jobs.AddRange(list);
        }
        Rescan();
    }

    public void Save()
    {
        lock (_gate) _store.Save(_jobs);
    }

    private string OutPathFor(XqdJob job)
    {
        string dir = OutDir;
        // 课表任务按营分子目录，和红果「<剧名>\<序号>.<集名>」的结构对齐
        if (!string.IsNullOrEmpty(job.Camp)) dir = Path.Combine(dir, XqdNaming.SafeName(job.Camp));

        string name = job.Seq > 0
            ? XqdNaming.LessonFileName(job.Seq, job.Title, job.WhenDate)
            : XqdNaming.StemFromTitle(job.Title) + ".mp4";

        string path = Path.Combine(dir, name);
        // 同名不同链接（不同课）不能互相覆盖：按清单里已占用的路径加 -2/-3
        var used = new HashSet<string>(
            _jobs.Where(j => !ReferenceEquals(j, job)).Select(j => j.OutPath),
            StringComparer.OrdinalIgnoreCase);
        int i = 2;
        string candidate = path;
        while (used.Contains(candidate) && i < 1000)
        {
            candidate = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)}-{i}.mp4");
            i++;
        }
        return candidate;
    }

    // ══════════════════════════════════════════════════════════════
    //  下载
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 跑一批任务。targets 为 null 时跑「所有待下载 / 失败」的。
    /// 返回 (成功, 失败, 跳过)。
    /// </summary>
    public async Task<(int ok, int fail, int skip)> RunAsync(
        IEnumerable<XqdJob> targets, CancellationToken ct)
    {
        if (_running) return (0, 0, 0);

        var list = (targets ?? _jobs)
            .Where(j => j.State is XqdState.Pending or XqdState.Failed
                        || XqdState.IsRunning(j.State) || j.State == XqdState.Paused)
            .ToList();
        if (list.Count == 0)
        {
            Log?.Invoke("没有需要下载的任务（已完成 / 已存在的不会重下）");
            return (0, 0, 0);
        }

        _running = true;
        _control = new XqdControl();
        int ok = 0, fail = 0, skip = 0;
        int workers = Math.Max(1, Options.LessonWorkers);

        Log?.Invoke($"开始下载 {list.Count} 节（课间并发 {workers}，分片并发 {Options.SegmentWorkers}）");

        using var gate = new SemaphoreSlim(workers);
        var tasks = new List<Task>();

        // ★ 整个「排队 + 跑」都必须在 try 里：
        //   用户在排队阶段点「停止」时 WaitAsync 会抛 OCE，
        //   只把 WhenAll 包进 try 的话 _running 会永远停在 true（按钮从此点不动）。
        try
        {
            foreach (var job in list)
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var r = await RunOneAsync(job, ct).ConfigureAwait(false);
                        if (r == "ok") Interlocked.Increment(ref ok);
                        else if (r == "skip") Interlocked.Increment(ref skip);
                        else Interlocked.Increment(ref fail);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, ct));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _running = false;
            _control = null;
            Save();
        }

        Log?.Invoke($"结束：成功 {ok} · 失败 {fail} · 跳过 {skip}");
        return (ok, fail, skip);
    }

    public void SetPaused(bool paused)
    {
        if (_control != null) _control.Paused = paused;
        Log?.Invoke(paused ? "已暂停（分片保留，可继续）" : "已继续");
    }

    /// <summary>中止：正在下的任务回到「待下载」，分片保留。</summary>
    public void Cancel()
    {
        if (_control != null) _control.Cancelled = true;
    }

    private async Task<string> RunOneAsync(XqdJob job, CancellationToken ct)
    {
        // 已存在就不重下（除非用户手动点了「重试」把状态改成待下载）
        if (job.State == XqdState.Exists) return "skip";

        var sw = Stopwatch.StartNew();
        job.State = XqdState.Queued;
        job.Message = "";
        job.Down = 0;
        job.Total = 0;
        job.Speed = 0;
        RaiseChanged(job);
        Log?.Invoke("▶ " + job.Display);

        var cb = new XqdCallbacks
        {
            Phase = phase => SetState(job, XqdState.PhaseWord(phase)),
            Progress = p => OnProgress(job, p, sw),
            Log = msg => Log?.Invoke("  " + job.Title + "：" + msg),
        };

        try
        {
            // 课表任务：现场解析播放地址（签名每次都在变，不缓存）
            string playUrl = job.Url;
            if (string.IsNullOrEmpty(playUrl))
            {
                if (job.FromCatalog)
                {
                    if (_platform == null)
                        throw new InvalidOperationException("尚未获取兴趣岛授权，无法解析课表任务的播放地址");
                    playUrl = await _platform.ResolveJobAsync(job, ct).ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidOperationException("任务缺少播放地址");
                }
            }

            string segDir = _store.PartsDirFor(job.OutPath);
            Directory.CreateDirectory(segDir);
            var result = await XqdDownloader.DownloadAsync(
                job, playUrl, Options, segDir, cb, _control, ct).ConfigureAwait(false);

            if (result.Ok)
            {
                job.State = XqdState.Done;
                job.OutPath = result.OutPath;
                job.Size = result.Size;
                job.Duration = result.Duration;
                job.Message = result.Message;
                job.Down = result.Size;
                job.Total = result.Size;
                job.Speed = 0;
                RaiseChanged(job);
                Log?.Invoke("✔ " + job.Title + " — " + result.Message);
                return "ok";
            }

            job.State = XqdState.Failed;
            job.Message = result.Message;
            job.Speed = 0;
            RaiseChanged(job);
            Log?.Invoke("✘ " + job.Title + " — " + result.Message);
            return "fail";
        }
        catch (OperationCanceledException)
        {
            job.State = XqdState.Pending;
            job.Message = "已中止";
            job.Speed = 0;
            RaiseChanged(job);
            Log?.Invoke("■ " + job.Title + " 已中止（分片保留）");
            return "fail";
        }
        catch (Exception e)
        {
            job.State = XqdState.Failed;
            job.Message = e.Message;
            job.Speed = 0;
            RaiseChanged(job);
            Log?.Invoke("✘ " + job.Title + " — " + e.Message);
            return "fail";
        }
    }

    private void SetState(XqdJob job, string state)
    {
        if (XqdState.IsTerminal(job.State)) return;
        if (state == job.State) return;
        job.State = state;
        RaiseChanged(job);
    }

    private void OnProgress(XqdJob job, XqdProgress p, Stopwatch sw)
    {
        job.Down = p.DoneBytes;
        job.Total = p.TotalBytes;
        if (p.Unit == "dur" && p.TotalDuration > 0) job.Duration = p.TotalDuration;
        double sec = sw.Elapsed.TotalSeconds;
        job.Speed = sec > 0.5 ? p.DoneBytes / sec : 0;

        // 限流：同一个任务 100ms 内只推一次
        string key = job.Id ?? job.Title;
        long now = Stopwatch.GetTimestamp();
        if (_lastEmitTicks.TryGetValue(key, out long last) &&
            (now - last) * 1000.0 / Stopwatch.Frequency < 100.0)
            return;
        _lastEmitTicks[key] = now;

        try { JobProgress?.Invoke(job, p); } catch { }
    }

    private void RaiseChanged(XqdJob job)
    {
        try { JobChanged?.Invoke(job); } catch { }
    }
}
