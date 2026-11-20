using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hgvd.Xqd;

/// <summary>
/// 兴趣岛 CDN 的网络层。
///
/// ★ **必须禁用系统代理**：清单/分片都走 CDN，走代理常被换成 HTML 或直接 403
///   （xqd 版实测结论，与本项目红果侧的结论一致）。
/// ★ TLS 校验证书关闭：这批 CDN 偶有证书链不全的节点。
/// </summary>
public static class XqdNet
{
    /// <summary>与 App 内播放器一致；CDN 对这个 UA 最稳定。</summary>
    public const string Ua = "okhttp/4.9.0";

    private static readonly HttpClient Client = CreateClient();

    public static HttpClient CreateClient(int timeoutSeconds = 60)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            Proxy = null,
            AllowAutoRedirect = true,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Ua);
        c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        return c;
    }

    public static async Task<byte[]> GetBytesAsync(string url, int timeoutSeconds = 30,
                                                   CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                                     .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
    }

    public static async Task<string> GetTextAsync(string url, int timeoutSeconds = 30,
                                                  CancellationToken ct = default)
    {
        var bytes = await GetBytesAsync(url, timeoutSeconds, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }
}
