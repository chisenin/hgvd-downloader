using System;

namespace Hgvd.Tests;

public static class Program
{
    public static int Main(string[] args)
    {
        // ★ 实机验证入口（不属于离线回归）
        //   抓流相关的 --mitmtest / --tlsprobe / --grabtest 随抓流一起裁掉（2026-11-22）
        if (args.Length > 0 && args[0] == "--playtest") return PlayTest.Run(args);

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
        // ★ 抓流回归（GrabLoopTests / GrabWhitelistTests）随抓流一起裁掉（2026-11-22）
        // ★ 兴趣岛内核（2026-11-21 合并）：链接解析 / 清单 / 命名 / 队列
        fail += XqdTests.Run();
        // ★ 2026-11-21 补齐：红果清单落盘 / 下载历史 / 去重口径 / 5 档筛选 / 剩余时间
        fail += StateTests.Run();
        // ★ 2026-11-22 共用清单：一个 downloads.json，两源 section + 旧文件迁移
        fail += ManifestTests.Run();
        // ★ 2026-11-22 P3：两源共用顶层工位池 + 共享暂停闸门
        fail += QueueCoreTests.Run();
        // ★ 2026-11-22 下载提速：进度记账 + 预检秒跳判据
        fail += ProgressTests.Run();
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
