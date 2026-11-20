using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hgvd.Avalonia.ViewModels;
using Hgvd.Core;
using Hgvd.Core.Api;
using Hgvd.Core.Engine;

namespace Hgvd.Avalonia.Views;

/// <summary>画质选项（下拉框里的一项）。</summary>
public sealed class QualityOption
{
    public QualityOption(string text, string value)
    {
        Text = text;
        Value = value;
    }
    public string Text { get; }
    public string Value { get; }
    public override string ToString() => Text;
}

public partial class MainWindow : Window
{
    private readonly HongguoService _svc = new();
    private CancellationTokenSource _cts = new();

    public ObservableCollection<ShelfRow> ShelfRows { get; } = new();
    /// <summary>
    /// 右栏下载清单（含每部剧的分组标题行）。
    /// ★ 勾选一部剧 → 它的分集**追加**到尾部；取消勾选 → 整组移出。
    ///   多部剧同时在里面时，靠分组标题行隔开，序号各归各的，不会串。
    /// </summary>
    public ObservableCollection<EpisodeRow> EpisodeRows { get; } = new();

    /// <summary>分集表**实际显示**的集合（= EpisodeRows 按当前筛选过滤后的结果）。</summary>
    public ObservableCollection<EpisodeRow> EpisodeView { get; } = new();

    public ObservableCollection<ShelfRow> FilteredShelf { get; } = new();

    /// <summary>vid → 分集原始数据（下载时按 Vid 取，多部剧共用一个缓存）。</summary>
    private readonly Dictionary<string, Core.Api.Episode> _epCache = new();
    /// <summary>series_id → 已加入清单的分集行（取消勾选时整组移除）。</summary>
    private readonly Dictionary<string, List<EpisodeRow>> _queueBySeries = new();
    /// <summary>series_id → 该剧的分组标题行。</summary>
    private readonly Dictionary<string, EpisodeRow> _queueHeaders = new();

    /// <summary>分集视图过滤（全部 / 待下载 / 已完成 / 失败）。</summary>
    private string _epViewTag = "all";
    /// <summary>收藏夹是否正在刷新（自检要等它彻底结束）。</summary>
    private volatile bool _shelfBusy;
    /// <summary>series_id → 左栏行（标题回落时要用）。</summary>
    private readonly Dictionary<string, ShelfRow> _rowMap = new();

    /// <summary>标题列宽度（表头与数据行共用，保证对齐）。</summary>
    private double _titleWidth = 240;
    /// <summary>左栏剧名列宽度。</summary>
    private double _shelfTitleWidth = 200;
    /// <summary>画质下拉框初始化中（避免初始化时误存配置）。</summary>
    private bool _qualityInit;

    public MainWindow()
    {
        InitializeComponent();

        EdOut.Text = _svc.Config.ResolveOutDir();
        // ★ 状态栏右下角显示版本号（取程序集版本，和 exe 名里的一致）
        LblVersion.Text = "v" + AboutWindow.AppVersion;
        Title = $"红果视频下载器  v{AboutWindow.AppVersion}";

        // ★ 画质下拉框（默认「最高画质」= 用户要求）
        _qualityInit = true;
        CmbQuality.ItemsSource = new[]
        {
            new QualityOption("最高画质 (1080p)", "1080p"),
            new QualityOption("高清 (720p)", "720p"),
            new QualityOption("省流量 (360p)", "360p"),
        };
        CmbQuality.SelectedIndex = 0;
        SelectQuality(_svc.Config.Quality);
        _qualityInit = false;

        LstShelf.ItemsSource = FilteredShelf;
        LstEpisodes.ItemsSource = EpisodeView;

        // ★ 标题列吃掉剩余宽度（列定义不支持 "*"，只能自己算）
        SizeChanged += (_, _) => UpdateEpisodeColumns();
        Opened += async (_, _) =>
        {
            UpdateEpisodeColumns();
            await StartupAsync();
        };
    }

    /// <summary>
    /// 按列表实际宽度重排列宽：除剧名列外都是固定宽，
    /// 剧名列把剩下的空间全吃掉 —— 否则窄窗口下右侧标签会被挤出面板。
    /// </summary>
    private void UpdateEpisodeColumns()
    {
        UpdateShelfColumns();
        UpdateEpisodeTableColumns();
    }

    /// <summary>左栏收藏夹：算剧名列的宽度（勾选框 32 + 类型标签 66 + 滚动条余量）。</summary>
    private void UpdateShelfColumns()
    {
        double avail = LstShelf.Bounds.Width;
        if (avail <= 0) return;
        double title = Math.Max(60, avail - 32 - ShelfRow.MetaWidth - 16);
        if (Math.Abs(_shelfTitleWidth - title) <= 0.5) return;
        _shelfTitleWidth = title;
        foreach (var r in ShelfRows) r.TitleWidth = title;
    }

    /// <summary>右栏分集表：标题列吃掉剩余宽度（列定义不支持 "*"，只能自己算）。</summary>
    private void UpdateEpisodeTableColumns()
    {
        double avail = LstEpisodes.Bounds.Width;
        if (avail <= 0) return;
        const double others = 58 + 54 + 126 + 74 + 136;
        double title = Math.Max(140, avail - others - 20);
        if (Math.Abs(_titleWidth - title) <= 0.5) return;
        _titleWidth = title;
        HdTitle.Width = title;
        foreach (var r in EpisodeRows) r.TitleWidth = title;
    }

    // ---------------------------------------------------------- 启动

    /// <summary>切到 UI 线程执行（不会死锁）。</summary>
    private static void RunOnUi(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }

