using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    /// <summary>读收藏夹。</summary>
    public async Task<List<ShelfItem>> ListShelfAsync(
        bool withDetails = true, int albumLimit = 30,
        CancellationToken ct = default)
    {
        var rows = await Shelf.ListAsync(_account.CookieHeader,
            _account.UserId, ct);

        var titles = new Dictionary<string, (string Title, int Cnt)>();
        if (withDetails && rows.Count > 0)
        {
            try { titles = await FetchTitlesAsync(rows, albumLimit, ct); }
            catch { }
        }

        var items = new List<ShelfItem>();
        foreach (var r in rows)
        {
            titles.TryGetValue(r.SeriesId, out var t);
            items.Add(new ShelfItem
            {
                SeriesId = r.SeriesId,
                Title = t.Title,
                EpisodeCnt = t.Cnt,
                BookType = r.BookType,
                ContentType = r.ContentType,
            });
        }
        return items;
    }

    /// <summary>
    /// 批量取标题与集数。
    ///
    /// ★ 两路并行：
    ///   1. <c>multi_video_detail/preload/v1</c> —— **一次 20 个**就能拿到
    ///      所有条目的标题（合集也返回 <c>series_title</c>），但 <c>episode_cnt</c> 是 0。
    ///   2. <c>album_detail/v1/</c> —— 逐个查，**才有真实的集数**，
    ///      但 222 个合集要 222 次请求，慢且容易被限流。
    ///   所以：**先用第 1 路把标题全补齐**，第 2 路只查前 <c>albumLimit</c> 个。
    /// </summary>
    private async Task<Dictionary<string, (string, int)>> FetchTitlesAsync(
        List<ShelfEntry> rows, int albumLimit, CancellationToken ct)
    {
        var outMap = new Dictionary<string, (string, int)>();

        // ---- 第 1 路：批量拿标题（全部条目，一次 20 个） ----
        var allIds = rows.Select(r => r.SeriesId).ToList();
        for (int i = 0; i < allIds.Count; i += 20)
        {
            var chunk = allIds.Skip(i).Take(20);
            string body = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, object>
                {
                    ["dr_scene"] = "preload",
                    ["series_id"] = string.Join(",", chunk),
                    ["biz_param"] = new Dictionary<string, object>
                    {
                        ["detail_page_version"] = 0,
                        ["disable_digg_stat"] = false,
                        ["source"] = 7,
                        ["need_all_video_definition"] = false,
                    },
                });
            try
            {
                using var doc = await Client.PostAsync(
                    SignedClient.PathDetailBatch, body, ct: ct);
                if (doc is null) continue;
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var prop in data.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                        if (!prop.Value.TryGetProperty("video_data", out var vd)) continue;
                        string title = J.Str(vd, "series_title");
                        int cnt = J.Int(vd, "episode_cnt");
                        if (cnt == 0 && vd.TryGetProperty("video_list", out var vl)
                            && vl.ValueKind == System.Text.Json.JsonValueKind.Array)
                            cnt = vl.GetArrayLength();
                        if (!string.IsNullOrEmpty(title))
                            outMap[prop.Name] = (title, cnt);
                    }
                }
            }
            catch { }
            await Task.Delay(350, ct);
        }

        // ---- 第 2 路：给前 albumLimit 个合集补真实集数 ----
        var albums = rows.Where(r => r.BookType == 3).Select(r => r.SeriesId).ToList();
        if (albumLimit > 0) albums = albums.Take(albumLimit).ToList();
        foreach (var id in albums)
        {
            try
            {
                var album = await Client.AlbumAsync(id, 3, ct);
                if (album == null) continue;
                int cnt = album.EpisodeCnt > 0 ? album.EpisodeCnt : album.Episodes.Count;
                outMap[id] = (album.Title, cnt);
            }
            catch { }
            await Task.Delay(600, ct);
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

    /// <summary>解析完整正片的播放地址（带缓存）。</summary>
    public async Task<(string Url, long Size, string Key, string Codec)>
        ResolveFullUrlAsync(string videoId, int videoIdType = 0, string quality = "720p",
        CancellationToken ct = default)
    {
        if (!LoggedIn) return ("", 0, "", "");

        string ck = $"{videoId}|{videoIdType}|{quality}";
        lock (PlaybackCache)
            if (PlaybackCache.TryGetValue(ck, out var hit) && !string.IsNullOrEmpty(hit.Url))
                return hit;

        try
        {
            var model = await Client.PlaybackAsync(videoId, videoIdType, ct);
            if (model.Streams.Count == 0) return ("", 0, "", "");
            var chosen = model.Streams.FirstOrDefault(
                s => string.Equals(s.Definition, quality, StringComparison.OrdinalIgnoreCase))
                ?? model.Streams[0];
            var result = (chosen.Url, chosen.Size, model.ContentKey, chosen.Codec);
            lock (PlaybackCache) PlaybackCache[ck] = result;
            return result;
        }
        catch (Exception e)
        {
            if (Environment.GetEnvironmentVariable("HGVD_DEBUG") == "1")
                Console.WriteLine($"[dbg] 播放地址失败：{e}");
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
