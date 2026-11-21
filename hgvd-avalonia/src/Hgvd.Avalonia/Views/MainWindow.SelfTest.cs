using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Hgvd.Avalonia.ViewModels;
using Hgvd.Core;
using Hgvd.Core.Engine;
using Hgvd.Xqd;
using Hgvd.Xqd.Api;

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
        SetRunningUi(true);
        await DownloadRowsAsync(rows.Skip(skip).Take(3).ToList());
        SetRunningUi(false);
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

        // 挑几部做冒烟。
        // ★ 优先 **BookType=3 教学合集**，并且**不要求 EpisodeCnt>0**：
        //   收藏夹里只有个别条目带集数，按集数筛会把候选池缩到只剩
        //   一两部「标成教学、实际没有播放流」的素材，门禁就长期红着。
        //   运行时 BatchDownloadAsync 会自己拉分集，无需预知集数。
        var teach = ShelfRows
            .Where(r => !r.IsShortDrama && r.BookType == 3 && !string.IsNullOrEmpty(r.Title))
            .OrderBy(r => r.EpisodeCnt)          // 集数未知的排前面（通常是真正的课程）
            .Take(seriesCount).ToList();
        var picked = teach.Count > 0
            ? teach
            : ShelfRows.Where(r => !r.IsShortDrama && !string.IsNullOrEmpty(r.Title))
                       .OrderBy(r => r.EpisodeCnt).Take(seriesCount).ToList();
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

        // ⑦★ 双击单集：只下这一集，不碰清单里的其它行
        //    ★ 用户 2026-11-21 要求：双击清单里的某一项 = 单独下载那一集。
        //      这里直接调 DownloadOneAsync（双击事件处理器的实体），
        //      事件绑定本身由 XAML 编译保证（写错名字编译就过不去）。
        int oneCalls = 0;
        string oneVid = "";
        DownloadHook = async (ep, q, cb, ct) =>
        {
            oneCalls++;
            oneVid = ep.Vid;
            cb.OnPhase?.Invoke("下载中");
            return new DownloadResult
            {
                Ok = true, OutPath = "(假)", Size = 1024, Duration = ep.Duration,
            };
        };
        try
        {
            var one = EpisodeRows.FirstOrDefault(r => r.IsEpisode);
            Check("清单里有可双击的分集", one != null);
            if (one != null)
            {
                int doneBefore = EpisodeRows.Count(r => r.IsEpisode && r.State == "已完成");
                one.State = "待下载";            // 模拟「重下这一集」
                await DownloadOneAsync(one);
                Check("双击只下载这一集（不是整个清单）", oneCalls == 1,
                    $"实际调用 {oneCalls} 次");
                Check("下载的正是双击的那一行", oneVid == one.Vid,
                    $"第 {one.Index} 集");
                Check("双击后该集状态变为已完成", one.State == "已完成", one.State);
                Check("清单其它行没被顺带重下",
                    EpisodeRows.Count(r => r.IsEpisode && r.State == "已完成") == doneBefore,
                    $"已完成 {doneBefore} → {EpisodeRows.Count(r => r.IsEpisode && r.State == "已完成")}");
            }
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

        // ⑪ ★ 工具栏在最小宽度下不能被挤出窗口
        //    ★★ 这条守的是用户 2026-11-21 报的问题：窗口拖窄后
        //       「提取App缓存」被挤出窗口右边缘（截图上红框标出的就是它）。
        //       根因是工具栏用了 DockPanel —— 它按「先左后右」依次分配，
        //       后面的拿剩下的，空间不够就被裁掉。已改成 Grid Auto,*,Auto，
        //       中间那段空白才是可压缩的。
        //    ★ 断言不写死数字：从 900 往上试，找出「四个按钮都在框内」的最小窗口宽，
        //      再要求它 ≤ MinWidth。这样既守住回归，又能直接打印出真实需求。
        //    ★★ 探测期间必须把 MinWidth 临时清 0 —— 否则 Width=900 会被 MinWidth
        //       顶回去（窗口根本缩不到 900），第一轮就「通过」，探测形同虚设。
        double oldW = Width, oldMin = MinWidth;
        MinWidth = 0;
        double need = 0;
        for (double w = 900; w <= 1250; w += 10)
        {
            Width = w;
            await Task.Delay(140);           // 等布局跑完
            bool fits = true;
            foreach (var c in new Control[] { CmbQuality, BtnRun, BtnCache, BtnHistory })
            {
                var p = c.TranslatePoint(new Point(c.Bounds.Width, 0), this);
                if (p is null || p.Value.X > Bounds.Width + 1) { fits = false; break; }
            }
            if (fits) { need = w; break; }
        }
        MinWidth = oldMin;
        Width = oldW;
        await Task.Delay(200);
        Check($"MinWidth {MinWidth:0} 能容纳工具栏（实测需要 {need:0}）",
            need > 0 && need <= MinWidth,
            need <= 0 ? "1250 宽都还挤"
                      : need > MinWidth ? $"MinWidth 至少要比 {need:0} 大"
                                        : "有余量");

        // ⑫ ★ 双源共用清单 + 按需授权（2026-11-21 第二版）
        //    ★ 用临时存储目录，绝不把测试清单写进用户真实的 ~/.hgv_tool/xqd/jobs.json；
        //      _testMode 同时禁掉课表自动发现（不联网）。
        string xqdRoot = Path.Combine(Path.GetTempPath(), "hgvd_xqd_uitest");
        try { if (Directory.Exists(xqdRoot)) Directory.Delete(xqdRoot, true); } catch { }
        UseXqdStoreForTest(xqdRoot);

        Check("授权按钮写的是「获取授权」（启动不自动取）",
            (BtnLogin.Content as string) == "获取授权",
            BtnLogin.Content?.ToString() ?? "");

        int hgRowsBefore = EpisodeRows.Count(r => r.IsEpisode && !r.IsXqd);

        TabXqd.IsChecked = true;
        await Task.Delay(400);
        Check("切到兴趣岛 → 左栏换成营列表", LstCamps.IsVisible && !LstShelf.IsVisible,
            $"营列表={LstCamps.IsVisible} 收藏夹={LstShelf.IsVisible}");
        Check("切到兴趣岛 → 隐藏红果专属的「提取App缓存」", !BtnCache.IsVisible);
        Check("切到兴趣岛 → 隐藏红果专属的「抓流」", !BtnGrab.IsVisible);
        Check("切到兴趣岛 → 授权状态切到兴趣岛",
            (LblAuth.Text ?? "").StartsWith("兴趣岛", StringComparison.Ordinal),
            LblAuth.Text ?? "");
        Check("切到兴趣岛 → 右栏还是同一张下载清单",
            ReferenceEquals(LstEpisodes.ItemsSource, EpisodeView),
            ReferenceEquals(LstEpisodes.ItemsSource, EpisodeView) ? "同一实例" : "ItemsSource 被换掉了");

        // 手动链接兜底入口：仍然进**同一张**清单
        EdLinks.Text =
            "测试课甲 | https://example.com/a.m3u8\n" +
            "https://example.com/b.m3u8\n" +
            "测试课甲 | https://example.com/a.m3u8";
        BtnAddLinks_Click(null, null);
        await Task.Delay(300);
        int xqdRows = EpisodeRows.Count(r => r.IsXqd && r.IsEpisode);
        Check("手动链接加入共用清单（去重后 2 条）", xqdRows == 2, $"{xqdRows} 行");
        bool hasLinkHeader = EpisodeRows.Any(r => r.IsGroupHeader && r.SeriesId == "xqd:__links");
        Check("兴趣岛的条目在清单里带分组标题", hasLinkHeader,
            hasLinkHeader ? "有「手动链接」分组" : "没有「手动链接」分组标题");

        // 来源列 / 暂停作用域 / 5 档筛选（2026-11-21 补齐）
        var xRow = EpisodeRows.FirstOrDefault(r => r.IsXqd && r.IsEpisode);
        Check("来源列：兴趣岛行标「兴趣岛」", xRow?.SourceText == "兴趣岛", xRow?.SourceText ?? "无行");
        // 单按钮三态：下载 →（点）暂停 →（点）继续 →（结束）下载
        _downloading = true;
        _paused = false;
        SetRunningUi(true);
        string cRun = BtnRun.Content as string ?? "";
        TogglePause();
        string cPause = BtnRun.Content as string ?? "";
        TogglePause();
        string cResume = BtnRun.Content as string ?? "";
        SetRunningUi(false);
        string cIdle = BtnRun.Content as string ?? "";
        Check("单按钮三态：下载 / 暂停 / 继续",
            cRun == "暂停" && cPause == "继续" && cResume == "暂停" && cIdle == "下载",
            $"{cRun} → {cPause} → {cResume} → {cIdle}");

        // 右键菜单的「来源行」解析：ContextMenu 不在可视树里，靠 Opening 记录 + Tag 兜底
        var probeRow = EpisodeRows.FirstOrDefault(r => r.IsXqd && r.IsEpisode);
        var probeMenu = new ContextMenu { DataContext = probeRow };
        EpMenu_Opening(probeMenu, new System.ComponentModel.CancelEventArgs());
        Check("右键菜单能定位到所在行",
            probeRow != null && ReferenceEquals(RowFromMenu(new MenuItem()), probeRow),
            probeRow?.Title ?? "无行");

        RbDone.IsChecked = true;
        await Task.Delay(200);
        // 前面用假下载把红果的 91 集标成了「已完成」，所以这里应当只剩它们：
        // 不能再有「待下载」的兴趣岛行，且可见行必须是终态。
        var doneView = EpisodeView.Where(r => r.IsEpisode).ToList();
        Check("筛选「已完成」→ 只剩已完成，待下载的课被隐藏",
            doneView.All(r => r.State is "已完成" or "已存在") && !doneView.Any(r => r.IsXqd),
            $"{doneView.Count} 行，其中兴趣岛 {doneView.Count(r => r.IsXqd)}");
        RbPending.IsChecked = true;
        await Task.Delay(200);
        Check("筛选「待下载」→ 两节课都在",
            EpisodeView.Count(r => r.IsEpisode && r.IsXqd) == 2,
            $"{EpisodeView.Count(r => r.IsEpisode && r.IsXqd)} 行");
        RbAll.IsChecked = true;
        await Task.Delay(200);
        Check("筛选「全部」→ 恢复完整清单",
            EpisodeView.Count(r => r.IsEpisode) == EpisodeRows.Count(r => r.IsEpisode),
            $"{EpisodeView.Count(r => r.IsEpisode)} / {EpisodeRows.Count(r => r.IsEpisode)}");

        TabHongguo.IsChecked = true;
        await Task.Delay(300);
        Check("切回红果 → 收藏夹恢复", LstShelf.IsVisible && !LstCamps.IsVisible);
        Check("切回红果 → 抓流按钮恢复", BtnGrab.IsVisible && BtnCache.IsVisible);
        // ★ 护栏：「自动抓剧」能力必须**不存在**（2026-11-21 复核后裁掉）。
        //   它按用户输入的剧 ID 取流，不查收藏夹、不查内容类型，默认剧名还是「短剧_<id>」，
        //   与目标口径（只下收藏夹里的教材/合集类视频）直接冲突。
        //   ⇒ 入口、对话框、HookGrabRunner、GrabManifest、Episode.DirectUrl 通道全部删除。
        //   ★ 若将来要重新引入「按剧取直链」，必须先让它走**白名单**（收藏夹教学合集），
        //     否则就是把这个越界口子重新打开。
        Check("自动抓剧能力已裁掉（界面里没有这个入口）",
            !this.GetVisualDescendants().OfType<Button>()
                .Any(b => b.Name == "BtnAutoGrab"));
        Check("切回红果 → 兴趣岛的条目仍在同一张清单里",
            EpisodeRows.Count(r => r.IsXqd && r.IsEpisode) == xqdRows,
            $"{EpisodeRows.Count(r => r.IsXqd && r.IsEpisode)} 行");
        Check("切回红果 → 红果的行没被影响",
            EpisodeRows.Count(r => r.IsEpisode && !r.IsXqd) == hgRowsBefore,
            $"{EpisodeRows.Count(r => r.IsEpisode && !r.IsXqd)} 行（原 {hgRowsBefore}）");

        // 右键 / Delete 的实体：单行移出（兴趣岛手动链接原本根本删不掉）
        var victim = EpisodeRows.FirstOrDefault(r => r.IsXqd && r.IsEpisode);
        int xqdBeforeRemove = EpisodeRows.Count(r => r.IsXqd && r.IsEpisode);
        bool removed = victim != null && RemoveRow(victim);
        // ★ 标题行是**替换**成新对象的（RefreshGroupHeader），必须重新取，不能用旧引用
        var linkHeader = EpisodeRows.FirstOrDefault(r => r.IsGroupHeader && r.SeriesId == "xqd:__links");
        Check("移出清单：行被删掉、分组计数同步",
            removed && EpisodeRows.Count(r => r.IsXqd && r.IsEpisode) == xqdBeforeRemove - 1
                    && (linkHeader?.HeaderText.Contains("共 1 节") ?? false),
            $"removed={removed} 剩 {EpisodeRows.Count(r => r.IsXqd && r.IsEpisode)} 行 "
            + $"标题=\"{(linkHeader?.HeaderText ?? "")}\"");
        Check("移出清单：兴趣岛队列里也删掉了（会落盘）",
            _xqd.Jobs.Count == 1, $"{_xqd.Jobs.Count} 个任务");

        // 红果清单：落盘 → 恢复（临时文件，不碰用户的 queue.json）
        string qpath = Path.Combine(Path.GetTempPath(), "hgvd_queue_uitest", "queue.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(qpath));
            File.WriteAllText(qpath, """
            [
              {"SeriesId":"uitest_s1","SeriesTitle":"恢复测试剧","ShortDrama":false,
               "Episodes":[
                 {"Vid":"uitest_v1","Index":1,"Title":"第 1 集","Duration":120,"VideoIdType":1},
                 {"Vid":"uitest_v2","Index":2,"Title":"第 2 集","Duration":121,"VideoIdType":1}
               ]}
            ]
            """);
        }
        catch { }
        ReloadHongguoQueueForTest();
        var restoredRows = EpisodeRows.Where(r => r.IsEpisode && !r.IsXqd).ToList();
        bool restoredHeader = EpisodeRows.Any(r => r.IsGroupHeader && r.SeriesId == "uitest_s1");
        Check("红果清单：从 queue.json 恢复（两集 + 分组标题）",
            restoredRows.Count == 2 && restoredHeader,
            $"{restoredRows.Count} 行，标题行={restoredHeader}");
        Check("红果清单：恢复后一律「待下载」（已存在由下载时判）",
            restoredRows.All(r => r.State == "待下载"),
            string.Join(",", restoredRows.Select(r => r.State)));

        // 历史窗口：裁剪后仍能构建（FuncDataTemplate 不靠反射，但顺手守一道）
        var hw = new HistoryWindow(new List<HistoryEntry>
        {
            new HistoryEntry { Source = "红果", Title = "剧", Episode = "第 1 集", Path = @"D://x//1.mp4", Size = 10, Ts = 1700000000 },
        });
        Check("历史窗口能构建并带记录", hw.Content != null && (hw.Title ?? "").Contains("1 条"), hw.Title ?? "");
        hw.Close();

        // 清理掉测试加进清单的行（避免影响退出前的状态）
        foreach (var r in EpisodeRows.Where(r => r.IsXqd || r.SeriesId == "uitest_s1").ToList())
        {
            EpisodeRows.Remove(r);
            EpisodeView.Remove(r);
        }

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

    /// <summary>
    /// 兴趣岛下载冒烟测试：给一条真实的 m3u8 / mp4 直链，走完整链路
    /// （清单解析 → 分片下载 → ffmpeg 合成 → ffprobe 校验）并打印成品信息。
    ///
    /// ★ 用法：<c>hgvd-downloader-vX.exe --xqdtest &lt;url&gt; [课名]</c>
    /// ★ 产物落在系统临时目录，用临时清单存储 —— 不碰用户的下载目录和真实 jobs.json。
    /// ★ 结果写 <c>~/.hgv_tool/xqdtest.log</c>（末行 <c>=== pass=1 ===</c>），供发版门禁读取。
    /// </summary>
    public async Task XqdTestAsync(string url, string title = "")
    {
        const string Hdr = "═══ 兴趣岛下载冒烟测试 ═══";
        Log(Hdr);
        Console.WriteLine(Hdr);

        var report = new StringBuilder();
        void Line(string s)
        {
            Log(s);
            Console.WriteLine(s);
            report.AppendLine(s);
        }

        if (string.IsNullOrWhiteSpace(url) ||
            !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
              url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            Line("✗ 用法：--xqdtest <m3u8 或 mp4 直链> [课名]");
            WriteXqdReport(report, false);
            return;
        }

        string outRoot = Path.Combine(Path.GetTempPath(), "hgvd_xqd_e2e");
        try { if (Directory.Exists(outRoot)) Directory.Delete(outRoot, true); } catch { }
        Directory.CreateDirectory(outRoot);

        UseXqdStoreForTest(Path.Combine(outRoot, "state"));
        EdOut.Text = outRoot;                       // 只改文本框，不写用户配置
        TabXqd.IsChecked = true;
        await Task.Delay(300);

        string name = string.IsNullOrWhiteSpace(title) ? "冒烟测试" : title;
        EdLinks.Text = $"{name} | {url}";
        BtnAddLinks_Click(null, null);
        var row = EpisodeRows.LastOrDefault(r => r.IsXqd && r.IsEpisode);
        Line(row == null ? "清单：加入 0 节" : "清单：加入 1 节");
        if (row == null)
        {
            Line("✗ 链接没能加入清单（格式不对？）");
            WriteXqdReport(report, false);
            return;
        }

        var sw = Stopwatch.StartNew();
        var (ok, fail) = await DownloadXqdRowsAsync(new List<EpisodeRow> { row });
        sw.Stop();
        Line($"结果：成功 {ok} · 失败 {fail} · 用时 {sw.Elapsed.TotalSeconds:0.0}s");

        string file = row.Xqd?.OutPath;
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
        {
            // 直链音频可能改了扩展名（.mp3/.m4a），按目录里最大的文件兜底
            string dir = Path.Combine(outRoot, "兴趣岛");
            if (Directory.Exists(dir))
                file = Directory.GetFiles(dir)
                                .OrderByDescending(f => { try { return new FileInfo(f).Length; } catch { return 0; } })
                                .FirstOrDefault();
        }

        bool pass = false;
        if (!string.IsNullOrEmpty(file) && File.Exists(file))
        {
            long size = new FileInfo(file).Length;
            double dur = Util.ProbeDuration(file);
            Line($"成品：{Path.GetFileName(file)} · {Util.FmtSize(size)} · {dur:0.0}s");
            pass = ok == 1 && size >= 10 * 1024 && dur > 0;
            Line(pass ? "✓ 端到端通过（成品可被 ffprobe 解析）" : "✗ 成品不合格（太小或无法解析）");
        }
        else
        {
            Line("✗ 没有成品文件");
        }

        WriteXqdReport(report, pass);
    }

    /// <summary>
    /// 兴趣岛课表诊断（--xqdcatalog [营名关键字]）：
    /// 取授权 → 列营 → 拉一个营的课表 → 解析第一节的播放地址。只读，不下载。
    /// ★ 结果写 <c>~/.hgv_tool/xqdcatalog.log</c>，末行 <c>=== pass=1 ===</c>。
    /// </summary>
    public async Task XqdCatalogTestAsync(string filter = "", string mode = "")
    {
        const string Hdr = "═══ 兴趣岛课表诊断 ═══";
        Log(Hdr);
        Console.WriteLine(Hdr);

        var report = new StringBuilder();
        void Line(string s)
        {
            Log(s);
            Console.WriteLine(s);
            report.AppendLine(s);
        }

        bool pass = false;
        try
        {
            var res = await XqdAuth.AcquireAsync(m => Line("· " + m));
            Line(res.Ok
                ? $"授权：{(res.FromCache ? "本机缓存" : "MuMu 读取")} — {res.Message}"
                : "授权失败：" + res.Message);
            if (!res.Ok) { WriteCatalogReport(report, false); return; }

            var platform = new QianliaoPlatform(res.Cookie);
            var camps = await platform.ListAllCampsAsync();
            int formal = camps.Count(c => c.Kind == "正式营");
            int trial = camps.Count(c => c.Kind == "体验营");
            Line($"营：{camps.Count} 个（正式营 {formal} · 体验营 {trial} · 公开课 {camps.Count - formal - trial}）");

            foreach (var c in camps.Take(8))
                Line($"  · {c.Name} — {c.Badge}"
                     + (string.IsNullOrEmpty(c.CampId) ? "（公开课）" : ""));

            var camp = camps.FirstOrDefault(c =>
                           !string.IsNullOrEmpty(filter) &&
                           (c.Name?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ?? false))
                       ?? camps.FirstOrDefault(c => c.Kind == "正式营")
                       ?? camps.FirstOrDefault();

            if (camp == null) { Line("✗ 没有可用的营"); WriteCatalogReport(report, false); return; }

            var lessons = await platform.CampCatalogAsync(camp);
            Line($"课表：「{camp.Name}」{lessons.Count} 节");
            foreach (var l in lessons.Take(3))
                Line($"  · {l.Index + 1:000}. {l.Title}  ({l.Duration:0}s, {l.When})");

            if (lessons.Count == 0) { WriteCatalogReport(report, false); return; }

            var url = await platform.ResolveAsync(camp, lessons[0]);
            Line("播放地址：" + (url.Length > 0 ? "✓ " + new Uri(url).Host : "✗ 空"));
            // ★ 排障用：--xqdcatalog <关键字> url 会打印完整地址，可直接喂给 --xqdtest
            if (string.Equals(mode, "url", StringComparison.OrdinalIgnoreCase))
                Line("URL=" + url);
            pass = lessons.Count > 0 && url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e)
        {
            Line("✗ 异常：" + e.Message);
        }

        WriteCatalogReport(report, pass);
    }

    private static void WriteCatalogReport(StringBuilder report, bool pass)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hgv_tool");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "xqdcatalog.log"),
                report + $"=== pass={(pass ? 1 : 0)} ===\n");
        }
        catch { }
        Console.WriteLine(pass ? "═══ 通过 ═══" : "═══ 未通过 ═══");
    }

    private static void WriteXqdReport(StringBuilder report, bool pass)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hgv_tool");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "xqdtest.log"),
                report + $"=== pass={(pass ? 1 : 0)} ===\n");
        }
        catch { }
        Console.WriteLine(pass ? "═══ 通过 ═══" : "═══ 未通过 ═══");
    }

    /// <summary>
    /// 开发期演示（--xqdui）：停在兴趣岛页并填几条示例，便于截图核对布局。
    /// ★ 用系统临时目录里的独立清单，不碰用户真实的 jobs.json / 下载目录。
    /// </summary>
    internal void PrepareXqdDemoForShot()
    {
        string root = Path.Combine(Path.GetTempPath(), "hgvd_xqd_shot");
        try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }

        UseXqdStoreForTest(Path.Combine(root, "state"));
        EdOut.Text = root;
        TabXqd.IsChecked = true;

        // 假营列表（截图用：结构真实、数据虚构，不联网）
        CampRows.Add(new XqdCampRow(new Camp
        { Name = "引气归源太极营", CampId = "1001", Kind = "正式营", Count = 24 }));
        CampRows.Add(new XqdCampRow(new Camp
        { Name = "静坐内观-身心共养计划", CampId = "1002", Kind = "正式营", Count = 18 }));
        CampRows.Add(new XqdCampRow(new Camp
        { Name = "五行养生音乐", CampId = "1003", Kind = "体验营", Count = 12 }));
        CampRows.Add(new XqdCampRow(new Camp
        { Name = "才艺名师公开课", PeriodId = "170814973572", Kind = "公开课", Count = 9 }));
        ApplyCampFilter();

        // 共用清单里放几条兴趣岛任务（示例，不下载）
        EdLinks.Text =
            "先导课：解谜自然之道 | https://example.com/a.m3u8 [48:12]\n" +
            "五行导引功第一课：龟形导引功教学 | https://example.com/b.m3u8 [36:40]\n" +
            "聚气凝神巩固课——五行导引功串联+六字呼吸法 | https://example.com/c.m3u8 [1:12:30]\n" +
            "【静坐姿势】道家七支坐法 | https://example.com/d.m3u8 [21:05]";
        BtnAddLinks_Click(null, null);
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

    // ──────────────────────────────────────────────────────────── 发版门禁探针
    // ★ 播放接口加固后，无人值守拿不到有效直链（直链带 100 秒时间戳），
    //   所以端到端下载门禁拆成两段本地可控的验证：
    //     TlsProbeAsync     —— MITM 证书链（原 _ref/csharp 少换行那个坑）
    //     DownloadProbeAsync —— 下载引擎 + 命名 + ffprobe 校验
    //   真实点播抓流（需人在 App 里点）保留为手工验证。

    private static void ProbeLog(string report, string line)
    {
        try
        {
            File.AppendAllText(report, line + "\n", Encoding.UTF8);
        }
        catch { }
    }

    /// <summary>本地 TLS 握手探针：验证证书链能发到客户端（Verification: OK 的等价检查）。</summary>
    public async Task TlsProbeAsync(int seconds = 12)
    {
        string report = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".hgv_tool", "tlsprobe.log");
        try { Directory.CreateDirectory(Path.GetDirectoryName(report)!); File.Delete(report); } catch { }

        int port = 18443;
        ProbeLog(report, "═══ TLS 证书链门禁 ═══");
        try
        {
            var ctx = Hgvd.Core.Grab.MitmCa.ContextFor("hgvd-selftest.local");
            if (ctx == null) { ProbeLog(report, "✗ 证书上下文为空"); return; }

            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

            var server = Task.Run(async () =>
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(cts.Token);
                    using var tls = new System.Net.Security.SslStream(client.GetStream(), false);
                    await tls.AuthenticateAsServerAsync(
                        new System.Net.Security.SslServerAuthenticationOptions
                        {
                            ServerCertificateContext = ctx,
                            ClientCertificateRequired = false,
                            CertificateRevocationCheckMode =
                                System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                            ApplicationProtocols = new List<System.Net.Security.SslApplicationProtocol>
                            {
                                System.Net.Security.SslApplicationProtocol.Http11,
                            },
                        }, cts.Token);
                    ProbeLog(report, $"✓ 握手成功 {tls.SslProtocol}");
                }
                catch (Exception ex) { ProbeLog(report, "✗ " + ex.GetType().Name + ": " + ex.Message); }
            });

            // 客户端：不校验证书（我们验的是「服务端能否发出完整链」，链本身用 openssl 单独验）
            using (var client = new System.Net.Sockets.TcpClient())
            {
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port, cts.Token);
                using var tls = new System.Net.Security.SslStream(client.GetStream(), false,
                    (_, _, _, _) => true);
                await tls.AuthenticateAsClientAsync("hgvd-selftest.local");
                ProbeLog(report, "✓ 客户端握手完成");
            }
            await server;
            listener.Stop();
            ProbeLog(report, "=== ok=1 fail=0 ===");
        }
        catch (Exception ex)
        {
            ProbeLog(report, "✗ 探针异常：" + ex.Message);
            ProbeLog(report, "=== ok=0 fail=1 ===");
        }
    }

    /// <summary>下载链路门禁：本地 http 小文件 → Downloader → ffprobe 校验时长。</summary>
    public async Task DownloadProbeAsync()
    {
        string report = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".hgv_tool", "dlprobe.log");
        try { Directory.CreateDirectory(Path.GetDirectoryName(report)!); File.Delete(report); } catch { }
        ProbeLog(report, "═══ 下载链路门禁 ═══");

        string tmp = Path.Combine(Path.GetTempPath(), "hgvd_dlprobe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        int ok = 0, fail = 0;
        try
        {
            // 用内置 ffmpeg 造一个 2 秒的小 mp4（比 http 服务更简单可靠）
            string ffmpeg = ResolveProbeFfmpeg();
            string src = Path.Combine(tmp, "src.mp4");
            var gen = Process.Start(new ProcessStartInfo(ffmpeg)
            {
                ArgumentList =
                {
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-f", "lavfi", "-i", "testsrc=size=160x120:rate=10:duration=2",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", src,
                },
                UseShellExecute = false, CreateNoWindow = true,
            });
            gen!.WaitForExit(30000);
            if (!File.Exists(src)) throw new IOException("本地测试 mp4 生成失败");

            // 起一个本地 http 服务（只服务这一个文件）
            using var http = new System.Net.HttpListener();
            http.Prefixes.Add("http://127.0.0.1:18444/");
            http.Start();
            var serving = Task.Run(async () =>
            {
                try
                {
                    var ctx = await http.GetContextAsync();
                    var bytes = File.ReadAllBytes(src);
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.ContentType = "video/mp4";
                    await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                    ctx.Response.Close();
                }
                catch { }
            });

            string outPath = Path.Combine(tmp, "抓流门禁样本.001.本地测试.mp4");
            var req = new DownloadRequest
            {
                Url = "http://127.0.0.1:18444/probe.mp4",
                OutPath = outPath,
                ExpectDuration = 2.0,
                ContentKey = "",           // 教学类不解密
            };
            var res = await Downloader.DownloadAsync(req, null, CancellationToken.None);
            if (!res.Ok)
            {
                fail++;
                ProbeLog(report, "✗ 下载失败：" + res.Error);
            }
            else
            {
                double d = Util.ProbeDuration(outPath);
                if (d > 1.5 && d < 2.5)
                {
                    ok++;
                    ProbeLog(report, $"✓ 下载+命名+ffprobe 校验通过：{Path.GetFileName(outPath)} {d:0.0}s {res.Size} 字节");
                }
                else { fail++; ProbeLog(report, $"✗ 时长不符：{d:0.0}s（期望 2s）"); }
            }
            http.Stop();
            await serving;
        }
        catch (Exception ex)
        {
            fail++;
            ProbeLog(report, "✗ 异常：" + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
        ProbeLog(report, $"=== ok={ok} fail={fail} ===");
    }

    /// <summary>找内置 ffmpeg（发行版解包在 ToolPaths，源码期在仓库 .cache）。</summary>
    private static string ResolveProbeFfmpeg()
    {
        string local = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(local)) return local;
        // 源码期：向上找仓库根 .cache/ffmpeg
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string p = Path.Combine(dir.FullName, ".cache", "ffmpeg", "ffmpeg.exe");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        return "ffmpeg";   // 最后的兜底：PATH
    }
}
