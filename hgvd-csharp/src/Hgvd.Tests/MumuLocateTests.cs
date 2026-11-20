using System;
using System.IO;
using System.Reflection;
using Hgvd.Core.Auth;

namespace Hgvd.Tests;

/// <summary>
/// MuMu 安装位置发现回归。
///
/// ★★ 为什么要有这个测试：
///   2026-11-17 用户反馈「模拟器开了、App 登录了，但就是拿不到授权」。
///   根因是 MuMu 装在 <c>D:\Program Files\Netease\MuMuPlayer-12.0</c>，
///   而代码只写死找了 <c>Netease\MuMu</c> —— 于是 FindMumu 直接抛异常。
///   这里用**临时目录模拟任意安装根目录**，锁住「不依赖具体目录名」这条行为。
/// </summary>
public static class MumuLocateTests
{
    private static int _pass;
    private static int _fail;

    private static void Ok(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  ✓ {name}"); }
        else
        {
            _fail++;
            Console.WriteLine($"  ✗ {name}");
            if (detail.Length > 0) Console.WriteLine($"      {detail}");
        }
    }

    /// <summary>用 WScript.Shell 造一个真实 .lnk（本机没有 WScript.Shell 时返回 false）。</summary>
    /// <remarks>
    /// ★ 与 MumuAuth.ResolveShortcut 保持一致：**不用 dynamic**。
    ///   测试项目虽然不参与发布裁剪，但同一套 COM 调用留两种写法容易抄错。
    /// </remarks>
    private static bool MakeShortcut(string lnkPath, string target)
    {
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return false;
            object shell = Activator.CreateInstance(t);
            object sc = shell.GetType().InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (sc == null) return false;
            sc.GetType().InvokeMember(
                "TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            sc.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            return File.Exists(lnkPath);
        }
        catch { return false; }
    }

    public static int Run()
    {
        Console.WriteLine("\n[MuMu] 安装位置发现（不同机器不同目录）");

        string root = Path.Combine(Path.GetTempPath(),
            "hgvd-mumu-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // 模拟 MuMu Player 12：<root>\nx_main\MuMuManager.exe
            string nx = Path.Combine(root, "nx_main");
            Directory.CreateDirectory(nx);
            string manager = Path.Combine(nx, "MuMuManager.exe");
            File.WriteAllText(manager, "");

            string hit = MumuAuth.ManagerIn(root);
            Ok("任意安装根目录 → nx_main/MuMuManager.exe",
               hit == manager, $"实际 {hit}");

            // 模拟「从运行中的进程路径回溯」
            string mainExe = Path.Combine(nx, "MuMuNxMain.exe");
            File.WriteAllText(mainExe, "");
            string hit2 = MumuAuth.ManagerIn(mainExe);
            Ok("进程 exe 路径 → 回溯到同级 MuMuManager.exe",
               hit2 == manager, $"实际 {hit2}");

            // 另一种布局：<root>\shell\MuMuManager.exe
            string root2 = Path.Combine(root, "MuMu Player 12");
            Directory.CreateDirectory(Path.Combine(root2, "shell"));
            string manager2 = Path.Combine(root2, "shell", "MuMuManager.exe");
            File.WriteAllText(manager2, "");
            string hit3 = MumuAuth.ManagerIn(root2);
            Ok("旧版布局 shell/MuMuManager.exe",
               hit3 == manager2, $"实际 {hit3}");

            // 注册表值清洗
            Ok("DisplayIcon 尾部 ,0 被剥掉",
               MumuAuth.CleanHint(@"D:\x\uninstall.exe,0") == @"D:\x\uninstall.exe");
            Ok("带引号的卸载命令行被剥成纯路径",
               MumuAuth.CleanHint("\"C:\\Program Files\\Netease\\MuMu\\uninstall.exe\" /S")
                   == @"C:\Program Files\Netease\MuMu\uninstall.exe");

            // 手动指定只接受 MuMuManager.exe（防止用户随手选了别的 exe）
            Ok("拒绝非 MuMuManager.exe",
               !MumuAuth.SetManagerPath(mainExe));

            // 快捷方式 → 安装目录（官方安装包必建 .lnk，装在任意目录都能命中）
            string lnkDir = Path.Combine(root, "shortcuts");
            Directory.CreateDirectory(lnkDir);
            string lnk = Path.Combine(lnkDir, "MuMu模拟器.lnk");
            if (MakeShortcut(lnk, mainExe))
            {
                var targets = MumuAuth.ShortcutTargets(new[] { lnkDir });
                string resolved = targets.Count > 0 ? MumuAuth.ManagerIn(targets[0]) : "";
                Ok("快捷方式目标 → nx_main/MuMuManager.exe",
                   resolved == manager, $"解析出 {resolved}");
            }
            else
            {
                Console.WriteLine("  · 跳过快捷方式解析（本机没有 WScript.Shell）");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine($"  —— 通过 {_pass}，失败 {_fail}");
        return _fail;
    }
}
