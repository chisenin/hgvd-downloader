using System;

namespace Hgvd.Core;

/// <summary>
/// 下载任务的状态词 —— 全工具**唯一的定义处**，红果与兴趣岛共用。
///
/// ★ 为什么放在 Core：两源的状态最终显示在同一张清单、同一套 5 档筛选里。
///   以前红果把引擎阶段名（「下载」「解密」「校验」）直接塞进界面状态，
///   兴趣岛却用「下载中 / 封装中 / 校验中」——
///   结果红果行在「下载中」筛选下会消失，按 `IsRunning` 判运行态也判不到。
///   现在引擎只报**阶段名**，界面状态一律走 <see cref="PhaseWord"/> 转换。
/// ★ 状态词是界面契约，别在别处另起一套：加一个词要同时改
///   筛选（EpViewFilter）、配色（EpisodeRow）、兴趣岛队列（XqdState 转发）与本处。
/// </summary>
public static class JobState
{
    // ── 未开始 / 进行中 ──
    public const string Pending = "待下载";
    public const string Queued = "排队中";
    public const string Resolving = "解析中";
    public const string Downloading = "下载中";
    public const string Muxing = "封装中";
    /// <summary>红果 CENC 内容解密（下载完成、落盘之后的独立阶段）。</summary>
    public const string Decrypting = "解密中";
    public const string Verifying = "校验中";
    public const string Paused = "已暂停";

    // ── 终态 ──
    public const string Done = "已完成";
    public const string Exists = "已存在";
    public const string Failed = "失败";

    /// <summary>进行中：可以被阶段名自由覆盖。</summary>
    public static bool IsRunning(string word) =>
        word is Queued or Resolving or Downloading or Muxing or Decrypting or Verifying;

    /// <summary>终态：不会被后续进度回调覆盖。</summary>
    public static bool IsTerminal(string word) => word is Done or Exists or Failed;

    /// <summary>
    /// 引擎阶段名 → 界面状态词。
    /// ★ 只映射「引擎内部叫法」，不认识的原样返回（例如后续新增阶段）——
    ///   宁可显示原文，也不要吞掉。
    /// </summary>
    public static string PhaseWord(string phase) => phase switch
    {
        "解析" => Resolving,
        "下载" => Downloading,
        "封装" => Muxing,
        "解密" => Decrypting,
        "校验" => Verifying,
        _ => phase ?? "",
    };
}
