using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hgvd.Core.Api;
using Hgvd.Core.Auth;
using Hgvd.Core.Engine;
using Hgvd.Core.Sign;

namespace Hgvd.Core;

/// <summary>收藏夹里的一项（供界面绑定）。</summary>
public sealed class ShelfItem
{
    public string SeriesId = "";
    public string Title = "";
    public string Cover = "";
    public int EpisodeCnt;
    public int BookType;
    public int ContentType;
    public bool IsAlbum;

    /// <summary>是不是**真短剧**（bytevc 加密，ffmpeg 解不出画面）。</summary>
    public bool IsShortDrama => BookType == 2 || ContentType == 1004;

    public string TypeLabel => IsShortDrama ? "短剧"
        : BookType == 4 ? "单个视频"
        : BookType == 3 ? "教学合集" : "其他";
}

/// <summary>
/// 业务编排层：收藏夹 → 剧目 → 分集 → 下载。
///
/// ★ 这是工具的「单一事实来源」—— 所有前端（GUI / CLI）只调这里，不自己碰 HTTP。
/// </summary>
public sealed class HongguoService
{
    private readonly AppConfig _cfg;
    private Account _account;
    private SignedClient _client;
    private DeviceIdentity _ident;
    private readonly ProgressStore _progress;

    public HongguoService(AppConfig cfg = null)
    {
        _cfg = cfg ?? AppConfig.Load();
        _account = Account.Load();
        _progress = new ProgressStore();
    }

    public AppConfig Config => _cfg;

    /// <summary>下载进度记账（开始/完成/失败 + 直链留档）。</summary>
    public ProgressStore Progress => _progress;

    /// <summary>当前账号标识（记账用它判断直链是否随账号更换而作废）。</summary>
    public string AccountKey =>
        !string.IsNullOrEmpty(_account.UserId) ? _account.UserId
        : (_account.Cookies.TryGetValue("install_id", out var iid) ? iid : "");

    public bool LoggedIn => _account.Ok;

    public Account Account => _account;

    public void SetAccount(Account acc)
    {
        _account = acc;
        _client = null;
        // ★ 换了账号/实例后，设备身份要按新 cookie 的 install_id 重新配对
        _ident = null;
    }

    /// <summary>
    /// 设备身份（签名必需）。
    ///
    /// ★★ <c>iid</c> / <c>device_id</c> 必须与 cookie **同源**
    ///   （文档 S2 / I 节实测结论）：
    ///   <c>iid</c> = cookie 里的 <c>install_id</c>，
    ///   <c>device_id</c> = App 的 <c>applog_stats.xml</c> 里的 <c>device_id</c>。
    ///   ★ 两个都来自**实例 1**（用户实际在用的那台）。
    ///   ★ 曾经试过用「另一个注册设备的 install_id」当 iid，
    ///     播放成功率并没有提升，反而破坏了同源关系 —— 已废弃。
    /// </summary>
    public DeviceIdentity Device
    {
        get
        {
            if (_ident == null)
            {
                // ★ 先按 cookie 的 install_id 取**同源**的设备档案（devices.json），
                //   多开时不会拿到另一台实例的 device_id。
                string cookieIid = _account.Cookies.TryGetValue("install_id", out var v) ? v : "";
                _ident = MumuAuth.LoadDevice(cookieIid);
                if (string.IsNullOrEmpty(_ident.DeviceId))
                {
                    // 兜底：实例 1 的 App 真机 device_id
                    _ident.DeviceId = "744826091366314";
                }
                if (string.IsNullOrEmpty(_ident.Iid))
                {
                    // 兜底：cookie 的 install_id（与 device_id 同源）
                    _ident.Iid = _account.Cookies.TryGetValue("install_id", out var iid)
                        ? iid : "744826091370410";
                }
                if (string.IsNullOrEmpty(_ident.VersionName))
                    _ident.VersionName = "7.3.9.32";
                if (string.IsNullOrEmpty(_ident.VersionCode))
                    _ident.VersionCode = "73932";
            }
            return _ident;
        }
    }

    public SignedClient Client
    {
        get
        {
            if (_client == null)
                _client = new SignedClient(Device, _account.CookieHeader);
            return _client;
        }
    }

    // ---------------------------------------------------------- 收藏夹

    /// <summary>
    /// 收藏夹详情回调：标题 / 集数**增量**到达时触发（同一 id 可能触发多次）。
    /// </summary>
    public delegate void ShelfDetailHandler(string seriesId, string title, int episodeCnt);

