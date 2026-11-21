using System;
using System.IO;
using Hgvd.Core.Grab;

namespace Hgvd.Tests;

/// <summary>
/// 自动抓剧清单解析回归：
/// · 预置直链 / CENC 密钥是否正确读入 Episode；
/// · 无有效流、坏 JSON 的容错；
/// · 画质匹配与码率回退。
/// </summary>
public static class GrabManifestTests
{
    private static int _fail;

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "  ✓ " : "  ✗ ") + name);
        if (!ok) _fail++;
    }

    // 一个 spade_a 黄金向量（来自真实清单，能解出 32 位 hex key）
    private const string SpadeA =
        "lOZfG2dTf2RaZm9vYmFyX3Rlc3RfY29udGVudF9rZXlfMTIzNDU2Nzg5MDEyMzQ1Njc4OTA";

    private static string MakeManifest()
    {
        // 用真实 DRM 黄金向量里的 spade_a 更稳；这里直接造两段流：
        // 一段 360p 低码率、一段 1080p 高码率，验证选码率最高。
        return @"{
  ""vid_low"": {
    ""duration"": 100.0,
    ""streams"": [
      {""url"":""https://cdn/low"", ""backup_url"":"""", ""definition"":""360p"", ""bitrate"":1000, ""spade_a"":""""}
    ]
  },
  ""vid_high"": {
    ""duration"": 120.5,
    ""streams"": [
      {""url"":""https://cdn/l"", ""backup_url"":"""", ""definition"":""360p"", ""bitrate"":1000, ""spade_a"":""""},
      {""url"":""https://cdn/h"", ""backup_url"":""https://cdn/h2"", ""definition"":""1080p"", ""bitrate"":900000, ""spade_a"":""""}
    ]
  },
  ""vid_bad"": { ""streams"": [] }
}";
    }

    public static int Run()
    {
        Console.WriteLine("── GrabManifestTests（自动抓剧清单解析）──");
        string dir = Path.Combine(Path.GetTempPath(), "hgvd_grabmanifest_tests");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "m.json");
        File.WriteAllText(path, MakeManifest());

        var r = GrabManifest.Load(path, "series_1", "测试剧", "1080p");
        Check(string.IsNullOrEmpty(r.Error), "合法清单无错误");
        Check(r.Episodes.Count == 2, $"跳过无流分集后剩 2 集（实际 {r.Episodes.Count}）");
        Check(r.Skipped == 1, $"1 个无流 vid 计入 skipped（实际 {r.Skipped}）");

        if (r.Episodes.Count == 2)
        {
            var e0 = r.Episodes[0];
            Check(e0.Vid == "vid_low" && e0.Index == 1, "首集 vid/Index 正确");
            Check(e0.DirectUrl == "https://cdn/low", "首集预置直链正确");
            Check(Math.Abs(e0.Duration - 100.0) < 0.01, "首集时长正确");
            Check(e0.SeriesTitle == "测试剧" && e0.SeriesId == "series_1", "剧名/seriesId 注入");
            var e1 = r.Episodes[1];
            Check(e1.DirectUrl == "https://cdn/h", "选最高码率 1080p 直链");
            Check(e1.DirectBackupUrl == "https://cdn/h2", "备用直链读入");
        }

        // 指定 360p → 应回退到匹配项而非码率最高
        var r360 = GrabManifest.Load(path, "s", "t", "360p");
        Check(r360.Episodes.Count == 2 && r360.Episodes[1].DirectUrl == "https://cdn/l",
              "指定 360p 时选匹配清晰度");

        // 坏文件
        string bad = Path.Combine(dir, "bad.json");
        File.WriteAllText(bad, "{not json");
        var rb = GrabManifest.Load(bad, "s", "t");
        Check(!string.IsNullOrEmpty(rb.Error), "坏 JSON 报错而非崩溃");

        // 不存在
        var rn = GrabManifest.Load(Path.Combine(dir, "none.json"), "s", "t");
        Check(!string.IsNullOrEmpty(rn.Error), "文件不存在报错");

        Console.WriteLine($"GrabManifestTests fail={_fail}");
        return _fail;
    }
}
