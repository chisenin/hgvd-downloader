using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Hgvd.Core.Sign;

namespace Hgvd.Core.Api;

/// <summary>
/// 请求失败的**种类**。
///
/// ★★ 为什么要分这么细：
///   过去所有失败都被笼统报成「服务端风控」，但它们其实是三件完全不同的事：
///     <see cref="EmptyBody"/>  —— HTTP 200 + 零字节响应。服务端把请求**丢弃**了
///                              （真限流 / 请求不被接受）。**只有这一种**才算限流。
///     <see cref="ErrorCode"/>  —— 服务端回了 JSON，但 code != 0。
///                              code=101000 通常是 **video_id_type 用错**（参数问题），
///                              重试无用，换参数才是正解。
///     <see cref="Network"/>    —— 连接/超时/解析失败，是网络问题。
///   把它们混为一谈会导致：参数错被当成风控 → 触发熔断 → 整批提前放弃。
/// </summary>
public enum ApiFailure
{
    /// <summary>空 body（服务端丢弃）—— 这才是限流。</summary>
    EmptyBody,
    /// <summary>服务端回了错误码。</summary>
    ErrorCode,
    /// <summary>网络 / 解析异常。</summary>
    Network,
}

/// <summary>带「失败种类」与「服务端错误码」的异常。</summary>
public sealed class ApiException : Exception
{
    public string Path { get; }
    public ApiFailure Kind { get; }
    /// <summary>服务端的业务错误码（0 表示没有）。</summary>
    public int Code { get; }
    /// <summary>服务端 message（可能为空）。</summary>
    public string ApiMessage { get; }

    public ApiException(string path, ApiFailure kind, int code, string message)
        : base($"{path} 失败（{Describe(kind, code, message)}）")
    {
        Path = path;
        Kind = kind;
        Code = code;
        ApiMessage = message ?? "";
    }

    /// <summary>把失败种类翻译成人话。</summary>
    public static string Describe(ApiFailure kind, int code, string message)
    {
        return kind switch
        {
            ApiFailure.EmptyBody =>
                "空 body（HTTP 200 但零字节）—— 服务端把请求丢弃了，" +
                "通常是**请求节奏过快被限流**。过几分钟再试即可恢复",
            ApiFailure.ErrorCode => code == 101000
                ? $"服务端返回 code=101000 服务异常 —— " +
                  $"**多半是 video_id_type 用错了**（教学类内容要用 1、短剧用 0），" +
                  $"不是风控"
                : $"服务端返回 code={code} {message}",
            _ => $"网络/解析异常：{message}",
        };
    }
}

/// <summary>
/// App 网关的带签名客户端。
///
/// ★ 端点：
///   - <c>/novel/player/album_detail/v1/</c>        合集分集（book_type=3 的唯一来源）
///   - <c>/novel/player/video_detail/v1/</c>       单集 vid
///   - <c>/novel/player/multi_video_detail/preload/v1</c>  短剧分集
///   - <c>/novel/player/video_model/v1/</c>        播放地址
/// ★ 服务端对密集请求会返回 **HTTP 200 + 空 body**（风控），必须有指数退避重试。
/// </summary>
public sealed class SignedClient
{
    public const string Host = "api5-normal-sinfonlinea.fqnovel.com";

    /// <summary>
    /// ★ 实际使用的网关地址。可用环境变量 <c>HGVD_HOST</c> 覆盖
    /// （诊断用：指向本地抓包代理，拿到 C# 真实发出的请求字节，
    ///   与 Python 版逐字节对比）。
    /// ★ 必须**整段**给（含 scheme），否则 URL 拼出来不合法。
    /// </summary>
    public static string BaseUrl
    {
        get
        {
            string v = Environment.GetEnvironmentVariable("HGVD_HOST");
            return string.IsNullOrWhiteSpace(v)
                ? "https://" + Host
                : v.TrimEnd('/');
        }
    }

    public const string PathAlbum = "/novel/player/album_detail/v1/";
    public const string PathVideoDetail = "/novel/player/video_detail/v1/";
    public const string PathDetailBatch = "/novel/player/multi_video_detail/preload/v1";
    public const string PathPlayback = "/novel/player/video_model/v1/";

    private readonly DeviceIdentity _ident;
    private readonly string _cookie;

    public SignedClient(DeviceIdentity ident, string cookie)
    {
        _ident = ident;
        _cookie = cookie;
    }

