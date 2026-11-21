using System;
using System.Collections.Generic;
using System.IO;
using Hgvd.Core.Auth;

namespace Hgvd.Tests;

/// <summary>
/// 通用 adb 层回归（设备列表解析 / 设备选择 / adb.exe 定位）。
///
/// ★★ 为什么要有这个测试：
///   2026-11-21 起模拟器正式改为雷电，所有「读 App 私有数据」都经过 <see cref="AdbDevices"/>。
///   这里锁住三条**换机器最容易崩**的行为：
///     ① <c>adb devices -l</c> 的输出解析（两种设备形态：emulator-xxxx / ip:port）；
///     ② 选设备时**优先模拟器**、且能按配置的序列号精确命中；
///     ③ 从「目录 / 主程序 exe / 上层目录」都能推出 adb.exe（不依赖任何写死盘符）。
/// </summary>
public static class AdbLocateTests
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

    public static int Run()
    {
        _pass = _fail = 0;
        Console.WriteLine("── adb 层：设备解析 / 选择 / adb.exe 定位 ──");

        ParseTests();
        PickTests();
        AdbInTests();

        Console.WriteLine($"  —— 通过 {_pass}，失败 {_fail}");
        return _fail;
    }

    // ---------------------------------------------------------------- 解析

    private const string SampleDevices =
        "List of devices attached\n"
        + "emulator-5554          device product:2509FPN0BC model:2509FPN0BC device:graceltexx transport_id:6\n"
        + "192.168.1.104:5555     offline product:Nicole model:NCO_AL00 device:Nicole transport_id:8\n"
        + "* daemon started successfully\n"
        + "\n";

    private static void ParseTests()
    {
        var list = AdbDevices.ParseDevices(SampleDevices);
        Ok("解析：只出 2 台（表头 / daemon 行被忽略）", list.Count == 2, "实际 " + list.Count);

        if (list.Count == 2)
        {
            AdbDevice a = list[0], b = list[1];
            Ok("解析：模拟器 serial/state", a.Serial == "emulator-5554" && a.Online && a.IsEmulator);
            Ok("解析：model 字段", a.Model == "2509FPN0BC", a.Model);
            Ok("解析：网络设备离线", b.Serial == "192.168.1.104:5555" && !b.Online && !b.IsEmulator);
            Ok("解析：Describe 带状态", a.Describe.Contains("在线"), a.Describe);
        }

        Ok("解析：空输入不炸", AdbDevices.ParseDevices("").Count == 0);
        Ok("解析：null 不炸", AdbDevices.ParseDevices(null).Count == 0);
    }

    // ---------------------------------------------------------------- 选择

    private static void PickTests()
    {
        var devs = AdbDevices.ParseDevices(SampleDevices);

        // 网络设备排在前，仍应优先选模拟器
        AdbDevice pick = AdbDevices.Pick(devs);
        Ok("选设备：优先 emulator-*", pick != null && pick.Serial == "emulator-5554",
           pick == null ? "null" : pick.Serial);

        // 指定序列号时精确命中
        AdbDevice want = AdbDevices.Pick(devs, "192.168.1.104:5555");
        Ok("选设备：指定序列号精确命中（离线则忽略）", want != null && want.Serial == "emulator-5554",
           want == null ? "null" : want.Serial);

        // 多台模拟器时取 serial 排序最小，保证每次一致
        var two = AdbDevices.ParseDevices(
            "List of devices attached\nemulator-5560 device\nemulator-5554 device\n");
        AdbDevice first = AdbDevices.Pick(two);
        Ok("选设备：多台模拟器按 serial 排序（稳定）",
           first != null && first.Serial == "emulator-5554",
           first == null ? "null" : first.Serial);

        // 全离线 → null
        var offline = AdbDevices.ParseDevices(
            "List of devices attached\nemulator-5554 offline\n");
        Ok("选设备：全离线返回 null", AdbDevices.Pick(offline) == null);
    }

    // ---------------------------------------------------------------- 定位 adb.exe

    private static void AdbInTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "hgvd_adb_" + Guid.NewGuid().ToString("N"));
        try
        {
            // 布局 A：<root>\adb.exe（雷电：dnplayer.exe 与 adb.exe 同目录）
            Directory.CreateDirectory(root);
            string adbA = Path.Combine(root, "adb.exe");
            File.WriteAllText(adbA, "");
            Ok("定位：目录里有 adb.exe", AdbDevices.AdbIn(root).Length > 0);

            // 传主程序 exe 也要能回推出同目录的 adb.exe
            string dn = Path.Combine(root, "dnplayer.exe");
            File.WriteAllText(dn, "");
            Ok("定位：传主程序 exe → 同目录 adb.exe",
               string.Equals(AdbDevices.AdbIn(dn), Path.GetFullPath(adbA), StringComparison.OrdinalIgnoreCase),
               AdbDevices.AdbIn(dn));

            // 布局 B：<root2>\nx_main\adb.exe（MuMu 形态），传上级目录也要找到
            string root2 = Path.Combine(Path.GetTempPath(), "hgvd_adb2_" + Guid.NewGuid().ToString("N"));
            string nx = Path.Combine(root2, "nx_main");
            Directory.CreateDirectory(nx);
            string adbB = Path.Combine(nx, "adb.exe");
            File.WriteAllText(adbB, "");
            Ok("定位：adb.exe 在 nx_main 子目录", AdbDevices.AdbIn(root2).Length > 0);
            Ok("定位：传子目录本身也命中", AdbDevices.AdbIn(nx).Length > 0);

            Directory.Delete(root2, true);

            Ok("定位：不存在的路径返回空", AdbDevices.AdbIn(Path.Combine(root, "nope")).Length == 0);
            Ok("定位：空串返回空", AdbDevices.AdbIn("").Length == 0);
        }
        catch (Exception e)
        {
            Ok("定位：临时目录读写", false, e.Message);
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }
}
