using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Hgvd.Avalonia.ViewModels;
using Hgvd.Core;
using Hgvd.Core.Engine;

namespace Hgvd.Avalonia.Views;

/// <summary>
/// 命令行自检与开发期演示。
/// ★ 与 WPF 版一一对应，便于对照回归。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 无头自检：加载收藏夹 → 展开指定合集的分集 → 打印摘要。
    /// ★ 用法：<c>hgvd-downloader-vX.exe --selftest [series_id]</c>
    /// </summary>
    public async Task SelfTestAsync(string seriesId)
    {
        Console.WriteLine("═══ 界面自检 ═══");
        Diag("SelfTest 开始");
        try
        {
            var items = await _svc.ListShelfAsync(true, 30, _cts.Token);
            Console.WriteLine($"[1] 收藏夹 {items.Count} 项");
            foreach (var it in items.Take(5))
                Console.WriteLine($"    · {it.Title}（{it.TypeLabel}，{it.EpisodeCnt} 集）");

            string sid = seriesId;
            if (string.IsNullOrEmpty(sid))
                sid = items.FirstOrDefault(i => !i.IsShortDrama)?.SeriesId ?? "";
            if (string.IsNullOrEmpty(sid)) { Console.WriteLine("没有可测的内容"); return; }

            var eps = await _svc.ListEpisodesAsync(sid, 3, _cts.Token);
            Console.WriteLine($"[2] 合集 {sid} → {eps.Count} 集");
            foreach (var e in eps.Take(5))
                Console.WriteLine($"    {e.Index}. {e.Title}  ({e.Duration:0}s)");

            if (eps.Count == 0) return;
            var (url, size, key, codec, failure, backup) = await _svc.ResolveFullUrlAsync(
                eps[0].Vid, eps[0].VideoIdType, "720p", _cts.Token);
            Console.WriteLine($"[3] 播放地址：url={(url.Length > 0 ? "有" : "无")} " +
                              $"size={size} codec={codec} 加密={(key.Length > 0 ? "是" : "否")}");
            if (failure != "") Console.WriteLine("    失败原因：" + failure);
            if (url.Length > 0) Console.WriteLine("    " + url[..Math.Min(120, url.Length)]);
        }
        catch (Exception ex)
        {
            Console.WriteLine("自检失败：" + ex);
        }
        Diag("SelfTest 结束");
    }

    /// <summary>
    /// 开发期演示：加载一部剧的分集并真的开始下载（不退出）。
    /// ★ 用途：截图核对进度条渐变、百分比居中、大小/速度列等显示效果。
    /// </summary>
    public async Task DemoAsync(string seriesId, int skip = 0)
    {
        if (string.IsNullOrEmpty(seriesId))
        {
            Log("用法：--demo <series_id>");
            return;
        }
        var rows = await LoadEpisodesRawAsync(seriesId, seriesId);
        if (rows.Count == 0) return;
        // ★ 演示用：把这几集临时挂到清单里，才能按 Vid 找到数据
        foreach (var r in rows.Take(3))
        {
            EpisodeRows.Add(r);
            EpisodeView.Add(r);
        }
        _stopRequested = false;
        _downloading = true;
        BtnRun.IsEnabled = false;
        BtnStop.IsEnabled = true;
        await DownloadRowsAsync(rows.Skip(skip).Take(3).ToList());
        _downloading = false;
        BtnRun.IsEnabled = true;
        BtnStop.IsEnabled = false;
    }

    /// <summary>
    /// 批量下载冒烟测试：真的挑几部剧下载一遍，验证整条链路。
    /// ★ 用法：<c>hgvd-downloader-vX.exe --batchtest [部数]</c>（默认 2 部，各取前 2 集）
    /// </summary>
    public async Task BatchTestAsync(int seriesCount = 2, int maxEpisodes = 2)
    {
        Log("═══ 批量下载冒烟测试 ═══");
        var report = new StringBuilder();
        void Line(string s) { Log(s); report.AppendLine(s); }

        for (int i = 0; i < 300 && (_shelfBusy || ShelfRows.Count == 0); i++)
            await Task.Delay(100);
        if (ShelfRows.Count == 0) { Line("✗ 收藏夹没加载出来"); }

        // 挑集数最少的几部（测起来快），且要有标题
        var picked = ShelfRows
            .Where(r => !r.IsShortDrama && !string.IsNullOrEmpty(r.Title))
            .Where(r => r.EpisodeCnt > 0)
            .OrderBy(r => r.EpisodeCnt)
            .Take(seriesCount).ToList();
        if (picked.Count == 0)
            picked = ShelfRows.Where(r => !r.IsShortDrama).Take(seriesCount).ToList();

        Line($"选中 {picked.Count} 部：{string.Join("、", picked.Select(r => r.Display))}");

        _stopRequested = false;
        _downloading = true;
        var res = await BatchDownloadAsync(picked);
        _downloading = false;

        Line($"结果：处理 {res.DoneSeries} 部 · 成功 {res.Ok} 集 · 失败 {res.Fail} 集"
             + (res.Aborted ? " · **空 body 中止**" : ""));
        foreach (var e in _downloadErrors) Line("  错误：" + e);
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hgv_tool");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "batchtest.log"),
                report.ToString() + $"=== ok={res.Ok} fail={res.Fail} ===\n");
        }
        catch { }
    }

    /// <summary>
    /// 界面自检：把「点列表行 / 点勾选框 / 点全选」这三条交互路径**真正跑一遍**
    /// （走 XAML 绑好的事件，不是直接调方法），逐条断言结果。
    ///
    /// ★ 用法：<c>hgvd-downloader-vX.exe --uitest</c>
    /// </summary>
    public async Task UiTestAsync()
    {
        Log("═══ 界面自检 ═══");
        int fail = 0;
        var report = new StringBuilder();
        void Check(string name, bool ok, string detail = "")
        {
            string line = $"  [{(ok ? "✓" : "✗")}] {name}{(detail.Length > 0 ? " — " + detail : "")}";
            Log(line);
            report.AppendLine(line);
            // ★ 同时打到控制台 —— 命令行跑 --uitest 时能直接看到结果
            Console.WriteLine(line);
            if (!ok) fail++;
        }

        // ① 等收藏夹**彻底**加载完（标题也要补齐）
        for (int i = 0; i < 300 && (_shelfBusy || ShelfRows.Count == 0); i++)
            await Task.Delay(100);
        Check("收藏夹已加载", ShelfRows.Count > 0, $"{ShelfRows.Count} 项");
        int titledByBatch = ShelfRows.Count(r => !string.IsNullOrEmpty(r.Title));

        // ①★ 标题回落（后台用 album_detail 补那几条没标题的）
        for (int i = 0; i < 80 && ShelfRows.Any(r => string.IsNullOrEmpty(r.Title)); i++)
            await Task.Delay(200);
        int noTitle = ShelfRows.Count(r => string.IsNullOrEmpty(r.Title));
        Check("剧名已补齐（批量 + album_detail 回落）", noTitle <= 2,
            $"批量拿到 {titledByBatch}/{ShelfRows.Count}，回落补齐后仍有 {noTitle} 项没标题（已下架）");

        // ② 默认**不勾选**：收藏夹一上来不应该自动替用户选好
        Check("收藏夹默认未勾选", ShelfRows.Count > 0 && ShelfRows.All(r => !r.IsChecked),
            $"勾了 {ShelfRows.Count(r => r.IsChecked)}/{ShelfRows.Count}");
        Check("右栏清单初始为空", EpisodeRows.Count == 0, $"{EpisodeRows.Count} 行");

        // ③ 点列表行 → **不应该**再加载分集（点行逻辑已取消）
        var first = FilteredShelf.FirstOrDefault(r => !r.IsShortDrama) ?? FilteredShelf.FirstOrDefault();
        if (first != null)
        {
            LstShelf.SelectedItem = first;      // SelectionMode=None，理论上选不中
            await Task.Delay(400);
            Check("点列表行不再触发加载", EpisodeRows.Count == 0,
                $"点「{first.Display}」后右栏 {EpisodeRows.Count} 行");
        }

        // ④ 勾选第一部 → 分集**追加**进右栏清单
        var a = FilteredShelf.FirstOrDefault(r => !r.IsShortDrama);
        if (a != null)
        {
            var cb = FindRowCheckBox(a);
            Check("能定位到行内复选框", cb != null);
            if (cb != null)
            {
                cb.IsChecked = true;
                cb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                for (int i = 0; i < 150 && !_queueBySeries.ContainsKey(a.SeriesId); i++)
                    await Task.Delay(100);
                Check("勾选剧目 → 分集进清单",
                    _queueBySeries.ContainsKey(a.SeriesId) && EpisodeRows.Any(r => r.IsGroupHeader),
                    $"「{a.Display}」{(_queueBySeries.TryGetValue(a.SeriesId, out var rowsA) ? rowsA.Count : 0)} 集");
                Check("清单里有该剧的分组标题行",
                    EpisodeRows.Any(r => r.IsGroupHeader && r.SeriesId == a.SeriesId),
                    $"标题行 {EpisodeRows.Count(r => r.IsGroupHeader)} 个");
            }
        }

        // ⑤ 再勾第二部 → **追加**而不是替换；两部剧序号各归各的
        var b = FilteredShelf.Skip(1).FirstOrDefault(r => !r.IsShortDrama && r.SeriesId != a?.SeriesId);
        if (a != null && b != null)
        {
            var cb = FindRowCheckBox(b);
            if (cb != null)
            {
                cb.IsChecked = true;
                cb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                for (int i = 0; i < 150 && !_queueBySeries.ContainsKey(b.SeriesId); i++)
                    await Task.Delay(100);
            }
            int rowsA = _queueBySeries.TryGetValue(a.SeriesId, out var ra) ? ra.Count : 0;
            int rowsB = _queueBySeries.TryGetValue(b.SeriesId, out var rb) ? rb.Count : 0;
            Check("勾第二部 → 第一部仍在清单里（追加不替换）", rowsA > 0, $"第一部 {rowsA} 集");
            Check("两部剧都在清单里", _queueBySeries.Count >= 2, $"共 {_queueBySeries.Count} 部");
            Check("分组标题行随部数增加",
                EpisodeRows.Count(r => r.IsGroupHeader) == _queueBySeries.Count,
                $"标题行 {EpisodeRows.Count(r => r.IsGroupHeader)} 个 / {_queueBySeries.Count} 部");
            Check("各剧分集序号独立（都从 1 开始）",
                _queueBySeries.Values.All(list => list.Count == 0 || list[0].Index == 1),
                string.Join(" | ", _queueBySeries.Values.Select(l => l.Count == 0 ? "-" : l[0].Index.ToString())));
        }

        // ⑥ 取消勾选第一部 → 只移出它自己，另一部不受影响
        if (a != null && b != null)
        {
            var cb = FindRowCheckBox(a);
            if (cb != null)
            {
                cb.IsChecked = false;
                cb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(200);
            }
            Check("取消勾选 → 该剧移出清单",
                !_queueBySeries.ContainsKey(a.SeriesId), $"剩余 {_queueBySeries.Count} 部");
            Check("另一部仍在清单里", _queueBySeries.ContainsKey(b.SeriesId), $"剩余 {_queueBySeries.Count} 部");
            Check("移出后分组标题行同步",
                EpisodeRows.Count(r => r.IsGroupHeader) == _queueBySeries.Count,
                $"标题行 {EpisodeRows.Count(r => r.IsGroupHeader)} 个 / {_queueBySeries.Count} 部");
        }

        // ⑦ ★ 清单整体下载（注入假下载器，不打网络）
        //    ★ 关键断言：清单里两部剧的集号各自从 1 起，下载日志/文件名不会串号
        int fakeCalls = 0;
        var seen = new List<string>();
        DownloadHook = async (ep, q, cb, ct) =>
        {
            fakeCalls++;
            seen.Add($"{ep.SeriesTitle}#{ep.Index}");
            cb.OnPhase?.Invoke("下载中");
            return new DownloadResult
            {
                Ok = true, OutPath = "(假)", Size = 1024, Duration = ep.Duration,
            };
        };
        try
        {
            var queued = _queueBySeries.Values.SelectMany(v => v).Where(r => r.IsEpisode).ToList();
            Check("清单里确实有待下载分集", queued.Count > 0, $"{queued.Count} 集");
            await DownloadRowsAsync(queued);
            Check("下载跑遍了清单里的每一集", fakeCalls == queued.Count,
                $"调用 {fakeCalls} 次 / {queued.Count} 集");
            Check("状态都标为已完成",
                queued.All(r => r.State == "已完成"),
                string.Join("/", queued.Select(r => r.State).Distinct()));
            // 集号串号检查：同一部剧的集号必须递增且不越过该剧集数
            bool numberingOk = _queueBySeries.Values.All(list =>
                list.Where(r => r.IsEpisode).Select(r => r.Index).SequenceEqual(
                    Enumerable.Range(1, list.Count(r => r.IsEpisode))));
            Check("各剧集号独立、不串号", numberingOk,
                string.Join(" | ", _queueBySeries.Values.Select(l =>
                    l.Count == 0 ? "-" : $"{l[0].Index}..{l[^1].Index}")));
        }
        finally { DownloadHook = null; }

        // ⑧ 状态栏的统计要跟清单一致
        int qTotal = EpisodeRows.Count(r => r.IsEpisode);
        int qDone = EpisodeRows.Count(r => r.IsEpisode && r.State == "已完成");
        Check("状态栏统计与清单一致",
            LblRight.Text == $"已完成 {qDone} / {qTotal}",
            $"LblRight=\"{LblRight.Text}\"，应为 已完成 {qDone} / {qTotal}");

        // ⑧ ★ 状态栏右侧要有版本号
        Check("状态栏右侧显示版本号",
            !string.IsNullOrWhiteSpace(LblVersion.Text) && LblVersion.Text.StartsWith("v"),
            $"LblVersion=\"{LblVersion.Text}\"");

        // ⑨ ★ 下载目录：改了文本框就应当生效（不用点「浏览…」）
        string oldOut = _svc.Config.OutDir;
        string probe = Path.Combine(Path.GetTempPath(), "hgvd_outdir_probe");
        EdOut.Text = probe;
        ApplyOutDir();
        Check("下载目录改文本框即生效",
            string.Equals(_svc.Config.OutDir, probe, StringComparison.OrdinalIgnoreCase),
            $"配置={_svc.Config.OutDir}");
        EdOut.Text = oldOut;
        ApplyOutDir();

        // ⑩ ★ logo 区单击 → 打开「关于」
        int before = AboutInstance == null ? 0 : 1;
        SimulateBrandClick();
        await Task.Delay(300);
        Check("logo 区单击打开「关于」", AboutInstance != null && before == 0,
            AboutInstance == null ? "窗口没打开" : "已打开");
        AboutInstance?.Close();

        Log(fail == 0 ? "═══ 全部通过 ═══" : $"═══ {fail} 项未通过 ═══");
        Console.WriteLine(fail == 0 ? "═══ 全部通过 ═══" : $"═══ {fail} 项未通过 ═══");
        Diag($"uitest fail={fail}");
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hgv_tool");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "uitest.log"),
                report.ToString() + $"=== fail={fail} ===\n");
        }
        catch { }
    }

    /// <summary>在可视树里找到某一行的勾选框。</summary>
    private CheckBox FindRowCheckBox(ShelfRow row)
    {
        foreach (var container in LstShelf.GetVisualDescendants())
        {
            if (container is ListBoxItem item && item.DataContext is ShelfRow r && r == row)
                return item.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault();
        }
        // 兜底：拿第一个有勾选框的行容器
        return LstShelf.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault();
    }

    /// <summary>
    /// 模拟「点一下 logo 区」。
    /// ★ Avalonia 的 PointerPressedEventArgs 需要一个真实的 IPointer 实例，
    ///   没法凭空构造 → 这里直接走「抬起」时调用的那条路径（OnBrandClicked），
    ///   验证的是**「关于」窗口能不能被打开**这个结果。
    /// </summary>
    private void SimulateBrandClick() => OnBrandClicked();
}
