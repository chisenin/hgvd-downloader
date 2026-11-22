using System;
using System.Threading;
using System.Threading.Tasks;
using Hgvd.Core.Engine;

namespace Hgvd.Core.Download;

/// <summary>
/// 两源共用的**顶层工位池**（一个批次内只有一个）。
///
/// ★ 红果的「集」和兴趣岛的「课」都算一个工位：红果单文件流占 1 格，
///   兴趣岛一课不论内部多少分片也只占 1 格（分片并发在课内自己管）。
///   这样混合批次的峰值可以被一个数字说清：红果 2 + 兴趣岛 1 = 3 格。
/// ★ 暂停也挂在这里：两源拿同一个 <see cref="PauseGate"/>，按钮只改一处状态。
/// ★ 它**不接管**两源各自的状态机：红果的预检/熔断、兴趣岛的 HLS/课表解析
///   仍是源适配器的职责；这里只负责「谁可以开始、最多几个同时在跑」。
/// </summary>
public sealed class DownloadQueueCore : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private int _disposed;

    public DownloadQueueCore(int capacity, PauseGate pause = null)
    {
        Capacity = Math.Max(1, capacity);
        _slots = new SemaphoreSlim(Capacity, Capacity);
        Pause = pause ?? new PauseGate();
    }

    public int Capacity { get; }

    /// <summary>两源共用的暂停闸门；引擎在分块/分片之间等它。</summary>
    public PauseGate Pause { get; }

    /// <summary>当前已占用的工位数（只用于日志/诊断）。</summary>
    public int InUse => Capacity - _slots.CurrentCount;

    /// <summary>拿一个工位；返回的租约 Dispose 即归还。取消时抛 <see cref="OperationCanceledException"/>。</summary>
    public async Task<SlotLease> AcquireAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(DownloadQueueCore));
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        return new SlotLease(_slots);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _slots.Dispose();
    }

    /// <summary>工位租约。重复 Dispose 不会多归还，避免把池子撑大。</summary>
    public sealed class SlotLease : IDisposable
    {
        private SemaphoreSlim _slots;

        internal SlotLease(SemaphoreSlim slots) => _slots = slots;

        public void Dispose()
        {
            var slots = Interlocked.Exchange(ref _slots, null);
            slots?.Release();
        }
    }
}
