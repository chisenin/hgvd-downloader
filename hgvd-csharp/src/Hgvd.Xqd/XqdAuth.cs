using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hgvd.Core.Auth;

namespace Hgvd.Xqd;

/// <summary>兴趣岛授权结果。</summary>
public sealed class XqdAuthResult
{
    public bool Ok { get; set; }
    public string Cookie { get; set; } = "";
    public string Message { get; set; } = "";
    public bool FromCache { get; set; }
    /// <summary>true = 读到了库但没有登录态（需要用户去 App 里登录）。</summary>
    public bool AppNotLogin { get; set; }
}

/// <summary>
/// 兴趣岛授权：从 MuMu 里读兴趣岛 App 的 WebView Cookie 库。
///
/// 原理（上游实测）：App 是 WebView 壳，登录态明文存在
///   /data/data/com.qlchat.xingqudao/app_webview/Default/Cookies
/// 的 value 列；用 MuMuManager 的内部 RPC 起 sqlite3 读表即可，不需要 adbd/root。
///
/// ★ 只读**正在运行**的实例；不会替用户启动模拟器（本工具的原则：
///   点「获取授权」时才动作，且不做用户没要求的副作用）。
/// ★ 日志/异常绝不带 Cookie 值，只报字段名。
/// </summary>
public static class XqdAuth
{
    public const string PackageName = "com.qlchat.xingqudao";
    private const string CookieDb =
        "/data/data/com.qlchat.xingqudao/app_webview/Default/Cookies";

    /// <summary>
    /// ★ 单百分号：写成 %% 是字面匹配两个百分号，一条都匹配不到。
    ///   域名覆盖 .qianliao.net / .xqdaolove.cn / .nicegoods.cn。
    /// </summary>
    private const string CookieQuery =
        "select host_key,name,value from cookies "
        + "where host_key like '%qianliao%' "
        + "   or host_key like '%xqdaolove%' "
        + "   or host_key like '%nicegoods%';";

    private static readonly string[] Required = { "userId", "rsessionId" };
    private static readonly string[] Optional =
        { "caller", "platform", "ver", "deviceType", "os" };

    public static bool HasCache => XqdCreds.Has;

    /// <summary>
    /// 取授权：默认先看本机缓存；force=true 时忽略缓存，强制去模拟器读一次。
    /// </summary>
    public static async Task<XqdAuthResult> AcquireAsync(
        Action<string> onStatus = null, bool force = false, CancellationToken ct = default)
    {
        if (!force)
        {
            string cached = XqdCreds.Load();
            if (cached.Length > 0)
                return new XqdAuthResult
                {
                    Ok = true, Cookie = cached, FromCache = true,
                    Message = "已使用本机保存的兴趣岛授权（未启动模拟器）。",
                };
        }

        string mumu;
        try { mumu = MumuAuth.FindMumu(); }
        catch (Exception e) { return Fail(e.Message); }

        onStatus?.Invoke("MuMu 管理器：" + mumu);

        List<MumuAuth.Instance> insts;
        try { insts = MumuAuth.ListInstances(mumu); }
        catch (Exception e) { return Fail("读取 MuMu 实例失败：" + e.Message); }

        var running = insts.Where(i => i.Started).OrderBy(i => i.Index).ToList();
        if (running.Count == 0)
            return Fail("MuMu 里没有正在运行的安卓实例。\n"
                        + "请先启动 MuMu，在兴趣岛 App 里登录，然后回来点「获取授权」。");

        var notes = new List<string>();
        bool sawNotLogin = false;

        foreach (var inst in running)
        {
            ct.ThrowIfCancellationRequested();
            onStatus?.Invoke($"读取实例 {inst.Index} 的兴趣岛登录态…");
            try
            {
                string cmd = "sqlite3 '" + CookieDb + "' \"" + CookieQuery + "\"";
                string dump = await Task.Run(
                    () => MumuAuth.RunShell(mumu, inst.Index, cmd), ct).ConfigureAwait(false);

                var (header, missing) = ParseCookieDump(dump);
                if (missing.Count > 0)
                {
                    sawNotLogin = true;
                    notes.Add($"实例 {inst.Index}：App 未登录（缺 {string.Join("、", missing)}）");
                    continue;
                }

                // 多台都有登录态时，取服务端真正认的那台
                var (ok, msg) = await new Api.QianliaoPlatform(header)
                    .CheckAuthAsync(ct).ConfigureAwait(false);
                if (!ok)
                {
                    notes.Add($"实例 {inst.Index}：{msg}");
                    continue;
                }

                XqdCreds.Save(header);
                return new XqdAuthResult
                {
                    Ok = true, Cookie = header,
                    Message = $"授权成功（实例 {inst.Index}），已保存到本机。",
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                notes.Add($"实例 {inst.Index}：{e.Message}");
            }
        }

        return new XqdAuthResult
        {
            Ok = false,
            AppNotLogin = sawNotLogin,
            Message = "没有读到可用的兴趣岛登录态。\n· "
                      + string.Join("\n· ", notes.Take(4))
                      + "\n请在 MuMu 的实例里打开兴趣岛确认已登录，再点一次「获取授权」。",
        };
    }

    private static XqdAuthResult Fail(string msg) => new() { Ok = false, Message = msg };

    /// <summary>
    /// sqlite3 的 <c>host|name|value</c> 输出 → Cookie 头 + 缺失的必需字段。
    /// ★ 顶层域（host 以 '.' 开头）优先：同名 cookie 取 .qianliao.net 那条。
    /// ★ public 是为了离线回归能直接测解析（不碰模拟器）。
    /// </summary>
    public static (string Header, List<string> Missing) ParseCookieDump(string dump)
    {
        var values = new Dictionary<string, (string Value, bool DomainPref)>(StringComparer.Ordinal);

        foreach (string raw in (dump ?? "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || !line.Contains("|")) continue;
            string[] parts = line.Split('|');
            if (parts.Length < 3) continue;

            string host = parts[0], name = parts[1], value = parts[2];
            bool domainPref = host.StartsWith(".", StringComparison.Ordinal);
            if (!values.TryGetValue(name, out var old) || (domainPref && !old.DomainPref))
                values[name] = (value, domainPref);
        }

        var missing = new List<string>();
        foreach (string name in Required)
            if (!values.TryGetValue(name, out var v) || string.IsNullOrEmpty(v.Value))
                missing.Add(name);

        var partsOut = new List<string>();
        foreach (string name in Required)
            if (values.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v.Value))
                partsOut.Add(name + "=" + v.Value);
        foreach (string name in Optional)
            if (values.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v.Value))
                partsOut.Add(name + "=" + v.Value);

        return (string.Join("; ", partsOut), missing);
    }
}
