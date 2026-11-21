using System;

namespace Hgvd.Tests;

public static class Program
{
    public static int Main(string[] args)
    {
        // ★ 实机验证入口（不属于离线回归）：起 MITM 代理看安卓能否握手
        if (args.Length > 0 && args[0] == "--mitmtest") return MitmTest.Run(args);
        if (args.Length > 0 && args[0] == "--tlsprobe") return TlsProbe.Run(args);
        if (args.Length > 0 && args[0] == "--playtest") return PlayTest.Run(args);
        if (args.Length > 0 && args[0] == "--grabtest") return GrabTest.Run(args);

        Console.WriteLine("═══ 红果下载器 C# 版 · 离线回归 ═══");
        int fail = 0;
        fail += SignTests.Run();
        fail += HeaderTests.Run();
        fail += ArgusTests.Run();
        fail += AvaloniaEpisodeRowTests.Run();
        // ★ EpisodeRowTests（WPF 界面层回归）随 WPF 版归档到
        //   archive/hgvd-csharp-wpf/EpisodeRowTests.cs，不再参与编译；
        //   发行版（Avalonia）的同名回归见 AvaloniaEpisodeRowTests。
        fail += NamingTests.Run();
        fail += MumuLocateTests.Run();
        fail += AdbLocateTests.Run();   // ★ 2026-11-21：雷电成为正式模拟器，adb 层要有护栏
        fail += DeviceStoreTests.Run();
        // ★ DRM 用真实黄金向量守「短剧密钥解包」——多切一字节就解不出 key，
        //   整条加密内容（短剧）链路全废，见 DrmTests 的注释。
        fail += DrmTests.Run();
        // ★ 抓流自动停止：一次性 / 并发安全 / 停抓流不取消下载
        fail += GrabLoopTests.Run();
        // ★ 抓流准入：白名单命中是唯一条件（只下收藏夹里的教材/合集类）
        fail += GrabWhitelistTests.Run();
        // ★ 兴趣岛内核（2026-11-21 合并）：链接解析 / 清单 / 命名 / 队列
        fail += XqdTests.Run();
        // ★ 2026-11-21 补齐：红果清单落盘 / 下载历史 / 去重口径 / 5 档筛选 / 剩余时间
        fail += StateTests.Run();
        Console.WriteLine();
        if (fail == 0)
        {
            Console.WriteLine("全部通过 ✓");
            return 0;
        }
        Console.WriteLine($"失败 {fail} 项 ✗");
        return 1;
    }
}
