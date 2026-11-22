using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hgvd.Core;
using Hgvd.Xqd;

namespace Hgvd.Tests;

/// <summary>
/// 两源共用清单（2026-11-22）：
/// 旧 queue.json / jobs.json 一次性迁移 → downloads.json；两源互不覆盖；字段不丢。
/// </summary>
public static class ManifestTests
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  ✓ {name}"); }
        else { _fail++; Console.WriteLine($"  ✗ {name}" + (detail.Length > 0 ? " — " + detail : "")); }
    }

    public static int Run()
    {
        Console.WriteLine("\n[共用清单] 一个文件 · 两源 section · 旧文件迁移");
        MigrationAndMerge();
        SaveBeforeLoadStillMigrates();
        RoundtripXqdFields();
        CrossSourceNoClobber();
        Console.WriteLine($"  —— 通过 {_pass}，失败 {_fail}");
        return _fail;
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hgvd_manifest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void MigrationAndMerge()
    {
        string dir = TempDir();
        try
        {
            // ── 旧格式：红果 queue.json + 兴趣岛 xqd\jobs.json ──
            string queuePath = Path.Combine(dir, "queue.json");
            File.WriteAllText(queuePath, JsonSerializer.Serialize(new[]
            {
                new QueueGroup
                {
                    SeriesId = "s1", SeriesTitle = "剧甲", ShortDrama = false,
                    Episodes =
                    {
                        new QueueEpisode { Vid = "v1", Index = 1, Title = "第 1 集", Duration = 120, VideoIdType = 2 },
                        new QueueEpisode { Vid = "v2", Index = 2, Title = "第 2 集", Duration = 121, VideoIdType = 2 },
                    },
                },
            }));

            string xqdRoot = Path.Combine(dir, "xqd");
            Directory.CreateDirectory(xqdRoot);
            File.WriteAllText(Path.Combine(xqdRoot, "jobs.json"), JsonSerializer.Serialize(new[]
            {
                new XqdJob
                {
                    Id = "j1", Title = "课程甲", Url = "https://e.com/a.m3u8", Camp = "营甲",
                    CampId = "c1", PeriodId = "p1", TopicId = "t1", RoomId = "r1",
                    LessonType = "VIDEO", Seq = 1, WhenDate = "2026-11-01",
                    OutPath = Path.Combine(dir, "out", "兴趣岛", "课程甲.mp4"),
                    ExpectDuration = 600, State = XqdState.Done, Size = 1234567,
                    Duration = 600, AddedAt = 100,
                },
            }));

            var hg = new QueueStore(queuePath);
            var xq = new XqdStore(xqdRoot);

            var hgGroups = hg.Load();
            Check("迁移：旧 queue.json 读回 1 组 / 2 集",
                hgGroups.Count == 1 && hgGroups[0].Episodes.Count == 2,
                $"{hgGroups.Count} 组");

            var xqJobs = xq.Load();
            Check("迁移：旧 jobs.json 读回 1 节", xqJobs.Count == 1, $"{xqJobs.Count} 节");
            Check("迁移：兴趣岛字段不丢（营/topic/路径/已完成）",
                xqJobs[0].Camp == "营甲" && xqJobs[0].TopicId == "t1"
                && xqJobs[0].OutPath.EndsWith("课程甲.mp4", StringComparison.Ordinal)
                && xqJobs[0].State == XqdState.Done,
                $"{xqJobs[0].Camp}/{xqJobs[0].TopicId}/{xqJobs[0].State}");

            string manifest = Path.Combine(dir, "downloads.json");
            Check("迁移：主存储是 downloads.json", File.Exists(manifest), manifest);
            Check("迁移：旧文件保留作备份",
                File.Exists(queuePath) && File.Exists(Path.Combine(xqdRoot, "jobs.json")));

            var sources = ReadSources(manifest);
            Check("迁移：同一文件里有两个 section",
                sources.Contains(ManifestSource.Hongguo) && sources.Contains(ManifestSource.Xiqudao),
                string.Join(",", sources));

            // 清空红果：旧 queue.json 还在，但不能被「复活」
            hg.Clear();
            Check("清空红果：读回空", hg.Load().Count == 0);
            Check("清空红果：不影响兴趣岛", xq.Load().Count == 1, $"{xq.Load().Count} 节");

            // 清空兴趣岛：旧 jobs.json 还在，但不能被「复活」
            xq.Save(Array.Empty<XqdJob>());
            Check("清空兴趣岛：读回空（旧 jobs.json 不复活）", xq.Load().Count == 0, $"{xq.Load().Count} 节");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void SaveBeforeLoadStillMigrates()
    {
        string dir = TempDir();
        try
        {
            string queuePath = Path.Combine(dir, "queue.json");
            File.WriteAllText(queuePath, JsonSerializer.Serialize(new[]
            {
                new QueueGroup
                {
                    SeriesId = "s1", SeriesTitle = "剧甲",
                    Episodes =
                    {
                        new QueueEpisode { Vid = "v1", Index = 1, Title = "第 1 集", Duration = 120 },
                        new QueueEpisode { Vid = "v2", Index = 2, Title = "第 2 集", Duration = 121 },
                    },
                },
            }));

            var hg = new QueueStore(queuePath);
            // ★ 模拟「界面还没恢复清单，就先发生了一次保存」：
            //   这时绝不能把旧 queue.json 的迁移标记提前打掉。
            hg.Save(new[]
            {
                new QueueGroup
                {
                    SeriesId = "s1", SeriesTitle = "剧甲",
                    Episodes = { new QueueEpisode { Vid = "v3", Index = 3, Title = "第 3 集", Duration = 122 } },
                },
            });

            var loaded = hg.Load();
            Check("先保存后加载：旧 queue.json 仍会合并进来（按 Id 补缺，不覆盖新行）",
                loaded.Count == 1 && loaded[0].Episodes.Count == 3
                && loaded[0].Episodes.Any(e => e.Vid == "v3"),
                loaded.Count == 1 ? $"{loaded[0].Episodes.Count} 集" : $"{loaded.Count} 组");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void RoundtripXqdFields()
    {
        string dir = TempDir();
        try
        {
            var store = new XqdStore(Path.Combine(dir, "state"));
            var job = new XqdJob
            {
                Id = "id-1", Title = "课程乙", Url = "https://e.com/b.m3u8", Camp = "体验营",
                CampId = "c2", PeriodId = "p2", TopicId = "t2", RoomId = "r2",
                LessonType = "AUDIO", Seq = 7, WhenDate = "2026-11-02",
                OutPath = Path.Combine(dir, "out", "兴趣岛", "课程乙-7.mp3"),
                ExpectDuration = 321.5, State = XqdState.Done, Size = 7654321,
                Duration = 320, AddedAt = 200,
            };
            store.Save(new[] { job });

            var loaded = new XqdStore(Path.Combine(dir, "state")).Load();
            Check("往返：兴趣岛全字段保留",
                loaded.Count == 1
                && loaded[0].Camp == "体验营"
                && loaded[0].CampId == "c2" && loaded[0].PeriodId == "p2"
                && loaded[0].TopicId == "t2" && loaded[0].RoomId == "r2"
                && loaded[0].LessonType == "AUDIO" && loaded[0].Seq == 7
                && loaded[0].WhenDate == "2026-11-02"
                && Math.Abs(loaded[0].ExpectDuration - 321.5) < 0.01
                && loaded[0].OutPath.EndsWith("课程乙-7.mp3", StringComparison.Ordinal)
                && loaded[0].State == XqdState.Done && loaded[0].Size == 7654321,
                loaded.Count == 1 ? loaded[0].Display : "空");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void CrossSourceNoClobber()
    {
        string dir = TempDir();
        try
        {
            var hg = new QueueStore(Path.Combine(dir, "queue.json"));
            var xq = new XqdStore(Path.Combine(dir, "xqd"));

            hg.Save(new[]
            {
                new QueueGroup
                {
                    SeriesId = "s9", SeriesTitle = "剧乙", ShortDrama = true,
                    Episodes = { new QueueEpisode { Vid = "v9", Index = 1, Title = "第 1 集", Duration = 90 } },
                },
            });
            xq.Save(new[]
            {
                new XqdJob { Id = "x9", Title = "课程丙", Url = "https://e.com/c.m3u8", Camp = "正式营" },
            });

            Check("共存：红果保存后兴趣岛仍在", xq.Load().Count == 1, $"{xq.Load().Count} 节");
            Check("共存：兴趣岛保存后红果仍在",
                hg.Load().Count == 1 && hg.Load()[0].SeriesTitle == "剧乙",
                $"{hg.Load().Count} 组");

            // 红果只写自己的 section：兴趣岛的重字段不能被冲掉
            hg.Save(hg.Load());
            var after = xq.Load();
            Check("共存：红果再保存不冲掉兴趣岛字段",
                after.Count == 1 && after[0].Camp == "正式营" && after[0].Url == "https://e.com/c.m3u8",
                after.Count == 1 ? after[0].Camp : "空");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static List<string> ReadSources(string manifestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var groups = doc.RootElement.GetProperty("Groups");
            return groups.EnumerateArray()
                .Select(g => g.GetProperty("Source").GetString() ?? "")
                .Distinct()
                .ToList();
        }
        catch { return new List<string>(); }
    }
}
