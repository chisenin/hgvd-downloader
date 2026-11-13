using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hgvd.Core;

/// <summary>HTTP 客户端工厂。</summary>
/// <remarks>
/// ★ ★★ **必须禁用代理** —— 抖音系网关会按客户端 IP 判定异常，
///   走系统代理常常直接返回空 body。参考项目也有同样结论。
/// </remarks>
public static class Net
{
    public const string AppUa =
        "com.phoenix.read/73932 (Linux; U; Android 14; zh_CN; 23127PN0CC; " +
        "Build/UKQ1.230804.001; Cronet/TTNetVersion:8d40f833)";

    private static readonly Lazy<HttpClient> Lazy = new(() =>
    {
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            Proxy = null,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppUa);
        return client;
    });

    public static HttpClient Client => Lazy.Value;

    public static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");
}
