using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hgvd.Xqd.Api;

/// <summary>
/// 兴趣岛业务网关客户端（移植自上游 Xqd.Core.Api.QianliaoClient）。
///
/// 已验证契约：
///   POST https://m.qianliao.net/financial/api/transfer/gate/&lt;endpoint&gt;
///   Body: {"transferData": {...}}
///   成功码在 <c>state.code</c>（不是顶层 code）
///   网关出错会返回 HTML（title=授权登录）→ 先判首字节再解析
///   必须禁用系统代理（XqdNet 已全局禁用）
/// </summary>
public sealed class QianliaoClient
{
    public const string DefaultHost = "m.qianliao.net";

    private static readonly string[] UrlKeys = { "playUrl", "coursePlayUrl", "url" };
    private const int MaxDepth = 8;

    public string Host { get; }
    public string Cookie { get; set; }

    public string GatewayUrl => "https://" + Host + "/financial/api/transfer";

    public QianliaoClient(string cookie = null, string host = DefaultHost)
    {
        Host = string.IsNullOrEmpty(host) ? DefaultHost : host;
        Cookie = string.IsNullOrEmpty(cookie) ? XqdCreds.Load() : cookie;
    }

    /// <summary>调业务网关，返回 data 节点。失败抛 InvalidOperationException。</summary>
    public async Task<JsonElement> ApiAsync(string endpoint,
        Dictionary<string, object> transferData = null,
        int timeoutSeconds = 30, int retry = 2, CancellationToken ct = default)
    {
        var td = transferData ?? new Dictionary<string, object>();
        var body = JsonSerializer.Serialize(new Dictionary<string, Dictionary<string, object>>
        {
            ["transferData"] = td,
        });
        string url = GatewayUrl + endpoint;
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Cookie", Cookie ?? ""),
            new("Origin", "https://" + Host),
            new("Referer", "https://" + Host + "/"),
        };

        string last = "";
        for (int attempt = 0; attempt <= retry; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            string raw;
            try
            {
                raw = await XqdNet.PostJsonAsync(url, body, headers, timeoutSeconds, ct)
                                   .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                last = "请求失败：" + e.Message;
                if (attempt < retry) { await Task.Delay(800, ct).ConfigureAwait(false); continue; }
                break;
            }

            // 200 不等于成功：网关出错时回的是 HTML
            string head = (raw ?? "").TrimStart();
            if (head.Length == 0 || head[0] != '{')
            {
                last = "网关返回非 JSON（可能未授权或被代理拦截；首字节 '"
                     + (head.Length > 0 ? head[0].ToString() : "") + "'）";
                if (attempt < retry) { await Task.Delay(800, ct).ConfigureAwait(false); continue; }
                break;
            }

            JsonElement root;
            try { root = J.Parse(raw); }
            catch (Exception e)
            {
                last = "JSON 解析失败：" + e.Message;
                if (attempt < retry) { await Task.Delay(800, ct).ConfigureAwait(false); continue; }
                break;
            }

            var state = root.Get("state");
            long code = state.Long("code", -999);
            if (code == 0)
            {
                var data = root.Get("data");
                return data.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    ? data : J.Parse("{}");
            }

            last = "code=" + code + " msg=" + state.Str("msg");
            if (code == 110) break;   // 未登录，重试无用
            if (attempt < retry) await Task.Delay(800, ct).ConfigureAwait(false);
        }
        throw new InvalidOperationException(endpoint + " 失败：" + last);
    }

    // ── 业务端点 ──

    public Task<JsonElement> MyCourseListV2Async(Dictionary<string, object> transferData = null,
        CancellationToken ct = default) =>
        ApiAsync("/gate/course/myCourseListV2", transferData, ct: ct);

    public Task<JsonElement> PlayListOfCourseAsync(string campId,
        CancellationToken ct = default) =>
        ApiAsync("/gate/course/playListOfCourse", new Dictionary<string, object>
        {
            ["campId"] = campId ?? "",
            ["onlyCourseCatalogue"] = true,
        }, ct: ct);

    /// <summary>公开课课表：公开课没有 campId，用 periodId 走这个专用端点。</summary>
    public Task<JsonElement> OpenCoursePlayListAsync(string periodId,
        CancellationToken ct = default) =>
        ApiAsync("/gate/openCourse/playListOfCourse", new Dictionary<string, object>
        {
            ["periodId"] = periodId ?? "",
        }, ct: ct);

    public Task<JsonElement> GetCoursePlayUrlAsync(string businessId, string businessType,
        string topicId, string roomId, string videoHost = "",
        CancellationToken ct = default) =>
        ApiAsync("/gate/course/getCoursePlayUrl", new Dictionary<string, object>
        {
            ["videoHost"] = videoHost ?? "",
            ["roomId"] = roomId ?? "",
            ["topicId"] = topicId ?? "",
            ["businessId"] = businessId ?? "",
            ["businessType"] = string.IsNullOrEmpty(businessType) ? "camp" : businessType,
        }, ct: ct);

    /// <summary>getCoursePlayUrl 的 data 可能多层嵌套，逐层找第一个 http 地址。</summary>
    public static string FindPlayUrl(JsonElement data) => FindHttp(data, 0);

    private static string FindHttp(JsonElement node, int depth)
    {
        if (depth > MaxDepth) return "";
        switch (node.ValueKind)
        {
            case JsonValueKind.String:
            {
                string s = node.GetString();
                return s != null && s.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? s : "";
            }
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    string r = FindHttp(item, depth + 1);
                    if (!string.IsNullOrEmpty(r)) return r;
                }
                return "";
            case JsonValueKind.Object:
                foreach (string key in UrlKeys)
                {
                    string r = FindHttp(node.Get(key), depth + 1);
                    if (!string.IsNullOrEmpty(r)) return r;
                }
                foreach (var p in node.EnumerateObject())
                {
                    if (IsUrlKey(p.Name)) continue;
                    string r = FindHttp(p.Value, depth + 1);
                    if (!string.IsNullOrEmpty(r)) return r;
                }
                return "";
            default:
                return "";
        }
    }

    private static bool IsUrlKey(string name)
    {
        foreach (string k in UrlKeys)
            if (string.Equals(name, k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
