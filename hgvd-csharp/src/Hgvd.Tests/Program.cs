using System;

namespace Hgvd.Tests;

public static class Program
{
    public static int Main(string[] args)
    {
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
        fail += DeviceStoreTests.Run();
        // ★ DRM 用真实黄金向量守「短剧密钥解包」——多切一字节就解不出 key，
        //   整条加密内容（短剧）链路全废，见 DrmTests 的注释。
        fail += DrmTests.Run();
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