    /// <summary>
    /// 读收藏夹（一次性拿全，CLI 用）。
    /// </summary>
    /// <param name="withDetails">是否补标题 / 集数。</param>
    /// <param name="albumLimit">
    /// 最多逐个查多少个合集的真实集数。★ 每查一个是一次带签名的 album_detail
    /// 请求（实测 0.2s，但要串行 + 间隔）—— 界面不要用这个，see
    /// <see cref="ListShelfStreamingAsync"/>。
    /// </param>
    public async Task<List<ShelfItem>> ListShelfAsync(
        bool withDetails = true, int albumLimit = 30,
        CancellationToken ct = default)
    {
        var rows = await Shelf.ListAsync(_account.CookieHeader, _account.UserId, ct);
        var items = BuildShelfItems(rows);
        if (withDetails && items.Count > 0)
        {
            var titles = await FetchTitlesAsync(rows, albumLimit, ct);
            foreach (var it in items)
                if (titles.TryGetValue(it.SeriesId, out var t))
                {
                    if (!string.IsNullOrEmpty(t.Title)) it.Title = t.Title;
                    if (t.Cnt > 0) it.EpisodeCnt = t.Cnt;
                }
        }
        return items;
    }

    /// <summary>
    /// **渐进式**读收藏夹 —— 界面专用。
    ///
    /// ★★ 为什么不能用 <see cref="ListShelfAsync"/>：
    ///   它最后会**串行**把前 N 个合集逐个查 album_detail（每个还要 600ms 间隔），
    ///   实测 30 个 ≈ 20~60s，界面只能对着空白干等。
    ///   → 参考量级（单次网络往返 ~0.2s）：
    ///     · 收藏夹原始列表：      **~0.4s**（只给 id，不给标题/集数）
    ///     · 批量标题（20 个/批）：串行 12 批 ~6s，**并发 4 路 ~0.3s/4 批**
    ///     · 单个合集 album_detail：**~0.2s**
    /// ★★ 这里的做法：
    ///   1) <paramref name="onRaw"/> —— 原始列表一到就逐条回调，界面**立刻**
    ///      就能列出全部条目（~0.5s），不用等标题；
    ///   2) <paramref name="onDetail"/> —— 标题靠 multi_video_detail 批量取，
    ///      **并发 4 路**，每批回来就把那 20 行的标题补上（逐项可见）；
    ///   3) ★ **启动时不再查合集集数** —— 集数在用户点开该剧时随分集一起拿
    ///      （album_detail 只要 0.2s），省掉几十秒空等。
    /// </summary>
    public async Task<List<ShelfItem>> ListShelfStreamingAsync(
        Action<ShelfItem> onRaw, ShelfDetailHandler onDetail,
        int titleConcurrency = 4, CancellationToken ct = default)
    {
        var rows = await Shelf.ListAsync(_account.CookieHeader, _account.UserId, ct);
        var items = BuildShelfItems(rows);
        foreach (var it in items) onRaw?.Invoke(it);
        if (items.Count == 0) return items;

        var byId = new Dictionary<string, ShelfItem>();
        foreach (var it in items) byId[it.SeriesId] = it;

        // 标题批量取：每批 20 个，最多 titleConcurrency 路并发
        var chunks = new List<List<ShelfEntry>>();
        for (int i = 0; i < rows.Count; i += 20)
            chunks.Add(rows.Skip(i).Take(20).ToList());

        using var sem = new SemaphoreSlim(Math.Max(1, titleConcurrency));
        var tasks = new List<Task>();
        foreach (var chunk in chunks)
            tasks.Add(FetchChunkAsync(chunk));

        await Task.WhenAll(tasks);
        return items;

        async Task FetchChunkAsync(List<ShelfEntry> chunk)
        {
            await sem.WaitAsync(ct);
            try
            {
                var map = await FetchChunkDetailAsync(chunk, ct);
                foreach (var kv in map)
                {
                    if (byId.TryGetValue(kv.Key, out var it))
                    {
                        if (!string.IsNullOrEmpty(kv.Value.Title)) it.Title = kv.Value.Title;
                        if (kv.Value.Cnt > 0) it.EpisodeCnt = kv.Value.Cnt;
                    }
                    onDetail?.Invoke(kv.Key, kv.Value.Title, kv.Value.Cnt);
                }
            }
            catch { /* 单批失败不影响其它批 */ }
            finally { sem.Release(); }
        }
    }

