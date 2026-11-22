using System.Threading;
using System.Threading.Tasks;

namespace Hgvd.Core;

/// <summary>
/// 下载过程中的「暂停闸门」—— 红果（PauseGate）与兴趣岛（XqdControl）共用。
/// ★ 只定义「要不要等」，不定义取消；取消仍然走 CancellationToken。
/// ★ 引擎在**分块之间**等，连接与已写进度都保留，恢复后接着写。
/// </summary>
public interface IPauseToken
{
    bool IsPaused { get; }
    Task WaitAsync(CancellationToken ct = default);
}
