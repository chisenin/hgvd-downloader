using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hgvd.Core.Sign;

namespace Hgvd.Core.Auth;

/// <summary>
/// 从 MuMu 模拟器里读红果 App 的登录态。
///
/// ★ 这比扫码快得多，也不用过滑块 —— 你本来就在 App 里收藏剧，App 一定是登录的。
/// ★ 读取方式：MuMu 的**内部 RPC**（<c>MuMuManager.exe sh -v N -c "..."</c>），
///   不是 adbd、不是 root shell。
/// ★ 登录态在 App 的 shared_prefs 里，值是**明文**：
///   - cookieStore.xml           → sessionid / sid_tt / install_id / odin_tt
///   - token_shared_preference.xml → X-Tt-Token
///   - applog_stats.xml          → device_id
/// ★ `app_webview/Default/Cookies` 里**只有** passport_csrf_token，没有 sessionid —— 别去那里找。
/// </summary>
public static class MumuAuth
{
    public const string Package = "com.phoenix.read";
    private const string MumuManager = @"D:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe";

    /// <summary>模拟器实例信息。</summary>
    public sealed class Instance
    {
        public int Index;
        public bool Started;
        public string Name = "";
    }

    public static string FindMumu()
    {
        foreach (string p in new[]
        {
            MumuManager,
            @"C:\Program Files\Netease\MuMu\nx_main\MuMuManager.exe",
        })
            if (File.Exists(p)) return p;
        throw new InvalidOperationException(
            "找不到 MuMuManager.exe —— 请确认装了 MuMu 模拟器");
    }