    /// <summary>
    /// **回落**补标题：批量详情接口没给标题的条目，单独查 <c>album_detail</c>。
    ///
    /// ★ 批量接口**不会**给全部标题：缺标题的都是 <c>book_type=3</c> 的合集，
    ///   它们在界面上会显示成一串 <c>series_id</c> 数字。
    ///   → 用 <c>album_detail</c> 单独查，约 **0.2s/条**，绝大多数能拿到标题
    ///   （极少数是真的已下架，服务端什么都不返回）。
    /// ★ 这个方法是**后台跑**的（不等它），因为它只影响少数几行，
    ///   不该拖慢整个列表。
    /// </summary>
    public async Task FillMissingTitlesAsync(List<ShelfItem> items,
        ShelfDetailHandler onDetail, CancellationToken ct = default)
    {
        var missing = items.Where(i => string.IsNullOrEmpty(i.Title)).ToList();
        if (missing.Count == 0) return;

        using var sem = new SemaphoreSlim(4);
        var tasks = missing.Select(async it =>
        {
            await sem.WaitAsync(ct);
            try
            {
                int bt = it.BookType > 0 ? it.BookType : 3;
                // ★ 退避要短：album_detail 失败时 PostAsync 默认 5 次重试
                //   累计能拖 40s+，这里只给 2 次 —— 已下架的条目快速放弃。
                var album = await Client.AlbumAsync(it.SeriesId, bt, ct: ct,
                    retries: 2, basePause: 1.0, maxPause: 3.0);
                if (album == null && bt != 3)
                    album = await Client.AlbumAsync(it.SeriesId, 3, ct: ct,
                        retries: 1, basePause: 1.0, maxPause: 2.0);
                if (album == null) return;
                if (!string.IsNullOrEmpty(album.Title)) it.Title = album.Title;
                if (album.EpisodeCnt > 0) it.EpisodeCnt = album.EpisodeCnt;
                onDetail?.Invoke(it.SeriesId, it.Title, it.EpisodeCnt);
            }
            catch { }
            finally { sem.Release(); }
        }).ToList();
        await Task.WhenAll(tasks);
    }

    private static List<ShelfItem> BuildShelfItems(List<ShelfEntry> rows)
    {
        var items = new List<ShelfItem>(rows.Count);
        foreach (var r in rows)
            items.Add(new ShelfItem
            {
                SeriesId = r.SeriesId,
                BookType = r.BookType,
                ContentType = r.ContentType,
            });
        return items;
    }

