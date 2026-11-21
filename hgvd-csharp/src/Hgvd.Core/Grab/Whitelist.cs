using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hgvd.Core.Api;

namespace Hgvd.Core.Grab;

/// <summary>白名单里的一集。</summary>
public sealed class WhitelistEntry
{
    public string Book = "";
    public int Index;
    public string Title = "";
    public double Duration;
}

/// <summary>
/// 教学合集白名单：vid → (剧名, 序号, 集名, 时长)。
/// </summary>
/// <remarks>
/// ★★ 为什么需要它：抓流能拿到的 preload 响应里**既没有 series_id 也没有内容类型**，
///   光看响应无法判断某个 vid 属于哪门课、第几集。而 `album_detail` 能列出
///   一门课的全部分集（vid/序号/标题/时长），所以先把它展开成白名单，
///   抓流时只认白名单里的 vid —— 这同时解决了「只下教学课」和「正确命名」两件事。
///
/// ★ 与 Python 侧共用同一个文件、同一套字段（`_ts` + `vids`），
///   否则两边各有一份白名单，抓流和缓存会认出不同的集。
/// </remarks>
public static class Whitelist
{
    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".hgv_tool", "grab_whitelist.json");

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>读缓存。文件不存在或坏了都返回空表，不抛。</summary>
    public static (Dictionary<string, WhitelistEntry> Map, double Timestamp) Load()
    {
        var map = new Dictionary<string, WhitelistEntry>();
        double ts = 0;
        try
        {
            if (!File.Exists(CachePath)) return (map, 0);
            using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
            var root = doc.RootElement;
            ts = root.TryGetProperty("_ts", out var t) && t.ValueKind == JsonValueKind.Number
                ? t.GetDouble() : 0;
            if (!root.TryGetProperty("vids", out var vids) || vids.ValueKind != JsonValueKind.Object)
                return (map, ts);

            foreach (var p in vids.EnumerateObject())
            {
                var a = p.Value;
                if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() < 3) continue;
                try
                {
                    map[p.Name] = new WhitelistEntry
                    {
                        Book = a[0].GetString() ?? "",
                        Index = a[1].GetInt32(),
                        Title = a[2].GetString() ?? "",
                        Duration = a.GetArrayLength() > 3 && a[3].ValueKind == JsonValueKind.Number
                            ? a[3].GetDouble() : 0,
                    };
                }
                catch (Exception) { /* 单条坏掉不影响整表 */ }
            }
        }
        catch (Exception) { }
        return (map, ts);
    }

    public static void Save(Dictionary<string, WhitelistEntry> map)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var payload = new Dictionary<string, object>
            {
                ["_ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["vids"] = map.ToDictionary(
                    kv => kv.Key,
                    kv => (object)new object[] { kv.Value.Book, kv.Value.Index, kv.Value.Title, kv.Value.Duration }),
            };
            File.WriteAllText(CachePath, JsonSerializer.Serialize(payload, WriteOptions));
        }
        catch (Exception) { }
    }

    /// <summary>把收藏夹里所有教学合集展开成白名单（album_detail 免强校验，能跑通）。</summary>
    /// <remarks>
    /// ★ 目标口径：白名单 = **收藏夹里的教材/合集类视频**。
    ///   判据用 <c>BookType == 3</c>（教学合集）**并且**排除
    ///   <see cref="ShelfItem.IsShortDrama"/>（<c>book_type==2 || content_type==1004</c>）。
    ///   ★ 两个条件都要：只判 book_type==3 时，个别 <c>content_type==1004</c> 的短剧
    ///     会被混进白名单 —— 短剧是 bytevc 加密，下载下来解不出画面。
    /// ★★ 重建必须**整表替换**：历史实现是增量合并，于是「收藏夹外教学内容」
    ///   （旧版「详情响应认出 content_type=1003」那条来源带进来的）永远清不掉。
    ///   口径是「只下收藏夹里的」⇒ 白名单只能是收藏夹教学合集的投影。
    /// </remarks>
    public static async Task<int> BuildAsync(HongguoService svc, Action<string> log,
                                            CancellationToken ct = default)
    {
        log ??= _ => { };

        var items = await svc.ListShelfAsync(withDetails: true, albumLimit: 0, ct: ct);
        var albums = items.Where(i => i.BookType == 3 && !i.IsShortDrama).ToList();
        int dramas = items.Count - items.Count(i => !i.IsShortDrama);
        if (albums.Count == 0)
        {
            log("· 收藏夹里没有教学合集");
            return 0;
        }

        log($"· 收藏夹 {items.Count} 部，其中教学合集 {albums.Count} 部"
            + (dramas > 0 ? $"（短剧 {dramas} 部已排除）" : "") + "，开始建白名单…");
        var fresh = new Dictionary<string, WhitelistEntry>();
        int done = 0;
        foreach (var it in albums)
        {
            ct.ThrowIfCancellationRequested();
            List<Episode> eps;
            try
            {
                eps = await svc.ListEpisodesAsync(it.SeriesId, 3, ct);
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var e in eps)
            {
                if (string.IsNullOrEmpty(e.Vid)) continue;
                fresh[e.Vid] = new WhitelistEntry
                {
                    Book = string.IsNullOrEmpty(e.SeriesTitle) ? it.Title : e.SeriesTitle,
                    Index = e.Index,
                    Title = e.Title,
                    Duration = e.Duration,
                };
            }
            done++;
            if (done % 20 == 0) log($"· 白名单 {done}/{albums.Count} 部，累计 {fresh.Count} 集");
        }

        Save(fresh);
        log($"＋ 白名单就绪：{fresh.Count} 集（{CachePath}）");
        return fresh.Count;
    }
}
