using System;
using System.Collections.Generic;
using Hgvd.Avalonia.ViewModels;
using Hgvd.Core.Api;

namespace Hgvd.Tests;

/// <summary>
/// 分集行（EpisodeRow）的显示与**属性通知**回归 —— **Avalonia 发行版**。
///
/// ★ 本文件是 WPF 版 <c>EpisodeRowTests.cs</c> 的移植版
///   （WPF 版已于 2026-11-14 归档到 archive/hgvd-csharp-wpf/）。
///   Avalonia 成为发行版后，界面回归必须跟着迁移过来 —— 否则
///   「在售版本没有界面回归保护」。
///
/// ★★ 为什么必须有这个测试：
///   2026-11-14 用户反馈「时长/大小/速度都是空的」。根因是
///   <c>Down</c> / <c>Total</c> / <c>Speed</c> 的 setter 只通知了**自己**
///   （PropertyChanged("Down")），而界面绑的是**计算属性**
///   <c>SizeText</c> / <c>SpeedText</c> —— 界面永远不会重新求值，
///   于是这两列永远是初始的空串。
///   ⇒ 「改了值但界面不刷新」这类 bug，**必须断言通知事件的名字**，
///   只断言最终值是测不出来的（值本身是对的）。
/// </summary>
public static class AvaloniaEpisodeRowTests
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  ✓ {name}"); }
        else { _fail++; Console.WriteLine($"  ✗ {name}  {detail}"); }
    }

    public static int Run()
    {
        Console.WriteLine("\n[界面] 分集行显示与属性通知");

        var ep = new Episode
        {
            Vid = "7700851630874315800",
            Index = 2,
            Title = "邪恶胖茄子，制霸海洋",
            Duration = 265,
            SeriesId = "7700851630874315800",
            SeriesTitle = "颠覆你的动植物科普",
            VideoIdType = 1,
        };
        var row = new EpisodeRow(ep);

        var seen = new List<string>();
        row.PropertyChanged += (_, e) => seen.Add(e.PropertyName);

        // ---- 时长 ----
        Check("时长显示为 mm:ss", row.DurationText == "04:25", $"实际 {row.DurationText}");

        // ---- 大小：初始是占位符，不是空串 ----
        Check("大小初始显示占位符", row.SizeText == "--", $"实际 \"{row.SizeText}\"");

        // ---- 大小：下载前显示预计值 ----
        row.PlanSize = 30_535_297;
        Check("大小显示预计值", row.SizeText == "≈ 29.12 MB", $"实际 {row.SizeText}");

        // ---- ★ 大小：改了 Total 之后 SizeText 必须被通知 ----
        seen.Clear();
        row.Total = 30_535_297;
        Check("改 Total 会通知 SizeText", seen.Contains(nameof(EpisodeRow.SizeText)),
              $"通知了：{string.Join(",", seen)}");
        Check("大小显示 已下/总计",
            row.SizeText == "0 B / 29.12 MB", $"实际 {row.SizeText}");

        seen.Clear();
        row.Down = 15_267_648;
        Check("改 Down 会通知 SizeText", seen.Contains(nameof(EpisodeRow.SizeText)),
              $"通知了：{string.Join(",", seen)}");
        Check("大小实时更新",
            row.SizeText == "14.56 MB / 29.12 MB", $"实际 {row.SizeText}");

        // ---- ★ 速度：改了 Speed 之后 SpeedText 必须被通知 ----
        seen.Clear();
        row.Speed = 2_097_152;
        Check("改 Speed 会通知 SpeedText", seen.Contains(nameof(EpisodeRow.SpeedText)),
              $"通知了：{string.Join(",", seen)}");
        Check("速度显示带 /s", row.SpeedText == "2 MB/s", $"实际 {row.SpeedText}");

        // ---- 进度 ----
        seen.Clear();
        row.Down = 7_633_824;
        Check("改 Down 会通知 Percent/ProgressText",
            seen.Contains(nameof(EpisodeRow.Percent))
            && seen.Contains(nameof(EpisodeRow.ProgressText)),
            $"通知了：{string.Join(",", seen)}");
        // 7633824 * 100 / 30535297 = 24.999… → 整数除法截断成 24
        Check("百分比计算正确", row.Percent == 24, $"实际 {row.Percent}");
        Check("百分比文本正确", row.ProgressText == "24%", $"实际 {row.ProgressText}");

        // ---- 状态变化也要通知（列表过滤依赖它）----
        seen.Clear();
        row.State = "下载中";
        Check("改 State 会通知 StateWord",
            seen.Contains(nameof(EpisodeRow.StateWord)),
            $"通知了：{string.Join(",", seen)}");

        Console.WriteLine($"  —— 通过 {_pass}，失败 {_fail}");
        return _fail;
    }
}
