using System;
using Hgvd.Core;

namespace Hgvd.Xqd;

/// <summary>
/// 兴趣岛这一侧的兼容入口 —— 判定本身在 <see cref="ExistingArtifact"/>。
///
/// ★ 上游（xqd v1.7.1）只看 ≥10KB：一个被截断的视频也能过，
///   用户点下载会直接跳过，成品永远是坏的。
/// ★ 合并后把判定统一到 Core：体积门槛 1MiB + 预期时长 vs ffprobe；
///   兴趣岛用 EXTINF 的精确时长，容差 `max(1s, 0.5%)`。
///   保留本类只是为了让既有调用与回归不必改名，**不要在这里另写一套算法**。
/// </summary>
public static class XqdDedupe
{
    /// <summary>回退门槛：1 MiB。低于这个一律当成没下完（宁可重下，不可留坏片）。</summary>
    public const long MinBytes = ExistingArtifact.MinBytes;

    /// <summary>时长容差：至少 1 秒，长课按 0.5%。</summary>
    public static double Tolerance(double expect) =>
        ExistingArtifact.Tolerance(MediaSource.Xiqudao, expect);

    /// <summary>
    /// 判一条任务是否已存在。
    /// </summary>
    /// <param name="bytes">磁盘上成品字节数（0 = 不存在）</param>
    /// <param name="expectDuration">任务预期时长（秒，0 = 未知）</param>
    /// <param name="probedDuration">ffprobe 读到的时长（秒，0 = 读不出）</param>
    public static bool IsExisting(long bytes, double expectDuration, double probedDuration) =>
        ExistingArtifact.IsExisting(bytes, expectDuration, probedDuration, MediaSource.Xiqudao);
}
