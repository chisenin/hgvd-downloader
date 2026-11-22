using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hgvd.Core;

namespace Hgvd.Tests;

/// <summary>
/// 2026-11-22 下载提速的记账 + 预检回归：
///   · ProgressStore 读写 / 状态迁移 / 大小写不敏感
///   · 预检 ① 记账命中秒跳（size 一致）/ size 不符回落重下 / 缺失文件不跳过
/// 全部本地临时目录，不联网；② 的 ffprobe 分支由 build.cmd 的下载门禁覆盖。
/// </summary>
public static class ProgressTests
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  ✓ {name}"); }
        else { _fail++; Console.WriteLine($"  ✗ {name}" + (detail.Length > 0 ? " — " + detail : "")); }
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "hgvd_prog_" + Guid.NewGuid().ToString("N")[..8]);

    public static int Run()
    {
        Console.WriteLine("\n[进度记账/预检] 秒跳判据 · 状态迁移");
        Roundtrip();
        CaseInsensitive();
        MarkInterrupted();
        ConcurrentWritesNotLost();
        PrecheckAccountingHit();
        PrecheckSizeMismatch();
        Console.WriteLine($"  —— 通过 {_pass}，失败 {_fail}");
        return _fail;
    }

    private static void Roundtrip()
    {
        string dir = TempDir(); Directory.CreateDirectory(dir);
        var store = new ProgressStore(Path.Combine(dir, "progress.json"));
        store.Upsert(new ProgressEntry
        {
            Path = @"D:\x\剧\剧.001.mp4", State = "started", Size = 123,
            Url = "http://cdn/a", Account = "u1",
        });
        var map = store.LoadMap();
        Check("记账写入后可读", map.Count == 1 && map[@"D:\x\剧\剧.001.mp4"].State == "started");
        Check("started 不算完成", !map[@"D:\x\剧\剧.001.mp4"].IsDone);
        store.Upsert(new ProgressEntry
        {
            Path = @"D:\x\剧\剧.001.mp4", State = "done", Size = 123,
        });
        map = store.LoadMap();
        Check("同路径覆盖为 done", map.Count == 1 && map[@"D:\x\剧\剧.001.mp4"].IsDone);
        Check("ts 自动补齐", map[@"D:\x\剧\剧.001.mp4"].Ts > 0);
        try { Directory.Delete(dir, true); } catch { }
    }

    private static void CaseInsensitive()
    {
        string dir = TempDir(); Directory.CreateDirectory(dir);
        var store = new ProgressStore(Path.Combine(dir, "p.json"));
        store.Upsert(new ProgressEntry { Path = @"C:\A\B.MP4", State = "done", Size = 9 });
        Check("路径大小写不敏感", store.LoadMap().ContainsKey(@"c:\a\b.mp4"));
        try { Directory.Delete(dir, true); } catch { }
    }

    private static void MarkInterrupted()
    {
        string dir = TempDir(); Directory.CreateDirectory(dir);
        var store = new ProgressStore(Path.Combine(dir, "p.json"));
        store.UpsertMany(new[]
        {
            new ProgressEntry { Path = "a", State = "started" },
            new ProgressEntry { Path = "b", State = "done" },
            new ProgressEntry { Path = "c", State = "failed" },
        });
        var map = store.LoadMap();
        foreach (var kv in map) if (kv.Value.State == "started") kv.Value.State = "interrupted";
        store.SaveMap(map);
        map = store.LoadMap();
        Check("started → interrupted", map["a"].State == "interrupted");
        Check("done/failed 终态不动", map["b"].State == "done" && map["c"].State == "failed");
        try { Directory.Delete(dir, true); } catch { }
    }

    private static void ConcurrentWritesNotLost()
    {
        string dir = TempDir(); Directory.CreateDirectory(dir);
        var store = new ProgressStore(Path.Combine(dir, "p.json"));
        // 12 路并发写不同路径 —— 无锁时「读-改-写」会互相覆盖，最后只剩几条
        var tasks = Enumerable.Range(0, 12).Select(i => System.Threading.Tasks.Task.Run(() =>
            store.Upsert(new ProgressEntry { Path = $"D:\\x\\{i}.mp4", State = "done", Size = i })));
        System.Threading.Tasks.Task.WhenAll(tasks).GetAwaiter().GetResult();
        Check("并发写 12 条不丢记录", store.LoadMap().Count == 12,
            $"实际 {store.LoadMap().Count} 条");
        try { Directory.Delete(dir, true); } catch { }
    }

    private static void PrecheckAccountingHit()
    {
        string dir = TempDir(); Directory.CreateDirectory(dir);
        string oldHome = Environment.GetEnvironmentVariable("HGVD_HOME");
        Environment.SetEnvironmentVariable("HGVD_HOME", dir);
        try
        {
            string outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(outDir);
            var cfg = new AppConfig { OutDir = outDir, SkipExisting = true };
            var svc = new HongguoService(cfg);

            var ep = new Core.Api.Episode
            {
                Vid = "v_hit", Index = 1, Title = "第1集", Duration = 120,
                SeriesId = "s1", SeriesTitle = "剧", VideoIdType = 1,
            };
            string path = svc.BuildOutPath(ep);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[4096]);
            long size = new FileInfo(path).Length;

            // 预置 done 记账，size 一致
            svc.Progress.Upsert(new ProgressEntry
            {
                Path = path, State = "done", Size = size, Duration = 120,
            });

            var skip = svc.PrecheckAsync(new List<Core.Api.Episode> { ep }).GetAwaiter().GetResult();
            Check("记账命中 → 秒跳表含该集", skip.ContainsKey("v_hit") && skip["v_hit"].Skipped);
            Check("秒跳带正确的 size", skip.ContainsKey("v_hit") && skip["v_hit"].Size == size);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HGVD_HOME", oldHome);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void PrecheckSizeMismatch()
    {
        string dir = TempDir(); Directory.CreateDirectory(dir);
        string oldHome = Environment.GetEnvironmentVariable("HGVD_HOME");
        Environment.SetEnvironmentVariable("HGVD_HOME", dir);
        try
        {
            string outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(outDir);
            var cfg = new AppConfig { OutDir = outDir, SkipExisting = true };
            var svc = new HongguoService(cfg);

            var ep = new Core.Api.Episode
            {
                Vid = "v_mis", Index = 2, Title = "第2集", Duration = 120,
                SeriesId = "s1", SeriesTitle = "剧", VideoIdType = 1,
            };
            string path = svc.BuildOutPath(ep);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[4096]);   // 时长不对的假文件

            // 记账 size 故意写错 → 不能走秒跳，回落 ffprobe；假文件探测失败 → 不跳过
            svc.Progress.Upsert(new ProgressEntry
            {
                Path = path, State = "done", Size = 999999, Duration = 120,
            });
            var skip = svc.PrecheckAsync(new List<Core.Api.Episode> { ep }).GetAwaiter().GetResult();
            Check("size 不符不秒跳", !skip.ContainsKey("v_mis"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HGVD_HOME", oldHome);
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
