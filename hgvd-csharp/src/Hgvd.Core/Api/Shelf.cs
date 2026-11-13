using System.Threading;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Hgvd.Core.Sign;

namespace Hgvd.Core.Api;

/// <summary>JSON 取值的几个小工具（对应 Python 版的 util.py）。</summary>
public static class J
{
    public static JsonElement? Get(JsonElement el, string name)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        return el.TryGetProperty(name, out var v) ? v : null;
    }

    public static string Str(JsonElement el, string name)
    {
        var v = Get(el, name);
        if (v is null || v.Value.ValueKind == JsonValueKind.Null) return "";
        return v.Value.ValueKind == JsonValueKind.String
            ? v.Value.GetString() ?? ""
            : v.Value.ToString();
    }

    public static int Int(JsonElement el, string name)
    {
        var v = Get(el, name);
        if (v is null) return 0;
        return v.Value.ValueKind switch
        {
            JsonValueKind.Number => v.Value.TryGetInt32(out var i) ? i : 0,
            JsonValueKind.String => int.TryParse(v.Value.GetString(), out var s) ? s : 0,
            _ => 0,
        };
    }

    public static double Dbl(JsonElement el, string name)
    {
        var v = Get(el, name);
        if (v is null) return 0;
        return v.Value.ValueKind switch
        {
            JsonValueKind.Number => v.Value.TryGetDouble(out var d) ? d : 0,
            JsonValueKind.String => double.TryParse(v.Value.GetString(), out var d) ? d : 0,
            _ => 0,
        };
    }
}

/// <summary>收藏夹条目。</summary>
public sealed class ShelfEntry
{
    public string SeriesId = "";
    public int BookType;
    public int ContentType;
    public long CollectTime;
    public string VideoCategoryType = "";

    /// <summary>是不是**真短剧**（bytevc 加密，ffmpeg 解不出画面）。</summary>
    public bool IsShortDrama => BookType == 2 || ContentType == 1004;

    public string TypeLabel => IsShortDrama ? "短剧"
        : BookType == 4 ? "单个视频"
        : BookType == 3 ? "教学合集" : "其他";
}

/// <summary>一集。</summary>
public sealed class Episode
{
    public string Vid = "";
    public int Index;
    public string Title = "";
    public double Duration;
    public string SeriesId = "";
    public string SeriesTitle = "";
    /// <summary>0 = 用 vid 查播放地址（短剧），1 = 用 series_id 查（教学内容）。</summary>
    public int VideoIdType = -1;
}

/// <summary>收藏夹接口（**免签名**，只要 Cookie）。</summary>
public static class Shelf
{
    public const string PathList = "/reading/bookapi/bookshelf/video/list/v";
    public const string PathInfo = "/reading/bookapi/bookshelf/video/info/v1/";
    public const string PathUpdate = "/reading/bookapi/bookshelf/video/update/v1/";

    private static readonly string[] Hosts =
    {
        "api5-normal-sinfonlinea.fqnovel.com",
        "api5-normal-lq.fqnovel.com",
        "reading.snssdk.com",
        "api5-normal-sinfonlineb.fqnovel.com",
        "fanqienovel.com",
    };

    private static readonly KeyValuePair<string, string>[] DeviceFields =
    {
        new("iid", "0"),
        new("device_id", "0"),
        new("ac", "wifi"),
        new("channel", "update_64"),
        new("aid", "8662"),
        new("app_name", "novelread"),
        new("cdid", "0"),
        new("version_code", "73532"),
        new("version_name", "7.3.5.32"),
        new("device_platform", "android"),
        new("os", "android"),
        new("ssmix", "a"),
        new("device_type", "Xiaomi 14"),
        new("device_brand", "Redmi"),
        new("language", "zh"),
        new("os_api", "34"),
        new("os_version", "15"),
        new("manifest_version_code", "73532"),
        new("resolution", "1080*2400"),
        new("dpi", "480"),
        new("update_version_code", "73532"),
        new("host_abi", "arm64-v8a"),
        new("dragon_device_type", "phone"),
        new("pv_player", "73532"),
        new("compliance_status", "0"),
        new("need_personal_recommend", "1"),
        new("player_so_load", "1"),
        new("is_android_pad_screen", "0"),
        new("openudid", "0"),
    };

