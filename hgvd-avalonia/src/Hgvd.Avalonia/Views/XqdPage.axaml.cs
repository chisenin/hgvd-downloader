using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hgvd.Xqd;

namespace Hgvd.Avalonia.Views;

/// <summary>
/// 兴趣岛课程页 —— 只保留下载内核：
/// 用户粘贴 / 导入链接清单 → 解析 HLS 清单 → 分片并发下载 → ffmpeg 合成 MP4。
/// 不碰模拟器、不取登录态，因此不需要授权。
/// </summary>
public partial class XqdPage : UserControl
{
    private readonly ObservableCollection<XqdRow> _rows = new();
    private XqdQueue _queue;

    private bool _loaded;
    private bool _paused;
    private CancellationTokenSource _cts;

    /// <summary>由主窗口提供「当前下载目录」（跟着顶栏输入框走）。</summary>
    public Func<string> ResolveOutDir { get; set; }

    /// <summary>状态栏回写（左：状态；右：补充信息）。</summary>
    public event Action<string, string> StatusChanged;

    public XqdPage()
    {
        InitializeComponent();

        _queue = new XqdQueue(new XqdOptions());
        WireQueue(_queue);

        LstRows.ItemsSource = _rows;

        CmbQuality.ItemsSource = new[]
        {
            new QualityOption("最高画质（自动）", "max"),
            new QualityOption("1080P", "1080"),
            new QualityOption("720P", "720"),
            new QualityOption("480P", "480"),
        };
        CmbQuality.SelectedIndex = 0;

        EdLinks.TextChanged += (_, _) => UpdateLinkCount();
        UpdateLinkCount();
    }

    // ══════════════════════════════════════════════════════════════
    //  载入 / 刷新
    // ══════════════════════════════════════════════════════════════

    private void WireQueue(XqdQueue queue)
    {
        queue.JobChanged += job => Dispatcher.UIThread.Post(() => SyncRow(job));
        queue.JobProgress += (job, _) => Dispatcher.UIThread.Post(() => SyncRow(job));
        queue.Log += AppendLog;
    }

    /// <summary>
    /// 给 --uitest 用：换成临时目录的存储，避免把测试清单写进用户真实的 jobs.json。
    /// </summary>
    internal void UseStoreForTest(string root)
    {
        _queue = new XqdQueue(new XqdOptions(), new XqdStore(root));
        WireQueue(_queue);
        _loaded = false;
        _rows.Clear();
        UpdateCounts();
    }

    /// <summary>首次切到本页时调用（只载入一次）。</summary>
    public void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        _queue.Options.OutRoot = (ResolveOutDir?.Invoke() ?? "").Trim();
        _queue.Load();
        RebuildRows();

