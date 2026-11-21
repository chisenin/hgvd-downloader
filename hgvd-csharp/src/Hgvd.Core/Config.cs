using System.Collections.Generic;
using System.Linq;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hgvd.Core;

/// <summary>
/// 工具目录与配置。
///
/// ★ 与 Python 版**共用同一个目录**（<c>%USERPROFILE%\.hgv_tool</c>），
///   这样两版可以直接互相复用登录态与设备档案。
/// </summary>
public static class Paths
{
    public static string ToolDir =>
        Environment.GetEnvironmentVariable("HGVD_HOME")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hgv_tool");

    public static string CredsPath => Path.Combine(ToolDir, "cookie.json");
    public static string DevicePath => Path.Combine(ToolDir, "device.json");

    /// <summary>
    /// 按实例归档的设备身份（key = 该实例的 install_id）。
    /// ★ 多开时每台的 install_id / device_id 都不同，而签名要求「cookie 与设备同源」；
    ///   只存一份 device.json 会在换实例时互相覆盖，表现为「只有一台的授权能用」。
    /// </summary>
    public static string DevicesPath => Path.Combine(ToolDir, "devices.json");
    public static string ConfigPath => Path.Combine(ToolDir, "config.json");
    public static string HistoryPath => Path.Combine(ToolDir, "history.json");
    /// <summary>红果侧的下载清单（重启恢复；与兴趣岛的 xqd\jobs.json 并列）。</summary>
    public static string QueuePath => Path.Combine(ToolDir, "queue.json");

    public static void EnsureDir() => Directory.CreateDirectory(ToolDir);
}

/// <summary>用户配置。</summary>
public sealed class AppConfig
{
    public string OutDir { get; set; } = "";
    /// <summary>清晰度：360p / 720p / 1080p。★ 默认**最高**（用户要求）。</summary>
    public string Quality { get; set; } = "1080p";
    public int Workers { get; set; } = 3;
    public int MaxEpisodes { get; set; } = 0;
    public bool SkipExisting { get; set; } = true;

    /// <summary>
    /// adb.exe 路径。留空 = **自动探测**（雷电优先：注册表 / 运行中进程 / 各盘常见目录，
    /// 其次其他模拟器，最后 PATH）。装到非常规目录时可在设置里手动指定。
    /// </summary>
    public string AdbPath { get; set; } = "";

    /// <summary>
    /// 指定 adb 设备序列号（雷电是 <c>emulator-5554</c>）。留空 = 自动挑一台在线模拟器。
    /// 多开模拟器、或同时接着真机时才需要填。
    /// </summary>
    public string AdbSerial { get; set; } = "";

    /// <summary>JSON 选项。</summary>
    /// ★ PropertyNameCaseInsensitive —— Python 版写的键是**全小写**
    ///   （``sessionid`` / ``user_id`` / ``saved_at``），要与它共用同一个凭据文件。
    /// </remarks>
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(
                    File.ReadAllText(Paths.ConfigPath), Opts) ?? new AppConfig();
        }
        catch { /* 配置坏了就用默认值 */ }
        return new AppConfig();
    }

    public void Save()
    {
        Paths.EnsureDir();
        File.WriteAllText(Paths.ConfigPath, JsonSerializer.Serialize(this, Opts));
    }

    /// <summary>解析下载目录（用户没配就用系统「下载」文件夹）。</summary>
    public string ResolveOutDir()
    {
        TryResolveOutDir(out string dir, out _);
        return dir;
    }

    /// <summary>
    /// 解析下载目录，并把「配置的目录不可用」**如实报出来**。
    ///
    /// ★★ 2026-11-20 修的 bug：以前这里 <c>catch { }</c> 之后就静默回落到系统「下载」，
    ///    于是 <c>Z:\movies\hgvd\</c> 这个盘拔掉之后，文件被悄悄下到
    ///    <c>C:\Users\xxx\Downloads\</c>，界面和 <c>--batchtest</c> 还报「成功」——
    ///    用户完全不知道文件去了哪。
    ///    现在由调用方自己决定：界面用它显示提示，下载入口用它**失败**（别假装成功）。
    /// </summary>
    /// <returns>配置目录可用（或本来就没配）返回 true；配置了但不可用返回 false。</returns>
    public bool TryResolveOutDir(out string dir, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(OutDir))
        {
            dir = DefaultDownloadDir();
            return true;
        }
        try
        {
            Directory.CreateDirectory(OutDir);
            dir = OutDir;
            return true;
        }
        catch (Exception e)
        {
            dir = DefaultDownloadDir();
            error = $"配置的下载目录不可用（{OutDir}）：{e.Message}";
            return false;
        }
    }

    public static string DefaultDownloadDir()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string name in new[] { "Downloads", "下载" })
        {
            string p = Path.Combine(profile, name);
            if (Directory.Exists(p)) return p;
        }
        // OneDrive 重定向的情况
        string od = Environment.GetEnvironmentVariable("OneDrive");
        if (!string.IsNullOrEmpty(od))
        {
            foreach (string name in new[] { "Downloads", "下载" })
            {
                string p = Path.Combine(od, name);
                if (Directory.Exists(p)) return p;
            }
        }
        return Path.Combine(profile, "Downloads");
    }
}

/// <summary>登录态。</summary>
public sealed class Account
{
    public Dictionary<string, string> Cookies { get; set; } = new();
    public string UserId { get; set; } = "";
    public string UserName { get; set; } = "";
    public long SavedAt { get; set; }

    public bool Ok => Cookies.Count > 0 && Cookies.ContainsKey("sessionid");

    public string CookieHeader =>
        string.Join("; ", Cookies.Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{kv.Key}={kv.Value}"));

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Account Load()
    {
        try
        {
            if (File.Exists(Paths.CredsPath))
                return JsonSerializer.Deserialize<Account>(
                    File.ReadAllText(Paths.CredsPath), Opts) ?? new Account();
        }
        catch { }
        return new Account();
    }

    public void Save()
    {
        Paths.EnsureDir();
        SavedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(Paths.CredsPath, JsonSerializer.Serialize(this, Opts));
    }

    public static void Clear()
    {
        try { if (File.Exists(Paths.CredsPath)) File.Delete(Paths.CredsPath); }
        catch { }
    }
}
