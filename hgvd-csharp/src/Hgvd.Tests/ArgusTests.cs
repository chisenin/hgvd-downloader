using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Hgvd.Core.Sign;

namespace Hgvd.Tests;

/// <summary>
/// x-argus / x-ladon 的**字节级**黄金向量回归。
///
/// ★★ 为什么必须单独有这个测试：
///   2026-11-14 实测发现 C# 版 x-argus 有两处静默错误 ——
///     ① PKCS#7 填充值用了 0（应为填充长度）
///     ② <c>ts &lt;&lt; 1</c> 在 int32 内溢出（3400000000 → -894967296）
///   两者都**不报错**，只是签名对不上。服务端收到后既不返回错误码、
///   也不返回数据，只回一个 **HTTP 200 + 空 body**，
///   表现出来就是「播放接口被风控」—— 极难定位。
///   而当时的 golden_headers.json 里 x-argus 只有 4 字节桩，
///   压根没覆盖真签名 ⇒ 漏网。
///
/// ★ 向量由 Python 版算出（见 <c>tools/gen_argus_golden.py</c>），
///   Python 版已与 Go 参考实现逐字节对齐。
/// </summary>
public static class ArgusTests
{
    private static int _pass;
    private static int _fail;

    private static void Eq(string name, string expected, string actual)
    {
        bool ok = expected == actual;
        if (ok) { _pass++; Console.WriteLine($"  ✓ {name}"); }
        else
        {
            _fail++;
            Console.WriteLine($"  ✗ {name}");
            Console.WriteLine($"      期望 {expected}");
            Console.WriteLine($"      实际 {actual}");
        }
    }

    public static int Run()
    {
        Console.WriteLine("\n[签名层] x-argus / x-ladon 字节级比对");

        string path = Path.Combine(AppContext.BaseDirectory, "golden_argus.json");
        if (!File.Exists(path))
        {
            string guess = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "tools", "golden_argus.json"));
            if (File.Exists(guess)) path = guess;
            else
            {
                Console.WriteLine("  ! 找不到 golden_argus.json，跳过");
                return 0;
            }
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var cases = doc.RootElement.GetProperty("cases");

        foreach (var c in cases.EnumerateArray())
        {
            string name = c.GetProperty("name").GetString() ?? "";
            string query = c.GetProperty("query").GetString() ?? "";
            byte[] bodyMd5 = Util.FromHex(c.GetProperty("body_md5_hex").GetString() ?? "");
            int ts = c.GetProperty("ts").GetInt32();
            uint nonce = c.GetProperty("nonce").GetUInt32();
            string aid = c.GetProperty("aid").GetString() ?? "";
            string deviceId = c.GetProperty("device_id").GetString() ?? "";
            string installId = c.GetProperty("install_id").GetString() ?? "";
            string wantArgus = c.GetProperty("x_argus").GetString() ?? "";
            string wantLadon = c.GetProperty("x_ladon").GetString() ?? "";

            string gotArgus = Argus.Header(query, bodyMd5, ts, nonce, aid, deviceId, installId);
            string gotLadon = Argus.LadonHeader(ts, nonce, aid);

            Eq($"{name} · x-argus 逐字节一致", wantArgus, gotArgus);
            Eq($"{name} · x-ladon 逐字节一致", wantLadon, gotLadon);
        }

        Console.WriteLine($"  —— 通过 {_pass}，失败 {_fail}");
        return _fail;
    }
}