    /// <summary>构造设备参数对（顺序与 App 一致）。</summary>
    public List<KeyValuePair<string, string>> DevicePairs() => new()
    {
        new("iid", _ident.Iid),
        new("device_id", _ident.DeviceId),
        new("ac", "wifi"),
        new("channel", "xiaomi_8662_64"),
        new("aid", "8662"),
        new("app_name", "novelread"),
        new("cdid", _ident.Cdid),
        new("version_code", _ident.VersionCode),
        new("version_name", _ident.VersionName),
        new("device_platform", "android"),
        new("os", "android"),
        new("ssmix", "a"),
        new("device_type", "Xiaomi 14"),
        new("device_brand", "Redmi"),
        new("language", "zh"),
        new("os_api", "34"),
        new("os_version", "14"),
        new("manifest_version_code", _ident.VersionCode),
        new("resolution", "1080*2400"),
        new("dpi", "480"),
        new("update_version_code", _ident.VersionCode),
        new("host_abi", "arm64-v8a"),
        new("dragon_device_type", "phone"),
        new("pv_player", _ident.VersionCode),
        new("compliance_status", "0"),
        new("need_personal_recommend", "1"),
        new("player_so_load", "1"),
        new("is_android_pad_screen", "0"),
        new("openudid", _ident.Openudid),
    };

