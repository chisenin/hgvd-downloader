using System;
using System.IO;
using Hgvd.Core.Auth;
using Hgvd.Core.Sign;

namespace Hgvd.Tests;

/// <summary>
/// 多实例设备档案回归。
///
/// ★★ 为什么要有这个测试：
///   MuMu 多开时每台实例的 <c>install_id</c> / <c>device_id</c> 都不同，
///   而签名要求「cookie 与设备身份同源」。旧实现只存一份全局 device.json，
///   换实例读授权就把上一台覆盖掉 —— 表现为「只有某一台的授权能用」，
///   用户很容易误判成账号互斥。这里锁住「按 install_id 归档、互不覆盖」。
/// </summary>
public static class DeviceStoreTests
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
        Console.WriteLine("\n[设备] 多实例设备档案（按 install_id 归档）");

        string old = Environment.GetEnvironmentVariable("HGVD_HOME");
        string dir = Path.Combine(Path.GetTempPath(),
            "hgvd-dev-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Environment.SetEnvironmentVariable("HGVD_HOME", dir);

            MumuAuth.SaveDevice(new DeviceIdentity
            {
                Iid = "iid-A",
                DeviceId = "dev-A",
                VersionName = "7.3.9.32",
                VersionCode = "73932",
            });
            MumuAuth.SaveDevice(new DeviceIdentity
            {
                Iid = "iid-B",
                DeviceId = "dev-B",
                VersionName = "7.3.9.32",
                VersionCode = "73932",
            });

            Ok("按 iid 取回同源设备（A）", MumuAuth.LoadDevice("iid-A").DeviceId == "dev-A");
            Ok("按 iid 取回同源设备（B）", MumuAuth.LoadDevice("iid-B").DeviceId == "dev-B");
            Ok("未知 iid 退回全局 latest", MumuAuth.LoadDevice("iid-X").DeviceId == "dev-B");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HGVD_HOME", old);
            try { Directory.Delete(dir, true); } catch { }
        }

        Console.WriteLine($"  —— 通过 {_pass}，失败 {_fail}");
        return _fail;
    }
}