    /// <summary>
    /// 一批（≤20 个）id 的标题 + 集数（批量详情接口，免不了签名但一次 20 个）。
    /// </summary>
    private async Task<Dictionary<string, (string Title, int Cnt)>> FetchChunkDetailAsync(
        List<ShelfEntry> chunk, CancellationToken ct)
    {
        var outMap = new Dictionary<string, (string Title, int Cnt)>();
        string body = JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                ["dr_scene"] = "preload",
                ["series_id"] = string.Join(",", chunk.Select(r => r.SeriesId)),
                ["biz_param"] = new Dictionary<string, object>
                {
                    ["detail_page_version"] = 0,
                    ["disable_digg_stat"] = false,
                    ["source"] = 7,
                    ["need_all_video_definition"] = false,
                },
            });
        using var doc = await Client.PostAsync(
            SignedClient.PathDetailBatch, body, ct: ct,
            // ★ 批量详情不是播放接口；别用播放那套 3.2s 起步的重试节奏，
            //   否则一批失败要拖 40s+。这里 3 次 / 2s 起步。
            retries: 3, basePause: 2.0, maxPause: 6.0);
        if (doc is null) return outMap;
        if (doc.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in data.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                if (!prop.Value.TryGetProperty("video_data", out var vd)) continue;
                string title = J.Str(vd, "series_title");
                int cnt = J.Int(vd, "episode_cnt");
                if (cnt == 0 && vd.TryGetProperty("video_list", out var vl)
                    && vl.ValueKind == JsonValueKind.Array)
                    cnt = vl.GetArrayLength();
                if (!string.IsNullOrEmpty(title)) outMap[prop.Name] = (title, cnt);
            }
        }
        return outMap;
    }

    /// <summary>
    /// 批量取标题与集数（CLI 用的一次性版本）。
    ///
    /// ★ 两路：
    ///   1. <c>multi_video_detail/preload/v1</c> —— **一次 20 个**就能拿到
    ///      所有条目的标题（合集也返回 <c>series_title</c>），但 <c>episode_cnt</c> 是 0。
    ///   2. <c>album_detail/v1/</c> —— 逐个查，**才有真实的集数**，
    ///      但 222 个合集要 222 次请求，慢且容易撞上端点丢包。
    ///   所以：**先用第 1 路把标题全补齐**（并发 4 路），第 2 路只查前
    ///   <c>albumLimit</c> 个。
    /// </summary>
    private async Task<Dictionary<string, (string Title, int Cnt)>> FetchTitlesAsync(
        List<ShelfEntry> rows, int albumLimit, CancellationToken ct)
    {
        var outMap = new Dictionary<string, (string Title, int Cnt)>();

        // ---- 第 1 路：批量拿标题（全部条目，一次 20 个，并发 4 路） ----
        var chunks = new List<List<ShelfEntry>>();
        for (int i = 0; i < rows.Count; i += 20)
            chunks.Add(rows.Skip(i).Take(20).ToList());

        using var sem = new SemaphoreSlim(4);
        var tasks = new List<Task>();
        foreach (var chunk in chunks)
        {
            var c = chunk;
            tasks.Add(Task.Run(async () =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    var map = await FetchChunkDetailAsync(c, ct);
                    lock (outMap) foreach (var kv in map) outMap[kv.Key] = kv.Value;
                }
                catch { }
                finally { sem.Release(); }
            }, ct));
        }
        await Task.WhenAll(tasks);

        // ---- 第 2 路：给前 albumLimit 个合集补真实集数 ----
        if (albumLimit > 0)
        {
            var albums = rows.Where(r => r.BookType == 3)
                             .Select(r => r.SeriesId).Take(albumLimit).ToList();
            foreach (var id in albums)
            {
                try
                {
                    var album = await Client.AlbumAsync(id, 3, ct: ct);
                    if (album == null) continue;
                    int cnt = album.EpisodeCnt > 0 ? album.EpisodeCnt : album.Episodes.Count;
                    outMap.TryGetValue(id, out var prev);
                    string title = !string.IsNullOrEmpty(album.Title)
                        ? album.Title : prev.Title;
                    outMap[id] = (title, cnt);
                }
                catch { }
                await Task.Delay(600, ct);
            }
        }
        return outMap;
    }

    // ---------------------------------------------------------- 分集

    /// <summary>取分集列表（合集优先，回落 preload）。</summary>
    public async Task<List<Episode>> ListEpisodesAsync(string seriesId,
        int bookType = 3, CancellationToken ct = default)
        => await Client.EpisodesAsync(seriesId, bookType, ct);

    // ---------------------------------------------------------- 下载

    /// <summary>
    /// 播放地址缓存。
    ///
    /// ★★ `video_model` 的空 body 是**服务端/网关丢弃请求**，与请求频率无关
    ///   （docs/03-开发要点.md §1.0：7 轮退避 19 分钟未恢复、隔 6 天第一发照旧空）。
    ///   ⇒ 同一集**只打一次**播放接口，之后从缓存取（省请求，也省排错干扰）。
    ///   缓存键：<c>videoId</c> + <c>videoIdType</c> + <c>quality</c>。
    /// </summary>
    private static readonly Dictionary<string, (string Url, long Size, string Key, string Codec, string Backup)>
        PlaybackCache = new();

    /// <summary>
    /// 预热缓存：勾选入清单时后台提前解析的直链。
    /// ★ 独立字典 + 时间戳 + 账号，与正式下载的 <see cref="PlaybackCache"/> 分开：
    ///   ① 预热的失败**绝不能计入熔断**（否则勾一部剧就把正在下的任务熔断掉）；
    ///   ② 直链会过期 → 带时间戳，超过 <see cref="WarmTtlMs"/> 作废；
    ///   ③ 换账号 → account 不匹配作废。
    /// </summary>
    private sealed class WarmEntry
    {
        public string Url = "", Key = "", Codec = "", Backup = "";
        public long Size;
        public long Ts;          // Unix 毫秒
        public string Account = "";
    }
    private static readonly Dictionary<string, WarmEntry> WarmCache = new();
    private const long WarmTtlMs = 30 * 60 * 1000;   // 30 分钟：勾选到开下通常几分钟，够用又不会拿到过期链

    /// <summary>清空播放地址缓存（换账号 / 签名失败时可手动调用）。含预热缓存。</summary>
    public static void ClearPlaybackCache()
    {
        lock (PlaybackCache) PlaybackCache.Clear();
        lock (WarmCache) WarmCache.Clear();
    }

    /// <summary>
    /// 解析单集直链并写进 <see cref="WarmCache"/>（失败静默、不碰熔断）。
    /// 供有界预取窗口与旧的批量预热共用；已在缓存/未过期则直接跳过。
    /// </summary>
    public async Task WarmOneAsync(Episode ep, string quality, CancellationToken ct = default)
    {
        if (ep == null || string.IsNullOrEmpty(ep.Vid) || !LoggedIn) return;
        quality ??= _cfg.Quality;
        string account = AccountKey;
        int type = ep.VideoIdType >= 0 ? ep.VideoIdType : 1;
        string ck = $"{ep.Vid}|{type}|{quality}";
        lock (PlaybackCache)
            if (PlaybackCache.TryGetValue(ck, out var hit) && !string.IsNullOrEmpty(hit.Url)) return;
        lock (WarmCache)
            if (WarmCache.TryGetValue(ck, out var w) && !string.IsNullOrEmpty(w.Url)
                && w.Account == account
                && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - w.Ts < WarmTtlMs) return;

        try
        {
            var model = await Client.PlaybackAsync(ep.Vid, type, ct)
                .WaitAsync(TimeSpan.FromSeconds(15), ct);
            if (model.Streams.Count == 0) return;          // 没流：静默，不计熔断
            var chosen = model.Streams.FirstOrDefault(
                s => string.Equals(s.Definition, quality, StringComparison.OrdinalIgnoreCase))
                ?? model.Streams[0];
            lock (WarmCache)
                WarmCache[ck] = new WarmEntry
                {
                    Url = chosen.Url, Size = chosen.Size, Key = model.ContentKey,
                    Codec = chosen.Codec, Backup = chosen.BackupUrl ?? "",
                    Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Account = account,
                };
        }
        catch { /* ★ 预热失败一律静默，不调 NotePlaybackEmpty —— 不触发熔断 */ }
    }

    /// <summary>
    /// 后台预热一批分集的直链（勾选入清单时调用）。
    /// ★ 低并发（2 路）、失败静默、不碰熔断；成功写 <see cref="WarmCache"/> 供正式下载命中。
    /// ★ 只热传进来的 vid（= 清单内），不碰收藏夹其它剧。
    /// </summary>
    public async Task WarmAsync(List<Episode> episodes, string quality,
        CancellationToken ct = default)
    {
        if (!LoggedIn || episodes == null || episodes.Count == 0) return;
        quality ??= _cfg.Quality;
        using var sem = new SemaphoreSlim(2);
        var tasks = episodes.Select(async ep =>
        {
            await sem.WaitAsync(ct);
            try { await WarmOneAsync(ep, quality, ct); }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// 正式解析前先查预热缓存（命中且未过期/账号一致则提升到正式缓存）。
    /// </summary>
    private bool TryTakeWarm(string ck, string account, out (string Url, long Size, string Key, string Codec, string Backup) result)
    {
        result = default;
        lock (WarmCache)
        {
            if (!WarmCache.TryGetValue(ck, out var w) || string.IsNullOrEmpty(w.Url)) return false;
            if (w.Account != account
                || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - w.Ts >= WarmTtlMs)
            {
                WarmCache.Remove(ck);                       // 过期/换号 → 丢弃，走正常解析
                return false;
            }
            result = (w.Url, w.Size, w.Key, w.Codec, w.Backup);
        }
        lock (PlaybackCache) PlaybackCache[ck] = result;    // 提升到正式缓存（进程内不再失效）
        return true;
    }

    // ---------------------------------------------------------- 播放接口熔断

    /// <summary>
    /// ★ 播放接口熔断：连续 <see cref="PlaybackEmptyLimit"/> 次拿到空 body
    ///   就**短路**掉后续所有播放请求。
    ///
    /// ★★ 为什么需要：空 body 时一次 <c>video_model</c> 要重试 5 次
    ///   （3.2+6.4+9.6+12+12 ≈ 43s），一集还要试两个 <c>video_id_type</c>。
    ///   批量下载几百部剧时会在重试里空转几十分钟 —— 用户看到的是「卡住了」。
    ///   熔断后**立刻**失败并提示改用 App 缓存路径。
    ///
    /// ★ 阈值取 2：一次下载会依次试两个 video_id_type，
    ///   两个都空 = 播放端点这条路当下走不通（**不是**限流，等也不会好）。
    /// </summary>
    private const int PlaybackEmptyLimit = 2;
    private static int _playbackEmptyStreak;
    private static bool _playbackBlocked;

    /// <summary>播放接口是否已被熔断（连续空 body，端点当下不可用）。</summary>
    public static bool PlaybackBlocked
    {
        get { lock (PlaybackCache) return _playbackBlocked; }
    }

    /// <summary>重置熔断（重启程序 / 换了出片路径之后调用）。</summary>
    public static void ResetPlaybackCircuit()
    {
        lock (PlaybackCache)
        {
            _playbackEmptyStreak = 0;
            _playbackBlocked = false;
        }
    }

    private static void NotePlaybackEmpty()
    {
        lock (PlaybackCache)
        {
            _playbackEmptyStreak++;
            if (_playbackEmptyStreak >= PlaybackEmptyLimit) _playbackBlocked = true;
        }
    }

    private static void NotePlaybackOk()
    {
        lock (PlaybackCache) _playbackEmptyStreak = 0;
    }

    /// <summary>
    /// 解析完整正片的播放地址（带缓存）。
    /// </summary>
    /// <returns>
    /// (地址, 大小, 内容密钥, 编码, 失败原因)。<paramref name="failure"/> 非空说明没拿到地址，
    /// 且里面写的是**真实原因**（空 body / 参数错 / 网络错），不再是笼统的「风控」。
    /// </returns>
    public async Task<(string Url, long Size, string Key, string Codec, string Failure, string Backup)>
        ResolveFullUrlAsync(string videoId, int videoIdType = 0, string quality = "720p",
        CancellationToken ct = default)
    {
        if (!LoggedIn) return ("", 0, "", "", "未登录：请先执行 hgvd login", "");
        if (PlaybackBlocked)
            return ("", 0, "", "", "播放接口连续多次返回空 body，已熔断本批。" +
                "★ 2026-11-21 起网关会校验播放签名（X-Helios / X-Medusa），空 body 即签名被拒；" +
                "不是限流、不是网络，等再久也不会恢复。急用请改用「提取App缓存」", "");

        string ck = $"{videoId}|{videoIdType}|{quality}";
        lock (PlaybackCache)
            if (PlaybackCache.TryGetValue(ck, out var hit) && !string.IsNullOrEmpty(hit.Url))
                return (hit.Url, hit.Size, hit.Key, hit.Codec, "", hit.Backup);

        // ★ 勾选时预热的结果：命中且未过期/账号一致 → 直接用，免一次解析
        if (TryTakeWarm(ck, AccountKey, out var warm))
            return (warm.Url, warm.Size, warm.Key, warm.Codec, "", warm.Backup);

        try
        {
            // ★★ 单集解析硬超时 15 秒（2026-11-22）：
            //   失败集会走 SignedClient 的 5 次退避（≈43s）× 两个 video_id_type（≈86s），
            //   并发下载时会长期占住一个工位。正常解析 ~0.2s，15s 足够宽松；
            //   超时按普通失败返回（不计熔断），快速让出工位。
            var model = await Client.PlaybackAsync(videoId, videoIdType, ct)
                .WaitAsync(TimeSpan.FromSeconds(15), ct);
            if (model.Streams.Count == 0)
            {
                // 拿到了 JSON 但没有流 —— 说明签名是通的，是**参数/内容**问题
                // （例如 video_id_type 不对、或该集没有可播的清晰度）。
                // ★ 拿到了 JSON，**不是**空 body，不计入熔断。
                NotePlaybackOk();
                return ("", 0, "", "", "服务端返回了数据但没有可用的清晰度" +
                    "（多半是 video_id_type 与内容类型不匹配）", "");
            }
            var chosen = model.Streams.FirstOrDefault(
                s => string.Equals(s.Definition, quality, StringComparison.OrdinalIgnoreCase))
                ?? model.Streams[0];
            var result = (chosen.Url, chosen.Size, model.ContentKey, chosen.Codec,
                          chosen.BackupUrl ?? "");
            lock (PlaybackCache) PlaybackCache[ck] = result;
            NotePlaybackOk();
            return (result.Item1, result.Item2, result.Item3, result.Item4, "", result.Item5);
        }
        catch (ApiException e)
        {
            // ★ 只有**空 body** 才计入熔断（服务端/网关丢弃了请求）
            if (e.Kind == ApiFailure.EmptyBody) NotePlaybackEmpty();
            else NotePlaybackOk();
            if (Environment.GetEnvironmentVariable("HGVD_DEBUG") == "1")
                Console.WriteLine($"[dbg] 播放地址失败：{e.Message}");
            return ("", 0, "", "", e.Message, "");
        }
        catch (Exception e)
        {
            if (Environment.GetEnvironmentVariable("HGVD_DEBUG") == "1")
                Console.WriteLine($"[dbg] 播放地址失败：{e}");
            NotePlaybackOk();
            return ("", 0, "", "", $"网络/解析异常：{e.Message}", "");
        }
    }

    /// <summary>
    /// 预检一批分集，得出「哪些可以直接跳过」（本批跳过表）。
    ///
    /// ★★ 为什么要有这一步：原来 SkipExisting 在下载循环里**逐集**跑 ffprobe
    ///   （每集起一个进程 ~45ms），用户 180 集里下了 100 集时，开头要空转 4.5s
    ///   「一集一集试到已存在才跳」。现在按下下载键先扫一遍，分三类：
    ///   ① 记账 done + 磁盘 size 一致 → 秒跳（0 次 ffprobe）；
    ///   ② 无记账但文件在 → 4 路并行 ffprobe，判据与原逻辑**完全相同**，通过者补记 done；
    ///   ③ 文件缺失 → 待下载。
    ///   ★ 结果缓存成表，循环里只查表，不再起进程。
    /// </summary>
    public async Task<Dictionary<string, DownloadResult>> PrecheckAsync(
        List<Episode> episodes, CancellationToken ct = default)
    {
        var skip = new Dictionary<string, DownloadResult>();
        if (!_cfg.SkipExisting || episodes == null || episodes.Count == 0) return skip;

        // 先按成品路径去重（同一集可能被重复加入清单）
        var byPath = new Dictionary<string, Episode>(StringComparer.OrdinalIgnoreCase);
        foreach (var ep in episodes)
        {
            if (ep == null || string.IsNullOrEmpty(ep.Vid)) continue;
            string path;
            try { path = BuildOutPath(ep); } catch { continue; }  // 目录不可用交给下载循环报错
            if (!byPath.ContainsKey(path)) byPath[path] = ep;
        }

        var map = _progress.LoadMap();
        var needProbe = new List<(string Path, Episode Ep)>();

        foreach (var kv in byPath)
        {
            string path = kv.Key;
            var ep = kv.Value;
            long diskSize;
            try { diskSize = File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch { diskSize = 0; }
            if (diskSize <= 0) continue;                       // ③ 文件不在 → 待下载

            if (map.TryGetValue(path, out var rec) && rec.IsDone && rec.Size == diskSize)
            {
                // ① 记账命中：0 次 ffprobe
                skip[ep.Vid] = new DownloadResult
                {
                    Ok = true, OutPath = path, Size = diskSize,
                    Duration = rec.Duration, Skipped = true,
                };
            }
            else
            {
                needProbe.Add((path, ep));                     // ② 无记账 / size 不符 → 探测兜底
            }
        }

        // ② 4 路并行 ffprobe；判据与原来的 SkipExisting 完全一致
        if (needProbe.Count > 0)
        {
            using var sem = new SemaphoreSlim(4);
            var fresh = new List<ProgressEntry>();
            var tasks = needProbe.Select(async item =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    double d = await Task.Run(() => Util.ProbeDuration(item.Path), ct);
                    if (d <= 0) return;                        // 探测失败 → 不跳过，宁可重下
                    double tol = Math.Max(3.0, item.Ep.Duration * 0.02);
                    if (item.Ep.Duration > 0 && Math.Abs(d - item.Ep.Duration) > tol) return;
                    long sz = new FileInfo(item.Path).Length;
                    lock (skip)
                    {
                        skip[item.Ep.Vid] = new DownloadResult
                        {
                            Ok = true, OutPath = item.Path, Size = sz,
                            Duration = d, Skipped = true,
                        };
                        // 补记 done → 下次走 ① 秒跳（老文件的一次性迁移）
                        fresh.Add(new ProgressEntry
                        {
                            Path = item.Path, State = "done", Size = sz,
                            Duration = d, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            Account = AccountKey,
                        });
                    }
                }
                catch { /* 单集探测失败不影响其它集 */ }
                finally { sem.Release(); }
            });
            await Task.WhenAll(tasks);
            if (fresh.Count > 0) _progress.UpsertMany(fresh);
        }

        return skip;
    }

    /// <summary>下载一集。<paramref name="preskipped"/> 非空 = 预检已判定跳过，不再跑 ffprobe。</summary>
    public async Task<DownloadResult> DownloadEpisodeAsync(
        Episode ep, string quality = null, DownloadCallbacks cb = null,
        CancellationToken ct = default, PauseGate gate = null,
        DownloadResult preskipped = null)
    {
        quality ??= _cfg.Quality;
        string outPath = BuildOutPath(ep);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        // ★ 预检表命中：直接返回，一次进程都不起
        if (preskipped != null && preskipped.Skipped)
            return preskipped;

        if (_cfg.SkipExisting && File.Exists(outPath))
        {
            double d = Util.ProbeDuration(outPath);
            if (d > 0 && (ep.Duration <= 0 || Math.Abs(d - ep.Duration) <= Math.Max(3, ep.Duration * 0.02)))
            {
                // 兜底路径也补一条记账（单集双击 / 没走预检时能迁移）
                _progress.Upsert(new ProgressEntry
                {
                    Path = outPath, State = "done", Size = new FileInfo(outPath).Length,
                    Duration = d, Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Account = AccountKey,
                });
                return new DownloadResult
                {
                    Ok = true, OutPath = outPath, Size = new FileInfo(outPath).Length,
                    Duration = d, Skipped = true,
                };
            }
        }

        string url = "", key = "", codec = "", failure = "", backupUrl = "";
        long size = 0;

        // ★ video_id_type：短剧 0（vid），教学内容 1（series_id）。失败自动回退。
        int[] types = ep.VideoIdType >= 0
            ? new[] { ep.VideoIdType, 1 - ep.VideoIdType }
            : new[] { 0, 1 };

        foreach (int t in types)
        {
            (url, size, key, codec, failure, backupUrl) =
                await ResolveFullUrlAsync(ep.Vid, t, quality, ct);
            if (!string.IsNullOrEmpty(url))
            {
                ep.VideoIdType = t;
                // ★ 已知总大小 → 通知界面（下载前就能显示「≈ 29.1 MB」）
                if (size > 0) cb?.OnResolvedSize?.Invoke(size);
                break;
            }
            // ★ 已熔断 → 第二个 video_id_type 同样打不通，别再空转 43s
            if (PlaybackBlocked) break;
        }

        if (string.IsNullOrEmpty(url))
        {
            _progress.Upsert(new ProgressEntry
            {
                Path = outPath, State = "failed", Duration = ep.Duration,
                Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Account = AccountKey,
            });
            return new DownloadResult
            {
                Ok = false, OutPath = outPath,
                Error = $"无法解析播放地址（vid={ep.Vid}）"
                    + (LoggedIn ? "" : "（未登录：请先执行 hgvd login）")
                    + (string.IsNullOrEmpty(failure) ? "" : $"：{failure}")
                    + "。★ 「提取App缓存」（hgvd cache）完全不经过 video_model，是另一条出片路径。",
            };
        }

        // ★ 下载开始前立即落盘「开始 + 直链」—— 异常退出时至少留有这一条
        //   （直链仅诊断留档，下次绝不拿它下载：会过期）
        _progress.Upsert(new ProgressEntry
        {
            Path = outPath, State = "started", Duration = ep.Duration,
            Url = url, Backup = backupUrl,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Account = AccountKey,
        });

        var req = new DownloadRequest
        {
            Url = url,
            OutPath = outPath,
            ExpectDuration = ep.Duration,
            Cookie = _account.CookieHeader,
            ContentKey = key,
            // ★ 主 CDN 主机连不上时自动换备用直链
            BackupUrl = backupUrl,
        };
        var res = await Downloader.DownloadAsync(req, cb, ct, gate);
        // ★ 终态立即落盘：done 才能走下次的 ① 秒跳；失败留 failed
        _progress.Upsert(new ProgressEntry
        {
            Path = outPath, State = res.Ok ? "done" : "failed",
            Size = res.Size > 0 ? res.Size : (File.Exists(outPath) ? new FileInfo(outPath).Length : 0),
            Duration = res.Duration > 0 ? res.Duration : ep.Duration,
            Url = url, Backup = backupUrl,
            Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Account = AccountKey,
        });
        return res;
    }

    /// <summary>
    /// 把仍在下载（started）但未落终态的记账标成 interrupted（正常退出时调用）。
    /// ★ 只改 started，不动 done/failed —— 那是已确认的终态。
    /// </summary>
    public void MarkInterrupted()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // ★ 整表读-改-写在 Store 的锁内完成，不与并发下载写记账打架
        _progress.Mutate(map =>
        {
            foreach (var kv in map)
            {
                if (kv.Value.State == "started")
                {
                    kv.Value.State = "interrupted";
                    kv.Value.Ts = now;
                }
            }
        });
    }

    /// <summary>构造成品路径：<c>下载目录\剧名\剧名.001.标题.mp4</c>。</summary>
    /// <exception cref="IOException">
    /// 配置的下载目录不可用（磁盘拔了 / 网络盘断了 / 无写权限）。
    /// ★ 这里**必须抛**：以前静默回落到系统「下载」，文件下到用户不知道的地方还报成功。
    ///   抛出去以后，界面会把这集标成「失败」并在日志里写清原因。
    /// </exception>
    public string BuildOutPath(Episode ep)
    {
        if (!_cfg.TryResolveOutDir(out string root, out string dirErr))
            throw new IOException(dirErr + "。请改成可写的目录，或插回对应磁盘后再下。");
        string rawSeries = string.IsNullOrEmpty(ep.SeriesTitle) ? ep.SeriesId : ep.SeriesTitle;
        string series = Util.SafeName(rawSeries, 60);
        // ★ 去掉标题里与剧名重复的前缀 ——
        //   否则会得到「《生物史前》.001.《生物史前》 P1.mp4」这种重复两遍的名字。
        //   去重后为空（标题就是剧名本身）→ 成品名不带集名部分。
        string rawTitle = Util.DedupTitle(rawSeries, ep.Title);
        string name = string.IsNullOrEmpty(rawTitle)
            ? $"{series}.{ep.Index:000}.mp4"
            : $"{series}.{ep.Index:000}.{Util.SafeName(rawTitle, 80)}.mp4";
        return Path.Combine(root, series, name);
    }

    // ---------------------------------------------------------- 收藏夹维护

    /// <summary>把指定条目从收藏夹移除。</summary>
    public async Task<(int Ok, List<string> Errors)> RemoveFromShelfAsync(
        List<string> seriesIds, CancellationToken ct = default)
    {
        var rows = await Shelf.ListAsync(_account.CookieHeader, _account.UserId, ct);
        var btype = rows.ToDictionary(r => r.SeriesId, r => r.BookType);
        int ok = 0;
        var errors = new List<string>();

        foreach (string sid in seriesIds)
        {
            int bt = btype.TryGetValue(sid, out var b) ? b : 2;
            string body = Shelf.BuildUpdateBody(sid, bt, remove: true);
            try
            {
                using var doc = await Client.PostAsync(
                    Shelf.PathUpdate, body, ct: ct);
                if (doc != null && J.Int(doc.RootElement, "code") == 0) ok++;
                else errors.Add($"{sid}: 返回 code={J.Int(doc?.RootElement ?? default, "code")}");
            }
            catch (Exception e) { errors.Add($"{sid}: {e.Message}"); }
            await Task.Delay(800, ct);
        }
        return (ok, errors);
    }
}
