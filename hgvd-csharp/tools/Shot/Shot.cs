using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Hgvd.Tools;

/// <summary>
/// 窗口截图 / 鼠标点击小工具（开发期自检用）。
///
/// ★ 用法：
///   hgvd-shot.exe &lt;out.png&gt; &lt;pid&gt;                     截图
///   hgvd-shot.exe &lt;out.png&gt; &lt;pid&gt; --click &lt;x&gt; &lt;y&gt;   先点窗口内 (x,y) 再截图
///   （x,y 是**窗口内**坐标，不是屏幕坐标）
/// </summary>
public static class Shot
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x02;
    private const uint MOUSEEVENTF_LEFTUP = 0x04;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_MOUSEMOVE = 0x0200;

    public static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : @"D:\hgvd_gui.png";
        string procName = args.Length > 1 ? args[1] : "hgvd";

        int clickX = -1, clickY = -1;
        bool usePost = false;
        for (int i = 2; i < args.Length - 2; i++)
        {
            if (args[i] == "--click" || args[i] == "--pclick")
            {
                if (args[i] == "--pclick") usePost = true;
                int.TryParse(args[i + 1], out clickX);
                int.TryParse(args[i + 2], out clickY);
            }
        }

        System.Diagnostics.Process proc = null;
        if (int.TryParse(procName, out int pid))
            proc = System.Diagnostics.Process.GetProcessById(pid);
        if (proc is null)
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(procName))
            {
                proc = p;
                break;
            }
        }
        if (proc == null)
        {
            Console.WriteLine($"找不到进程 {procName}");
            return 1;
        }

        IntPtr h = proc.MainWindowHandle;
        ShowWindow(h, 9);          // SW_RESTORE
        SetForegroundWindow(h);
        System.Threading.Thread.Sleep(700);
        if (!GetWindowRect(h, out var r)) { Console.WriteLine("取窗口位置失败"); return 1; }
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        Console.WriteLine($"窗口 {w}x{ht} @ {r.Left},{r.Top}");
        if (w <= 0 || ht <= 0) { Console.WriteLine("窗口尺寸异常"); return 1; }

        if (clickX >= 0)
        {
            int sx = r.Left + clickX, sy = r.Top + clickY;
            Console.WriteLine($"点击窗口内 ({clickX},{clickY}) → 屏幕 ({sx},{sy}) {(usePost ? "[PostMessage]" : "[真实鼠标]")}");
            SetCursorPos(sx, sy);
            System.Threading.Thread.Sleep(250);

            if (usePost)
            {
                // PostMessage 定点投递：WPF 只有一个 HWND，靠 WM_LBUTTON* 的
                // 坐标自己做命中测试 → 不依赖焦点。
                IntPtr lp = (IntPtr)((clickY << 16) | (clickX & 0xFFFF));
                PostMessage(h, WM_MOUSEMOVE, IntPtr.Zero, lp);
                System.Threading.Thread.Sleep(120);
                PostMessage(h, WM_LBUTTONDOWN, (IntPtr)1, lp);
                System.Threading.Thread.Sleep(120);
                PostMessage(h, WM_LBUTTONUP, IntPtr.Zero, lp);
            }
            else
            {
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                System.Threading.Thread.Sleep(90);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
            }
            System.Threading.Thread.Sleep(250);
        }

        if (!GetWindowRect(h, out var r2)) { Console.WriteLine("取窗口位置失败"); return 1; }
        w = r2.Right - r2.Left; ht = r2.Bottom - r2.Top;
        using var bmp = new Bitmap(w, ht);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(r2.Left, r2.Top, 0, 0, bmp.Size);
        bmp.Save(outPath, ImageFormat.Png);
        Console.WriteLine("已保存 " + outPath);
        return 0;
    }
}
