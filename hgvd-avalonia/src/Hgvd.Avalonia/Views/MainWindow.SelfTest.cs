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
        await LoadEpisodesAsync(seriesId, seriesId);
        if (EpisodeRows.Count == 0) return;
        _stopRequested = false;
        _downloading = true;
        BtnRun.IsEnabled = false;
        BtnStop.IsEnabled = true;
        await DownloadRowsAsync(EpisodeRows.Skip(skip).Take(3).ToList());
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
             + (res.Aborted ? " · **被限流中止**" : ""));
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

        Check("全选框状态与列表一致",
            ChkAll.IsChecked == (ShelfRows.Count > 0 && ShelfRows.All(r => r.IsChecked)),
            $"全选框={ChkAll.IsChecked}，勾了 {ShelfRows.Count(r => r.IsChecked)}/{ShelfRows.Count}");

        // ② 点第一行（走 XAML 绑定的 SelectionChanged）
        var first = FilteredShelf.FirstOrDefault();
        if (first != null)
        {
            LstShelf.SelectedItem = first;
            for (int i = 0; i < 100 && EpisodeRows.Count == 0; i++) await Task.Delay(100);
            Check("点列表行 → 右边出分集", EpisodeRows.Count > 0,
                $"「{first.Display}」{EpisodeRows.Count} 集");
        }

        // ③ 勾选第二行 → 应切到第二部剧
        var second = FilteredShelf.Skip(1).FirstOrDefault();
        if (second != null)
        {
            var cb = FindRowCheckBox(second);
            Check("能定位到行内复选框", cb != null);
            if (cb != null)
            {
                cb.IsChecked = false;    // 先取消
                cb.IsChecked = true;     // 再勾上
                cb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                // 等「切到该剧」且分集真的加载出来
                for (int i = 0; i < 150; i++)
                {
                    if (_currentSeries == second.SeriesId && EpisodeRows.Count > 0) break;
                    await Task.Delay(100);
                }
                Check("勾选剧目 → 右边切到该剧",
                    _currentSeries == second.SeriesId && EpisodeRows.Count > 0,
                    $"「{second.Display}」{EpisodeRows.Count} 集");
            }
        }

        // ④ 点全选（半选 → 全清 → 全选）
        ChkAll.IsChecked = null;    // 模拟「全勾状态点一下」先变半选
        ChkAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("点全选 → 全清", ShelfRows.Count > 0 && ShelfRows.All(r => !r.IsChecked),
            $"勾了 {ShelfRows.Count(r => r.IsChecked)}/{ShelfRows.Count}");
        Check("全清后全选框同步", ChkAll.IsChecked == false, $"全选框={ChkAll.IsChecked}");

        ChkAll.IsChecked = true;    // 模拟「全清状态点一下」变全勾
        ChkAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("再点全选 → 全选", ShelfRows.All(r => r.IsChecked),
            $"勾了 {ShelfRows.Count(r => r.IsChecked)}/{ShelfRows.Count}");
        Check("全选后全选框同步", ChkAll.IsChecked == true, $"全选框={ChkAll.IsChecked}");

        // ⑤ 半选状态：取消一项 → 全选框应变半选
        if (ShelfRows.Count > 1)
        {
            ShelfRows[1].IsChecked = false;
            Check("取消一项 → 全选框变半选", ChkAll.IsChecked == null,
                $"全选框={ChkAll.IsChecked}");
            ShelfRows[1].IsChecked = true;
        }

        // ⑥ 批量下载按钮的计数要跟着勾选走
        int batchable = ShelfRows.Count(r => r.IsChecked && !r.IsShortDrama);
        Check("批量下载按钮计数正确",
            BtnBatchRun.Content?.ToString() == $"批量下载 ({batchable})",
            $"按钮={BtnBatchRun.Content}，应有 {batchable} 部");
        Check("批量下载按钮可用", BtnBatchRun.IsEnabled == (batchable > 0),
            $"IsEnabled={BtnBatchRun.IsEnabled}");

        // ⑦ ★ 批量下载逻辑本身（注入假下载器，绕开服务端风控）
        int fakeCalls = 0;
        DownloadHook = async (ep, q, cb, ct) =>
        {
            fakeCalls++;
            cb.OnPhase?.Invoke("下载中");
            return new DownloadResult
            {
                Ok = true, OutPath = "(假)", Size = 1024, Duration = ep.Duration,
            };
        };
        try
        {
            var two = FilteredShelf.Take(2).ToList();
            var br = await BatchDownloadAsync(two);
            Check("批量下载：处理了 2 部", br.DoneSeries == 2, $"DoneSeries={br.DoneSeries}");
            Check("批量下载：逐集都跑了", fakeCalls == br.Ok, $"调用 {fakeCalls} 次，成功 {br.Ok} 集");
            Check("批量下载：状态标为已完成",
                EpisodeRows.Count > 0 && EpisodeRows.All(r => r.State == "已完成"),
                $"{EpisodeRows.Count} 行，状态={string.Join("/", EpisodeRows.Select(r => r.State).Distinct())}");
            Check("批量下载：无失败无中止", br.Fail == 0 && !br.Aborted,
                $"失败 {br.Fail}，中止={br.Aborted}");
        }
        finally { DownloadHook = null; }

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
