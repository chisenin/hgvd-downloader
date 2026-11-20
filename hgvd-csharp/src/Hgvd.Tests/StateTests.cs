using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hgvd.Avalonia.ViewModels;
using Hgvd.Core;
using Hgvd.Xqd;

namespace Hgvd.Tests;

/// <summary>
/// 2026-11-21 补齐的能力回归：
/// 红果清单落盘 / 两源共用的下载历史 / 兴趣岛已存在判定 / 5 档筛选 / 剩余时间列。
/// 全部是纯函数与本地文件操作，不联网、不跑 ffmpeg。
/// </summary>
public static class StateTests
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
        Console.WriteLine("\n[清单/历史/去重] 落盘 · 恢复 · 两源一致");
        QueueStoreRoundtrip();
        QueueStoreBadFile();
        HistoryRoundtrip();
        HistoryPythonCompat();
        HistoryCap();
        Dedupe();
        Filter();
        RowTexts();
        Pause();
        Console.WriteLine($"  —— 通过 {_pass}，失败 {_fail}");
        return _fail;
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hgvd_state_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void QueueStoreRoundtrip()
    {
        string dir = TempDir();
        try
        {
            var store = new QueueStore(Path.Combine(dir, "queue.json"));
            store.Save(new[]
            {
                new QueueGroup
                {
                    SeriesId = "s1", SeriesTitle = "教学合集甲", ShortDrama = false,
                    Episodes =
                    {
                        new QueueEpisode { Vid = "v1", Index = 1, Title = "第 1 集", Duration = 300, VideoIdType = 1 },
                        new QueueEpisode { Vid = "v2", Index = 2, Title = "第 2 集", Duration = 301.5, VideoIdType = 1 },
                    },
                },
                new QueueGroup
                {
                    SeriesId = "s2", SeriesTitle = "短剧乙", ShortDrama = true,
                    Episodes = { new QueueEpisode { Vid = "v3", Index = 1, Title = "第 1 集", Duration = 60, VideoIdType = 0 } },
                },
            });

            var loaded = new QueueStore(Path.Combine(dir, "queue.json")).Load();
            Check("红果清单：两组都能读回", loaded.Count == 2, $"{loaded.Count} 组");
            Check("红果清单：组字段（剧名/短剧标记）保留",
                loaded[1].SeriesTitle == "短剧乙" && loaded[1].ShortDrama,
                $"{loaded[1].SeriesTitle} / {loaded[1].ShortDrama}");
            Check("红果清单：分集字段（vid/序号/时长）保留",
                loaded[0].Episodes.Count == 2
                && loaded[0].Episodes[1].Vid == "v2"
                && loaded[0].Episodes[1].Index == 2
                && Math.Abs(loaded[0].Episodes[1].Duration - 301.5) < 0.01,
                JsonSerializer.Serialize(loaded[0].Episodes[1]));

            store.Clear();
            Check("红果清单：Clear 后读回空", new QueueStore(Path.Combine(dir, "queue.json")).Load().Count == 0);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void QueueStoreBadFile()
    {
        string dir = TempDir();
        try
        {
            string p = Path.Combine(dir, "queue.json");
            File.WriteAllText(p, "{ 这不是 JSON ");
            Check("红果清单：坏文件当成空清单，不抛异常", new QueueStore(p).Load().Count == 0);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void HistoryRoundtrip()
    {
        string dir = TempDir();
        try
        {
            var h = new HistoryStore(Path.Combine(dir, "history.json"));
            h.Add(new HistoryEntry { Source = "红果", Title = "剧甲", Episode = "第 1 集", Path = @"D:\a\1.mp4", Size = 100, Ts = 1000 });
            h.Add(new HistoryEntry { Source = "兴趣岛", Title = "营乙", Episode = "课 1", Path = @"D:\b\1.mp4", Size = 200, Ts = 2000 });
            h.Add(new HistoryEntry { Source = "红果", Title = "剧甲", Episode = "第 1 集", Path = @"D:\a\1.mp4", Size = 120, Ts = 3000 });

            var list = h.Load();
            Check("历史：同一成品路径只留一条", list.Count == 2, $"{list.Count} 条");
            Check("历史：最新在前", list[0].Ts == 3000 && list[1].Ts == 2000,
                string.Join(",", list.Select(x => x.Ts)));
            Check("历史：同路径记录被更新（大小 120）", list[0].Size == 120, list[0].Size.ToString());
            Check("历史：两源来源都保留", list[0].Source == "红果" && list[1].Source == "兴趣岛",
                $"{list[0].Source}/{list[1].Source}");
            Check("历史：显示用文本（标题 · 集名 / 时间）",
                list[0].TitleText == "剧甲 · 第 1 集" && list[0].AtText != "--",
                list[0].TitleText + " / " + list[0].AtText);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void HistoryPythonCompat()
    {
        string dir = TempDir();
        try
        {
            string p = Path.Combine(dir, "history.json");
            // Python 版写出的键是 snake_case，必须能读
            File.WriteAllText(p, @"[
              {""path"":""D:\\x\\1.mp4"",""title"":""剧丙"",""episode"":""第 2 集"",
               ""series_id"":""s9"",""vid"":""v9"",""state"":""已完成"",
               ""size"":123,""duration"":45.5,""ts"":1700000000}
            ]");
            var list = new HistoryStore(p).Load();
            Check("历史：能读 Python 版写的 history.json",
                list.Count == 1 && list[0].SeriesId == "s9" && list[0].Vid == "v9"
                && list[0].Episode == "第 2 集" && Math.Abs(list[0].Duration - 45.5) < 0.01,
                JsonSerializer.Serialize(list));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void HistoryCap()
    {
        string dir = TempDir();
        try
        {
            var h = new HistoryStore(Path.Combine(dir, "history.json"));
            var many = Enumerable.Range(0, HistoryStore.Max + 50)
                .Select(i => new HistoryEntry { Path = $@"D:\h\{i}.mp4", Title = "T" + i, Ts = i + 1 })
                .ToList();
            h.Save(many);
            Check($"历史：最多保留 {HistoryStore.Max} 条",
                h.Load().Count == HistoryStore.Max, h.Load().Count.ToString());

            h.Add(new HistoryEntry { Path = @"D:\h\new.mp4", Title = "新", Ts = 999999 });
            var list = h.Load();
            Check("历史：新增后排最前且不超上限",
                list.Count == HistoryStore.Max && list[0].Path == @"D:\h\new.mp4",
                $"{list.Count} 条，首条 {list[0].Path}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void Dedupe()
    {
        const long mb = 1024 * 1024;
        Check("去重：小于 1MB 一律不算已存在", !XqdDedupe.IsExisting(mb - 1, 100, 100));
        Check("去重：≥1MB 且无预期时长 → 算已存在", XqdDedupe.IsExisting(2 * mb, 0, 0));
        Check("去重：时长在容差内 → 算已存在", XqdDedupe.IsExisting(2 * mb, 100, 100.8));
        Check("去重：时长超容差 → 不算（重下）", !XqdDedupe.IsExisting(2 * mb, 100, 102));
        Check("去重：长课按 0.5% 容差（4000s ±15s）",
            XqdDedupe.IsExisting(2 * mb, 4000, 4015) && !XqdDedupe.IsExisting(2 * mb, 4000, 4030));
        Check("去重：ffprobe 读不出时长 → 回退体积门槛", XqdDedupe.IsExisting(2 * mb, 4000, 0));
    }

    private static void Filter()
    {
        Check("筛选：待下载", EpViewFilter.Match("待下载", EpViewFilter.Pending)
            && !EpViewFilter.Match("下载中", EpViewFilter.Pending));
        Check("筛选：下载中（含暂停/封装/校验）",
            EpViewFilter.Match("封装中", EpViewFilter.Running)
            && EpViewFilter.Match("已暂停", EpViewFilter.Running)
            && !EpViewFilter.Match("已完成", EpViewFilter.Running));
        Check("筛选：已完成（含已存在）",
            EpViewFilter.Match("已完成", EpViewFilter.Done)
            && EpViewFilter.Match("已存在", EpViewFilter.Done));
        Check("筛选：失败 / 全部", EpViewFilter.Match("失败", EpViewFilter.Failed)
            && EpViewFilter.Match("失败", EpViewFilter.All)
            && EpViewFilter.Match("随便什么", EpViewFilter.All));
    }

    private static void Pause()
    {
        var gate = new Hgvd.Core.Engine.PauseGate();
        Check("暂停闸门：默认放行", gate.WaitAsync().IsCompleted);

        gate.Pause();
        var wait = gate.WaitAsync();
        Check("暂停闸门：暂停后阻塞", !wait.IsCompleted && gate.IsPaused);
        gate.Resume();
        Check("暂停闸门：继续后放行", wait.Wait(1000) && !gate.IsPaused);

        gate.Pause();
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        bool canceled = false;
        try { gate.WaitAsync(cts.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { canceled = true; }
        Check("暂停闸门：取消能打断等待", canceled);
        gate.Resume();
    }

    private static void RowTexts()
    {
        var hg = new EpisodeRow(new Core.Api.Episode
        {
            Vid = "v", Index = 1, Title = "第 1 集", Duration = 60,
            SeriesId = "s", SeriesTitle = "剧", VideoIdType = 1,
        });
        Check("来源列：红果行", hg.SourceText == "红果", hg.SourceText);

        var job = new XqdJob { Title = "课", Camp = "营", OutPath = @"D:\x\1.mp4" };
        var xq = EpisodeRow.FromXqd(job);
        Check("来源列：兴趣岛行", xq.SourceText == "兴趣岛", xq.SourceText);
        Check("来源列：分组标题行留空",
            EpisodeRow.Header("s", "剧", 1).SourceText == "");

        hg.State = "下载中";
        hg.Total = 10 * 1024 * 1024;
        hg.Down = 2 * 1024 * 1024;
        var raised = new List<string>();
        hg.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
        hg.Speed = 1024 * 1024;      // 剩 8MB / 1MBps = 8s
        Check("剩余时间：按剩余字节/速度算", hg.EtaText == "00:08", hg.EtaText);
        Check("剩余时间：速度变化要通知 EtaText", raised.Contains(nameof(EpisodeRow.EtaText)),
            string.Join(",", raised));
        hg.State = "已完成";
        Check("剩余时间：完成后显示占位符", hg.EtaText == "--", hg.EtaText);
    }
}
