using System.Threading;
using System.Threading.Tasks;

namespace Hgvd.Core.Engine;

/// <summary>
/// 可暂停/继续的闸门（红果下载用）。
///
/// ★ 为什么不用「取消 + 重新开始」冒充暂停：取消会让当前这一集回到「待下载」，
///   下次从头解析播放地址；而这里的暂停是**保留当前连接与进度**，恢复后接着写。
/// ★ 只在拷贝循环里等待，锁粒度极小；取消仍然走 CancellationToken。
/// </summary>
public sealed class PauseGate
{
    private volatile bool _paused;
    private TaskCompletionSource<bool> _resume = Completed();

    private static TaskCompletionSource<bool> Completed()
    {
        var t = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        t.TrySetResult(true);
        return t;
    }

    public bool IsPaused => _paused;

    public void Pause()
    {
        if (_paused) return;
        _resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _paused = true;
    }

    public void Resume()
    {
        _paused = false;
        _resume.TrySetResult(true);
    }

    /// <summary>暂停中就等在这里；恢复（或取消）后返回。</summary>
    public Task WaitAsync(CancellationToken ct = default)
        => _paused ? _resume.Task.WaitAsync(ct) : Task.CompletedTask;
}
