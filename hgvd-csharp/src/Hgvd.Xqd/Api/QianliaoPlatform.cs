using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hgvd.Xqd.Api;

/// <summary>
/// 兴趣岛「我的课程」数据源（移植自上游 Xqd.Core.Api.QianliaoPlatform）。
///
/// 三个能力：
///   1. 营列表 —— myCourseListV2（不传 skyType 会带回公开课）
///   2. 课表   —— 正课用 campId，公开课用 periodId（专用端点）
///   3. 播放地址 —— getCoursePlayUrl（按 topicId/roomId + businessId/Type）
///
/// ★ 缓存 key 必须用 campId / periodId，不能用营名：同名营有多个批次，按名字会互相覆盖。
/// ★ 缓存读写加锁，但**不锁网络请求**（否则多营并发会被串行化）。
/// </summary>
public sealed class QianliaoPlatform
{
    public const string PlatformKey = "qianliao";

    public static readonly IReadOnlyList<string> Kinds = new[] { "全部", "正式营", "体验营", "公开课" };

    private static readonly Dictionary<string, string> SkyTypes = new()
    {
        ["正式营"] = "FINANCIAL",
        ["体验营"] = "INTENTION",
    };

    private readonly QianliaoClient _client;
    private readonly Dictionary<string, Camp> _campIndex = new();
    private readonly Dictionary<string, List<Lesson>> _catalogs = new();
    private readonly object _cacheLock = new();

    public QianliaoPlatform(string cookie = "")
    {
        _client = new QianliaoClient(cookie);
    }

    public string Cookie => _client.Cookie;

    // ── 营列表 ──
    public async Task<List<Camp>> ListCampsAsync(string kind = "全部",
                                                CancellationToken ct = default)
    {
        kind ??= "";
        bool onlyOpenCourse = kind == "公开课";
        string sky = SkyTypes.TryGetValue(kind, out var s) ? s : null;

        var td = new Dictionary<string, object>
        {
            ["page"] = new Dictionary<string, object> { ["page"] = 1, ["size"] = 200 },
        };
        if (sky != null) td["skyType"] = sky;

        var data = await _client.MyCourseListV2Async(td, ct).ConfigureAwait(false);
        var camps = new List<Camp>();
        foreach (var c in data.Arr("courses"))
        {
            var camp = ParseCamp(c, kind);
            if (onlyOpenCourse && camp.SkuType != "OPEN_COURSE") continue;
            camps.Add(camp);
            if (!string.IsNullOrEmpty(camp.Name))
            {
                lock (_cacheLock)
                {
                    // 同名时优先保留有 campId 的（付费营），否则公开课会顶掉正课
                    if (!_campIndex.TryGetValue(camp.Name, out var old) ||
                        string.IsNullOrEmpty(old.CampId))
                        _campIndex[camp.Name] = camp;
                }
            }
        }
        return camps;
    }

