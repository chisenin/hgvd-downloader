using System;

namespace Hgvd.Core;

/// <summary>成品来源 —— 只用来选「时长容差」这一条口径，不参与业务分支。</summary>
public enum MediaSource
{
    /// <summary>红果：预期时长来自播放接口的标称值，精度较低。</summary>
    Hongguo = 0,
    /// <summary>兴趣岛：预期时长来自 HLS 的 EXTINF（分片时长逐段精确累加）。</summary>
    Xiqudao = 1,
}

/// <summary>
/// 「成品是否已存在」的唯一判定入口（两源共用）。
///
/// ★ 统一的是**判定结构**：体积门槛 + 预期时长 vs ffprobe 实测。
///   容差按源标定，是实测结论，不是拍脑袋：
///   · 红果：接口标称时长与成品常差 1~2 秒，容差 `max(3s, 2%)`，
///     太严会把已下完的集判成「缺」而反复重下；
///   · 兴趣岛：EXTINF 是逐片精确值，容差 `max(1s, 0.5%)`，
///     太松会把截断的课当成成品（上游只看 ≥10KB 就犯过这个错）。
/// ★ 门槛统一 ≥1MiB：低于这个体积一律当成没下完，宁可重下，不可留坏片。
/// ★ 红果的「预检秒跳」、单集兜底；兴趣岛的 Rescan —— 三处都必须走这里，
///   别再各写一遍 `Math.Abs(...) &lt; 3 ...`。
/// </summary>
public static class ExistingArtifact
{
    /// <summary>体积门槛：1 MiB。</summary>
    public const long MinBytes = 1024 * 1024;

    /// <summary>按来源取时长容差（秒）。预期时长未知时返回 0。</summary>
    public static double Tolerance(MediaSource source, double expectDuration)
    {
        if (expectDuration <= 0) return 0;
        return source == MediaSource.Hongguo
            ? Math.Max(3.0, expectDuration * 0.02)
            : Math.Max(1.0, expectDuration * 0.005);
    }

    /// <summary>
    /// 判一条成品是否「已存在」。
    /// </summary>
    /// <param name="bytes">磁盘上的成品字节数（0 = 不存在）</param>
    /// <param name="expectDuration">任务预期时长（秒，0 = 未知）</param>
    /// <param name="probedDuration">ffprobe 读到的时长（秒，0 = 读不出）</param>
    /// <param name="source">成品来源（决定容差）</param>
    public static bool IsExisting(long bytes, double expectDuration, double probedDuration,
        MediaSource source)
    {
        if (bytes < MinBytes) return false;
        // 没有预期时长 / 读不出实测时长 → 只认体积门槛
        if (expectDuration <= 0 || probedDuration <= 0) return true;
        return Math.Abs(probedDuration - expectDuration) <= Tolerance(source, expectDuration);
    }
}