    /// <summary>带签名地 POST 一个 JSON 请求。失败返回 null。</summary>
    public async Task<JsonDocument?> PostAsync(
        string path, string jsonBody,
        bool playback = false,
        int retries = 5, double basePause = 3.2, double maxPause = 12.0,
        CancellationToken ct = default)
    {
        byte[] raw = Encoding.UTF8.GetBytes(jsonBody);
        string last = "未知";

        for (int attempt = 0; attempt < retries; attempt++)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var (query, headers) = playback
                ? Signer.SignPlayback(path, DevicePairs(), now, _ident.DeviceId,
                    _ident.VersionName, raw, installId: _ident.Iid, aid: "8662")
                : Signer.SignRequest(path, DevicePairs(), now, _ident.DeviceId,
                    _ident.VersionName, raw);

            try
            {
                using var req = new HttpRequestMessage(
                    HttpMethod.Post, $"{BaseUrl}{path}?{query}")
                {
                    Content = new ByteArrayContent(raw),
                };
                req.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
                    { CharSet = "UTF-8" };
                // ★★ 不要在这里手动加 x-xs-from-web 等签名头 ——
                //   TryAddWithoutValidation 对已存在的 key 会**追加**第二个值，
                //   变成 "0, 0"，服务端就报
                //   「IsFromWeb: invalid request value, expected bool, actual value: 0, 0」。
                //   这些头统一由下面的 headers 循环提供。
                req.Headers.TryAddWithoutValidation("X-SS-DP", "8662");
                if (!string.IsNullOrEmpty(_cookie))
                    req.Headers.TryAddWithoutValidation("Cookie", _cookie);
                foreach (var kv in headers)
                    req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);

                using var resp = await Net.Client.SendAsync(req, ct);
                string text = await resp.Content.ReadAsStringAsync(ct);
                if (string.IsNullOrWhiteSpace(text))
                {
                    last = $"空 body (HTTP {(int)resp.StatusCode})";
                    await Task.Delay(TimeSpan.FromSeconds(
                        Math.Min(basePause * (attempt + 1), maxPause)), ct);
                    continue;
                }
                var doc = JsonDocument.Parse(text);
                int code = J.Int(doc.RootElement, "code");
                if (Environment.GetEnvironmentVariable("HGVD_DEBUG") == "1")
                {
                    var dbg = doc.RootElement.ToString();
                    if (dbg.Length > 600) dbg = dbg[..600] + "…";
                    Console.WriteLine($"[dbg] {path} → {dbg}");
                }
                if (code == 0) return doc;
                last = $"code={code} {J.Str(doc.RootElement, "message")}";
                // 100001 = 参数错，重试没用
                if (code == 100001) break;
                // ★★ 播放接口：拿到 **JSON**（哪怕 code != 0）说明签名是通的 ——
                //   空 body 才是「被服务端丢弃/限流」的信号。
                //   code=101000 多半是 **video_id_type 用错了**（教学类内容要用 1），
                //   这时重试 5 次纯属浪费（3.2+6.4+9.6+12+12 ≈ 43 秒），
                //   而且会把请求密度推高、反而更容易触发限流。
                //   ⇒ 直接把 doc 交回调用方，由它换另一种 video_id_type 再试。
                if (playback)
                {
                    throw new ApiException(path, ApiFailure.ErrorCode, code,
                        J.Str(doc.RootElement, "message"));
                }
                // ★ 服务端对 video_model 的限流是「全局节奏」——
                //   实测约每 3 秒才放行一次，同一 id 连续请求时
                //   前 3 次空 body、第 4 次才成功。
                //   所以这里固定 3.2s 起步，不要太短。
                await Task.Delay(TimeSpan.FromSeconds(
                    Math.Min(basePause * (attempt + 1), maxPause)), ct);
            }
            catch (ApiException) { throw; }
            catch (Exception e)
            {
                last = e.Message;
                await Task.Delay(TimeSpan.FromSeconds(
                    Math.Min(basePause * (attempt + 1), maxPause)), ct);
            }
        }
        throw new ApiException(path, ApiFailure.EmptyBody, 0, last);
    }

    // ---------------------------------------------------------------- 合集

    /// <summary>取合集详情（含完整分集列表）。</summary>
    public async Task<AlbumInfo?> AlbumAsync(string albumId, int bookType = 3,
        int retries = 5, double basePause = 3.2, double maxPause = 12.0,
        CancellationToken ct = default)
    {
        string body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["album_id"] = albumId,
            ["book_type"] = bookType,
        });
        using var doc = await PostAsync(PathAlbum, body, ct: ct,
            retries: retries, basePause: basePause, maxPause: maxPause);
        if (doc is null) return null;
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("album_data", out var ad))
            return null;

        var info = new AlbumInfo
        {
            AlbumId = J.Str(ad, "album_id_str"),
            Title = J.Str(ad, "title"),
            Intro = J.Str(ad, "intro"),
            Cover = J.Str(ad, "cover"),
            EpisodeCnt = J.Int(ad, "episode_cnt"),
            FollowCnt = J.Int(ad, "followed_cnt"),
        };
        if (ad.TryGetProperty("episode_entrance_text", out var ent)
            && ent.ValueKind == JsonValueKind.Array)
            info.EntranceText = ent.EnumerateArray()
                .Select(x => x.ToString()).ToList();

        if (ad.TryGetProperty("video_detail_list", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string sid = J.Str(item, "series_id_str");
                if (string.IsNullOrEmpty(sid)) sid = J.Str(item, "series_id");
                if (string.IsNullOrEmpty(sid)) continue;
                info.Episodes.Add(new Episode
                {
                    Vid = sid,
                    Index = info.Episodes.Count + 1,
                    Title = J.Str(item, "series_title"),
                    Duration = J.Dbl(item, "duration"),
                    SeriesId = albumId,
                    SeriesTitle = info.Title,
                    VideoIdType = 1,   // ★ 合集分集用 series_id 查播放地址
                });
            }
        }
        return info;
    }

    // ---------------------------------------------------------------- 分集

    /// <summary>用 preload 端点取分集（短剧 / 单个视频用）。</summary>
    public async Task<List<Episode>> DetailAsync(string seriesId,
        CancellationToken ct = default)
    {
        string body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["dr_scene"] = "preload",
            ["series_id"] = seriesId,
            ["biz_param"] = new Dictionary<string, object>
            {
                ["detail_page_version"] = 0,
                ["disable_digg_stat"] = false,
                ["source"] = 7,
                ["need_all_video_definition"] = false,
            },
        });
        using var doc = await PostAsync(PathDetailBatch, body, ct: ct);
        var outEp = new List<Episode>();
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var data))
            return outEp;

        foreach (var item in data.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.Object) continue;
            if (!item.Value.TryGetProperty("video_data", out var vd)) continue;
            string title = J.Str(vd, "series_title");
            if (!vd.TryGetProperty("video_list", out var vl)
                || vl.ValueKind != JsonValueKind.Array) continue;
            foreach (var e in vl.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                string vid = J.Str(e, "vid");
                if (string.IsNullOrEmpty(vid)) continue;
                outEp.Add(new Episode
                {
                    Vid = vid,
                    Index = outEp.Count + 1,
                    Title = J.Str(e, "title"),
                    Duration = J.Dbl(e, "duration"),
                    SeriesId = seriesId,
                    SeriesTitle = title,
                    // ★ vid == series_id 时用 type=1，否则用 type=0
                    VideoIdType = vid == seriesId ? 1 : 0,
                });
            }
            if (outEp.Count > 0) break;
        }
        return outEp;
    }

    /// <summary>取合集分集；取不到就回落到 preload。</summary>
    public async Task<List<Episode>> EpisodesAsync(string seriesId, int bookType = 3,
        CancellationToken ct = default)
    {
        try
        {
            var album = await AlbumAsync(seriesId, bookType, ct: ct);
            if (album != null && album.Episodes.Count > 0) return album.Episodes;
        }
        catch { /* 回落 */ }
        return await DetailAsync(seriesId, ct);
    }

    // ---------------------------------------------------------------- 播放

    /// <summary>取播放模型（各清晰度的直链 + 加密信息）。</summary>
    public async Task<PlaybackModel> PlaybackAsync(string videoId, int videoIdType = 0,
        CancellationToken ct = default)
    {
        string body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["biz_param"] = new Dictionary<string, object>
            {
                ["need_all_video_definition"] = true,
                ["video_id_type"] = videoIdType,
                ["video_platform"] = 3,
            },
            ["video_id"] = videoId,
        });
        // ★ 必须用**播放专用签名**（真 argus/ladon、无 medusa）
        using var doc = await PostAsync(PathPlayback, body, playback: true, ct: ct);
        return PlaybackModel.Parse(doc!);
    }
}