    /// <summary>
    /// 一次把三个分类都拉下来并按 id 去重（界面只要一份完整营列表）。
    /// ★ 单次「全部」已经包含公开课；这里额外拉正式/体验只是为了拿到稳定的分类字段，
    ///   失败不影响主列表。
    /// </summary>
    public async Task<List<Camp>> ListAllCampsAsync(CancellationToken ct = default)
    {
        var all = await ListCampsAsync("全部", ct).ConfigureAwait(false);

        foreach (string kind in new[] { "正式营", "体验营" })
        {
            try
            {
                var extra = await ListCampsAsync(kind, ct).ConfigureAwait(false);
                foreach (var c in extra)
                {
                    var hit = all.FirstOrDefault(x =>
                        (!string.IsNullOrEmpty(c.CampId) && x.CampId == c.CampId) ||
                        (!string.IsNullOrEmpty(c.PeriodId) && string.IsNullOrEmpty(c.CampId) &&
                         x.PeriodId == c.PeriodId && x.Name == c.Name));
                    if (hit == null) all.Add(c);
                    else if (string.IsNullOrEmpty(hit.Kind)) hit.Kind = c.Kind;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 分类拉取失败不影响主列表 */ }
        }

        // 稳定排序：正式营 → 体验营 → 公开课，同类按名称
        return all
            .GroupBy(c => string.IsNullOrEmpty(c.CampId) ? "p:" + c.PeriodId + c.Name : "c:" + c.CampId)
            .Select(g => g.First())
            .OrderBy(c => KindOrder(c.Kind))
            .ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static int KindOrder(string kind) => kind switch
    {
        "正式营" => 0,
        "体验营" => 1,
        _ => 2,
    };

    private static Camp ParseCamp(JsonElement c, string kind)
    {
        string sku = c.Str("skuType");
        int total = (int)c.Long("courseTotalNum");
        return new Camp
        {
            Name = c.Str("campName"),
            CampId = c.Str("campId"),
            PeriodId = c.Str("periodId"),
            SkuType = sku,
            Kind = string.IsNullOrEmpty(kind) || kind == "全部" ? CampTypeOf(sku) : kind,
            Count = total,
        };
    }

    public static string CampTypeOf(string sku) =>
        sku == "FINANCIAL" ? "正式营" :
        (sku == "LAUNCH" || sku == "INTENTION") ? "体验营" : "公开课";

    // ── 课表 ──
    public async Task<List<Lesson>> CampCatalogAsync(Camp camp, CancellationToken ct = default)
    {
        string name = camp?.Name ?? "";
        var info = camp;

        // 既没 campId 又没 periodId 才需要按名字反查
        if (info != null && string.IsNullOrEmpty(info.CampId) && string.IsNullOrEmpty(info.PeriodId))
            info = await EnsureCampAsync(name, ct).ConfigureAwait(false);

        string campId = info?.CampId ?? "";
        string periodId = info?.PeriodId ?? "";
        string key = !string.IsNullOrEmpty(campId) ? "c:" + campId
                   : !string.IsNullOrEmpty(periodId) ? "p:" + periodId
                   : "n:" + name;

        lock (_cacheLock)
        {
            if (_catalogs.TryGetValue(key, out var cached)) return cached;
        }

        List<Lesson> lessons;
        if (!string.IsNullOrEmpty(campId))
        {
            var data = await _client.PlayListOfCourseAsync(campId, ct).ConfigureAwait(false);
            lessons = ParseLessons(data, info, name, campId, periodId);
        }
        else if (!string.IsNullOrEmpty(periodId))
        {
            var data = await _client.OpenCoursePlayListAsync(periodId, ct).ConfigureAwait(false);
            lessons = ParseLessons(data, info, name, campId, periodId);
        }
        else
        {
            return new List<Lesson>();
        }

        lock (_cacheLock) { _catalogs[key] = lessons; }
        return lessons;
    }

    private static List<Lesson> ParseLessons(JsonElement data, Camp info, string name,
                                             string campId, string periodId)
    {
        var lessons = new List<Lesson>();
        int index = 0;
        foreach (var c in data.Arr("courses"))
        {
            long start = c.Long("startTime");
            lessons.Add(new Lesson
            {
                Index = index++,
                Title = c.Str("name"),
                When = XqdNaming.FmtWhen(start),
                WhenDate = XqdNaming.FmtDate(start),
                Duration = c.Dbl("duration"),
                Type = c.Str("type"),
                TopicId = c.Str("topicId"),
                RoomId = c.Str("roomId"),
                CampName = name,
                CampId = campId,
                PeriodId = periodId,
            });
        }
        return lessons;
    }

    private async Task<Camp> EnsureCampAsync(string name, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(name)) return null;
        lock (_cacheLock)
        {
            if (_campIndex.TryGetValue(name, out var hit0)) return hit0;
        }
        foreach (string kind in new[] { "正式营", "体验营", "全部" })
        {
            await ListCampsAsync(kind, ct).ConfigureAwait(false);
            lock (_cacheLock)
            {
                if (_campIndex.TryGetValue(name, out var hit)) return hit;
            }
        }
        return null;
    }

    public Camp LookupCamp(string name) =>
        EnsureCampAsync(name, CancellationToken.None).GetAwaiter().GetResult();

    // ── 播放地址 ──
    public async Task<string> ResolveAsync(Camp camp, Lesson lesson, CancellationToken ct = default)
    {
        string name = camp?.Name ?? lesson?.CampName ?? "";
        var info = camp;
        if (info != null && string.IsNullOrEmpty(info.CampId) && string.IsNullOrEmpty(info.PeriodId))
            info = await EnsureCampAsync(name, ct).ConfigureAwait(false);

        string campId = info?.CampId ?? "";
        string businessType, businessId;
        if (string.IsNullOrEmpty(campId))
        {
            businessType = "OPEN_COURSE";
            businessId = info?.PeriodId ?? lesson?.PeriodId ?? "";
        }
        else
        {
            businessType = string.Equals(lesson?.Type, "OPEN_COURSE",
                StringComparison.OrdinalIgnoreCase) ? "OPEN_COURSE" : "camp";
            businessId = campId;
        }

        string topicId = lesson?.TopicId ?? "";
        string roomId = lesson?.RoomId ?? "";
        if (string.IsNullOrEmpty(topicId) && string.IsNullOrEmpty(roomId))
            throw new InvalidOperationException("缺少 topicId / roomId，无法解析播放地址");

        var data = await _client.GetCoursePlayUrlAsync(businessId, businessType,
            topicId, roomId, "", ct).ConfigureAwait(false);
        string url = QianliaoClient.FindPlayUrl(data);
        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException("未取得播放地址：getCoursePlayUrl 响应里找不到 http 地址");
        return url;
    }

    /// <summary>给一条课表任务解析播放地址（队列里用）。</summary>
    public Task<string> ResolveJobAsync(XqdJob job, CancellationToken ct = default)
    {
        var camp = new Camp
        {
            Name = job.Camp,
            CampId = job.CampId,
            PeriodId = job.PeriodId,
            Kind = "",
        };
        var lesson = new Lesson
        {
            Title = job.Title,
            Type = job.LessonType,
            TopicId = job.TopicId,
            RoomId = job.RoomId,
            CampName = job.Camp,
            CampId = job.CampId,
            PeriodId = job.PeriodId,
        };
        return ResolveAsync(camp, lesson, ct);
    }

    // ── 授权校验 ──
    public async Task<(bool Ok, string Message)> CheckAuthAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_client.Cookie)) return (false, "尚未获取授权");
        try
        {
            await _client.ApiAsync("/gate/course/myCourseListV2", new Dictionary<string, object>
            {
                ["skyType"] = "FINANCIAL",
                ["page"] = new Dictionary<string, object> { ["page"] = 1, ["size"] = 1 },
            }, timeoutSeconds: 20, retry: 0, ct: ct).ConfigureAwait(false);
            return (true, "授权有效");
        }
        catch (Exception e)
        {
            return (false, "授权无效：" + e.Message);
        }
    }
}
