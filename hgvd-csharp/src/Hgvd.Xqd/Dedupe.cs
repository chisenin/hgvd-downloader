using System;

namespace Hgvd.Xqd;

/// <summary>
/// 「成品已存在」的判定 —— 两源统一口径的唯一入口。
///
/// ★ 上游（xqd v1.7.1）只看 ≥10KB：一个被截断的视频也能过，
///   用户点下载会直接跳过，成品永远是坏的。
/// ★ 红果侧的做法是「文件在 + ffprobe 时长对得上」；合并后把这套标准
///   推广到兴趣岛：有预期时长且 ffprobe 读得出 → 校验时长，
///   读不出（或本来就没有时长）→ 回退到 ≥1MB 的体积门槛。
/// </summary>
public static class XqdDedupe
{
    /// <summary>回退门槛：1 MiB。低于这个一律当成没下完（宁可重下，不可留坏片）。</summary>
    public const long MinBytes = 1024 * 1024;

    /// <summary>时长容差：至少 1 秒，长课按 0.5%。</summary>
    public static double Tolerance(double expect) => Math.Max(1.0, expect * 0.005);

    /// <summary>
    /// 判一条任务是否已存在。
    /// </summary>
    /// <param name="bytes">磁盘上成品字节数（0 = 不存在）</param>
    /// <param name="expectDuration">任务预期时长（秒，0 = 未知）</param>
    /// <param name="probedDuration">ffprobe 读到的时长（秒，0 = 读不出）</param>
    public static bool IsExisting(long bytes, double expectDuration, double probedDuration)
    {
        if (bytes < MinBytes) return false;
        if (expectDuration <= 0 || probedDuration <= 0) return true;   // 没时长可比 → 只认体积
        return Math.Abs(probedDuration - expectDuration) <= Tolerance(expectDuration);
    }
}
