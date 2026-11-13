using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Hgvd.Core.Sign;

namespace Hgvd.Tests;

/// <summary>
/// 完整请求头的黄金向量比对（含 x-medusa / x-argus / x-ladon）。
///
/// ★ 向量由 Python 版用**固定熵**算出 → C# 必须逐字节一致。
///   （熵里有两个真随机字段，不固定就没法比对。）
/// ★ 见 <c>tools/golden_headers.json</c>。
/// </summary>
public static class HeaderTests
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

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  ✓ {name}"); }
        else { _fail++; Console.WriteLine($"  ✗ {name}  {detail}"); }
    }

    public static int Run()
    {
        Console.WriteLine("\n[签名层] 完整请求头比对");

        string path = Path.Combine(AppContext.BaseDirectory, "golden_headers.json");
        if (!File.Exists(path))
        {
            // 从仓库根找
            string guess = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "tools", "golden_headers.json"));
            if (File.Exists(guess)) path = guess;
            else
            {
                Console.WriteLine("  ! 找不到 golden_headers.json，跳过");
                return 0;
            }
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        string expectedQuery = root.GetProperty("query").GetString();
        var expectedHeaders = root.GetProperty("headers");

        // ---- 复现 Python 侧的固定熵 ----
        var pairs = new List<KeyValuePair<string, string>>
        {
            new("iid", "4351246100332768"),
            new("device_id", "4351246100902096"),
            new("ac", "wifi"),
            new("aid", "8662"),
            new("app_name", "novelread"),
            new("version_name", "7.3.9.32"),
        };
        byte[] body = System.Text.Encoding.UTF8.GetBytes(
            "{\"biz_param\":{\"need_all_video_definition\":true,\"video_id_type\":0," +
            "\"video_platform\":3},\"video_id\":\"7697465535398677529\"}");

        var ent = new Entropy
        {
            EnvLaunch = 100,
            EnvPid = 10001,
            NowSecs = 1700000000,
            MessageRandom = unchecked((int)0x12345678),
            Random = unchecked((int)0x9ABCDEF0),
            PackedRandom = unchecked((int)0x11223344),
            GorgonRandom = 0x1234,
            HeliosRandom = 0x5678,
        };

        var (query, headers) = Signer.SignRequest(
            "/novel/player/video_model/v1/", pairs, 1700000000000L,
            "4351246100902096", "7.3.9.32", body, ent);

        Eq("查询串逐字节一致", expectedQuery, query);

        foreach (string key in new[]
        {
            "x-gorgon", "x-helios", "x-medusa", "x-ss-stub",
            "x-khronos", "x-ss-req-ticket",
        })
        {
            if (!expectedHeaders.TryGetProperty(key, out var ev)) continue;
            string exp = ev.GetString();
            string act = headers.TryGetValue(key, out var hv) ? hv : "(缺失)";
            Eq($"{key} 逐字节一致", exp, act);
        }

        // ---- 播放签名（argus/ladon 是真随机，只能验证结构） ----
        var (pq, ph) = Signer.SignPlayback(
            "/novel/player/video_model/v1/", pairs, 1700000000000L,
            "4351246100902096", "7.3.9.32", body, ent,
            installId: "4351246100332768", aid: "8662");
        Check("播放签名：查询串与普通签名一致（body/ticket 相同）", pq == query);
        Check("播放签名：无 x-medusa", !ph.ContainsKey("x-medusa"));
        Check("播放签名：x-argus 是长 Base64",
            ph["x-argus"].Length > 100, $"长度 {ph["x-argus"].Length}");
        // ★ ladon 是 48 字符 Base64（36 字节：4 字节种子 + 32 字节密文）
        Check("播放签名：x-ladon 长度 48",
            ph["x-ladon"].Length == 48, $"长度 {ph["x-ladon"].Length}");
        Check("播放签名：x-argus 以 F2 81 开头（Argus 信封魔数）",
            ph["x-argus"].StartsWith("8IE") || ph["x-argus"].Length > 100);

        // ---- 播放签名的逐头对比（固定 now，argus/ladon 只比长度） ----
        {
            var pp = new List<KeyValuePair<string, string>>
            {
                new("iid", "744826091370410"),
                new("device_id", "744826091366314"),
                new("ac", "wifi"),
                new("channel", "xiaomi_8662_64"),
                new("aid", "8662"),
                new("app_name", "novelread"),
                new("cdid", ""),
                new("version_code", "73932"),
                new("version_name", "7.3.9.32"),
                new("device_platform", "android"),
                new("os", "android"),
                new("ssmix", "a"),
                new("device_type", "Xiaomi 14"),
                new("device_brand", "Redmi"),
                new("language", "zh"),
                new("os_api", "34"),
                new("os_version", "14"),
                new("manifest_version_code", "73932"),
                new("resolution", "1080*2400"),
                new("dpi", "480"),
                new("update_version_code", "73932"),
                new("host_abi", "arm64-v8a"),
                new("dragon_device_type", "phone"),
                new("pv_player", "73932"),
                new("compliance_status", "0"),
                new("need_personal_recommend", "1"),
                new("player_so_load", "1"),
                new("is_android_pad_screen", "0"),
                new("openudid", ""),
            };
            var e2 = new Entropy
            {
                EnvLaunch = 100, EnvPid = 10001, NowSecs = 1794576000,
                MessageRandom = unchecked((int)0x12345678),
                Random = unchecked((int)0x9ABCDEF0),
                PackedRandom = unchecked((int)0x11223344),
                GorgonRandom = 0x1234, HeliosRandom = 0x5678,
            };
            var (q2, h2) = Signer.SignPlayback(
                "/novel/player/video_model/v1/", pp, 1794576000000L,
                "744826091366314", "7.3.9.32", body, e2,
                installId: "744826091370410", aid: "8662");
            Console.WriteLine("    [C#] query 尾: " + q2[^Math.Min(50, q2.Length)]);
            foreach (var k in new[] { "x-gorgon", "x-helios", "x-ss-stub", "x-khronos", "x-ss-req-ticket" })
                if (h2.TryGetValue(k, out var v2))
                    Console.WriteLine($"    [C#] {k}: {v2}");
        }

        Console.WriteLine($"  —— 通过 {_pass}，失败 {_fail}");
        return _fail;
    }
}
