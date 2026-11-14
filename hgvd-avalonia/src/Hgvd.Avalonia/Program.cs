using System;
using System.Runtime.InteropServices;
using Avalonia;

namespace Hgvd.Avalonia;

/// <summary>进程入口。</summary>
internal static class Program
{
    /// <summary>
    /// 自检模式（--selftest / --uitest / --batchtest）要把结果打到控制台，
    /// 但本程序是 <c>WinExe</c>（GUI 子系统）→ 默认没有控制台。
    /// ★ 挂上**父进程**的控制台，这样从 cmd 里跑就能看到输出。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    public static int Main(string[] args)
    {
        if (OperatingSystem.IsWindows()) AttachConsole(-1);   // ATTACH_PARENT_PROCESS
        // ★ 控制台默认是 GBK，中文输出会变乱码 —— 改成 UTF-8
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Avalonia 应用构建器（供 XAML 预览器 / 单元测试复用）。</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
