using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
using Hgvd.Xqd;
using Hgvd.Xqd.Api;

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

    // ── 兴趣岛（第二个内容源）：课表发现 + 自己的下载队列 ──
    private XqdQueue _xqd = new(new XqdOptions());
    private string _xqdCookie = "";
    private bool _xqdSource;                 // false = 红果，true = 兴趣岛
    private bool _campsLoaded;
    private volatile bool _campsBusy;
    private readonly PauseGate _hgGate = new();
    private bool _paused;
    private bool _testMode;                  // --uitest：不碰网络、不写用户清单
    private string _hgQuality = "1080p";
    private string _xqdQuality = "max";

    /// <summary>红果清单的落盘（重启恢复）。兴趣岛用 XqdStore 的 jobs.json。</summary>
    private QueueStore _hgQueueStore = new();
    private bool _hgRestored;

    /// <summary>下载历史（两源共用 history.json，与 Python 版同格式）。</summary>
    private readonly HistoryStore _history = new();

    public ObservableCollection<XqdCampRow> CampRows { get; } = new();
    public ObservableCollection<XqdCampRow> FilteredCamps { get; } = new();

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
    /// <summary>营名 → 已加入清单的课表行（取消勾选营时整组移除）。</summary>
    private readonly Dictionary<string, List<EpisodeRow>> _queueByCamp = new();
    /// <summary>series_id / 营名 → 该组的分组标题行。</summary>
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

    /// <summary>
    /// 配置的下载目录不可用时的错误（空 = 正常）。
    /// ★ 2026-11-20：以前目录不可用会静默回落到系统「下载」，文件下到用户
    ///   完全不知道的地方还报「成功」（实测 Z: 盘拔掉后的表现）。
    ///   现在一律如实报出来，绝不再偷偷改存。
    /// </summary>
    private string _outDirError = "";

    public MainWindow()
    {
        InitializeComponent();

        // ★ 目录不可用时**显示用户配置的原值**，别把回退后的路径填进文本框骗人
        if (_svc.Config.TryResolveOutDir(out string outDir, out string outDirErr))
            EdOut.Text = outDir;
        else
        {
            EdOut.Text = _svc.Config.OutDir;
            _outDirError = outDirErr;
        }
        // ★ 状态栏右下角显示版本号（取程序集版本，和 exe 名里的一致）
        LblVersion.Text = "v" + AboutWindow.AppVersion;
        Title = $"红果视频下载器  v{AboutWindow.AppVersion}";

        // ★ 画质下拉框（红果的取值；切到兴趣岛时换成 max/1080/720/480）
        _qualityInit = true;
        _hgQuality = string.IsNullOrEmpty(_svc.Config.Quality) ? "1080p" : _svc.Config.Quality;
        ApplyQualityOptions(false);
        _qualityInit = false;

        LstShelf.ItemsSource = FilteredShelf;
        LstEpisodes.ItemsSource = EpisodeView;
        LstCamps.ItemsSource = FilteredCamps;

        // ★ 兴趣岛队列 → 共用下载清单。进度回调来自后台线程，统一切回 UI 线程。
        WireXqd();
        _xqdCookie = XqdCreds.Load();
        if (_xqdCookie.Length > 0) _xqd.Configure(_xqdCookie);

        // ★ 初始化时 Checked 会早于控件创建触发（被 guard 挡掉），这里补一次源相关控件的显隐
        Src_Checked(TabHongguo, new RoutedEventArgs());

        // ★ 标题列吃掉剩余宽度（列定义不支持 "*"，只能自己算）
        SizeChanged += (_, _) => UpdateEpisodeColumns();
        Opened += async (_, _) =>
        {
            UpdateEpisodeColumns();
            await StartupAsync();
        };
    }

    /// <summary>
    /// 内容源切换：红果视频 ↔ 兴趣岛课程。
    /// ★ 只切**左栏**与源专属控件；右栏下载清单、日志、底栏两源共用。
    /// ★ 用 sender 判断，不要读 TabXqd.IsChecked —— 单选组「取消另一项」发生在本事件之后。
    /// </summary>
    private void Src_Checked(object sender, RoutedEventArgs e)
    {
        // InitializeComponent 期间 Checked 会先于其它控件赋值触发
        if (LstShelf == null || LstCamps == null || CmbQuality == null) return;

        bool xqd = ReferenceEquals(sender, TabXqd);
        _xqdSource = xqd;

        LstShelf.IsVisible = !xqd;
        LstCamps.IsVisible = xqd;
        EdSearch.IsVisible = !xqd;
        EdCampSearch.IsVisible = xqd;
        BtnRefresh.IsVisible = !xqd;
        BtnRefreshCamps.IsVisible = xqd;
        ChkAllCamps.IsVisible = xqd;
        BtnRescanCamps.IsVisible = xqd;
        BtnManualToggle.IsVisible = xqd;
        BtnCache.IsVisible = !xqd;      // 提取 App 缓存只对红果有意义
        UpdateQueueSummary();
        LblHint.IsVisible = !xqd;

        _qualityInit = true;
        ApplyQualityOptions(xqd);
        _qualityInit = false;

        SetAuthLabel();
        LblStatus.Text = "就绪";
        LblRight.Text = "";

        if (xqd)
        {
            UpdateQueueSummary();
            _ = EnsureCampsAsync();
        }
        else
        {
            UpdateQueueSummary();
            if (!_svc.LoggedIn)
                ShowBanner("红果：尚未获取授权。点右上角「获取授权」从 MuMu 里的红果 App 读取登录态。",
                           BannerKind.Info);
            else HideBanner();
        }
    }

    /// <summary>按内容源切换画质下拉框的取值（红果按分辨率后缀，兴趣岛按短边）。</summary>
    private void ApplyQualityOptions(bool xqd)
    {
        if (xqd)
        {
            CmbQuality.ItemsSource = new[]
            {
                new QualityOption("最高画质（自动）", "max"),
                new QualityOption("1080P", "1080"),
                new QualityOption("720P", "720"),
                new QualityOption("480P", "480"),
            };
            SelectQuality(_xqdQuality);
            _xqd.Options.Quality = _xqdQuality;
        }
        else
        {
            CmbQuality.ItemsSource = new[]
            {
                new QualityOption("最高画质 (1080p)", "1080p"),
                new QualityOption("高清 (720p)", "720p"),
                new QualityOption("省流量 (360p)", "360p"),
            };
            SelectQuality(_hgQuality);
        }
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

        // 兴趣岛营列表：右侧只有一个「正式营 · N 节」的小胶囊
        double campTitle = Math.Max(60, avail - 32 - 92);
        foreach (var r in CampRows) r.TitleWidth = campTitle;
    }

    /// <summary>右栏分集表：标题列吃掉剩余宽度（列定义不支持 "*"，只能自己算）。</summary>
    private void UpdateEpisodeTableColumns()
    {
        double avail = LstEpisodes.Bounds.Width;
        if (avail <= 0) return;
        const double others = 58 + 46 + 54 + 126 + 72 + 58 + 122;
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

        // ★ 2026-11-21 用户要求：启动**不再自动取授权**，只读本机缓存的状态。
        //   要取授权必须点右上角「获取授权」，而且只取当前内容源的那一份。
        SetAuthLabel();

        // ★ 先恢复上次的红果清单（不依赖网络与登录态），再按需刷新收藏夹
        RestoreHongguoQueueRows();

        if (!_svc.LoggedIn)
        {
            Diag("未登录（等待用户点获取授权）");
            Log("尚未获取红果授权 —— 点右上角「获取授权」才会去 MuMu 读取（启动不自动取）");
            ShowBanner("尚未获取授权。点右上角「获取授权」按需读取当前内容源的登录态。\n"
                     + "★ 启动时不会自动启动模拟器，也不会读 MuMu。", BannerKind.Info);
            return;
        }
        Log("已读取本机保存的红果授权（未启动模拟器）");

        // ★ 下载目录不可用 → 启动就说清楚（别等用户下完 30 集才发现文件不见了）
        if (!string.IsNullOrEmpty(_outDirError))
        {
            Diag(_outDirError);
            ShowBanner(_outDirError
                     + "\n★ 下载会直接报错，不会偷偷改存到别处。"
                     + "点右上角改下载目录，或插回对应磁盘。", BannerKind.Err);
            Log("！" + _outDirError);
        }

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
        // ★ 不再整组清空清单：刷新是「同步收藏夹」，不是「丢弃用户的选择」。
        //   读完以后按还存在的剧剔除（见 PruneQueueToShelf）。

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
            PruneQueueToShelf();
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
        // ★ 两源共用一张表：组数 = 红果的剧 + 兴趣岛的营（+「手动链接」组）
        int groups = _queueBySeries.Count + _queueByCamp.Count;
        int total = EpisodeRows.Count(r => r.IsEpisode);
        int done = EpisodeRows.Count(r => r.IsEpisode && r.State is "已完成" or "已存在");
        int failed = EpisodeRows.Count(r => r.IsEpisode && r.State == "失败");

        LblEpHint.Text = groups == 0
            ? "在左边勾选剧目 / 营，条目会自动加入这里（可累计）"
            : $"已选 {groups} 组 · 共 {total} 条 · 已完成 {done} · 失败 {failed}";
        LblRight.Text = groups == 0 ? "" : $"已完成 {done} / {total}";
    }

    private void ShelfRow_PropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShelfRow.IsChecked)) UpdateQueueSummary();
    }

    private void SetAuthLabel()
    {
        if (_xqdSource)
            LblAuth.Text = _xqdCookie.Length > 0 ? "兴趣岛：已授权" : "兴趣岛：未授权";
        else
            LblAuth.Text = _svc.LoggedIn ? "授权：已就绪" : "授权：未登录";
    }

    // ---------------------------------------------------------- 下载清单

    /// <summary>
    /// 收藏夹刷新完 → 只剔除**已消失**的剧，其余选择保留。
    /// ★ 以前是 ClearQueue() 整组清空（连用户勾好的 20 集一起丢），
    ///   合并后兴趣岛那半却会保留 —— 同一张表两种命运。现在两边一致：同步而非清空。
    /// </summary>
    private void PruneQueueToShelf()
    {
        if (ShelfRows.Count == 0) return;      // 没读到收藏夹就别删东西

        var alive = ShelfRows.Select(s => s.SeriesId).ToHashSet(StringComparer.Ordinal);
        foreach (string sid in _queueBySeries.Keys.Where(k => !alive.Contains(k)).ToList())
            RemoveSeriesGroup(sid);

        foreach (var s in ShelfRows)
        {
            bool inQueue = _queueBySeries.ContainsKey(s.SeriesId);
            if (s.IsChecked != inQueue) s.IsChecked = inQueue;

            // 剧名可能被服务端改过 → 同步分组标题（就地替换，不跳滚动）
            if (inQueue && _queueHeaders.TryGetValue(s.SeriesId, out var h)
                && h.SeriesTitle != s.Display)
                RefreshGroupHeader(s.SeriesId, s.Display, _queueBySeries[s.SeriesId].Count);
        }

        PersistHongguoQueue();
    }

    /// <summary>把红果清单落盘（测试模式不碰用户文件）。</summary>
    private void PersistHongguoQueue()
    {
        if (_testMode) return;
        var groups = new List<QueueGroup>();
        foreach (var kv in _queueBySeries)
        {
            var rows = kv.Value.Where(r => r.IsEpisode).ToList();
            if (rows.Count == 0) continue;
            var g = new QueueGroup
            {
                SeriesId = kv.Key,
                SeriesTitle = rows[0].SeriesTitle,
                ShortDrama = _rowMap.TryGetValue(kv.Key, out var s) && s.IsShortDrama,
            };
            foreach (var r in rows)
                g.Episodes.Add(new QueueEpisode
                {
                    Vid = r.Vid,
                    Index = r.Index,
                    Title = r.Title,
                    Duration = r.Duration,
                    VideoIdType = r.VideoIdType,
                });
            groups.Add(g);
        }
        _hgQueueStore.Save(groups);
    }

    /// <summary>把上次的红果清单重建到右栏（不联网、不依赖登录态）。</summary>
    private void RestoreHongguoQueueRows(bool force = false)
    {
        if (_hgRestored) return;
        _hgRestored = true;
        if (_testMode && !force) return;

        var groups = _hgQueueStore.Load();
        if (groups.Count == 0) return;

        int total = 0;
        foreach (var g in groups)
        {
            if (_queueBySeries.ContainsKey(g.SeriesId)) continue;
            string title = string.IsNullOrEmpty(g.SeriesTitle) ? g.SeriesId : g.SeriesTitle;

            var rows = new List<EpisodeRow>();
            foreach (var e in g.Episodes)
            {
                var ep = new Core.Api.Episode
                {
                    Vid = e.Vid,
                    Index = e.Index,
                    Title = e.Title,
                    Duration = e.Duration,
                    SeriesId = g.SeriesId,
                    SeriesTitle = title,
                    VideoIdType = e.VideoIdType,
                };
                _epCache[ep.Vid] = ep;
                var r = new EpisodeRow(ep) { TitleWidth = _titleWidth };
                r.PropertyChanged += EpisodeRow_PropertyChanged;
                rows.Add(r);
            }
            if (rows.Count == 0) continue;

            var header = EpisodeRow.Header(g.SeriesId, title, rows.Count);
            header.TitleWidth = _titleWidth;
            _queueHeaders[g.SeriesId] = header;
            _queueBySeries[g.SeriesId] = rows;

            EpisodeRows.Add(header);
            EpisodeView.Add(header);
            foreach (var r in rows) { EpisodeRows.Add(r); EpisodeView.Add(r); }
            total += rows.Count;
        }

        Log($"红果：恢复上次清单 {total} 集（{groups.Count} 部）");
        ApplyEpView();
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
            PersistHongguoQueue();
            ApplyEpView();
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
        RemoveSeriesGroup(row.SeriesId);
    }

    /// <summary>按 series_id 移除整组（不依赖左栏行，刷新剔除时也能用）。</summary>
    private void RemoveSeriesGroup(string sid)
    {
        if (!_queueBySeries.TryGetValue(sid, out var rows)) return;
        string title = rows.Count > 0 ? rows[0].SeriesTitle : sid;

        foreach (var r in rows)
        {
            EpisodeRows.Remove(r);
            EpisodeView.Remove(r);
            _epCache.Remove(r.Vid);
        }
        RemoveHeader(sid);
        _queueBySeries.Remove(sid);
        Log($"「{title}」已从下载清单移出（当前 {_queueBySeries.Count} 部）");
        PersistHongguoQueue();
        UpdateQueueSummary();
    }

    // ---------------------------------------------------------- 兴趣岛：课表发现

    /// <summary>把队列事件接到界面（换测试实例时也要重接）。</summary>
    private void WireXqd()
    {
        _xqd.JobChanged += job => RunOnUi(() => SyncXqdRow(job));
        _xqd.JobProgress += (job, _) => RunOnUi(() => SyncXqdRow(job));
        _xqd.Log += msg => RunOnUi(() => Log(msg));
    }

    /// <summary>
    /// 给 --uitest / 截图用：换到临时清单目录，并禁掉自动发现（不联网）。
    /// </summary>
    internal void UseXqdStoreForTest(string root)
    {
        _testMode = true;
        _xqdRestored = false;
        _xqd = new XqdQueue(new XqdOptions(), new XqdStore(root));
        WireXqd();
        _xqdCookie = "";
    }

    /// <summary>
    /// --uitest：在 Show 之前就进测试模式 —— 不恢复、不写入用户真实的清单与历史。
    /// ★ 必须在 StartupAsync（Opened 事件）之前调用，否则会先把用户清单恢复进来。
    /// </summary>
    internal void MarkUiTest()
    {
        _testMode = true;
        _hgRestored = true;
        _xqdRestored = true;
        _hgQueueStore = new QueueStore(
            Path.Combine(Path.GetTempPath(), "hgvd_queue_uitest", "queue.json"));
    }

    /// <summary>--uitest：清掉现有红果行后，从临时 queue.json 真走一次恢复。</summary>
    internal void ReloadHongguoQueueForTest()
    {
        foreach (string sid in _queueBySeries.Keys.ToList()) RemoveSeriesGroup(sid);
        _hgRestored = false;
        RestoreHongguoQueueRows(force: true);
    }

    /// <summary>给 --uitest 用：红果清单的存储换到临时目录，不碰用户文件。</summary>
    internal void UseHongguoQueueStoreForTest(string path)
    {
        _testMode = true;
        _hgRestored = false;
        _hgQueueStore = new QueueStore(path);
    }

    /// <summary>首次切到兴趣岛时拉营列表（没授权就只提示，不偷偷去读模拟器）。</summary>
    private async Task EnsureCampsAsync()
    {
        if (_testMode) return;
        RestoreXqdQueueRows();
        if (_campsLoaded || _campsBusy) return;
        if (_xqdCookie.Length == 0)
        {
            LblHint.Text = "未授权";
            LblStatus.Text = "兴趣岛：未授权";
            ShowBanner("兴趣岛：尚未获取授权。点右上角「获取授权」从 MuMu 里的兴趣岛 App 读取登录态。",
                       BannerKind.Info);
            return;
        }
        await LoadCampsAsync();
    }

    private async Task LoadCampsAsync()
    {
        if (_campsBusy) return;
        if (_xqdCookie.Length == 0)
        {
            ShowBanner("兴趣岛：先点右上角「获取授权」。", BannerKind.Info);
            return;
        }

        _campsBusy = true;
        LblStatus.Text = "正在读取兴趣岛课程…";
        LblHint.Text = "读取中…";
        try
        {
            _xqd.Configure(_xqdCookie);
            var camps = await _xqd.Platform!.ListAllCampsAsync(_cts.Token);
            CampRows.Clear();
            foreach (var c in camps)
                CampRows.Add(new XqdCampRow(c) { TitleWidth = _shelfTitleWidth });
            ApplyCampFilter();
            _campsLoaded = true;
            // 恢复出来的清单可能先于营列表到达，这里把勾选状态补上
            foreach (var r in CampRows) r.IsChecked = _queueByCamp.ContainsKey(r.Name);
            UpdateCampCheckState();

            int formal = CampRows.Count(r => r.Kind == "正式营");
            int trial = CampRows.Count(r => r.Kind == "体验营");
            int open = CampRows.Count - formal - trial;
            Log($"兴趣岛：{CampRows.Count} 个营（正式营 {formal} · 体验营 {trial} · 公开课 {open}）");
            LblHint.Text = $"共 {CampRows.Count} 个营";
            LblStatus.Text = "就绪";
            HideBanner();
        }
        catch (Exception e)
        {
            Log("读取兴趣岛课程失败：" + e.Message);
            ShowBanner("读取兴趣岛课程失败：" + e.Message
                     + "\n点右上角「获取授权」可重新读取登录态。", BannerKind.Err);
            LblStatus.Text = "就绪";
        }
        finally { _campsBusy = false; }
    }

    private bool _xqdRestored;

    /// <summary>
    /// 把上次遗留的兴趣岛任务重建到共用清单里（按营分组）。
    /// ★ 只在第一次进入兴趣岛时做；红果侧不受影响。
    /// </summary>
    private void RestoreXqdQueueRows()
    {
        if (_xqdRestored) return;
        _xqdRestored = true;

        _xqd.Load();
        var jobs = _xqd.Jobs;
        if (jobs.Count == 0) return;

        foreach (var group in jobs.GroupBy(j => j.Camp ?? ""))
        {
            string camp = group.Key;
            string key = "xqd:" + (camp.Length > 0 ? camp : "__links");
            string title = camp.Length > 0 ? camp : "手动链接";

            var rows = new List<EpisodeRow>();
            foreach (var j in group)
            {
                var r = EpisodeRow.FromXqd(j);
                r.TitleWidth = _titleWidth;
                r.PropertyChanged += EpisodeRow_PropertyChanged;
                r.SyncFromXqd();
                rows.Add(r);
            }

            var header = EpisodeRow.Header(key, title, rows.Count);
            header.TitleWidth = _titleWidth;
            _queueHeaders[key] = header;
            // ★ 手动链接统一用 "__links" 作 key（新增与恢复必须一致，
            //   否则删单条时找不到组、标题计数不更新）
            _queueByCamp[camp.Length > 0 ? camp : "__links"] = rows;

            EpisodeRows.Add(header);
            EpisodeView.Add(header);
            foreach (var r in rows)
            {
                EpisodeRows.Add(r);
                EpisodeView.Add(r);
            }
        }

        // 已加入清单的营：把左栏勾选框补上（程序化赋值不会触发 Click）
        foreach (var r in CampRows)
            r.IsChecked = _queueByCamp.ContainsKey(r.Name);

        Log($"兴趣岛：恢复上次清单 {jobs.Count} 节（{_queueByCamp.Count} 组）");
        ApplyEpView();
        UpdateQueueSummary();
    }

    private void ApplyCampFilter()    {
        string kw = (EdCampSearch.Text ?? "").Trim();
        FilteredCamps.Clear();
        foreach (var r in CampRows)
        {
            if (kw.Length == 0 ||
                (r.Display?.Contains(kw, StringComparison.CurrentCultureIgnoreCase) ?? false))
                FilteredCamps.Add(r);
        }
        UpdateCampCheckState();
    }

    private void EdCampSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyCampFilter();

    private async void CampChk_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.DataContext is not XqdCampRow row) return;
        if (cb.IsChecked == true) await AddCampToQueueAsync(row);
        else RemoveCampFromQueue(row);
        UpdateCampCheckState();
    }

    private async void LstCamps_DoubleTapped(object sender, TappedEventArgs e)
    {
        var row = (e.Source as Visual)?
            .GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?
            .DataContext as XqdCampRow;
        if (row == null) return;
        row.IsChecked = !row.IsChecked;
        if (row.IsChecked) await AddCampToQueueAsync(row);
        else RemoveCampFromQueue(row);
        UpdateCampCheckState();
    }

    /// <summary>
    /// 勾选一个营 → 拉课表 → 把这一营的课**追加**进共用下载清单（带分组标题行）。
    /// </summary>
    private async Task AddCampToQueueAsync(XqdCampRow row)
    {
        if (row is null) return;
        if (_queueByCamp.ContainsKey(row.Name)) return;
        if (_xqdCookie.Length == 0)
        {
            row.IsChecked = false;
            ShowBanner("兴趣岛：先点右上角「获取授权」，再勾选营。", BannerKind.Info);
            return;
        }

        LblStatus.Text = $"正在读取「{row.Name}」的课表…";
        try
        {
            _xqd.Configure(_xqdCookie);
            var lessons = await _xqd.Platform!.CampCatalogAsync(row.Camp, _cts.Token);

            // 等待期间用户可能已取消勾选
            if (!row.IsChecked || _queueByCamp.ContainsKey(row.Name)) return;

            if (lessons.Count == 0)
            {
                row.IsChecked = false;
                Log($"「{row.Name}」课表为空（可能没有排课）");
                LblStatus.Text = "就绪";
                return;
            }

            int added = _xqd.AddLessons(row.Camp, lessons);
            var jobs = _xqd.JobsOfCamp(row.Name);
            var rows = new List<EpisodeRow>();
            foreach (var j in jobs)
            {
                var r = EpisodeRow.FromXqd(j);
                r.TitleWidth = _titleWidth;
                r.PropertyChanged += EpisodeRow_PropertyChanged;
                rows.Add(r);
            }

            string key = "xqd:" + row.Name;
            var header = EpisodeRow.Header(key, row.Name, rows.Count);
            _queueHeaders[key] = header;
            _queueByCamp[row.Name] = rows;

            EpisodeRows.Add(header);
            EpisodeView.Add(header);
            foreach (var r in rows)
            {
                EpisodeRows.Add(r);
                EpisodeView.Add(r);
            }

            Marquee.Text = rows.Count > 0 ? rows[0].Title : row.Name;
            Log($"「{row.Name}」{rows.Count} 节已加入下载清单（新加 {added} 节）");
            ApplyEpView();
            UpdateQueueSummary();
            LblStatus.Text = "就绪";
        }
        catch (Exception e)
        {
            row.IsChecked = false;
            Log($"取「{row.Name}」课表失败：{e.Message}");
            ShowBanner($"取「{row.Name}」课表失败：{e.Message}", BannerKind.Err);
            LblStatus.Text = "就绪";
        }
    }

    /// <summary>取消勾选营 → 整组移出清单（顺手把队列里的任务也删掉）。</summary>
    private void RemoveCampFromQueue(XqdCampRow row)
    {
        if (row is null) return;
        string key = "xqd:" + row.Name;
        if (_queueByCamp.TryGetValue(row.Name, out var rows))
        {
            foreach (var r in rows)
            {
                EpisodeRows.Remove(r);
                EpisodeView.Remove(r);
                if (r.Xqd != null) _xqd.Remove(r.Xqd);
            }
            _queueByCamp.Remove(row.Name);
        }
        if (_queueHeaders.TryGetValue(key, out var header))
        {
            EpisodeRows.Remove(header);
            EpisodeView.Remove(header);
            _queueHeaders.Remove(key);
        }
        Log($"「{row.Name}」已从下载清单移出");
        UpdateQueueSummary();
    }

    private void UpdateCampCheckState()
    {
        int n = CampRows.Count, k = CampRows.Count(r => r.IsChecked);
        ChkAllCamps.IsChecked = n == 0 || k == 0 ? false : (k >= n ? true : null);
    }

    // ---------------------------------------------------------- 单行移出
    //
    // ★ 为什么必须有：兴趣岛原来靠右键「移出清单」删单条；合并进共用表后右键没了，
    //   而手动链接任务没有营/剧的勾选框 —— 粘错一条就再也删不掉（还会写进 jobs.json）。
    //   现在右键与 Delete 都走同一个入口。

    private void MenuRemoveRow_Click(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenu(sender);
        if (row != null) RemoveRow(row);
    }

    private EpisodeRow _menuRow;

    /// <summary>右键菜单打开时记住来源行（ContextMenu 不在可视树里，DataContext 未必可靠）。</summary>
    private void EpMenu_Opening(object sender, CancelEventArgs e)
    {
        var cm = sender as ContextMenu;
        _menuRow = cm?.DataContext as EpisodeRow;
        if (_menuRow == null && cm?.PlacementTarget is Control target)
            _menuRow = target.DataContext as EpisodeRow;
    }

    /// <summary>右键菜单的来源行。Tag / DataContext / 打开时记录的行，三个都试。</summary>
    private EpisodeRow RowFromMenu(object sender)
        => (sender as MenuItem)?.Tag as EpisodeRow
           ?? (sender as MenuItem)?.DataContext as EpisodeRow
           ?? _menuRow;

    private async void MenuDownloadRow_Click(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenu(sender);
        if (row != null) await DownloadOneAsync(row);
    }

    private void MenuRetryRow_Click(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenu(sender);
        if (row == null) return;

        if (row.IsXqd && row.Xqd != null)
        {
            _xqd.Retry(row.Xqd);          // 清分片 + 重置 + 落盘
            row.SyncFromXqd();
            Log($"「{row.Xqd.Title}」已清缓存并重置为待下载");
            UpdateQueueSummary();
            return;
        }

        if (!_epCache.TryGetValue(row.Vid, out var ep))
        {
            Log("！这一条还没拿到播放信息，无法清缓存");
            return;
        }
        try
        {
            string part = _svc.BuildOutPath(ep) + ".part";
            if (File.Exists(part)) File.Delete(part);
        }
        catch (Exception ex)
        {
            Log("！清缓存失败：" + ex.Message);
            return;
        }
        row.State = "待下载";
        row.Down = 0;
        row.Total = 0;
        Log($"「{row.Title}」已清临时文件；完整成品仍会按「已存在」跳过，要重下请先删成品");
        UpdateQueueSummary();
    }

    private async void MenuCopyUrlRow_Click(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenu(sender);
        if (row == null) return;
        string url = row.Xqd?.Url ?? "";
        if (url.Length == 0)
        {
            Log("！这一条没有现成链接（课表任务的地址在下载时才现场解析）");
            return;
        }
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip != null) await clip.SetTextAsync(url);
        Log("链接已复制");
    }

    private async void MenuCopyTitleRow_Click(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenu(sender);
        if (row == null) return;
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip != null) await clip.SetTextAsync(row.Title ?? "");
        Log("标题已复制");
    }

    private void MenuOpenFolderRow_Click(object sender, RoutedEventArgs e)
    {
        var row = RowFromMenu(sender);
        if (row == null) return;
        try
        {
            string path = row.IsXqd
                ? row.Xqd?.OutPath ?? ""
                : (_epCache.TryGetValue(row.Vid, out var ep) ? _svc.BuildOutPath(ep) : "");
            if (path.Length == 0)
            {
                Log("！这一条还没有成品路径");
                return;
            }
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
                { UseShellExecute = true });
            else
            {
                string dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) return;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"")
                { UseShellExecute = true });
            }
        }
        catch (Exception ex) { Log("！打开文件夹失败：" + ex.Message); }
    }

    private void LstEpisodes_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        var rows = LstEpisodes.SelectedItems?.Cast<EpisodeRow>()
            .Where(r => r.IsEpisode).ToList() ?? new List<EpisodeRow>();
        if (rows.Count == 0) return;
        foreach (var r in rows) RemoveRow(r);
        LstEpisodes.Selection.Clear();
        e.Handled = true;
    }

    private static string CampKey(string camp) => "xqd:" + (camp.Length > 0 ? camp : "__links");
    private static string CampTitle(string camp) => camp.Length > 0 ? camp : "手动链接";

    /// <summary>把某一行从共用清单里移出；组空了就把标题行也删掉。</summary>
    internal bool RemoveRow(EpisodeRow row)
    {
        if (row == null || row.IsGroupHeader) return false;

        if (row.IsXqd)
        {
            string camp = row.Xqd?.Camp ?? "";
            string campKey = camp.Length > 0 ? camp : "__links";
            if (row.Xqd != null) _xqd.Remove(row.Xqd);      // 队列里删掉并落盘

            if (_queueByCamp.TryGetValue(campKey, out var rows))
            {
                rows.Remove(row);
                if (rows.Count == 0)
                {
                    _queueByCamp.Remove(campKey);
                    RemoveHeader(CampKey(camp));
                    var campRow = CampRows.FirstOrDefault(c => c.Name == camp);
                    if (campRow != null) campRow.IsChecked = false;
                    UpdateCampCheckState();
                }
                else RefreshGroupHeader(CampKey(camp), CampTitle(camp), rows.Count);
            }
        }
        else
        {
            string sid = row.SeriesId;
            if (_queueBySeries.TryGetValue(sid, out var rows))
            {
                rows.Remove(row);
                _epCache.Remove(row.Vid);
                if (rows.Count == 0)
                {
                    _queueBySeries.Remove(sid);
                    RemoveHeader(sid);
                    if (_rowMap.TryGetValue(sid, out var shelf)) shelf.IsChecked = false;
                }
                else RefreshGroupHeader(sid, row.SeriesTitle, rows.Count);
            }
        }

        EpisodeRows.Remove(row);
        EpisodeView.Remove(row);
        PersistHongguoQueue();
        Log($"已移出清单：{row.Title}（磁盘上的成品不动）");
        UpdateQueueSummary();
        return true;
    }

    private void RemoveHeader(string key)
    {
        if (!_queueHeaders.TryGetValue(key, out var header)) return;
        EpisodeRows.Remove(header);
        EpisodeView.Remove(header);
        _queueHeaders.Remove(key);
    }

    private void ChkAllCamps_Click(object sender, RoutedEventArgs e)
    {
        bool want = ChkAllCamps.IsChecked == true;
        foreach (var r in FilteredCamps.Where(x => x.IsChecked != want).ToList())
        {
            r.IsChecked = want;
            if (want) _ = AddCampToQueueAsync(r);
            else RemoveCampFromQueue(r);
        }
        UpdateCampCheckState();
    }

    private async void BtnRefreshCamps_Click(object sender, RoutedEventArgs e)
    {
        if (_xqdCookie.Length == 0)
        {
            ShowBanner("兴趣岛：先点右上角「获取授权」。", BannerKind.Info);
            return;
        }
        _campsLoaded = false;
        // 刷新会重拉营列表；已加入清单的课不受影响
        await LoadCampsAsync();
    }

    /// <summary>按磁盘上的成品重判兴趣岛任务状态（红果侧由下载时的 SkipExisting 判定）。</summary>
    private void BtnRescanCamps_Click(object sender, RoutedEventArgs e)
    {
        if (_xqdCookie.Length == 0)
        {
            ShowBanner("兴趣岛：先点右上角「获取授权」。", BannerKind.Info);
            return;
        }
        int changed = _xqd.Rescan();
        foreach (var r in EpisodeRows.Where(r => r.IsXqd).ToList()) r.SyncFromXqd();
        Log($"重新扫描：{changed} 节状态有变化");
        ApplyEpView();
        UpdateQueueSummary();
    }

    private void BtnManualToggle_Click(object sender, RoutedEventArgs e)
    {
        ManualPanel.IsVisible = !ManualPanel.IsVisible;
        if (ManualPanel.IsVisible) EdLinks.Focus();
    }

    /// <summary>手动粘贴链接（自动发现之外的兜底）→ 加入同一张下载清单。</summary>
    private void BtnAddLinks_Click(object sender, RoutedEventArgs e)
    {
        var items = LinkParser.Parse(EdLinks.Text ?? "");
        LblLinkCount.Text = $"{items.Count} 条";
        if (items.Count == 0)
        {
            ShowBanner("没解析到链接。一行一条：课名 | https://…/xxx.m3u8", BannerKind.Info);
            return;
        }

        AddXqdLinkItems(items, "手动链接");
        EdLinks.Text = "";
        LblLinkCount.Text = "0 条";
    }

    private void EdLinks_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LblLinkCount == null) return;
        int n = LinkParser.Parse(EdLinks.Text ?? "").Count;
        LblLinkCount.Text = $"{n} 条";
    }

    private void BtnClearLinks_Click(object sender, RoutedEventArgs e)
    {
        EdLinks.Text = "";
        LblLinkCount.Text = "0 条";
    }

    private async void BtnImportLinks_Click(object sender, RoutedEventArgs e)
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
                Log($"读入 {f.Name}：{list.Count} 条"
                    + (errors.Count > 0 ? $"，{errors.Count} 行无法识别" : ""));
            }
            catch (Exception ex)
            {
                Log($"！读取 {f.Name} 失败：{ex.Message}");
            }
        }

        var merged = LinkParser.Merge(sources);
        if (merged.Count == 0)
        {
            ShowBanner("这些文件里没有可用的链接。", BannerKind.Info);
            return;
        }
        AddXqdLinkItems(merged, "导入清单");
    }

    /// <summary>把解析好的链接加入兴趣岛队列，并同步到共用清单（去重由 XqdQueue.Add 负责）。</summary>
    private void AddXqdLinkItems(List<LinkItem> items, string action)
    {
        var before = _xqd.Jobs.Select(j => j.Id).ToHashSet(StringComparer.Ordinal);
        int added = _xqd.Add(items);
        var newJobs = _xqd.Jobs.Where(j => !before.Contains(j.Id)).ToList();

        const string key = "xqd:__links";
        if (newJobs.Count > 0)
        {
            _queueByCamp.TryGetValue("__links", out var rows);
            rows ??= new List<EpisodeRow>();
            if (!_queueHeaders.ContainsKey(key))
            {
                var header = EpisodeRow.Header(key, "手动链接", 0);
                _queueHeaders[key] = header;
                EpisodeRows.Add(header);
                EpisodeView.Add(header);
            }
            foreach (var j in newJobs)
            {
                var r = EpisodeRow.FromXqd(j);
                r.TitleWidth = _titleWidth;
                r.PropertyChanged += EpisodeRow_PropertyChanged;
                rows.Add(r);
                EpisodeRows.Add(r);
                EpisodeView.Add(r);
            }
            _queueByCamp["__links"] = rows;
            RefreshGroupHeader(key, "手动链接", rows.Count);
        }

        Log($"{action}：加入 {added} 节（解析 {items.Count} 条）");
        ApplyEpView();
        UpdateQueueSummary();
    }

    /// <summary>用新的节数替换分组标题行（就地替换，滚动位置不跳）。</summary>
    private void RefreshGroupHeader(string key, string title, int count)
    {
        if (!_queueHeaders.TryGetValue(key, out var old)) return;
        var fresh = EpisodeRow.Header(key, title, count);
        fresh.TitleWidth = _titleWidth;
        _queueHeaders[key] = fresh;

        int idx = EpisodeRows.IndexOf(old);
        if (idx >= 0) EpisodeRows[idx] = fresh;
        int vi = EpisodeView.IndexOf(old);
        if (vi >= 0) EpisodeView[vi] = fresh;
    }

    /// <summary>队列状态变了 → 同步到共用清单里的那一行。</summary>
    private void SyncXqdRow(XqdJob job)
    {
        var row = EpisodeRows.FirstOrDefault(r => ReferenceEquals(r.Xqd, job));
        if (row == null) return;
        string prev = row.State;
        row.SyncFromXqd();
        if (job.State == XqdState.Done && prev != XqdState.Done)
            RecordHistory("兴趣岛", job.Camp, job.Title, job.CampId, job.TopicId,
                          job.OutPath, job.Size, job.Duration, "已完成");
        if (XqdState.IsRunning(job.State) || job.State == XqdState.Paused)
            Marquee.Text = job.Display;
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
        if (_epViewTag == EpViewFilter.All) return;
        ApplyEpView();
    }

    /// <summary>按当前过滤条件刷新分集视图（保留选中行；分组标题行跟随本组是否有可见行）。</summary>
    private void ApplyEpView()
    {
        // 先记下选中的行（★ 按对象认，不按 Vid —— 兴趣岛行没有 Vid）
        var sel = LstEpisodes.SelectedItems?.Cast<EpisodeRow>()
            .Where(r => r.IsEpisode).ToHashSet() ?? new HashSet<EpisodeRow>();

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
            if (EpisodeView[i].IsEpisode && sel.Contains(EpisodeView[i]))
                LstEpisodes.Selection.Select(i);
    }

    private static bool MatchEpView(EpisodeRow r, string tag)
        => EpViewFilter.Match(r.State, tag);

    // ---------------------------------------------------------- 下载

    private void BtnHistory_Click(object sender, RoutedEventArgs e)
    {
        var win = new HistoryWindow(_history.Load());
        _ = win.ShowDialog(this);
    }

    /// <summary>记一条下载历史（--uitest 的假下载不写用户文件）。</summary>
    private void RecordHistory(string source, string title, string episode,
                               string seriesId, string vid, string path,
                               long size, double duration, string state)
    {
        if (_testMode || string.IsNullOrEmpty(path)) return;
        try
        {
            _history.Add(new HistoryEntry
            {
                Source = source,
                Title = title ?? "",
                Episode = episode ?? "",
                SeriesId = seriesId ?? "",
                Vid = vid ?? "",
                Path = path,
                Size = size,
                Duration = duration,
                State = state,
            });
        }
        catch { }
    }

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
        // ★ 一个按钮三态：空闲=下载，运行中=暂停，暂停中=继续。
        if (_downloading) { TogglePause(); return; }

        var queued = EpisodeRows.Where(r => r.IsEpisode).ToList();
        if (queued.Count == 0)
        {
            ShowBanner("先在左边勾选剧目/营，它们的条目会加到右边清单里。", BannerKind.Info);
            return;
        }

        // ★ 有选中行 → 只下选中的；没选中 → 下清单里所有**待下载 / 失败**的
        var sel = LstEpisodes.SelectedItems?.Cast<EpisodeRow>()
            .Where(r => r.IsEpisode).ToList() ?? new List<EpisodeRow>();
        var targets = sel.Count > 0
            ? sel
            : queued.Where(r => r.State is "待下载" or "失败").ToList();
        if (targets.Count == 0)
        {
            ShowBanner("清单里没有待下载的条目（都已完成）。", BannerKind.Info);
            return;
        }

        // ★ 短剧默认跳过（bytevc 加密，解不出画面）—— 只针对红果的行
        int dramas = targets.Count(t => !t.IsXqd && t.State == "待下载" && IsDramaRow(t));
        if (dramas > 0)
            ShowBanner($"已跳过 {dramas} 部短剧 —— 它的编码是字节私有 bytevc，下载后无法正常播放。",
                       BannerKind.Info);
        targets = targets.Where(t => t.IsXqd || !IsDramaRow(t)).ToList();
        if (targets.Count == 0)
        {
            ShowBanner("没有可下载的分集（短剧会自动跳过）。", BannerKind.Info);
            return;
        }

        await RunBatchAsync(targets);
    }

    /// <summary>跑一批（下载按钮与自检共用）。</summary>
    private async Task RunBatchAsync(List<EpisodeRow> targets)
    {
        // ★ 下载前再强制应用一次目录 —— 即使用户没点别处、也没回车
        ApplyOutDir();
        // ★ 上次被取消过的话令牌已失效，换一个新的，否则这一批会立刻被判为取消
        if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();

        _stopRequested = false;
        _paused = false;
        _hgGate.Resume();
        _xqd.SetPaused(false);
        SetRunningUi(true);
        var res = await DownloadRowsAsync(targets);
        SetRunningUi(false);
        LblStatus.Text = "就绪";
        UpdateQueueSummary();
        if (res.Aborted)
            ShowBanner("播放接口连续多次返回空 body，已停止本批。"
                     + "\n★ 这不是限流，等再久也不会自己恢复；先看日志里的判据，"
                     + "急用可改用「提取App缓存」。", BannerKind.Err);
        Log($"—— 完成 {res.Ok}，失败 {res.Fail}");
    }

    /// <summary>统一的运行态 UI：下载 ↔ 暂停 ↔ 继续（两源同一个按钮）。</summary>
    private void SetRunningUi(bool running)
    {
        _downloading = running;
        BtnRun.IsEnabled = true;          // 运行中也要能点，否则没法暂停
        BtnRun.Content = running ? (_paused ? "继续" : "暂停") : "下载";
        if (running) return;
        _paused = false;
        _hgGate.Resume();
        _xqd.SetPaused(false);
    }

    /// <summary>一个按钮管两源：红果走 PauseGate（保留连接），兴趣岛走 XqdQueue 的暂停。</summary>
    private void TogglePause()
    {
        _paused = !_paused;
        if (_paused) _hgGate.Pause(); else _hgGate.Resume();
        _xqd.SetPaused(_paused);
        BtnRun.Content = _paused ? "继续" : "暂停";
        LblStatus.Text = _paused ? "已暂停（进度与分片保留）" : "下载中";
        Log(_paused ? "已暂停（两源同时生效；进度/分片保留）" : "已继续");
    }

    /// <summary>
    /// 双击清单里的某一集 → **只下这一集**（不看勾选、不受筛选影响）。
    ///
    /// ★ 与点「下载」的区别：点「下载」是批量语义（有选中行下选中的，没选中下所有待下载），
    ///   双击是「明确指定这一集」。所以双击**不做短剧过滤** ——
    ///   用户已经点到具体某一集了，再替他把短剧剔掉只会让人困惑。
    /// </summary>
    private async void LstEpisodes_DoubleTapped(object sender, TappedEventArgs e)
    {
        var row = EpisodeRowFromEvent(e);
        if (row != null) await DownloadOneAsync(row);
    }

    /// <summary>单集下载的实体（双击走这里；抽出来也方便 --uitest 直接调）。</summary>
    internal async Task DownloadOneAsync(EpisodeRow row)
    {
        if (row == null) return;
        if (_downloading)
        {
            Log("！正在下载中，双击已忽略（要换一集请先点「停止」）");
            return;
        }
        if (row.IsGroupHeader)
        {
            Log("！这是分组标题行，双击无效 —— 请双击具体分集");
            return;
        }

        // ★ 兴趣岛的行：不需要 _epCache，直接交给队列
        if (row.IsXqd)
        {
            await DownloadXqdSingleAsync(row);
            return;
        }

        if (!_epCache.ContainsKey(row.Vid))
        {
            Log($"！「{row.Title}」还没拿到播放信息，双击无效");
            return;
        }

        ApplyOutDir();      // 与点「下载」一致：文本框里改的目录立即生效
        if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();
        _stopRequested = false;
        _paused = false;
        _hgGate.Resume();
        _xqd.SetPaused(false);
        SetRunningUi(true);
        LblStatus.Text = $"单独下载第 {row.Index} 集…";
        Log($"▶ 双击单集：第 {row.Index} 集");
        try
        {
            var (ok, _, _) = await DownloadRowsAsync(new List<EpisodeRow> { row });
            LblStatus.Text = ok > 0 ? $"第 {row.Index} 集完成" : $"第 {row.Index} 集失败";
        }
        catch (Exception ex)
        {
            Log("✗ 单集下载异常：" + ex.Message);
        }
        finally
        {
            SetRunningUi(false);
            UpdateQueueSummary();
        }
    }

    /// <summary>双击兴趣岛某一行 → 只下这一节。</summary>
    private async Task DownloadXqdSingleAsync(EpisodeRow row)
    {
        ApplyOutDir();
        if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();
        _stopRequested = false;
        _paused = false;
        _hgGate.Resume();
        _xqd.SetPaused(false);
        SetRunningUi(true);
        LblStatus.Text = $"单独下载：{row.Title}";
        Log($"▶ 双击单条：{row.Title}");
        try
        {
            var (ok, _) = await DownloadXqdRowsAsync(new List<EpisodeRow> { row });
            LblStatus.Text = ok > 0 ? "已完成" : "下载失败（详见日志）";
        }
        catch (Exception ex)
        {
            Log("✗ 单条下载异常：" + ex.Message);
        }
        finally
        {
            SetRunningUi(false);
            UpdateQueueSummary();
        }
    }

    /// <summary>从双击事件回溯到清单行（点在行内 TextBlock 上也能找到）。</summary>
    private static EpisodeRow EpisodeRowFromEvent(TappedEventArgs e)
    {
        if (e.Source is ListBoxItem direct) return direct.DataContext as EpisodeRow;
        if (e.Source is Visual v)
            foreach (var p in v.GetVisualAncestors())
                if (p is ListBoxItem li) return li.DataContext as EpisodeRow;
        return null;
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
                ShowBanner("播放接口连续多次返回空 body，已停止本批。"
                         + "\n★ 这不是限流，等再久也不会自己恢复；先看日志里的判据，"
                         + "急用可改用「提取App缓存」出片。", BannerKind.Err);
                break;
            }
        }
        return new BatchResult(ok, fail, doneSeries, false);
    }

    /// <summary>下载中的错误明细（批量测试要写进报告）。</summary>
    private readonly List<string> _downloadErrors = new();

    /// <summary>
    /// ★ 测试钩子：替换掉真正的下载（不打网络，用它验证批量逻辑本身）。
    /// </summary>
    internal Func<Core.Api.Episode, string, DownloadCallbacks, CancellationToken,
        Task<DownloadResult>>? DownloadHook;

    private async Task<(int Ok, int Fail, bool Aborted)> DownloadRowsAsync(
        List<EpisodeRow> targets)
    {
        int ok = 0, fail = 0, addrFail = 0;

        // ★ 两源共用一张清单：兴趣岛的行走自己的队列，红果的行照旧走签名链路。
        var xqdTargets = targets.Where(r => r.IsXqd && r.IsEpisode).ToList();
        targets = targets.Where(r => !r.IsXqd).ToList();

        // ★★ 开下之前先确认下载目录真的可写。
        //    否则每一集都会在 BuildOutPath 里抛一次同样的错、刷满日志，
        //    而且用户很难看出「全失败」的根因是路径。
        //    ★ 绝不再静默回落到系统「下载」——那会让用户以为下成功了却找不到文件。
        //    ★ 走 DownloadHook（--uitest 的模拟下载）时不检查：那条路径根本不落盘，
        //      检查了会让界面自检依赖「本机下载目录可用」这个无关条件。
        if (DownloadHook == null && !_svc.Config.TryResolveOutDir(out _, out string dirErr))
        {
            ShowBanner(dirErr + "\n★ 已中止本次下载，不会改存到别处。", BannerKind.Err);
            Log($"✗ {dirErr}");
            _downloadErrors.Add(dirErr);
            foreach (var row in targets)
            {
                if (row.IsGroupHeader) continue;
                row.State = "失败";
                row.Error = dirErr;
                fail++;
            }
            return (0, fail, false);
        }

        foreach (var row in targets)
        {
            if (_stopRequested) break;
            await _hgGate.WaitAsync(_cts.Token);             // 暂停时在两集之间也停住
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
                    : await _svc.DownloadEpisodeAsync(ep, GetQuality(), cb, _cts.Token, _hgGate);
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
                    RecordHistory("红果", series, row.Title, row.SeriesId, row.Vid,
                                  res.OutPath, res.Size, res.Duration,
                                  res.Skipped ? "已存在" : "已完成");
                }
                else
                {
                    fail++;
                    // ★★ 只有「空 body」才计入熔断；
                    //   其余（code=101000 参数错 / 网络错 / 502）不该触发中止。
                    bool rateLimited = res.Error?.Contains("空 body") == true;
                    if (res.Error?.Contains("无法解析播放地址") == true && rateLimited) addrFail++;
                    RunOnUi(() => { row.State = "失败"; row.Error = res.Error; });
                    Log($"✗ {series} [{row.Index}] {res.Error}");
                    _downloadErrors.Add($"「{series}」[{row.Index}] {res.Error}");
                    // ★ 连续空 body → Core 里已置熔断位，立刻停
                    if (addrFail >= 3 || HongguoService.PlaybackBlocked)
                    {
                        Log("！播放接口连续空 body，停止本批（不是限流，等待无效）。"
                            + "先看日志判据，急用可改用「提取App缓存」出片。");
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

        // 兴趣岛的条目排在红果之后跑：它们要先解析播放地址，慢一些
        if (xqdTargets.Count > 0 && !_stopRequested)
        {
            await _hgGate.WaitAsync(_cts.Token);             // 暂停中先别切到兴趣岛阶段
            var (xok, xfail) = await DownloadXqdRowsAsync(xqdTargets);
            ok += xok;
            fail += xfail;
        }

        return (ok, fail, false);
    }

    /// <summary>
    /// 兴趣岛部分的下载：交给 XqdQueue（课间并发 2 / 课内分片 12），
    /// 进度通过 JobChanged/JobProgress 回调同步到共用清单的同一行。
    /// </summary>
    private async Task<(int Ok, int Fail)> DownloadXqdRowsAsync(List<EpisodeRow> rows)
    {
        var jobs = rows.Where(r => r.Xqd != null).Select(r => r.Xqd).Distinct().ToList();
        if (jobs.Count == 0) return (0, 0);

        if (_xqdCookie.Length > 0) _xqd.Configure(_xqdCookie);
        if (!_xqd.HasAuth && jobs.Any(j => j.FromCatalog))
        {
            ShowBanner("兴趣岛课程需要授权：点右上角「获取授权」读取兴趣岛 App 的登录态后再下载。",
                       BannerKind.Err);
            return (0, jobs.Count);
        }

        string outRoot = (EdOut.Text ?? "").Trim();
        if (outRoot.Length == 0)
        {
            ShowBanner("没有下载目录：请先在上方设置。", BannerKind.Err);
            return (0, jobs.Count);
        }
        _xqd.Options.OutRoot = outRoot;
        _xqd.Options.Quality = _xqdQuality;
        _xqd.RebasePaths();
        _xqd.Rescan();
        foreach (var r in rows) r.SyncFromXqd();

        // 统一暂停态：进入兴趣岛阶段时把当前 _paused 应用给队列
        _xqd.SetPaused(_paused);
        try
        {
            var (ok, fail, skip) = await _xqd.RunAsync(jobs, _cts.Token);
            return (ok + skip, fail);
        }
        finally
        {
            foreach (var r in rows) r.SyncFromXqd();
        }
    }

    private bool IsDramaRow(EpisodeRow row)
    {
        var shelf = ShelfRows.FirstOrDefault(s => s.SeriesId == row.SeriesId);
        return shelf?.IsShortDrama ?? false;
    }

    private bool _stopRequested;

    /// <summary>正在下载（用来禁掉「批量下载」按钮，避免重复点）。</summary>
    private bool _downloading;

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
        _epViewTag = sender switch
        {
            RadioButton rb when ReferenceEquals(rb, RbPending) => EpViewFilter.Pending,
            RadioButton rb when ReferenceEquals(rb, RbRunning) => EpViewFilter.Running,
            RadioButton rb when ReferenceEquals(rb, RbDone) => EpViewFilter.Done,
            RadioButton rb when ReferenceEquals(rb, RbFailed) => EpViewFilter.Failed,
            _ => EpViewFilter.All,
        };
        ApplyEpView();
    }

    private void CmbQuality_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_qualityInit || !IsLoaded) return;
        if (_xqdSource)
        {
            _xqdQuality = GetQuality();
            _xqd.Options.Quality = _xqdQuality;
            Log($"兴趣岛画质切换为 {_xqdQuality}");
            return;
        }
        _hgQuality = GetQuality();
        _svc.Config.Quality = _hgQuality;
        _svc.Config.Save();
        Log($"画质切换为 {_hgQuality}");
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

    /// <summary>
    /// 取兴趣岛授权（只在用户点「获取授权」时调用）。
    /// 先看本机缓存并在线验证；无效再去 MuMu 里读兴趣岛 App 的 Cookie。
    /// </summary>
    private async Task AuthXqdAsync()
    {
        LblStatus.Text = "正在获取兴趣岛授权…";
        void Say(string m) => RunOnUi(() => { LblStatus.Text = m; Log(m); });

        try
        {
            var res = await XqdAuth.AcquireAsync(Say, force: false, CancellationToken.None);

            if (res.Ok && res.FromCache)
            {
                Say("校验本机保存的兴趣岛授权…");
                var (valid, _) = await new QianliaoPlatform(res.Cookie)
                    .CheckAuthAsync(CancellationToken.None);
                if (!valid)
                {
                    Say("本机授权已失效，改为从 MuMu 读取…");
                    res = await XqdAuth.AcquireAsync(Say, force: true, CancellationToken.None);
                }
            }

            // ★ 模拟器在、但没开或 App 没登录 → 边等边取（与红果同一套等待窗）
            if (!res.Ok)
            {
                var insts = TryListMumuInstances();
                if (insts.Count > 0)
                {
                    Say("等待兴趣岛登录（打开模拟器里的兴趣岛并确认已登录）…");
                    var dlg = new WaitAuthWindow(
                        "等待兴趣岛登录",
                        "还没拿到兴趣岛授权 —— 请打开安卓机里的兴趣岛并确认已登录",
                        "1. 在 MuMu 里启动装了兴趣岛的安卓机（多开时任一台都行）\n"
                        + "2. 打开「兴趣岛」，确认已经登录（能看到「我的课程」）\n"
                        + "3. 回到这里等几秒，读到登录态会自动继续",
                        insts,
                        "等待超时。可以先启动安卓机、在兴趣岛里登录，然后再点一次「获取授权」。",
                        (say, ct) => WaitXqdAuthAsync(say, ct));
                    await dlg.ShowDialog(this);
                    if (dlg.FoundIndex >= 0)
                        res = await XqdAuth.AcquireAsync(Say, force: true, CancellationToken.None);
                }
            }

            if (!res.Ok)
            {
                Log("✗ " + res.Message);
                ShowBanner(res.Message, BannerKind.Err);
                SetAuthLabel();
                return;
            }

            _xqdCookie = res.Cookie;
            _xqd.Configure(_xqdCookie);
            SetAuthLabel();
            Log("兴趣岛：" + res.Message);
            ShowBanner("兴趣岛：" + res.Message, BannerKind.Ok);

            // 授权成功 → 立刻把营列表拉出来（这是用户点按钮的实际目的）
            _campsLoaded = false;
            await LoadCampsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log("✗ 获取兴趣岛授权失败：" + ex.Message);
            ShowBanner("获取兴趣岛授权失败：" + ex.Message, BannerKind.Err);
        }
        finally { LblStatus.Text = "就绪"; }
    }

    private static List<Core.Auth.MumuAuth.Instance> TryListMumuInstances()
    {
        try
        {
            string mumu = Core.Auth.MumuAuth.FindMumu();
            return Core.Auth.MumuAuth.ListInstances(mumu).ToList();
        }
        catch { return new List<Core.Auth.MumuAuth.Instance>(); }
    }

    /// <summary>兴趣岛侧的「边等边取」：每 3 秒读一次，最多 180 秒。</summary>
    private static async Task<int> WaitXqdAuthAsync(Action<string> say, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(180);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            bool anyRunning = TryListMumuInstances().Any(i => i.Started);
            if (!anyRunning)
            {
                say("没检测到正在运行的安卓机，3 秒后再看…");
                await Task.Delay(3000, ct);
                continue;
            }

            say("读取 MuMu 里的兴趣岛登录态…");
            var res = await XqdAuth.AcquireAsync(null, force: true, ct);
            if (res.Ok) return 0;

            string first = (res.Message ?? "").Split('\n')[0];
            if (first.Length > 60) first = first.Substring(0, 60) + "…";
            say(first + "　3 秒后再试…");
            await Task.Delay(3000, ct);
        }
        return -1;
    }

    private async void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        BtnLogin.IsEnabled = false;
        try
        {
            // ★ 按**当前内容源**取授权：两个 App 的登录态互不相干
            if (_xqdSource) { await AuthXqdAsync(); return; }

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
            ShowBanner("没等到授权：请先启动安卓机并在红果里确认已登录，再点「获取授权」。",
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

    private void HideBanner()
    {
        if (Dispatcher.UIThread.CheckAccess()) Banner.IsVisible = false;
        else Dispatcher.UIThread.Post(() => Banner.IsVisible = false);
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
