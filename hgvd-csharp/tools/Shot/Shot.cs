using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace Hgvd.Tools;

/// <summary>窗口截图小工具（开发期自检用）。</summary>
public static class Shot
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);

    public static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : @"D:\hgvd_gui.png";
        string procName = args.Length > 1 ? args[1] : "hgvd";

        // ★ 直接按进程 ID 取（GetProcesses 在某些环境下拿不到窗口句柄）
        System.Diagnostics.Process proc = null;
        if (int.TryParse(procName, out int pid))
            proc = System.Diagnostics.Process.GetProcessById(pid);
        if (proc is null)
        {
            // 退而求其次：按名字前缀找
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(procName))
            {
                proc = p;
                break;
            }
        }
        if (proc is not null && proc.MainWindowHandle == IntPtr.Zero)
        {
            proc.Refresh();
        }
        if (proc is null)
        {
            Console.WriteLine($"找不到进程 {procName}");
            return 1;
        }

        IntPtr h = proc.MainWindowHandle;
        ShowWindow(h, 9);          // SW_RESTORE
        SetForegroundWindow(h);
        System.Threading.Thread.Sleep(900);

        if (!GetWindowRect(h, out var r)) { Console.WriteLine("取窗口位置失败"); return 1; }
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        Console.WriteLine($"窗口 {w}x{ht} @ {r.Left},{r.Top}");
        if (w <= 0 || ht <= 0) { Console.WriteLine("窗口尺寸异常"); return 1; }

        using var bmp = new Bitmap(w, ht);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
        bmp.Save(outPath, ImageFormat.Png);
        Console.WriteLine("已保存 " + outPath);
        return 0;
    }
}