    private static string Sh(string mumu, int index, string cmd, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo(mumu)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in new[] { "sh", "-v", index.ToString(), "-c", cmd })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("启动 MuMuManager 失败");
        var sb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.Append(e.Data).Append('\n'); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(timeoutMs)) { try { proc.Kill(); } catch { } }
        return sb.ToString();
    }

    public static List<Instance> ListInstances(string mumu)
    {
        var list = new List<Instance>();
        string outp = Sh(mumu, 0, "");   // 用一次无效调用确保进程可用
        _ = outp;

        // MuMuManager 的 info 命令返回 JSON
        for (int i = 0; i < 8; i++)
        {
            string txt;
            try { txt = RunInfo(mumu, i); }
            catch { break; }
            if (string.IsNullOrWhiteSpace(txt) || !txt.Contains("\"index\"")) continue;

            int index = ExtractInt(txt, "index") ?? i;
            bool started = ExtractBool(txt, "is_android_started") ?? false;
            string name = ExtractString(txt, "name") ?? $"实例 {index}";
            list.Add(new Instance { Index = index, Started = started, Name = name });
        }
        return list;
    }

    private static string RunInfo(string mumu, int index)
    {
        var psi = new ProcessStartInfo(mumu)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("info");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add(index.ToString());
        using var proc = Process.Start(psi)!;
        string s = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(20000);
        return s;
    }

    private static int? ExtractInt(string json, string key)
    {
        var m = Regex.Match(json, $"\"{key}\"\\s*:\\s*(\\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    private static bool? ExtractBool(string json, string key)
    {
        var m = Regex.Match(json, $"\"{key}\"\\s*:\\s*(true|false)");
        return m.Success ? m.Groups[1].Value == "true" : null;
    }

    private static string ExtractString(string json, string key)
    {
        var m = Regex.Match(json, $"\"{key}\"\\s*:\\s*\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// 读取 App 的登录态。
    /// </summary>
    /// <returns>cookie 字典 + 设备身份。</returns>
    public static (Dictionary<string, string> Cookies, DeviceIdentity Device)
        ReadAppState(int index, string mumu)
    {
        string cs = Sh(mumu, index, $"cat {Package}/shared_prefs/cookieStore.xml 2>/dev/null");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException($"实例 {index} 里读不到 cookieStore.xml（App 装了吗？）");

        string token = Sh(mumu, index,
            $"cat {Package}/shared_prefs/token_shared_preference.xml 2>/dev/null");
        string stats = Sh(mumu, index,
            $"cat {Package}/shared_prefs/applog_stats.xml 2>/dev/null");

        var cookies = new Dictionary<string, string>();
        foreach (string name in new[] { "sessionid", "sid_tt", "install_id", "odin_tt" })
        {
            string v = ExtractPrefValue(cs, name);
            if (!string.IsNullOrEmpty(v)) cookies[name] = v;
        }
        if (!cookies.ContainsKey("sessionid"))
            throw new InvalidOperationException($"实例 {index} 的 App 没有登录态（没有 sessionid）");

        string csrf = ExtractPrefValue(cs, "passport_csrf_token");
        if (string.IsNullOrEmpty(csrf))
            csrf = ExtractPrefValue(token, "passport_csrf_token");
        if (!string.IsNullOrEmpty(csrf)) cookies["passport_csrf_token"] = csrf;

        var device = new DeviceIdentity
        {
            Iid = cookies.TryGetValue("install_id", out var iid) ? iid : "",
            DeviceId = ExtractPrefValue(stats, "device_id"),
            Cdid = "",
            Openudid = "",
            VersionName = "7.3.9.32",
            VersionCode = "73932",
        };
        return (cookies, device);
    }

    /// <summary>
    /// 从 shared_prefs 的 XML 里取某个键的值。
    /// ★ App 用的是 **Java 序列化 + XML 转义 + 十六进制** 三重包装，
    ///   所以要：XML 实体解码 → hex 解码 → 在字节里找可打印 ASCII。
    /// </summary>
    public static string ExtractPrefValue(string xml, string name)
    {
        if (string.IsNullOrEmpty(xml)) return "";
        string decoded = System.Net.WebUtility.HtmlDecode(xml);

        var m = Regex.Match(decoded, $@"\|{Regex.Escape(name)}""[^>]*>(.*?)</string>",
            RegexOptions.Singleline);
        if (!m.Success) return "";

        string hex = m.Groups[1].Value.Replace(" ", "").Replace("\n", "").Replace("\r", "");
        if (hex.Length < 2 || hex.Length % 2 != 0) return "";
        try
        {
            byte[] bytes = Convert.FromHexString(hex);
            // 在字节里找长度 >= 8 的十六进制/字母数字串（sessionid / install_id 都是十六进制）
            var sb = new StringBuilder();
            var cur = new StringBuilder();
            foreach (byte b in bytes)
            {
                char c = (char)b;
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (ok) cur.Append(c);
                else
                {
                    if (cur.Length >= 8) { sb.Append(cur); break; }
                    cur.Clear();
                }
            }
            if (sb.Length == 0 && cur.Length >= 8) sb.Append(cur);
            return sb.ToString();
        }
        catch { return ""; }
    }

    /// <summary>
    /// 找一个能用（有登录态）的实例。
    /// ★ 优先选**最近活动过**的 —— 判断依据是 App 的**数据库文件 mtime**。
    ///   ★★ 必须用 <c>ls -lt</c>（按 mtime 排），**不能用 <c>ls -t</c>** ——
    ///     后者按**文件名**排序，数据库文件名里没有时间信息，会随机挑实例。
    /// </summary>
    public static int FindUsableInstance(string mumu, out Instance picked)
    {
        var instances = ListInstances(mumu).Where(i => i.Started).ToList();
        if (instances.Count == 0)
            throw new InvalidOperationException(
                "没有正在运行的 MuMu 实例 —— 请先启动模拟器并打开红果 App");

        int best = -1;
        string bestStamp = "";
        foreach (var inst in instances)
        {
            string stamp = "";
            foreach (string path in new[]
            {
                $"{Package}/databases/", $"{Package}/files/disk_collect/", $"{Package}/shared_prefs/",
            })
            {
                try
                {
                    string o = Sh(mumu, inst.Index,
                        $"ls -lt {path} 2>/dev/null | awk 'NR==2{{print $6, $7}}'");
                    stamp = o.Trim();
                }
                catch { }
                if (!string.IsNullOrEmpty(stamp)) break;
            }
            if (best < 0 || (stamp.Length > 0 && stamp.CompareTo(bestStamp) > 0))
            {
                best = inst.Index;
                bestStamp = stamp;
            }
        }

        var order = new List<int>();
        if (best >= 0) order.Add(best);
        order.AddRange(instances.Where(i => i.Index != best).Select(i => i.Index));

        var errors = new List<string>();
        foreach (int idx in order)
        {
            try
            {
                var (cookies, _) = ReadAppState(idx, mumu);
                if (cookies.ContainsKey("sessionid"))
                {
                    picked = instances.First(i => i.Index == idx);
                    return idx;
                }
                errors.Add($"实例 {idx}: 没有 sessionid（App 可能未登录）");
            }
            catch (Exception e) { errors.Add($"实例 {idx}: {e.Message}"); }
        }
        throw new InvalidOperationException(
            "找不到已登录的红果 App。\n" + string.Join("\n", errors));
    }

    /// <summary>完整登录：读登录态 → 保存凭据与设备档案。</summary>
    public static Account Login(int index = -1, bool save = true)
    {
        string mumu = FindMumu();
        int idx;
        if (index >= 0) idx = index;
        else idx = FindUsableInstance(mumu, out _);

        var (cookies, device) = ReadAppState(idx, mumu);
        var acc = new Account { Cookies = cookies };
        if (save)
        {
            acc.Save();
            SaveDevice(device);
        }
        return acc;
    }

    public static void SaveDevice(DeviceIdentity device)
    {
        Paths.EnsureDir();
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(Paths.DevicePath, JsonSerializer.Serialize(device, opts));
    }

    public static DeviceIdentity LoadDevice()
    {
        try
        {
            if (File.Exists(Paths.DevicePath))
                return JsonSerializer.Deserialize<DeviceIdentity>(
                    File.ReadAllText(Paths.DevicePath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new DeviceIdentity();
        }
        catch { }
        return new DeviceIdentity();
    }
}