    public static string BuildQuery(IEnumerable<KeyValuePair<string, string>> extra)
    {
        var pairs = new List<KeyValuePair<string, string>>(DeviceFields);
        if (extra != null) pairs.AddRange(extra);
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        pairs.Add(new("ts", ts.ToString()));
        pairs.Add(new("_rticket", (ts * 1000).ToString()));
        return Signer.EncodeQuery(pairs);
    }

    /// <summary>读收藏夹原始条目。</summary>
    public static async Task<List<ShelfEntry>> ListAsync(
        string cookie, string targetUserId = "", CancellationToken ct = default)
    {
        var extra = string.IsNullOrEmpty(targetUserId)
            ? null
            : new List<KeyValuePair<string, string>>
            {
                new("target_user_id", targetUserId),
            };
        string query = BuildQuery(extra);

        string last = "";
        foreach (string host in Hosts)
        {
            try
            {
                using var req = new HttpRequestMessage(
                    HttpMethod.Get, $"https://{host}{PathList}?{query}");
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                req.Headers.TryAddWithoutValidation("X-SS-DP", "8662");
                req.Headers.TryAddWithoutValidation("X-XS-From-Web", "0");
                if (!string.IsNullOrEmpty(cookie))
                    req.Headers.TryAddWithoutValidation("Cookie", cookie);

                using var resp = await Net.Client.SendAsync(req, ct);
                if (resp.StatusCode != System.Net.HttpStatusCode.OK)
                {
                    last = $"{host}: HTTP {(int)resp.StatusCode}";
                    continue;
                }
                string text = await resp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(text)) { last = $"{host}: 空响应"; continue; }

                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (J.Int(root, "code") != 0)
                {
                    last = $"{host}: code={J.Int(root, "code")} {J.Str(root, "message")}";
                    continue;
                }
                var rows = new List<ShelfEntry>();
                if (root.TryGetProperty("data", out var data)
                    && data.TryGetProperty("video_shelf_info", out var arr)
                    && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        var row = new ShelfEntry
                        {
                            SeriesId = J.Str(item, "series_id"),
                            BookType = J.Int(item, "book_type"),
                            ContentType = J.Int(item, "content_type"),
                            CollectTime = J.Int(item, "collect_time"),
                            VideoCategoryType = J.Str(item, "video_category_type"),
                        };
                        if (!string.IsNullOrEmpty(row.SeriesId)) rows.Add(row);
                    }
                }
                return rows;
            }
            catch (Exception e) { last = $"{host}: {e.Message}"; }
        }
        throw new InvalidOperationException($"收藏夹接口全部 host 失败：{last}");
    }

    /// <summary>构造收藏夹更新请求体（0 = 加入，1 = 移除）。</summary>
    public static string BuildUpdateBody(string seriesId, int bookType, bool remove)
    {
        var entry = new Dictionary<string, object>
        {
            ["book_id"] = seriesId,
            ["book_type"] = bookType,
            ["modify_time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["video_shelf_operate_type"] = remove ? 1 : 0,
        };
        if (remove) entry["group_name"] = "";

        var body = new Dictionary<string, object>
        {
            ["is_cancelled"] = false,
            ["update_bookshelf_video_list"] = new[] { entry },
        };
        if (!remove)
        {
            body["shark_extra"] = new Dictionary<string, object>
            {
                ["enter_from"] = "MainFragmentActivity",
                ["previous_page"] = "",
                ["page_list"] = "MainFragmentActivity",
                ["is_active_behavior"] = "true",
                ["inactive_type"] = "0",
            };
        }
        return JsonSerializer.Serialize(body);
    }
}