        int total = _rows.Count;
        if (total > 0)
        {
            int done = _rows.Count(r => r.Job.State is XqdState.Done or XqdState.Exists);
            AppendLog($"已载入清单 {total} 节（已完成/已存在 {done} 节）");
        }
        else
        {
            AppendLog("兴趣岛：把课程链接粘到左边，点「加入清单」。无需授权，也不需要模拟器。");
        }
        UpdateSummary();
    }

    private void RebuildRows()
    {
        _rows.Clear();
        foreach (var job in _queue.Jobs) _rows.Add(new XqdRow(job));
        UpdateCounts();
        UpdateSummary();
    }

    /// <summary>下载目录可能被改过：重算成品路径并重判「已存在」。</summary>
    public void SyncOutDir()
    {
        _queue.Options.OutRoot = (ResolveOutDir?.Invoke() ?? "").Trim();
        _queue.RebasePaths();
        _queue.Rescan();
        foreach (var row in _rows) row.SyncFromJob();
        UpdateCounts();
        UpdateSummary();
    }

    private void SyncRow(XqdJob job)
    {
        var row = _rows.FirstOrDefault(r => ReferenceEquals(r.Job, job));
        row?.SyncFromJob();

        if (XqdState.IsRunning(job.State) || job.State == XqdState.Paused)
            Marquee.Text = job.Display;
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        LblJobCount.Text = $"{_rows.Count} 节";
        int pending = _rows.Count(r => r.Job.State == XqdState.Pending);
        int done = _rows.Count(r => r.Job.State is XqdState.Done or XqdState.Exists);
        int fail = _rows.Count(r => r.Job.State == XqdState.Failed);
        LblSummary.Text = _rows.Count == 0
            ? "暂无任务：把课程链接粘到左边，点「加入清单」"
            : $"共 {_rows.Count} 节 · 待下载 {pending} · 已完成 {done} · 失败 {fail}"
              + (fail > 0 ? " —— 失败的分片已保留，再点「下载」会自动续传" : "");
    }

    private void UpdateSummary()
    {
        UpdateCounts();
        try
        {
            long bytes = _queue.Store.PartsBytes();
            LblCacheHint.Text = bytes > 0
                ? "分片缓存 " + Hgvd.Core.Util.FmtSize(bytes) + "（失败重跑自动续传）"
                : "";
        }
        catch { }
    }

    private void UpdateLinkCount()
    {
        try
        {
            var items = LinkParser.Parse(EdLinks.Text ?? "");
            LblLinkCount.Text = $"{items.Count} 条";
        }
        catch { LblLinkCount.Text = "0 条"; }
    }

    // ══════════════════════════════════════════════════════════════
    //  加入 / 导入
    // ══════════════════════════════════════════════════════════════

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        var items = LinkParser.Parse(EdLinks.Text ?? "", errors);
        if (items.Count == 0)
        {
            AppendLog("！没有解析到链接。一行一条：课名 | https://…/xxx.m3u8");
            foreach (string err in errors.Take(5)) AppendLog("  · " + err);
            return;
        }

        int added = _queue.Add(items);
        EdLinks.Text = "";
        UpdateLinkCount();
        RebuildRows();
        AppendLog($"加入 {added} 节（解析 {items.Count} 条，重复 {items.Count - added} 条已忽略）");
        foreach (string err in errors.Take(3)) AppendLog("  · " + err);
        StatusChanged?.Invoke("就绪", $"兴趣岛：清单 {_rows.Count} 节");
    }

    private async void BtnImport_Click(object sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider == null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入课程清单（可多选：links.json + names.json 会自动合并）",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("清单文件 (*.txt;*.json;*.m3u8;*.csv)")
                {
                    Patterns = new[] { "*.txt", "*.json", "*.m3u8", "*.csv" },
                },
                FilePickerFileTypes.All,
            },
        });
        if (files == null || files.Count == 0) return;

        var sources = new List<List<LinkItem>>();
        foreach (var f in files)
        {
            try
            {
                await using var stream = await f.OpenReadAsync();
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                string text = await reader.ReadToEndAsync();
                var errors = new List<string>();
                var list = LinkParser.Parse(text, errors);
                sources.Add(list);
                AppendLog($"读入 {f.Name}：{list.Count} 条" + (errors.Count > 0 ? $"，{errors.Count} 行无法识别" : ""));
            }
            catch (Exception ex)
            {
                AppendLog($"！读取 {f.Name} 失败：{ex.Message}");
            }
        }

        var merged = LinkParser.Merge(sources);
        if (merged.Count == 0)
        {
            AppendLog("！这些文件里没有可用的链接");
            return;
        }

        int added = _queue.Add(merged);
        RebuildRows();
        AppendLog($"导入完成：可用 {merged.Count} 条，新加入 {added} 节");
        StatusChanged?.Invoke("就绪", $"兴趣岛：清单 {_rows.Count} 节");
    }

    private void BtnRescan_Click(object sender, RoutedEventArgs e)
    {
        int changed = _queue.Rescan();
        RebuildRows();
        AppendLog($"重新扫描完成：{changed} 节状态有变化");
        UpdateSummary();
    }

    private void BtnClearInput_Click(object sender, RoutedEventArgs e)
    {
        EdLinks.Text = "";
        UpdateLinkCount();
    }

    // ══════════════════════════════════════════════════════════════
    //  下载
    // ══════════════════════════════════════════════════════════════

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (_queue.Running)
        {
            AppendLog("！正在下载，要换任务请先点「停止」");
            return;
        }

        var selected = LstRows.SelectedItems?.OfType<XqdRow>().Select(r => r.Job).ToList();
        var targets = selected != null && selected.Count > 0 ? selected : null;

        if (_rows.Count == 0)
        {
            AppendLog("！清单是空的：先把课程链接粘到左边");
            return;
        }

        await RunJobsAsync(targets);
    }

    private async Task<(int ok, int fail, int skip)> RunJobsAsync(IEnumerable<XqdJob> targets)
    {
        string dir = (ResolveOutDir?.Invoke() ?? "").Trim();
        if (dir.Length == 0)
        {
            AppendLog("！没有下载目录：请先在上方「下载目录」里设置");
            return (0, 0, 0);
        }
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            AppendLog("！下载目录不可用（" + dir + "）：" + ex.Message);
            return (0, 0, 0);
        }

        _queue.Options.OutRoot = dir;
        _queue.RebasePaths();
        _queue.Rescan();
        foreach (var row in _rows) row.SyncFromJob();

        _cts = new CancellationTokenSource();
        SetRunningUi(true);
        AppendLog("下载到：" + Path.Combine(dir, "兴趣岛"));

        int rOk = 0, rFail = 0, rSkip = 0;
        try
        {
            var (ok, fail, skip) = await _queue.RunAsync(targets, _cts.Token);
            rOk = ok; rFail = fail; rSkip = skip;
            StatusChanged?.Invoke("就绪", $"兴趣岛：成功 {ok} · 失败 {fail} · 跳过 {skip}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppendLog("！下载异常：" + ex.Message);
        }
        finally
        {
            SetRunningUi(false);
            _cts?.Dispose();
            _cts = null;
            foreach (var row in _rows) row.SyncFromJob();
            UpdateSummary();
        }
        return (rOk, rFail, rSkip);
    }

    private async void LstRows_DoubleTapped(object sender, TappedEventArgs e)
    {
        var row = RowFromEvent(e);
        if (row == null || _queue.Running) return;

        if (row.Job.State is XqdState.Done or XqdState.Exists)
        {
            AppendLog($"「{row.Job.Title}」已经下过了；要重下请右键「重新下载（清缓存）」");
            return;
        }
        await RunJobsAsync(new[] { row.Job });
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        if (!_queue.Running) return;
        _paused = !_paused;
        _queue.SetPaused(_paused);
        BtnPause.Content = _paused ? "继续" : "暂停";
        StatusChanged?.Invoke(_paused ? "已暂停" : "下载中", "");
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _queue.Cancel();
        try { _cts?.Cancel(); } catch { }
        AppendLog("正在停止…（已下的分片会保留，下次自动续传）");
    }

    private void SetRunningUi(bool running)
    {
        BtnRun.IsEnabled = !running;
        BtnStop.IsEnabled = running;
        BtnPause.IsEnabled = running;
        BtnPause.Content = "暂停";
        _paused = false;
        if (!running) Marquee.Text = "";
        StatusChanged?.Invoke(running ? "下载中" : "就绪", "");
    }

    // ══════════════════════════════════════════════════════════════
    //  右键菜单
    // ══════════════════════════════════════════════════════════════

    private async void MenuDownload_Click(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenu(sender);
        if (row == null || _queue.Running) return;
        if (row.Job.State is XqdState.Done or XqdState.Exists)
        {
            row.Job.State = XqdState.Pending;
            row.SyncFromJob();
        }
        await RunJobsAsync(new[] { row.Job });
    }

    private void MenuRetry_Click(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenu(sender);
        if (row == null) return;
        _queue.Store.PurgeParts(row.Job.OutPath);
        row.Job.State = XqdState.Pending;
        row.Job.Message = "";
        row.Job.Down = 0;
        row.Job.Total = 0;
        row.Job.Speed = 0;
        row.SyncFromJob();
        _queue.Save();
        AppendLog($"「{row.Job.Title}」已清缓存并重置为待下载");
    }

    private async void MenuCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenu(sender);
        if (row == null) return;
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip != null) await clip.SetTextAsync(row.Job.Url ?? "");
        AppendLog("链接已复制");
    }

    private async void MenuCopyTitle_Click(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenu(sender);
        if (row == null) return;
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip != null) await clip.SetTextAsync(row.Job.Title ?? "");
        AppendLog("课名已复制");
    }

    private void MenuOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenu(sender);
        if (row == null) return;
        try
        {
            string path = row.Job.OutPath;
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
                { UseShellExecute = true });
            else
            {
                string dir = Path.GetDirectoryName(path);
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"")
                { UseShellExecute = true });
            }
        }
        catch (Exception ex) { AppendLog("！打开文件夹失败：" + ex.Message); }
    }

    private void MenuRemove_Click(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenu(sender);
        if (row == null) return;
        _queue.Remove(row.Job);
        RebuildRows();
        AppendLog($"已移出清单：{row.Job.Title}（磁盘上的成品不动）");
    }

    private XqdRow RowFromMenu(object sender) =>
        (sender as Control)?.DataContext as XqdRow
        ?? LstRows.SelectedItems?.OfType<XqdRow>().FirstOrDefault();

    private static XqdRow RowFromEvent(TappedEventArgs e)
    {
        if (e.Source is ListBoxItem direct) return direct.DataContext as XqdRow;
        if (e.Source is Visual v)
            foreach (var p in v.GetVisualAncestors())
                if (p is ListBoxItem li) return li.DataContext as XqdRow;
        return null;
    }

    // ══════════════════════════════════════════════════════════════
    //  杂项
    // ══════════════════════════════════════════════════════════════

    private void CmbQuality_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbQuality.SelectedItem is QualityOption q) _queue.Options.Quality = q.Value;
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e) => TxtLog.Text = "";

    private void AppendLog(string msg)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AppendLog(msg));
            return;
        }

        string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
        string text = TxtLog.Text ?? "";
        text = text.Length == 0 ? line : text + "\n" + line;

        // 日志太长会拖慢渲染：超过 1200 行砍掉最前面的
        string[] lines = text.Split('\n');
        if (lines.Length > 1200)
            text = string.Join("\n", lines.Skip(lines.Length - 1200));

        TxtLog.Text = text;
        TxtLog.CaretIndex = text.Length;
    }

    /// <summary>给 --uitest 用：等清单载入完成。</summary>
    internal int JobRowCount => _rows.Count;

    /// <summary>给 --uitest 用：直接走一遍「解析 + 加入清单」。</summary>
    internal int AddFromText(string text)
    {
        var items = LinkParser.Parse(text ?? "");
        int n = _queue.Add(items);
        RebuildRows();
        return n;
    }

    /// <summary>给 --xqdtest 用：真跑一次整批下载，返回成功/失败/跳过。</summary>
    internal Task<(int ok, int fail, int skip)> RunAllForTestAsync() => RunJobsAsync(null);

    /// <summary>给 --xqdtest 用：当前清单里的成品路径。</summary>
    internal IReadOnlyList<string> JobPaths()
        => _queue.Jobs.Select(j => j.OutPath).ToList();
}
