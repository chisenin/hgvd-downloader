using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hgvd.Core.Api;

namespace Hgvd.Core.Engine;

/// <summary>
/// 直链预取窗口：始终让「下载游标前方 N 集」的直链提前解析好，
/// 下载侧直接命中缓存，不必在开下前现场等解析（那 ~10s 的停顿）。
///
/// ★★ 为什么是有界窗口、不全量解析（2026-11-22 用户定）：
///   · 全量解析 180 集会打 180 次播放接口，用户担心服务端压力；
///   · 固定只领先 10 集：足够盖住解析延迟（下载远慢于解析），又不给网关添压；
///   · 下载消费一集 → 窗口补一集，滑移前进。
/// ★ 解析走 <see cref="HongguoService.WarmOneAsync"/>：失败静默、不触发熔断、
///   带时间戳/账号（超期作废重解析）。
/// </summary>
public sealed class PrefetchWindow
{
    private readonly HongguoService _svc;
    private readonly List<Episode> _queue;
    private readonly string _quality;
    private readonly int _window;
    private readonly int _parseWorkers;

    private int _nextIndex;      // 下一个待解析的队列位置
    private int _readyCount;     // 已消费/正在消费之后、还没取走的预备额度
    private readonly object _gate = new();
    private Task _worker;
    private CancellationTokenSource _cts;

    /// <summary>
    /// <paramref name="queue"/> 按下载顺序；只放真实分集（非空 vid）。
    /// </summary>
    public PrefetchWindow(HongguoService svc, List<Episode> queue, string quality,
        int window = 10, int parseWorkers = 2)
    {
        _svc = svc;
        _queue = queue ?? new List<Episode>();
        _quality = quality;
        _window = Math.Max(1, window);
        _parseWorkers = Math.Max(1, parseWorkers);
    }

    /// <summary>已解析好、等待消费的集数（诊断用）。</summary>
    public int ReadyCount { get { lock (_gate) return _readyCount; } }

    public void Start()
    {
        if (_queue.Count == 0) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _worker = Task.Run(async () =>
        {
            using var sem = new SemaphoreSlim(_parseWorkers);
            var inflight = new List<Task>();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int idx = -1;
                    lock (_gate)
                    {
                        if (_readyCount < _window && _nextIndex < _queue.Count)
                            idx = _nextIndex++;
                    }
                    if (idx >= 0)
                    {
                        var ep = _queue[idx];
                        await sem.WaitAsync(ct);
                        inflight.Add(Task.Run(async () =>
                        {
                            try { await _svc.WarmOneAsync(ep, _quality, ct); }
                            catch { }
                            finally { sem.Release(); }
                        }, ct));
                    }
                    else
                    {
                        // 窗口满 / 队列暂时到底 → 歇一下再看（Consume 会腾出额度）
                        if (_nextIndex >= _queue.Count && inflight.Count == 0)
                        {
                            // 全部解析任务已派完，仍需留在循环里等 Consume 腾额度？
                            // 队列见底说明没有更多集可解析，直接退出。
                            break;
                        }
                        await Task.Delay(200, ct);
                    }
                    inflight.RemoveAll(t => t.IsCompleted);
                }
                await Task.WhenAll(inflight);
            }
            catch (OperationCanceledException) { }
            catch { }
        }, ct);
    }

    /// <summary>
    /// 下载端在每集**开下前**调用（无论是否命中缓存）：消耗一个预备额度，
    /// worker 下一轮轮询会补解析后面一集，窗口持续滑移。
    /// </summary>
    public void Consume()
    {
        lock (_gate)
        {
            if (_readyCount > 0) _readyCount--;
        }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
    }
}
