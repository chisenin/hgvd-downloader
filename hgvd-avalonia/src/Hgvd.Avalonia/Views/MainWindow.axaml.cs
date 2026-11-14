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
    public ObservableCollection<EpisodeRow> EpisodeRows { get; } = new();

    /// <summary>分集表**实际显示**的集合（= EpisodeRows 按当前筛选过滤后的结果）。</summary>
    public ObservableCollection<EpisodeRow> EpisodeView { get; } = new();

    public ObservableCollection<ShelfRow> FilteredShelf { get; } = new();

    private readonly List<Core.Api.Episode> _eps = new();
    private string _currentSeries = "";
    private string _currentTitle = "";

    /// <summary>正在加载分集的剧目 id（防止点勾选框 + 点列表行重复触发）。</summary>
    private string _loadingSeries = "";
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
        Log($"红果视频下载器 v{AboutWindow.AppVersion} 启动");
        if (!Util.HasFfprobe())
        {
            ShowBanner("未检测到 ffprobe —— 下载后的时长校验与解密会失败。"
                       + "请安装 ffmpeg 并加入 PATH。", BannerKind.Err);
            Log("！缺少 ffprobe（ffmpeg）");
        }

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
        SyncChkAll();

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
            SyncChkAll();
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

    /// <summary>
    /// 把「全选」框同步成列表的真实状态（全勾 / 半选 / 全不选）。
    /// </summary>
    private void SyncChkAll()
    {
        int total = ShelfRows.Count;
        int on = ShelfRows.Count(r => r.IsChecked);
        if (total == 0) ChkAll.IsChecked = false;
        else if (on == total) ChkAll.IsChecked = true;
        else if (on == 0) ChkAll.IsChecked = false;
        else ChkAll.IsChecked = null;   // 半选

        // 批量下载按钮跟着勾选数走
        int batchable = ShelfRows.Count(r => r.IsChecked && !r.IsShortDrama);
        BtnBatchRun.Content = $"批量下载 ({batchable})";
        BtnBatchRun.IsEnabled = batchable > 0 && !_downloading;
    }

    private void ShelfRow_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShelfRow.IsChecked)) SyncChkAll();
    }

    private void SetAuthLabel()
    {
        LblAuth.Text = _svc.LoggedIn ? "授权：已就绪" : "授权：未登录";
    }

    // ---------------------------------------------------------- 分集

    /// <summary>
    /// 选中某部剧 → 右侧加载分集。
    /// ★ 点击列表行、点勾选框都走这里。
    /// </summary>
    private async Task SelectSeriesAsync(ShelfRow row)
    {
        if (row is null) return;
        // 同一部剧已经在显示了就不重复请求
        if (row.SeriesId == _currentSeries
            && (EpisodeRows.Count > 0 || _loadingSeries == row.SeriesId))
            return;
        _currentSeries = row.SeriesId;
        _currentTitle = row.Display;
        await LoadEpisodesAsync(row.SeriesId, row.Display, row);
    }

    private async Task LoadEpisodesAsync(string seriesId, string title, ShelfRow row = null)
    {
        _loadingSeries = seriesId;
        _currentSeries = seriesId;
        _currentTitle = title;      // ★ 错误信息里要带剧名（批量下载时尤其重要）
        LblStatus.Text = $"正在读取「{title}」的分集…";
        LblEpHint.Text = $"正在读取「{title}」的分集…";
        _eps.Clear();
        EpisodeRows.Clear();
        EpisodeView.Clear();
        try
        {
            var eps = await _svc.ListEpisodesAsync(seriesId, 3, _cts.Token);
            _eps.AddRange(eps);
            foreach (var e in eps)
            {
                var r = new EpisodeRow(e) { TitleWidth = _titleWidth };
                r.PropertyChanged += EpisodeRow_PropertyChanged;
                EpisodeRows.Add(r);
                EpisodeView.Add(r);
            }

            string totalDur = eps.Count > 0
                ? TimeSpan.FromSeconds(eps.Sum(x => x.Duration)).ToString(@"hh\:mm\:ss")
                : "";
            LblEpHint.Text = $"{title} · 共 {eps.Count} 集"
                + (totalDur.Length > 0 ? $" · 总时长约 {totalDur}" : "");
            Marquee.Text = eps.Count > 0 ? $"{title} — {eps[0].Title}" : title;
            Log($"「{title}」共 {eps.Count} 集");
            LblStatus.Text = "就绪";

            // ★ 顺手把这一行的真实集数补上（列表右侧就能看到「N 集」）
            if (row != null && eps.Count > 0) row.EpisodeCnt = eps.Count;
        }
        catch (Exception e)
        {
            Log($"取分集失败：{e.Message}");
            ShowBanner($"取分集失败：{e.Message}", BannerKind.Err);
            LblStatus.Text = "就绪";
        }
        finally { _loadingSeries = ""; }
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

    /// <summary>按当前过滤条件刷新分集视图（保留选中行）。</summary>
    private void ApplyEpView()
    {
        // 先记下选中的行（按 Vid 认，刷新后行对象会换）
        var sel = LstEpisodes.SelectedItems?.Cast<EpisodeRow>()
            .Select(r => r.Vid).ToHashSet() ?? new HashSet<string>();

        EpisodeView.Clear();
        foreach (var r in EpisodeRows)
            if (MatchEpView(r, _epViewTag)) EpisodeView.Add(r);

        if (sel.Count == 0) return;
        LstEpisodes.Selection.Clear();
        for (int i = 0; i < EpisodeView.Count; i++)
            if (sel.Contains(EpisodeView[i].Vid))
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
        if (_eps.Count == 0)
        {
            ShowBanner("先在左边选一部剧。", BannerKind.Info);
            return;
        }

        var sel = LstEpisodes.SelectedItems?.Cast<EpisodeRow>().ToList() ?? new List<EpisodeRow>();
        // ★ 没选中行时，下的是**当前视图**里的（待下载视图 = 只下待下载的）。
        var targets = sel.Count > 0 ? sel : EpisodeView.ToList();

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
        SyncChkAll();
        var res = await DownloadRowsAsync(targets);
        _downloading = false;
        BtnRun.IsEnabled = true;
        BtnStop.IsEnabled = false;
        LblStatus.Text = "就绪";
        SyncChkAll();
        if (res.Aborted)
            ShowBanner("播放接口被服务端限流（连续多次返回空 body），已停止。"
                     + "\n等待几分钟后重试即可恢复；急用可改用「提取App缓存」。", BannerKind.Err);
        Log($"—— 完成 {res.Ok}，失败 {res.Fail}");
    }

    /// <summary>
    /// ★ 按左栏**勾选**批量下载多部剧。
    /// </summary>
    private async void BtnBatchRun_Click(object sender, RoutedEventArgs e)
    {
        var checkedList = ShelfRows.Where(r => r.IsChecked).ToList();
        if (checkedList.Count == 0)
        {
            ShowBanner("先在左边勾选要下载的剧目。", BannerKind.Info);
            return;
        }
        ApplyOutDir();   // ★ 同上：批量下载前先落实目录
        int dramas = checkedList.Count(r => r.IsShortDrama);
        var todo = checkedList.Where(r => !r.IsShortDrama).ToList();
        if (todo.Count == 0)
        {
            ShowBanner("勾选的都是短剧（bytevc 加密），下载出来也没法播。", BannerKind.Info);
            return;
        }

        // 勾了几十部就下几十部 —— 这种不可逆的大动作先确认一下
        if (todo.Count > 1)
        {
            bool ok = await MsgBox.ConfirmAsync(this, "批量下载确认",
                $"下载勾选的 {todo.Count} 部剧？\n\n"
                + $"· 短剧 {dramas} 部会自动跳过\n"
                + "· 已经下载过的会自动跳过\n"
                + "· 过程中可以点「停止」");
            if (!ok) return;
        }

        _stopRequested = false;
        _downloading = true;
        BtnBatchRun.IsEnabled = false;
        BtnRun.IsEnabled = false;
        BtnStop.IsEnabled = true;
        SyncChkAll();

        var res = await BatchDownloadAsync(todo);

        BtnBatchRun.IsEnabled = true;
        BtnRun.IsEnabled = true;
        BtnStop.IsEnabled = false;
        _downloading = false;
        LblStatus.Text = "就绪";
        SyncChkAll();
        if (res.Aborted)
            ShowBanner("播放接口被服务端限流（连续多次返回空 body），已停止。"
                     + "\n等待几分钟后重试即可恢复；急用可改用「提取App缓存」出片。", BannerKind.Err);
        Log($"—— 批量下载结束：{res.DoneSeries}/{todo.Count} 部 · 成功 {res.Ok} 集 · 失败 {res.Fail} 集");
    }

    /// <summary>批量下载的结果。</summary>
    public readonly record struct BatchResult(int Ok, int Fail, int DoneSeries, bool Aborted);

    /// <summary>
    /// 批量下载的核心：依次处理每部剧 —— 加载分集 → 逐集下载 → 换下一部。
    /// </summary>
    private async Task<BatchResult> BatchDownloadAsync(List<ShelfRow> todo)
    {
        int ok = 0, fail = 0, doneSeries = 0;
        foreach (var row in todo)
        {
            if (_stopRequested) break;
            LblStatus.Text = $"正在准备「{row.Display}」的分集…";
            await LoadEpisodesAsync(row.SeriesId, row.Display, row);

            var pending = EpisodeRows.Where(r => r.State == "待下载").ToList();
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
            var ep = _eps.FirstOrDefault(x => x.Vid == row.Vid);
            if (ep is null) continue;

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
                    Log($"✓ [{row.Index}] {tag} "
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
                    Log($"✗ [{row.Index}] {res.Error}");
                    _downloadErrors.Add($"「{_currentTitle}」[{row.Index}] {res.Error}");
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
                Log($"✗ [{row.Index}] {ex.Message}");
                _downloadErrors.Add($"「{_currentTitle}」[{row.Index}] {ex.Message}");
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

    private void UpdateHint()
    {
        int done = EpisodeRows.Count(r => r.State == "已完成");
        int failed = EpisodeRows.Count(r => r.State == "失败");
        LblEpHint.Text = $"{_currentTitle} · 共 {_eps.Count} 集 · 已完成 {done} · 失败 {failed}";
        LblRight.Text = $"已完成 {done} / {_eps.Count}";
    }

    // ---------------------------------------------------------- 界面事件

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        => _ = RefreshShelfAsync();

    private void EdSearch_TextChanged(object sender, TextChangedEventArgs e)
        => ApplyFilter();

    private void ChkAll_Click(object sender, RoutedEventArgs e)
    {
        // ★ 三态语义：半选 → 点一下变「全选」；全选 → 点一下「全清」；全清 → 「全选」。
        //   Click 在 IsChecked 变化之后触发，所以直接取新值当目标状态。
        bool target = ChkAll.IsChecked == true;
        foreach (var r in ShelfRows) r.IsChecked = target;
        SyncChkAll();
        Log(target ? $"已全选 {ShelfRows.Count} 项" : "已取消全部勾选");
    }

    /// <summary>
    /// 点勾选框（不是点列表行）→ 也要把右边分集切到这部剧。
    /// ★ 用 Click 而不是 Checked/Unchecked —— Click 只在**用户操作**时触发，
    ///   程序化改 IsChecked（比如全选）不会误触发加载。
    /// </summary>
    private async void ShkChk_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        if (cb.DataContext is not ShelfRow row) return;
        SyncChkAll();
        if (cb.IsChecked == true) await SelectSeriesAsync(row);
    }

    /// <summary>点左侧收藏夹列表 → 右边加载该剧的分集。</summary>
    private async void LstShelf_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        if (LstShelf.SelectedItem is not ShelfRow row) return;
        await SelectSeriesAsync(row);
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
        LblStatus.Text = "正在从 MuMu 读取登录态…";
        try
        {
            var acc = Core.Auth.MumuAuth.Login();
            _svc.SetAccount(acc);
            SetAuthLabel();
            Log("已从模拟器读取登录态：" + string.Join(", ", acc.Cookies.Keys));
            ShowBanner("授权成功。", BannerKind.Ok);
            await RefreshShelfAsync();
        }
        catch (Exception ex)
        {
            Log("读取登录态失败：" + ex.Message);
            ShowBanner("读取登录态失败：" + ex.Message, BannerKind.Err);
        }
        finally { BtnLogin.IsEnabled = true; LblStatus.Text = "就绪"; }
    }

    private void BtnEnv_Click(object sender, RoutedEventArgs e)
    {
        bool ff = Util.HasFfprobe();
        Log(ff ? "✓ ffprobe 可用" : "✗ 找不到 ffprobe");

        // 模拟器与登录态
        string mumuNote = "✗ 找不到 MuMuManager";
        try
        {
            string mumu = Core.Auth.MumuAuth.FindMumu();
            mumuNote = $"✓ MuMu：{mumu}";
        }
        catch (Exception ex) { mumuNote = $"✗ {ex.Message}"; }
        Log(mumuNote);
        Log(_svc.LoggedIn
            ? $"✓ 已登录（sessionid {(_svc.Account.Cookies.TryGetValue("sessionid", out var s) ? s.Length : 0)} 位）"
            : "✗ 未授权 —— 点「重新授权」从 MuMu 读 App 登录态");

        // 设备身份是否与 cookie 同源（文档 S2）
        var dev = _svc.Device;
        bool sameOrigin = !string.IsNullOrEmpty(dev.Iid)
            && _svc.Account.Cookies.TryGetValue("install_id", out var iid)
            && dev.Iid == iid;
        Log(sameOrigin
            ? $"✓ 设备同源（iid={dev.Iid} device_id={dev.DeviceId}）"
            : $"⚠ 设备与 cookie 不同源（iid={dev.Iid}）");

        ShowBanner(ff ? "环境检查完成，详见下方日志。" : "缺少 ffmpeg / ffprobe。",
                   ff ? BannerKind.Ok : BannerKind.Err);
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

    private void Window_PointerMoved(object sender, PointerEventArgs e) => UpdateDrag(e);

    private void Window_PointerReleased(object sender, PointerReleasedEventArgs e)
    {
        bool moved = _dragMoved;
        bool onBrand = _pressOnBrand;
        EndDrag();
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
        var name = (e.Source as Control)?.Name ?? (sender as Control)?.Name;
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
        try { BeginResizeDrag(edge.Value, e); } catch { }
        e.Handled = true;
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
