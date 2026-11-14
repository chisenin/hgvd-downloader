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
    public static string ConfigPath => Path.Combine(ToolDir, "config.json");
    public static string HistoryPath => Path.Combine(ToolDir, "history.json");

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
        if (!string.IsNullOrWhiteSpace(OutDir))
        {
            try
            {
                Directory.CreateDirectory(OutDir);
                return OutDir;
            }
            catch { /* 落到默认 */ }
        }
        return DefaultDownloadDir();
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
