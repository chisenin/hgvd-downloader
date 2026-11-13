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

    public HongguoService(AppConfig cfg = null)
    {
        _cfg = cfg ?? AppConfig.Load();
        _account = Account.Load();
    }

    public AppConfig Config => _cfg;

    public bool LoggedIn => _account.Ok;

    public Account Account => _account;

    public void SetAccount(Account acc)
    {
        _account = acc;
        _client = null;
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
                _ident = MumuAuth.LoadDevice();
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
    ///   → 参考量级：
    ///     · 收藏夹原始列表：      **0.44s**（只给 id，不给标题/集数）
    ///     · 批量标题（20 个/批）：串行 12 批 6.4s，**并发 4 路 0.3s/4 批**
    ///     · 单个合集 album_detail：**0.2s**
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
    /// ★ 实测：批量接口**不会**给全部标题
    ///   （全是 <c>book_type=3</c> 的合集）—— 这些在界面上就显示成一串
    ///   <c>series_id</c> 数字。
    ///   → 用 <c>album_detail</c> 单独查，约 **0.2s/条**，绝大多数能拿到标题
    ///   （极少数是真的已下架，服务端什么都不返回）。
    /// ★ 这个方法是**后台跑**的（不等它），因为它只影响少数几行，不该拖慢整个列表。
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
            // ★ 批量详情不是播放接口，风控轻；但也别用播放那套 3.2s 起步的退避，
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
    ///      但 222 个合集要 222 次请求，慢且容易被限流。
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
    /// ★★ 服务端对 <c>video_model</c> 的风控是**账号级**的（文档 X6）：
    ///   密集请求会持续返回 HTTP 200 + 空 body，退避 60~120s 也未必恢复。
    ///   ⇒ 同一集**只打一次**播放接口，之后从缓存取。
    ///   缓存键：<c>videoId</c> + <c>videoIdType</c> + <c>quality</c>。
    /// </summary>
    private static readonly Dictionary<string, (string Url, long Size, string Key, string Codec)>
        PlaybackCache = new();

    /// <summary>清空播放地址缓存（换账号 / 签名失败时可手动调用）。</summary>
    public static void ClearPlaybackCache() => PlaybackCache.Clear();

    // ---------------------------------------------------------- 播放接口熔断

    /// <summary>
    /// ★ 播放接口熔断：连续 <see cref="PlaybackEmptyLimit"/> 次拿到空 body
    ///   就**短路**掉后续所有播放请求。
    ///
    /// ★★ 为什么需要：被风控后，一次 <c>video_model</c> 请求要退避重试 5 次
    ///   （3.2+6.4+9.6+12+12 ≈ 43s），一集还要试两个 <c>video_id_type</c>。
    ///   批量下载几百部剧时，会在退避里空转几十分钟 —— 用户看到的是「卡住了」。
    ///   熔断后**立刻**失败并提示改用 App 缓存路径。
    ///
    /// ★ 阈值取 2：一次下载会依次试两个 video_id_type，
    ///   两个都空 = 不是参数错，是风控。
    /// </summary>
    private const int PlaybackEmptyLimit = 2;
    private static int _playbackEmptyStreak;
    private static bool _playbackBlocked;

    /// <summary>播放接口是否已被熔断（服务端在风控）。</summary>
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

    /// <summary>解析完整正片的播放地址（带缓存）。</summary>
    public async Task<(string Url, long Size, string Key, string Codec)>
        ResolveFullUrlAsync(string videoId, int videoIdType = 0, string quality = "720p",
        CancellationToken ct = default)
    {
        if (!LoggedIn) return ("", 0, "", "");
        if (PlaybackBlocked) return ("", 0, "", "");

        string ck = $"{videoId}|{videoIdType}|{quality}";
        lock (PlaybackCache)
            if (PlaybackCache.TryGetValue(ck, out var hit) && !string.IsNullOrEmpty(hit.Url))
                return hit;

        try
        {
            var model = await Client.PlaybackAsync(videoId, videoIdType, ct);
            if (model.Streams.Count == 0) { NotePlaybackEmpty(); return ("", 0, "", ""); }
            var chosen = model.Streams.FirstOrDefault(
                s => string.Equals(s.Definition, quality, StringComparison.OrdinalIgnoreCase))
                ?? model.Streams[0];
            var result = (chosen.Url, chosen.Size, model.ContentKey, chosen.Codec);
            lock (PlaybackCache) PlaybackCache[ck] = result;
            NotePlaybackOk();
            return result;
        }
        catch (Exception e)
        {
            if (Environment.GetEnvironmentVariable("HGVD_DEBUG") == "1")
                Console.WriteLine($"[dbg] 播放地址失败：{e}");
            NotePlaybackEmpty();
            return ("", 0, "", "");
        }
    }

    /// <summary>下载一集。</summary>
    public async Task<DownloadResult> DownloadEpisodeAsync(
        Episode ep, string quality = null, DownloadCallbacks cb = null,
        CancellationToken ct = default)
    {
        quality ??= _cfg.Quality;
        string outPath = BuildOutPath(ep);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        if (_cfg.SkipExisting && File.Exists(outPath))
        {
            double d = Util.ProbeDuration(outPath);
            if (d > 0 && (ep.Duration <= 0 || Math.Abs(d - ep.Duration) <= Math.Max(3, ep.Duration * 0.02)))
            {
                return new DownloadResult
                {
                    Ok = true, OutPath = outPath, Size = new FileInfo(outPath).Length,
                    Duration = d, Skipped = true,
                };
            }
        }

        // ★ video_id_type：短剧 0（vid），教学内容 1（series_id）。失败自动回退。
        int[] types = ep.VideoIdType >= 0
            ? new[] { ep.VideoIdType, 1 - ep.VideoIdType }
            : new[] { 0, 1 };

        string url = "", key = "", codec = "";
        long size = 0;
        foreach (int t in types)
        {
            (url, size, key, codec) = await ResolveFullUrlAsync(ep.Vid, t, quality, ct);
            if (!string.IsNullOrEmpty(url)) { ep.VideoIdType = t; break; }
            // ★ 已被风控熔断 → 第二个 video_id_type 同样打不通，别再空转 43s
            if (PlaybackBlocked) break;
        }

        if (string.IsNullOrEmpty(url))
            return new DownloadResult
            {
                Ok = false, OutPath = outPath,
                Error = $"无法解析播放地址（vid={ep.Vid}）"
                    + (LoggedIn ? "" : "（未登录：请先执行 hgvd login）")
                    // ★ 服务端对 video_model 的风控是账号级的（文档 X6）——
                    //   密集请求后持续返回 HTTP 200 + 空 body，退避也不一定恢复。
                    //   这时唯一的出片路径是 App 缓存。
                    + "。★ 若日志显示「空 body (HTTP 200)」，是服务端风控，"
                    + "请改用「提取App缓存」（hgvd cache）——完全绕过风控。",
            };

        var req = new DownloadRequest
        {
            Url = url,
            OutPath = outPath,
            ExpectDuration = ep.Duration,
            Cookie = _account.CookieHeader,
            ContentKey = key,
        };
        return await Downloader.DownloadAsync(req, cb, ct);
    }

    /// <summary>构造成品路径：<c>下载目录\剧名\剧名.001.标题.mp4</c>。</summary>
    public string BuildOutPath(Episode ep)
    {
        string root = _cfg.ResolveOutDir();
        string series = Util.SafeName(
            string.IsNullOrEmpty(ep.SeriesTitle) ? ep.SeriesId : ep.SeriesTitle, 60);
        string title = Util.SafeName(ep.Title, 80);
        string name = $"{series}.{ep.Index:000}.{title}.mp4";
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