/// <summary>合集信息。</summary>
public sealed class AlbumInfo
{
    public string AlbumId = "";
    public string Title = "";
    public string Intro = "";
    public string Cover = "";
    public int EpisodeCnt;
    public int FollowCnt;
    public List<string> EntranceText = new();
    public List<Episode> Episodes = new();
}

/// <summary>一条清晰度流。</summary>
public sealed class Stream
{
    public string Url = "";
    public string BackupUrl = "";
    public string Definition = "";
    public string Codec = "";
    public string VType = "";
    public int Width;
    public int Height;
    public long Bitrate;
    public long Size;
    public double Duration;
    public bool Encrypted;
    public string SpadeA = "";
    public string Kid = "";
}

/// <summary>播放模型。</summary>
public sealed class PlaybackModel
{
    public List<Stream> Streams = new();
    public string ContentKey = "";

    public static PlaybackModel Parse(JsonDocument doc)
    {
        var model = new PlaybackModel();
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data)) return model;

        // ★ data.video_model 是一个**嵌套的 JSON 字符串**
        string raw = J.Str(data, "video_model");
        if (string.IsNullOrEmpty(raw)) return model;

        JsonElement vm;
        try
        {
            using var inner = JsonDocument.Parse(raw);
            vm = inner.RootElement.Clone();
        }
        catch { return model; }

        double duration = J.Dbl(vm, "video_duration");

        // ★ video_list 也可能是 JSON 字符串（双层嵌套）
        JsonElement list = vm;
        if (vm.TryGetProperty("video_list", out var vl0))
        {
            if (vl0.ValueKind == JsonValueKind.String)
            {
                try
                {
                    using var l2 = JsonDocument.Parse(vl0.GetString()!);
                    list = l2.RootElement.Clone();
                }
                catch { }
            }
            else list = vl0;
        }

        if (list.ValueKind != JsonValueKind.Array) return model;

        string spade = "";
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string url = J.Str(item, "main_url");
            if (string.IsNullOrEmpty(url)) url = J.Str(item, "backup_url");
            if (string.IsNullOrEmpty(url)) continue;

            var meta = J.Get(item, "video_meta");
            var enc = J.Get(item, "encrypt_info");

            var st = new Stream
            {
                Url = url,
                BackupUrl = J.Str(item, "backup_url"),
                Definition = meta is null ? "" : J.Str(meta.Value, "definition"),
                Codec = meta is null ? "" : J.Str(meta.Value, "codec_type"),
                VType = meta is null ? "" : J.Str(meta.Value, "vtype"),
                Width = meta is null ? 0 : J.Int(meta.Value, "vwidth"),
                Height = meta is null ? 0 : J.Int(meta.Value, "vheight"),
                Bitrate = meta is null ? 0 : J.Int(meta.Value, "bitrate"),
                Size = meta is null ? 0 : J.Int(meta.Value, "size"),
                Duration = duration,
            };
            if (enc is not null && enc.Value.ValueKind == JsonValueKind.Object)
            {
                st.Encrypted = J.Str(enc.Value, "encrypt") is "true" or "1";
                st.SpadeA = J.Str(enc.Value, "spade_a");
                st.Kid = J.Str(enc.Value, "kid");
                if (!string.IsNullOrEmpty(st.SpadeA)) spade = st.SpadeA;
            }
            model.Streams.Add(st);
        }

        if (!string.IsNullOrEmpty(spade))
        {
            try { model.ContentKey = Drm.DeriveContentKey(spade); }
            catch { }
        }
        return model;
    }
}
