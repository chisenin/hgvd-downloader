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
