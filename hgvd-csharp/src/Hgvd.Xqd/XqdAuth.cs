using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hgvd.Core;
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

        var cfg = AppConfig.Load();

        // ★★ 2026-11-21：正式改用雷电 —— **adb 源优先，MuMu 降为兜底**。
        var viaAdb = await AcquireFromAdbAsync(cfg, onStatus, ct).ConfigureAwait(false);
        if (viaAdb.Ok) return viaAdb;

        var viaMumu = await AcquireFromMumuAsync(onStatus, ct).ConfigureAwait(false);
        if (viaMumu.Ok) return viaMumu;

        return new XqdAuthResult
        {
            Ok = false,
            AppNotLogin = viaAdb.AppNotLogin || viaMumu.AppNotLogin,
            Message = "没有读到可用的兴趣岛登录态。\n"
                      + "【雷电 / adb】" + viaAdb.Message + "\n"
                      + "【MuMu】" + viaMumu.Message + "\n"
                      + "请在模拟器里打开兴趣岛确认已登录，再点一次「获取授权」。",
        };
    }

    /// <summary>
    /// 从 adb 设备读兴趣岛 Cookie（本项目主用雷电）。
    ///
    /// ★ 用设备端自带的 <c>sqlite3</c> 读库，**不需要给 C# 加 SQLite 依赖**；
    ///   输出格式（host|name|value）与 MuMu 那条完全一致，<see cref="ParseCookieDump"/> 原样复用。
    /// ★ 命令经 <see cref="AdbDevices.Shell"/> 的 base64 包装下发 —— SQL 里同时有单/双引号。
    /// ★ 先试 root（雷电有 su），失败再试普通 shell（MuMu 的 adb 读不到 /data/data，会在这里落空）。
    /// </summary>
    private static async Task<XqdAuthResult> AcquireFromAdbAsync(
        AppConfig cfg, Action<string> onStatus, CancellationToken ct)
    {
        string adb;
        try { adb = AdbDevices.FindAdb(cfg.AdbPath); }
        catch (Exception e) { return Fail(e.Message); }

        onStatus?.Invoke("adb：" + adb);

        List<AdbDevice> all;
        try { all = AdbDevices.List(adb, cfg.AdbPath); }
        catch (Exception e) { return Fail("读取 adb 设备失败：" + e.Message); }

        var online = all.Where(d => d.Online).ToList();
        if (online.Count == 0)
            return Fail("没有在线的 adb 设备（请先启动雷电模拟器）。");

        var notes = new List<string>();
        bool sawNotLogin = false;

        string prefer = (cfg.AdbSerial ?? "").Trim();
        var ordered = online
            .OrderByDescending(d => prefer.Length > 0
                                    && string.Equals(d.Serial, prefer, StringComparison.OrdinalIgnoreCase))
            .ThenBy(d => d.IsEmulator ? 0 : 1)
            .ThenBy(d => d.Serial, StringComparer.Ordinal)
            .ToList();

        foreach (AdbDevice dev in ordered)
        {
            ct.ThrowIfCancellationRequested();
            onStatus?.Invoke($"读取设备 {dev.Serial} 的兴趣岛登录态…");
            try
            {
                ShellResult pm = await Task.Run(
                    () => AdbDevices.Shell(dev.Serial, "pm list packages " + PackageName,
                                           root: false, timeoutMs: 20000, adb: adb), ct).ConfigureAwait(false);
                if (pm.Ok && !pm.All.Contains(PackageName))
                {
                    notes.Add($"{dev.Serial} 没装兴趣岛");
                    continue;
                }

                string cmd = "sqlite3 '" + CookieDb + "' \"" + CookieQuery + "\"";
                ShellResult dump = await Task.Run(
                    () => AdbDevices.Shell(dev.Serial, cmd, root: true, timeoutMs: 25000, adb: adb),
                    ct).ConfigureAwait(false);

                if (dump.Out.Trim().Length == 0)
                {
                    dump = await Task.Run(
                        () => AdbDevices.Shell(dev.Serial, cmd, root: false, timeoutMs: 25000, adb: adb),
                        ct).ConfigureAwait(false);
                }

                var (header, missing) = ParseCookieDump(dump.Out);
                if (missing.Count > 0)
                {
                    sawNotLogin = true;
                    notes.Add($"{dev.Serial} App 未登录（缺 {string.Join("、", missing)}）");
                    continue;
                }

                // 多台都有登录态时，取服务端真正认的那台
                var (ok, msg) = await new Api.QianliaoPlatform(header)
                    .CheckAuthAsync(ct).ConfigureAwait(false);
                if (!ok)
                {
                    notes.Add($"{dev.Serial} {msg}");
                    continue;
                }

                XqdCreds.Save(header);
                return new XqdAuthResult
                {
                    Ok = true, Cookie = header,
                    Message = $"授权成功（{dev.Serial}），已保存到本机。",
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                notes.Add($"{dev.Serial} {e.Message}");
            }
        }

        return new XqdAuthResult
        {
            Ok = false,
            AppNotLogin = sawNotLogin,
            Message = notes.Count == 0 ? "没读到登录态。" : string.Join("；", notes.Take(4)),
        };
    }

    /// <summary>兜底路径：老的 MuMu 通道（MuMuManager 内部 RPC）。</summary>
    private static async Task<XqdAuthResult> AcquireFromMumuAsync(
        Action<string> onStatus, CancellationToken ct)
    {
        string mumu;
        try { mumu = MumuAuth.FindMumu(); }
        catch (Exception e) { return Fail(e.Message); }

        onStatus?.Invoke("MuMu 管理器：" + mumu);

        List<MumuAuth.Instance> insts;
        try { insts = MumuAuth.ListInstances(mumu); }
        catch (Exception e) { return Fail("读取 MuMu 实例失败：" + e.Message); }

        var running = insts.Where(i => i.Started).OrderBy(i => i.Index).ToList();
        if (running.Count == 0)
            return Fail("MuMu 里没有正在运行的安卓实例。");

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
                    Message = $"授权成功（MuMu 实例 {inst.Index}），已保存到本机。",
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
            Message = notes.Count == 0 ? "没读到登录态。" : string.Join("；", notes.Take(4)),
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
