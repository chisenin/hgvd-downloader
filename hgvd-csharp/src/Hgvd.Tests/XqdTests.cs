using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hgvd.Xqd;
using Hgvd.Xqd.Engine;

namespace Hgvd.Tests;

/// <summary>
/// 兴趣岛内核（Hgvd.Xqd）的离线回归：
/// 链接清单解析 / JSON 合并 / 命名 / HLS 清单解析 / 队列去重与落盘。
///
/// ★ 这些全是纯函数与本地文件操作，不联网、不依赖 ffmpeg。
/// </summary>
public static class XqdTests
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  ✓ {name}"); }
        else
        {
            _fail++;
            Console.WriteLine($"  ✗ {name}" + (detail.Length > 0 ? " — " + detail : ""));
        }
    }

    private static void Eq(string name, string expected, string actual)
        => Check(name, expected == actual, $"期望「{expected}」实际「{actual}」");

    public static int Run()
    {
        Console.WriteLine("\n[兴趣岛] 链接解析 / 清单 / 命名 / 队列");
        LinkText();
        LinkJson();
        Naming();
        Manifest();
        Queue();
        Console.WriteLine($"  —— 通过 {_pass}，失败 {_fail}");
        return _fail;
    }

    // ══════════════════════════════════════════════════════════════

    private static void LinkText()
    {
        var items = LinkParser.Parse(
            "# 这是注释\n" +
            "\n" +
            "- 第一节：试看 | https://cdn.example.com/a.m3u8\n" +
            "第二节\t https://cdn.example.com/b.m3u8 \n" +
            "第三节 https://cdn.example.com/c.m3u8\n" +
            "https://cdn.example.com/d.mp4\n");
        Check("纯文本：4 条全部解析", items.Count == 4, $"{items.Count} 条");
        Eq("竖线分隔的标题", "第一节：试看", items[0].Title);
        Eq("竖线分隔的链接", "https://cdn.example.com/a.m3u8", items[0].Url);
        Eq("Tab 分隔", "第二节", items[1].Title);
        Eq("空格分隔", "第三节", items[2].Title);
        Eq("只给链接时用文件名当标题", "d", items[3].Title);

        var noUrl = new List<string>();
        var bad = LinkParser.Parse("这一行没有链接", noUrl);
        Check("无链接的行被丢弃并报错", bad.Count == 0 && noUrl.Count == 1);

        var dur = LinkParser.Parse(
            "课甲 | https://e.com/a.m3u8 [12:34]\n" +
            "课乙 | https://e.com/b.m3u8 时长=754");
        Check("尾部 [mm:ss] 解析为秒", Math.Abs(dur[0].Duration - 754) < 0.01,
            dur[0].Duration.ToString());
        Check("尾部 时长=秒 解析", Math.Abs(dur[1].Duration - 754) < 0.01,
            dur[1].Duration.ToString());

        var dup = LinkParser.Parse(
            "同一课 | https://e.com/x.m3u8\n同一课 | https://e.com/x.m3u8");
        Check("完全相同的行去重", dup.Count == 1, $"{dup.Count} 条");
    }

    private static void LinkJson()
    {
        var arr = LinkParser.Parse(
            "[{\"title\":\"课程A\",\"url\":\"https://e.com/a.m3u8\",\"camp\":\"太极营\"}," +
            " {\"name\":\"课程B\",\"url\":\"https://e.com/b.m3u8\"}]");
        Check("JSON 数组：2 条", arr.Count == 2, $"{arr.Count} 条");
        Eq("JSON 数组的 camp", "太极营", arr[0].Camp);

        var map = LinkParser.Parse("{\"课程C\":\"https://e.com/c.m3u8\"}");
        Check("JSON 标题→链接 映射", map.Count == 1 && map[0].Title == "课程C",
            map.Count.ToString());

        // xqd 的 links.json（只有 fileId + url）与 names.json（只有 fileId + name）合并
        var links = LinkParser.Parse(
            "{\"1397757892370777222\":{\"url\":\"https://e.com/1.m3u8\",\"camp\":\"引气归源太极营\"}}");
        var names = LinkParser.Parse("{\"1397757892370777222\":{\"name\":\"先导课：解谜自然之道\"}}");
        var merged = LinkParser.Merge(new[] { links, names });
        Check("links.json + names.json 按 fileId 合并成 1 条", merged.Count == 1,
            $"{merged.Count} 条");
        if (merged.Count == 1)
        {
            Eq("合并后拿到课名", "先导课：解谜自然之道", merged[0].Title);
            Eq("合并后拿到营名", "引气归源太极营", merged[0].Camp);
            Eq("合并后保留链接", "https://e.com/1.m3u8", merged[0].Url);
        }

        var namesOnly = LinkParser.Merge(new[] { names });
        Check("只有课名没有链接的清单不会进队列", namesOnly.Count == 0,
            $"{namesOnly.Count} 条");
    }

    private static void Naming()
    {
        Eq("非法字符替换", "a_b", XqdNaming.SafeName("a/b"));
        Eq("全角冒号保留", "先导课：解谜自然之道", XqdNaming.SafeName("先导课：解谜自然之道"));
        Eq("空名回落", "未命名", XqdNaming.SafeName(""));

        // ★ 直链音频踩过：标题带 .mp3 → 「xxx.mp3.mp4」→ 换扩展名后「xxx.mp3.mp3」
        Eq("标题里的媒体扩展名被剥掉", "第3课", XqdNaming.StemFromTitle("第3课.mp3"));
        Eq("正常标题不动", "先导课：解谜自然之道", XqdNaming.StemFromTitle("先导课：解谜自然之道"));

        string part = XqdNaming.TempPartPath(Path.Combine("D:", "v", "课.mp4"));
        Check("ffmpeg 临时文件以 .part.mp4 结尾",
            part.EndsWith("课.part.mp4", StringComparison.Ordinal), part);
    }

    private static void Manifest()
    {
        string text =
            "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:6\n" +
            "#EXTINF:5.0,\nseg0.ts\n#EXTINF:6.5,\nseg1.ts\n#EXT-X-ENDLIST\n";
        var segs = ManifestParser.ParseMediaForTest(text, "https://cdn.e.com/x/y/index.m3u8");
        Check("媒体清单分片数", segs.Count == 2, $"{segs.Count} 片");
        Eq("相对地址补成绝对", "https://cdn.e.com/x/y/seg0.ts", segs[0].Url);
        Check("EXTINF 时长", Math.Abs(segs[1].Duration - 6.5) < 0.001,
            segs[1].Duration.ToString());

        Check("短边取 min(w,h)（竖屏课按宽度比）",
            ManifestParser.ShortSideForTest("720x1280") == 720);
        Check("竖屏 480x852 → 480",
            ManifestParser.ShortSideForTest("480x852") == 480);
        Eq("DRM 孪生编号", "1440285",
            ManifestParser.TwinNumForTest("https://e.com/a/voddrm.token.xx.video_1440285_0.m3u8"));
        Check("分片文件名带 5 位序号前缀",
            SegmentDownloader.SegName("https://e.com/seg9.ts?sign=1", 9) == "00009_seg9.ts",
            SegmentDownloader.SegName("https://e.com/seg9.ts?sign=1", 9));
    }

    private static void Queue()
    {
        string root = Path.Combine(Path.GetTempPath(), "hgvd_xqd_test_" + Guid.NewGuid().ToString("N")[..8]);
        string outRoot = Path.Combine(root, "out");
        var store = new XqdStore(Path.Combine(root, "state"));
        var q = new XqdQueue(new XqdOptions { OutRoot = outRoot }, store);

        int added = q.Add(new[]
        {
            new LinkItem { Title = "课程甲", Url = "https://e.com/a.m3u8" },
            new LinkItem { Title = "课程乙", Url = "https://e.com/b.m3u8" },
            new LinkItem { Title = "课程甲", Url = "https://e.com/a.m3u8" },   // 重复
        });
        Check("队列去重后加入 2 节", added == 2, $"{added} 节");
        Check("行数 2", q.Jobs.Count == 2, $"{q.Jobs.Count} 行");
        Check("成品路径落在「下载目录\\兴趣岛」",
            q.Jobs.All(j => j.OutPath.StartsWith(Path.Combine(outRoot, "兴趣岛"), StringComparison.OrdinalIgnoreCase)),
            q.Jobs[0].OutPath);
        Check("成品名 = 课名.mp4",
            Path.GetFileName(q.Jobs[0].OutPath) == "课程甲.mp4",
            Path.GetFileName(q.Jobs[0].OutPath));

        // 同名不同链接 → 第二个改名 -2，绝不互相覆盖
        q.Add(new[] { new LinkItem { Title = "课程甲", Url = "https://e.com/other.m3u8" } });
        Check("同名不同链接自动改名 -2",
            q.Jobs.Any(j => Path.GetFileName(j.OutPath) == "课程甲-2.mp4"),
            string.Join(" | ", q.Jobs.Select(j => Path.GetFileName(j.OutPath))));

        // 落盘 → 新实例读回（模拟重启）
        var q2 = new XqdQueue(new XqdOptions { OutRoot = outRoot }, new XqdStore(Path.Combine(root, "state")));
        q2.Load();
        Check("重启后清单还在", q2.Jobs.Count == 3, $"{q2.Jobs.Count} 行");

        // 清单里写一个「已完成」再读回：非终态才会被重置
        var list = q2.Jobs.ToList();
        list[0].State = XqdState.Downloading;
        new XqdStore(Path.Combine(root, "state")).Save(list);
        var q3 = new XqdQueue(new XqdOptions { OutRoot = outRoot }, new XqdStore(Path.Combine(root, "state")));
        q3.Load();
        Check("重启时「下载中」回到「待下载」",
            q3.Jobs.First(j => j.Title == "课程甲").State == XqdState.Pending,
            q3.Jobs.First(j => j.Title == "课程甲").State);

        // 已存在判定：成品 ≥10KB 才算「已存在」，半截文件不能当成成品
        var jobA = q3.Jobs.First(j => j.Title == "课程甲");
        Directory.CreateDirectory(Path.GetDirectoryName(jobA.OutPath));
        File.WriteAllBytes(jobA.OutPath, new byte[20 * 1024]);
        var jobB = q3.Jobs.First(j => j.Title == "课程乙");
        Directory.CreateDirectory(Path.GetDirectoryName(jobB.OutPath));
        File.WriteAllBytes(jobB.OutPath, new byte[1024]);
        int changed = q3.Rescan();
        Check("成品 ≥10KB → 已存在", jobA.State == XqdState.Exists,
            $"{jobA.State}（变化 {changed} 项）");
        Check("1KB 的半截文件不算已存在", jobB.State == XqdState.Pending, jobB.State);

        try { Directory.Delete(root, true); } catch { }
    }
}