    internal static void Diag(string msg)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".hgv_tool");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "gui-diag.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { }
    }

    private async Task StartupAsync()
    {
        Diag("StartupAsync 开始");
        Diag("ffmpeg 目录：" + (ToolPaths.FfmpegDir.Length > 0 ? ToolPaths.FfmpegDir : "未内置（用 PATH）"));
        Log($"红果视频下载器 v{AboutWindow.AppVersion} 启动");
        // ★ ffmpeg / ffprobe 已随 exe 一起打包（首次运行自解压到本机缓存），
        //   这里不再做「环境检查」，缺少依赖时由调用处给出具体报错。

        if (!_svc.LoggedIn)
        {
            Diag("未登录");
            LblAuth.Text = "授权：未登录";
            ShowBanner("尚未授权。点右上角「重新授权」可从 MuMu 模拟器读取红果 App 的登录态。",
                       BannerKind.Info);
            Log("未登录 —— 点「重新授权」从 MuMu 读登录态");
            return;
        }
        LblAuth.Text = "授权：已就绪";
        Log("已登录（复用本机保存的授权）");
        Diag("开始读收藏夹");
        await RefreshShelfAsync();
        Diag("收藏夹读完");
    }

    // ---------------------------------------------------------- 收藏夹

    /// <summary>
    /// 读收藏夹 —— **渐进式**，不等全部就绪才显示。
    ///
    /// ★ 分三步走：
    ///   ① 收藏夹原始列表 → 立刻逐条上屏；
    ///   ② 标题靠批量详情接口并发取 → 逐项补齐；
    ///   ③ 合集的真实集数 → 用户点开该剧时随分集一起拿。
    /// </summary>
    private async Task RefreshShelfAsync()
    {
        LblStatus.Text = "正在读取收藏夹…";
        LblHint.Text = "读取中…";
        ShelfRows.Clear();
        _rowMap.Clear();
        ApplyFilter();
        ClearQueue();       // 清单跟收藏夹同步重建，避免留下已消失的剧

        int titled = 0;
        var sw = Stopwatch.StartNew();
        _shelfBusy = true;

        try
        {
            var items = await _svc.ListShelfStreamingAsync(
                onRaw: it => RunOnUi(() =>
                {
                    var row = new ShelfRow(it) { TitleWidth = _shelfTitleWidth };
                    row.PropertyChanged += ShelfRow_PropertyChanged;
                    _rowMap[it.SeriesId] = row;
                    ShelfRows.Add(row);
                    // ★ 增量上屏：每次重建整表（Clear + 224 次 Add）会白白卡住界面。
                    string kw = EdSearch.Text?.Trim() ?? "";
                    if (kw.Length == 0) FilteredShelf.Add(row);
                    else if (row.Display?.Contains(kw) ?? false) FilteredShelf.Add(row);
                    LblHint.Text = $"已读 {ShelfRows.Count} 项…";
                }),
                onDetail: (sid, title, cnt) => RunOnUi(() =>
                {
                    if (!_rowMap.TryGetValue(sid, out var row)) return;
                    if (!string.IsNullOrEmpty(title)) { row.Title = title; titled++; }
                    if (cnt > 0) row.EpisodeCnt = cnt;
                }),
                ct: _cts.Token);

            // 收尾对账（个别回调可能漏掉）
            foreach (var it in items)
                if (_rowMap.TryGetValue(it.SeriesId, out var row))
                {
                    if (!string.IsNullOrEmpty(it.Title)) row.Title = it.Title;
                    if (it.EpisodeCnt > 0) row.EpisodeCnt = it.EpisodeCnt;
                }

            ApplyFilter();
            UpdateQueueSummary();
            int dramas = ShelfRows.Count(r => r.IsShortDrama);
            Log($"收藏夹共 {ShelfRows.Count} 项（短剧 {dramas} 部，教学内容 {ShelfRows.Count - dramas} 部）"
                + $" · 标题 {titled} 个 · 用时 {sw.Elapsed.TotalSeconds:0.0}s");
            LblHint.Text = $"共 {ShelfRows.Count} 项";
            LblStatus.Text = "就绪";
            SetAuthLabel();

            // ★ 批量接口没给标题的那几条，后台用 album_detail 回落。
            //   不 await —— 它只影响几行，不该拖慢整个列表。
            _ = FillMissingTitlesAsync(items);
        }
        catch (Exception e)
        {
            Log($"读取收藏夹失败：{e.Message}");
            ShowBanner($"读取收藏夹失败：{e.Message}", BannerKind.Err);
            LblStatus.Text = "就绪";
        }
        finally { _shelfBusy = false; }
    }

    /// <summary>后台补标题：批量接口没返回标题的，单独查 album_detail。</summary>
    private async Task FillMissingTitlesAsync(List<Core.ShelfItem> items)
    {
        int need = items.Count(i => string.IsNullOrEmpty(i.Title));
        if (need == 0) return;
        try
        {
            await _svc.FillMissingTitlesAsync(items, (sid, title, cnt) => RunOnUi(() =>
            {
                if (!_rowMap.TryGetValue(sid, out var row)) return;
                if (!string.IsNullOrEmpty(title)) row.Title = title;
                if (cnt > 0) row.EpisodeCnt = cnt;
            }), _cts.Token);

            // 回落完了还是没标题的 → 服务端真的给不出来（已下架）
            RunOnUi(() =>
            {
                int dead = 0;
                foreach (var row in ShelfRows)
                    if (string.IsNullOrEmpty(row.Title)) { row.Unavailable = true; dead++; }
                if (dead > 0)
                    Log($"⚠ {dead} 项取不到标题（已下架或已被删除），列表里显示的是 id");
            });
        }
        catch (Exception e) { Diag("回落补标题失败：" + e.Message); }
    }

    private void ApplyFilter()
    {
        string kw = EdSearch.Text?.Trim() ?? "";
        FilteredShelf.Clear();
        foreach (var r in ShelfRows)
            if (kw.Length == 0 || (r.Display?.Contains(kw) ?? false))
                FilteredShelf.Add(r);
    }

    /// <summary>把右栏下载清单的统计刷到状态栏 / 提示行。</summary>
    private void UpdateQueueSummary()
    {
        int series = _queueBySeries.Count;
        int total = EpisodeRows.Count(r => r.IsEpisode);
        int done = EpisodeRows.Count(r => r.IsEpisode && r.State == "已完成");
        int failed = EpisodeRows.Count(r => r.IsEpisode && r.State == "失败");

        LblEpHint.Text = series == 0
            ? "在左边勾选剧目，它的分集会自动加入这里（可累计多部）"
            : $"已选 {series} 部 · 共 {total} 集 · 已完成 {done} · 失败 {failed}";
        LblRight.Text = series == 0 ? "" : $"已完成 {done} / {total}";
    }

    private void ShelfRow_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShelfRow.IsChecked)) UpdateQueueSummary();
    }

    private void SetAuthLabel()
    {
        LblAuth.Text = _svc.LoggedIn ? "授权：已就绪" : "授权：未登录";
    }

    // ---------------------------------------------------------- 下载清单

    /// <summary>清空右栏下载清单（刷新收藏夹时用）。</summary>
    private void ClearQueue()
    {
        EpisodeRows.Clear();
        EpisodeView.Clear();
        _queueBySeries.Clear();
        _queueHeaders.Clear();
        _epCache.Clear();
        Marquee.Text = "";
        UpdateQueueSummary();
    }

    /// <summary>
    /// 勾选一部剧 → 把它的分集**追加**到右栏清单末尾（不是替换）。
    /// ★ 多部剧各自成组，分组标题行把序号隔开，下载文件名也按各自的集号走。
    /// </summary>
    private async Task AddSeriesToQueueAsync(ShelfRow row)
    {
        if (row is null) return;
        if (_queueBySeries.ContainsKey(row.SeriesId)) return;      // 已在清单里

        LblStatus.Text = $"正在读取「{row.Display}」的分集…";
        try
        {
            var eps = await _svc.ListEpisodesAsync(row.SeriesId, 3, _cts.Token);
            // ★ 等待期间用户可能已取消勾选 → 二次确认，别把已取消的又加回去
            if (!row.IsChecked || _queueBySeries.ContainsKey(row.SeriesId)) return;

            foreach (var e in eps) _epCache[e.Vid] = e;
            var rows = new List<EpisodeRow>();
            foreach (var e in eps)
            {
                var r = new EpisodeRow(e) { TitleWidth = _titleWidth };
                r.PropertyChanged += EpisodeRow_PropertyChanged;
                rows.Add(r);
            }

            var header = EpisodeRow.Header(row.SeriesId, row.Display, eps.Count);
            _queueHeaders[row.SeriesId] = header;
            _queueBySeries[row.SeriesId] = rows;

            EpisodeRows.Add(header);
            EpisodeView.Add(header);
            foreach (var r in rows)
            {
                EpisodeRows.Add(r);
                EpisodeView.Add(r);
            }

            if (eps.Count > 0) row.EpisodeCnt = eps.Count;
            Marquee.Text = eps.Count > 0 ? $"{row.Display} — {eps[0].Title}" : row.Display;
            Log($"「{row.Display}」{eps.Count} 集已加入下载清单（当前 {_queueBySeries.Count} 部）");
            UpdateQueueSummary();
            LblStatus.Text = "就绪";
        }
        catch (Exception e)
        {
            // 读取失败 → 把勾去掉，别让用户以为加进来了
            row.IsChecked = false;
            Log($"取分集失败：{e.Message}");
            ShowBanner($"取「{row.Display}」分集失败：{e.Message}", BannerKind.Err);
            LblStatus.Text = "就绪";
        }
    }

    /// <summary>取消勾选 → 把这部剧的整组分集从清单里移出。</summary>
    private void RemoveSeriesFromQueue(ShelfRow row)
    {
        if (row is null) return;
        string sid = row.SeriesId;
        if (!_queueBySeries.TryGetValue(sid, out var rows)) return;

        foreach (var r in rows)
        {
            EpisodeRows.Remove(r);
            EpisodeView.Remove(r);
            _epCache.Remove(r.Vid);
        }
        if (_queueHeaders.TryGetValue(sid, out var header))
        {
            EpisodeRows.Remove(header);
            EpisodeView.Remove(header);
            _queueHeaders.Remove(sid);
        }
        _queueBySeries.Remove(sid);
        Log($"「{row.Display}」已从下载清单移出（当前 {_queueBySeries.Count} 部）");
        UpdateQueueSummary();
    }

    /// <summary>
    /// 单独把某一部剧的分集读出来（只返回数据行，**不往右栏清单里加**）。
    /// ★ 给 --batchtest / 批量下载这类「一次性过一遍」的流程用，不动用户已勾选的清单。
    /// </summary>
    private async Task<List<EpisodeRow>> LoadEpisodesRawAsync(string seriesId, string title)
    {
        var eps = await _svc.ListEpisodesAsync(seriesId, 3, _cts.Token);
        var rows = new List<EpisodeRow>();
        foreach (var e in eps)
        {
            _epCache[e.Vid] = e;    // 下载时按 vid 取数据
            var r = new EpisodeRow(e) { TitleWidth = _titleWidth };
            rows.Add(r);
        }
        Log($"「{title}」共 {eps.Count} 集");
        return rows;
    }

    /// <summary>
    /// 分集状态变了 → 如果当前是过滤视图（待下载 / 已完成 / 失败），重新过一遍筛。
    /// </summary>
    private void EpisodeRow_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EpisodeRow.State)) return;
        if (_epViewTag == "all") return;
        ApplyEpView();
    }

    /// <summary>按当前过滤条件刷新分集视图（保留选中行；分组标题行跟随本组是否有可见行）。</summary>
    private void ApplyEpView()
    {
        // 先记下选中的行（按 Vid 认，刷新后行对象会换）
        var sel = LstEpisodes.SelectedItems?.Cast<EpisodeRow>()
            .Where(r => r.IsEpisode)
            .Select(r => r.Vid).ToHashSet() ?? new HashSet<string>();

        EpisodeView.Clear();
        EpisodeRow? lastHeader = null;
        bool headerAdded = false;
        foreach (var r in EpisodeRows)
        {
            if (r.IsGroupHeader)
            {
                lastHeader = r;
                headerAdded = false;
                continue;
            }
            if (!MatchEpView(r, _epViewTag)) continue;
            // ★ 只有本组真有可见分集时才把标题行带出来，别留个空标题
            if (lastHeader != null && !headerAdded)
            {
                EpisodeView.Add(lastHeader);
                headerAdded = true;
            }
            EpisodeView.Add(r);
        }

        if (sel.Count == 0) return;
        LstEpisodes.Selection.Clear();
        for (int i = 0; i < EpisodeView.Count; i++)
            if (EpisodeView[i].IsEpisode && sel.Contains(EpisodeView[i].Vid))
                LstEpisodes.Selection.Select(i);
    }

    private static bool MatchEpView(EpisodeRow r, string tag)
    {
        return tag switch
        {
            "pending" => r.State == "待下载",
            "done" => r.State == "已完成",
            "failed" => r.State == "失败",
            _ => true,
        };
    }

    // ---------------------------------------------------------- 下载

    private async void BtnCache_Click(object sender, RoutedEventArgs e)
    {
        BtnCache.IsEnabled = false;
        try
        {
            ShowBanner("正在从 MuMu 提取 App 视频缓存…", BannerKind.Info);
            string mumu = Core.Auth.MumuAuth.FindMumu();
            int inst = Core.Auth.MumuAuth.FindUsableInstance(mumu, out _);
            Log($"从实例 {inst} 提取缓存…");

            string outDir = Path.Combine(_svc.Config.ResolveOutDir(), "_app_cache");
            var entries = await Task.Run(() => Core.Auth.CacheExtract.ExtractAll(
                mumu, inst, outDir, 100_000, 0, Log, true));

            long total = entries.Sum(e => e.Size);
            Log($"✓ 提取 {entries.Count} 个完整视频，共 {total / 1024 / 1024} MB → {outDir}");
            foreach (var ce in entries.Take(20))
                Log($"  · {ce.Name}  {ce.Codec} {ce.Width}x{ce.Height} {ce.Duration:0.0}s");

            ShowBanner(entries.Count > 0
                ? $"已提取 {entries.Count} 个完整视频到 {outDir}"
                : "App 缓存里没有已下载完成的视频（先在 App 里播放一些内容，并等它下完）。",
                entries.Count > 0 ? BannerKind.Ok : BannerKind.Info);

            // 提取成功 → 播放接口的熔断状态可以清了（那是另一条出片路径）
            if (entries.Count > 0)
            {
                HongguoService.ResetPlaybackCircuit();
                Log("已重置播放接口熔断状态");
            }
        }
        catch (Exception ex)
        {
            Log($"✗ 缓存提取失败：{ex.Message}");
            ShowBanner($"缓存提取失败：{ex.Message}", BannerKind.Err);
        }
        finally { BtnCache.IsEnabled = true; }
    }

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;   // 下载中重复点会打乱状态，直接忽略
        var queued = _queueBySeries.Values
            .SelectMany(v => v)
            .Where(r => r.IsEpisode).ToList();
        if (queued.Count == 0)
        {
            ShowBanner("先在左边勾选剧目，它的分集会加到右边清单里。", BannerKind.Info);
            return;
        }

        // ★ 有选中行 → 只下选中的；没选中 → 下清单里所有**待下载**的（已完成不重复跑）
        var sel = LstEpisodes.SelectedItems?.Cast<EpisodeRow>()
            .Where(r => r.IsEpisode).ToList() ?? new List<EpisodeRow>();
        var targets = sel.Count > 0
            ? sel
            : queued.Where(r => r.State == "待下载").ToList();
        if (targets.Count == 0)
        {
            ShowBanner("清单里没有待下载的分集（都已完成）。", BannerKind.Info);
            return;
        }

        // ★ 短剧默认跳过（bytevc 加密，解不出画面）
        if (targets.Any(t => t.State == "待下载" && IsDramaRow(t)))
        {
            int n = targets.Count(t => t.State == "待下载" && IsDramaRow(t));
            ShowBanner($"已跳过 {n} 部短剧 —— 它的编码是字节私有 bytevc，下载后无法正常播放。",
                       BannerKind.Info);
        }
        targets = targets.Where(t => !IsDramaRow(t)).ToList();
        if (targets.Count == 0)
        {
            ShowBanner("没有可下载的分集（短剧会自动跳过）。", BannerKind.Info);
            return;
        }

        // ★ 下载前再强制应用一次目录 —— 即使用户没点别处、也没回车，
        //   文本框里改的内容也必须生效。
        ApplyOutDir();

        _stopRequested = false;
        _downloading = true;
        BtnRun.IsEnabled = false;
        BtnStop.IsEnabled = true;
        var res = await DownloadRowsAsync(targets);
        _downloading = false;
        BtnRun.IsEnabled = true;
        BtnStop.IsEnabled = false;
        LblStatus.Text = "就绪";
        UpdateQueueSummary();
        if (res.Aborted)
            ShowBanner("播放接口被服务端限流（连续多次返回空 body），已停止。"
                     + "\n等待几分钟后重试即可恢复；急用可改用「提取App缓存」。", BannerKind.Err);
        Log($"—— 完成 {res.Ok}，失败 {res.Fail}");
    }

    /// <summary>批量下载的结果。</summary>
    public readonly record struct BatchResult(int Ok, int Fail, int DoneSeries, bool Aborted);

    /// <summary>
    /// 批量下载的核心：依次处理每部剧 —— 取分集（**不进右栏清单**）→ 逐集下载。
    /// ★ 给 --batchtest 用；用户日常路径是「勾选累计到右栏清单，再点下载」，
    ///   这里是独立流程，不碰用户已经勾好的清单。
    /// </summary>
    private async Task<BatchResult> BatchDownloadAsync(List<ShelfRow> todo)
    {
        int ok = 0, fail = 0, doneSeries = 0;
        foreach (var row in todo)
        {
            if (_stopRequested) break;
            LblStatus.Text = $"正在准备「{row.Display}」的分集…";

            List<EpisodeRow> rows;
            try
            {
                rows = await LoadEpisodesRawAsync(row.SeriesId, row.Display);
            }
            catch (Exception e)
            {
                Log($"✗ 取「{row.Display}」分集失败：{e.Message}");
                fail++;
                doneSeries++;
                continue;
            }

            var pending = rows.Where(r => r.State == "待下载").ToList();
            if (pending.Count == 0)
            {
                Log($"「{row.Display}」没有待下载的分集，跳过");
                doneSeries++;
                continue;
            }

            LblStatus.Text = $"正在下载「{row.Display}」（第 {doneSeries + 1}/{todo.Count} 部，"
                           + $"{pending.Count} 集）";
            var r = await DownloadRowsAsync(pending);
            ok += r.Ok;
            fail += r.Fail;
            doneSeries++;

            if (r.Aborted)
            {
                ShowBanner("播放接口被服务端限流（连续多次返回空 body），已停止。"
                         + "\n等待几分钟后重试即可恢复；急用可改用「提取App缓存」出片。", BannerKind.Err);
                break;
            }
        }
        return new BatchResult(ok, fail, doneSeries, false);
    }

    /// <summary>下载中的错误明细（批量测试要写进报告）。</summary>
    private readonly List<string> _downloadErrors = new();

    /// <summary>
    /// ★ 测试钩子：替换掉真正的下载（服务端被风控时，用它验证批量逻辑本身）。
    /// </summary>
    internal Func<Core.Api.Episode, string, DownloadCallbacks, CancellationToken,
        Task<DownloadResult>>? DownloadHook;

    private async Task<(int Ok, int Fail, bool Aborted)> DownloadRowsAsync(
        List<EpisodeRow> targets)
    {
        int ok = 0, fail = 0, addrFail = 0;
        foreach (var row in targets)
        {
            if (_stopRequested) break;
            if (row.IsGroupHeader) continue;                 // 分组标题不是真实分集
            if (!_epCache.TryGetValue(row.Vid, out var ep)) continue;

            string series = string.IsNullOrEmpty(row.SeriesTitle) ? row.SeriesId : row.SeriesTitle;
            row.State = "下载中";
            row.Error = "";

            // ★ 进度刷新要**节流**：原来每个 256KB 分片都往 UI 线程派一次任务，
            //   30MB 的文件 = 120+ 次；下载时 UI 线程被派发队列灌满，
            //   鼠标事件排队 → 窗口「拖不动」。
            //   现在限制成最多 10 次/秒，最后一次（d == t）不丢。
            long lastUi = 0;
            var cb = new DownloadCallbacks
            {
                OnPhase = p => RunOnUi(() => { row.State = p; }),
                OnResolvedSize = sz => RunOnUi(() => { row.PlanSize = sz; }),
                OnProgress = (d, t, s) =>
                {
                    long now = Environment.TickCount64;
                    bool last = t > 0 && d >= t;
                    if (!last && now - lastUi < 100) return;
                    lastUi = now;
                    RunOnUi(() =>
                    {
                        row.Down = d;
                        row.Total = t;
                        row.Speed = s;
                    });
                },
            };

            try
            {
                var res = DownloadHook != null
                    ? await DownloadHook(ep, GetQuality(), cb, _cts.Token)
                    : await _svc.DownloadEpisodeAsync(ep, GetQuality(), cb, _cts.Token);
                if (res.Ok)
                {
                    ok++;
                    addrFail = 0;
                    RunOnUi(() =>
                    {
                        row.State = "已完成";
                        row.Down = res.Size;
                        row.Total = res.Size;
                    });
                    string tag = res.Skipped ? "已存在" : "完成";
                    Log($"✓ {series} [{row.Index}] {tag} "
                        + $"{Util.FmtSize(res.Size)} {Util.FmtDuration(res.Duration)}");
                }
                else
                {
                    fail++;
                    // ★★ 只有「空 body」才说明服务端真的在限流；
                    //   其余（code=101000 参数错 / 网络错）不是限流，不该触发中止。
                    bool rateLimited = res.Error?.Contains("空 body") == true;
                    if (res.Error?.Contains("无法解析播放地址") == true && rateLimited) addrFail++;
                    RunOnUi(() => { row.State = "失败"; row.Error = res.Error; });
                    Log($"✗ {series} [{row.Index}] {res.Error}");
                    _downloadErrors.Add($"「{series}」[{row.Index}] {res.Error}");
                    // ★ 连续空 body → 服务端限流（Core 里已熔断），立刻停
                    if (addrFail >= 3 || HongguoService.PlaybackBlocked)
                    {
                        Log("！播放接口被服务端限流（连续空 body），停止本批。"
                            + "等待几分钟后重试，或改用「提取App缓存」出片。");
                        return (ok, fail, true);
                    }
                }
            }
            catch (Exception ex)
            {
                fail++;
                RunOnUi(() => { row.State = "失败"; row.Error = ex.Message; });
                Log($"✗ {series} [{row.Index}] {ex.Message}");
                _downloadErrors.Add($"「{series}」[{row.Index}] {ex.Message}");
            }
            UpdateHint();
        }
        return (ok, fail, false);
    }

    private bool IsDramaRow(EpisodeRow row)
    {
        var shelf = ShelfRows.FirstOrDefault(s => s.SeriesId == row.SeriesId);
        return shelf?.IsShortDrama ?? false;
    }

    private bool _stopRequested;

    /// <summary>正在下载（用来禁掉「批量下载」按钮，避免重复点）。</summary>
    private bool _downloading;

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _stopRequested = true;
        _cts.Cancel();
        Log("已请求停止");
        BtnStop.IsEnabled = false;
    }

    private string GetQuality()
    {
        if (CmbQuality.SelectedItem is QualityOption opt) return opt.Value;
        return "1080p";
    }

    /// <summary>按配置里的画质值选中下拉框对应项（找不到就保持默认）。</summary>
    private void SelectQuality(string quality)
    {
        if (string.IsNullOrWhiteSpace(quality)) return;
        var opts = CmbQuality.ItemsSource as QualityOption[];
        if (opts == null) return;
        for (int i = 0; i < opts.Length; i++)
            if (string.Equals(opts[i].Value, quality, StringComparison.OrdinalIgnoreCase))
            {
                CmbQuality.SelectedIndex = i;
                return;
            }
    }

    private void UpdateHint() => UpdateQueueSummary();

    // ---------------------------------------------------------- 界面事件

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        => _ = RefreshShelfAsync();

    private void EdSearch_TextChanged(object sender, TextChangedEventArgs e)
        => ApplyFilter();

    /// <summary>
    /// 勾选框 → 勾上就**追加**该剧到右栏下载清单，取消就整组移出。
    /// ★ 用 Click 而不是 Checked/Unchecked —— Click 只在**用户操作**时触发，
    ///   程序化改 IsChecked（比如刷新后重置）不会误触发请求。
    /// </summary>
    private async void ShkChk_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        if (cb.DataContext is not ShelfRow row) return;
        if (cb.IsChecked == true) await AddSeriesToQueueAsync(row);
        else RemoveSeriesFromQueue(row);
        UpdateQueueSummary();
    }

    /// <summary>
    /// 点左侧收藏夹列表 → **只是选中高亮，不再加载分集**（加不加由勾选框决定）。
    /// ★ Avalonia 没有 SelectionMode=None，这里选完立刻清空选中，
    ///   免得高亮让用户误以为「点这一行 = 要下载这部」。
    /// </summary>
    private void LstShelf_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstShelf.SelectedItems == null || LstShelf.SelectedItems.Count == 0) return;
        LstShelf.Selection.Clear();
    }

    private void RbView_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        // ★ 目前界面上只保留「全部」；筛选逻辑留着，加回来很快。
        _epViewTag = sender == RbAll ? "all" : "all";
        ApplyEpView();
    }

    private void CmbQuality_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_qualityInit || !IsLoaded) return;
        _svc.Config.Quality = GetQuality();
        _svc.Config.Save();
        Log($"画质切换为 {GetQuality()}");
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        => TxtLog.Text = "";

    private async void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        // ★ 用 Avalonia 的 StorageProvider（跨平台的文件夹选择器），
        //   不再依赖 WinForms 的 FolderBrowserDialog。
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择下载目录",
                AllowMultiple = false,
            });
            if (folders != null && folders.Count > 0)
            {
                string path = folders[0].Path.LocalPath;
                EdOut.Text = path;
                ApplyOutDir();
            }
        }
        catch (Exception ex)
        {
            Log($"选择目录失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 下载目录：文本框改完直接回车或点别处就生效。
    /// </summary>
    private void EdOut_LostFocus(object sender, RoutedEventArgs e) => ApplyOutDir();

    private void EdOut_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyOutDir();
        e.Handled = true;
        BtnBrowse.Focus();
    }

    /// <summary>把输入框里的路径写进配置并落盘。</summary>
    private void ApplyOutDir()
    {
        string dir = (EdOut.Text ?? "").Trim().Trim('"');
        if (dir.Length == 0) return;
        if (string.Equals(_svc.Config.OutDir, dir, StringComparison.OrdinalIgnoreCase)) return;
        _svc.Config.OutDir = dir;
        _svc.Config.Save();
        Log($"下载目录已改为：{dir}");
        LblStatus.Text = "就绪";
    }

    private async void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        BtnLogin.IsEnabled = false;
        try
        {
            if (await LoginOnceAsync()) return;

            // ★ 第一步：先确认能不能找到 MuMuManager（装在哪里都行）
            string mumu = "";
            try { mumu = Core.Auth.MumuAuth.FindMumu(); } catch { }

            if (mumu.Length == 0)
            {
                // 自动找不到（用户装在任意目录）→ 直接让用户选一次 MuMuManager.exe，
                // 而不是丢一句「请设环境变量」给普通用户。
                string picked = await PickMumuManagerAsync();
                if (picked.Length == 0) return;
                if (!Core.Auth.MumuAuth.SetManagerPath(picked))
                {
                    Log("这个文件不是 MuMuManager.exe：" + picked);
                    ShowBanner("请选 MuMu 安装目录 nx_main 里的 MuMuManager.exe。", BannerKind.Err);
                    return;
                }
                Log("已记住 MuMu 位置：" + picked);
                mumu = picked;
                if (await LoginOnceAsync()) return;
            }

            // ★★ 第二步：找到了模拟器但读不到授权 → 引导用户开安卓机 / 登录红果，
            //    后台每 2 秒扫一次运行中的实例，读到就自动继续（不用再点按钮）。
            await GuideToLoginAsync(mumu);
        }
        finally { BtnLogin.IsEnabled = true; LblStatus.Text = "就绪"; }
    }

    /// <summary>引导用户打开安卓机 + 登录红果，并在此期间自动取授权。</summary>
    private async Task GuideToLoginAsync(string mumu)
    {
        List<Core.Auth.MumuAuth.Instance> insts;
        try { insts = Core.Auth.MumuAuth.ListInstances(mumu); }
        catch { insts = new List<Core.Auth.MumuAuth.Instance>(); }

        LblStatus.Text = "等待安卓机里的红果登录态…";
        var dlg = new WaitAuthWindow(mumu, insts);
        await dlg.ShowDialog(this);
        LblStatus.Text = "就绪";

        if (dlg.FoundIndex < 0)
        {
            ShowBanner("没等到授权：请先启动安卓机并在红果里确认已登录，再点「重新授权」。",
                       BannerKind.Err);
            return;
        }

        try
        {
            var acc = Core.Auth.MumuAuth.Login(dlg.FoundIndex);
            _svc.SetAccount(acc);
            SetAuthLabel();
            Log($"授权成功（实例 {dlg.FoundIndex}）：{acc.Cookies.Count} 个 cookie 字段"
                + (acc.UserId.Length > 0 ? $"，uid={acc.UserId}" : ""));
            ShowBanner("授权成功。", BannerKind.Ok);
            await RefreshShelfAsync();
        }
        catch (Exception ex)
        {
            Log("读取登录态失败：" + ex.Message);
            ShowBanner("读取登录态失败：" + ex.Message, BannerKind.Err);
        }
    }

    /// <summary>走一次「从模拟器读登录态」，返回是否成功。</summary>
    private async Task<bool> LoginOnceAsync()
    {
        LblStatus.Text = "正在从 MuMu 读取登录态…";
        try
        {
            var acc = Core.Auth.MumuAuth.Login();
            _svc.SetAccount(acc);
            SetAuthLabel();
            Log($"已从模拟器读取登录态（实例 {Core.Auth.MumuAuth.LastPickedIndex}）："
                + string.Join(", ", acc.Cookies.Keys));
            ShowBanner("授权成功。", BannerKind.Ok);
            await RefreshShelfAsync();
            return true;
        }
        catch (Exception ex)
        {
            Log("读取登录态失败：" + ex.Message);
            ShowBanner("读取登录态失败：" + ex.Message, BannerKind.Err);
            return false;
        }
    }

    /// <summary>手动选一次 MuMuManager.exe（只在该机器上选一次，之后走缓存）。</summary>
    private async Task<string> PickMumuManagerAsync()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 MuMuManager.exe（在 MuMu 安装目录的 nx_main 里）",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("MuMuManager.exe")
                    {
                        Patterns = new[] { "MuMuManager.exe" },
                    },
                },
            });
            if (files == null || files.Count == 0) return "";
            return files[0].TryGetLocalPath() ?? "";
        }
        catch (Exception ex)
        {
            Log("选择 MuMuManager 失败：" + ex.Message);
            return "";
        }
    }

    private void BtnWinMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnWinMax_Click(object sender, RoutedEventArgs e) => ToggleMax();

    private void BtnWinClose_Click(object sender, RoutedEventArgs e) => Close();

    // ---------------------------------------------------------- 自绘窗口 / 拖动 / 缩放

    /// <summary>
    /// ★ 拖动窗口。
    ///
    /// ★★ 之前的实现有两个致命问题：
    ///   ① 只在 logo 那一小块上监听 MouseMove，鼠标一移出那块就收不到事件；
    ///   ② 拖动期间没有鼠标捕获，按下之后鼠标滑出控件，松手时状态对不上。
    ///   ⇒ 改成：顶栏整条都能拖 + 显式捕获鼠标 + 用**屏幕坐标**算位移。
    /// </summary>
    private bool _dragging;
    private bool _dragMoved;
    private bool _pressOnBrand;
    private PixelPoint _dragStartScreen;
    private PixelPoint _dragOriginPos;
    private IPointer _captured;

    /// <summary>
    /// ★ 缩放状态（自己算坐标，不走 BeginResizeDrag）。
    ///
    /// 自绘窗口（SystemDecorations=None + WS_POPUP）没有系统的可缩放边框，
    /// Windows 的 SC_SIZE 在无边框窗口上直接不生效 —— 这就是「窗体拖边
    /// 改不了大小」的根因。所以这里跟拖动一样：捕获指针、按屏幕坐标
    /// 自己算出 Width/Height/Position。
    /// </summary>
    private bool _resizing;
    private WindowEdge _resizeEdge;
    private PixelPoint _resizeStartScreen;
    private PixelPoint _resizeStartPos;
    private double _resizeStartW;
    private double _resizeStartH;

    /// <summary>点顶栏空白处 → 拖动窗口（按钮/输入框上不触发）。</summary>
    private void TopBar_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        var src = e.Source as IInputElement;
        // ★ 顶栏里的按钮、输入框、下拉框自己处理鼠标，别抢
        Diag($"顶栏按下 client={e.GetPosition(this)} screen={this.PointToScreen(e.GetPosition(this))} src={src?.GetType().Name} interactive={IsInteractive(src)}");
        if (IsInteractive(src)) return;
        if (e.ClickCount >= 2 && FindAncestor<Border>(src) == BrandBox) { ToggleMax(); return; }
        _pressOnBrand = FindAncestor<Border>(src) == BrandBox;
        BeginDrag(e);
    }

    private void Window_PointerMoved(object sender, PointerEventArgs e)
    {
        UpdateDrag(e);
        UpdateResize(e);
    }

    private void Window_PointerReleased(object sender, PointerReleasedEventArgs e)
    {
        bool moved = _dragMoved;
        bool onBrand = _pressOnBrand;
        EndDrag();
        EndResize();
        _pressOnBrand = false;
        // ★ 单击（没拖动）→ 弹「关于」；拖动过就不弹。
        if (!moved && onBrand) OnBrandClicked();
    }

    /// <summary>logo 区单击 → 打开「关于」（自检也走这里）。</summary>
    internal void OnBrandClicked() => ShowAbout();

    private void BeginDrag(PointerPressedEventArgs e)
    {
        _dragging = true;
        _dragMoved = false;
        // 最大化状态下拖 → 先还原成正常窗口，否则改位置也没反应
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        _dragOriginPos = Position;
        _dragStartScreen = this.PointToScreen(e.GetPosition(this));
        Diag($"开始拖动 起点屏幕={_dragStartScreen.X},{_dragStartScreen.Y} 窗口={Position.X},{Position.Y}");
        // ★ 捕获指针：这样鼠标滑出窗口仍然能收到 PointerMoved
        _captured = e.Pointer;
        try { e.Pointer.Capture(this); } catch { }
    }

    private void UpdateDrag(PointerEventArgs e)
    {
        if (!_dragging) return;
        var now = this.PointToScreen(e.GetPosition(this));
        int dx = now.X - _dragStartScreen.X;
        int dy = now.Y - _dragStartScreen.Y;
        // 小于 3 像素当作「没动」，避免手抖时窗口乱跳
        if (!_dragMoved && Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return;
        _dragMoved = true;
        Position = new PixelPoint(_dragOriginPos.X + dx, _dragOriginPos.Y + dy);
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        try { _captured?.Capture(null); } catch { }
        _captured = null;
    }

    /// <summary>事件源是不是按钮/输入框之类要自己处理鼠标的控件。</summary>
    private static bool IsInteractive(IInputElement src)
    {
        var v = src as Visual;
        while (v != null)
        {
            if (v is Button || v is TextBox || v is ComboBox
                || v is CheckBox || v is RadioButton || v is ToggleButton)
                return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    /// <summary>沿可视树往上找某个祖先控件。</summary>
    private static T FindAncestor<T>(IInputElement src) where T : class
    {
        var v = src as Visual;
        while (v != null)
        {
            if (v is T t) return t;
            v = v.GetVisualParent();
        }
        return null;
    }

    private void ToggleMax()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;

    /// <summary>窗口边缘的缩放热区。</summary>
    private void Grip_PointerPressed(object sender, PointerPressedEventArgs e)
    {
        if (!CanResize) return;
        if (WindowState != WindowState.Normal) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var name = (sender as Control)?.Name ?? (e.Source as Control)?.Name;
        WindowEdge? edge = name switch
        {
            "GripLeft" => WindowEdge.West,
            "GripRight" => WindowEdge.East,
            "GripTop" => WindowEdge.North,
            "GripBottom" => WindowEdge.South,
            "GripTopLeft" => WindowEdge.NorthWest,
            "GripTopRight" => WindowEdge.NorthEast,
            "GripBottomLeft" => WindowEdge.SouthWest,
            "GripBottomRight" => WindowEdge.SouthEast,
            _ => null,
        };
        if (edge == null) return;

        _resizing = true;
        _resizeEdge = edge.Value;
        _resizeStartScreen = this.PointToScreen(e.GetPosition(this));
        _resizeStartPos = Position;
        _resizeStartW = Bounds.Width;
        _resizeStartH = Bounds.Height;
        _captured = e.Pointer;
        try { e.Pointer.Capture(this); } catch { }
        e.Handled = true;
    }

    /// <summary>拖边缩放：按屏幕坐标算新宽度/高度，贴左/上边时同步挪窗口位置。</summary>
    private void UpdateResize(PointerEventArgs e)
    {
        if (!_resizing) return;

        var now = this.PointToScreen(e.GetPosition(this));
        double scale = RenderScaling <= 0 ? 1 : RenderScaling;
        // 屏幕位移换算成 DIP（Width/Height 是 DIP，Position 是物理像素）
        double dx = (now.X - _resizeStartScreen.X) / scale;
        double dy = (now.Y - _resizeStartScreen.Y) / scale;

        double minW = MinWidth > 0 ? MinWidth : 400;
        double minH = MinHeight > 0 ? MinHeight : 300;

        double w = _resizeStartW;
        double h = _resizeStartH;
        int left = _resizeStartPos.X;
        int top = _resizeStartPos.Y;

        bool west = _resizeEdge is WindowEdge.West or WindowEdge.NorthWest or WindowEdge.SouthWest;
        bool east = _resizeEdge is WindowEdge.East or WindowEdge.NorthEast or WindowEdge.SouthEast;
        bool north = _resizeEdge is WindowEdge.North or WindowEdge.NorthWest or WindowEdge.NorthEast;
        bool south = _resizeEdge is WindowEdge.South or WindowEdge.SouthWest or WindowEdge.SouthEast;

        if (west)
        {
            w = Math.Max(minW, _resizeStartW - dx);
            left = _resizeStartPos.X + (int)Math.Round((_resizeStartW - w) * scale);
        }
        else if (east)
        {
            w = Math.Max(minW, _resizeStartW + dx);
        }

        if (north)
        {
            h = Math.Max(minH, _resizeStartH - dy);
            top = _resizeStartPos.Y + (int)Math.Round((_resizeStartH - h) * scale);
        }
        else if (south)
        {
            h = Math.Max(minH, _resizeStartH + dy);
        }

        if (Math.Abs(Width - w) > 0.5) Width = w;
        if (Math.Abs(Height - h) > 0.5) Height = h;
        if (left != Position.X || top != Position.Y)
            Position = new PixelPoint(left, top);
    }

    private void EndResize()
    {
        if (!_resizing) return;
        _resizing = false;
        try { _captured?.Capture(null); } catch { }
        _captured = null;
    }

    /// <summary>当前打开的「关于」窗口（没有则 null）。</summary>
    internal static AboutWindow AboutInstance { get; private set; }

    /// <summary>
    /// logo 区单击 → 弹「关于」窗口。
    /// ★ 用 Show() 而不是 ShowDialog()：模态窗口会卡住整个应用，
    ///   而且用户在读说明时想顺手切回主窗口。
    /// </summary>
    private void ShowAbout()
    {
        try
        {
            if (AboutInstance is { } w)
            {
                w.Activate();
                return;
            }
            var win = new AboutWindow();
            win.SetOwner(this);
            win.Closed += (_, _) => AboutInstance = null;
            AboutInstance = win;
            win.Show();
        }
        catch (Exception e)
        {
            Log($"打开「关于」失败：{e.Message}");
        }
    }

    // ---------------------------------------------------------- 日志 / 提示

    private enum BannerKind { Info, Ok, Err }

    /// <summary>写日志。</summary>
    /// ★★ 不要用 Dispatcher.Invoke —— async 方法 await 之后会回到 UI 线程，
    ///   此时 Invoke 会等自己执行 → **死锁**。
    ///   用 CheckAccess 判断当前是不是 UI 线程。
    private void Log(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        void Append()
        {
            string cur = TxtLog.Text ?? "";
            // 日志别无限长 —— 超过 20 万字符就把开头截掉
            if (cur.Length > 200_000) cur = cur[(cur.Length - 150_000)..];
            cur += line + Environment.NewLine;
            TxtLog.Text = cur;
            TxtLog.CaretIndex = cur.Length;
        }
        if (Dispatcher.UIThread.CheckAccess()) Append();
        else Dispatcher.UIThread.Post(Append);
    }

    private void ShowBanner(string msg, BannerKind kind)
    {
        void Apply()
        {
            Banner.IsVisible = true;
            string bg, bd, fg;
            switch (kind)
            {
                case BannerKind.Ok: bg = "BannerOkBg"; bd = "BannerOkBd"; fg = "BannerOkFg"; break;
                case BannerKind.Err: bg = "BannerErrBg"; bd = "BannerErrBd"; fg = "BannerErrFg"; break;
                default: bg = "BannerInfoBg"; bd = "BannerInfoBd"; fg = "BannerInfoFg"; break;
            }
            Banner.Background = Controls.Palette.Brush(bg);
            Banner.BorderBrush = Controls.Palette.Brush(bd);
            TxtBanner.Foreground = Controls.Palette.Brush(fg);
            TxtBanner.Text = msg;
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }
}
