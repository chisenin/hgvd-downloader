using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Hgvd.Xqd;

/// <summary>兴趣岛侧的小工具（不引 Hgvd.Core.Util，避免语义混淆：那边的 FmtDuration 是 mm:ss）。</summary>
public static class XqdUtil
{
    /// <summary>秒 → 天:时:分:秒（按需省略前导单位：45 / 1:30 / 2:15:30 / 1:02:15:30）。</summary>
    public static string FmtClock(double sec)
    {
        if (sec < 0 || double.IsNaN(sec) || double.IsInfinity(sec)) return "--";
        long s = (long)Math.Round(sec);
        long d = s / 86400, h = s % 86400 / 3600, m = s % 3600 / 60, ss = s % 60;
        if (d > 0) return $"{d}:{h:00}:{m:00}:{ss:00}";
        if (h > 0) return $"{h}:{m:00}:{ss:00}";
        if (m > 0) return $"{m}:{ss:00}";
        return ss.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>速率 → 紧凑字符串（4.2MB/s）。</summary>
    public static string FmtSpeed(double bps)
    {
        if (bps <= 0 || double.IsNaN(bps)) return "--";
        if (bps < 1024) return $"{(long)bps}B/s";
        if (bps < 1048576) return $"{(long)(bps / 1024.0)}KB/s";
        return $"{bps / 1048576.0:0.0}MB/s";
    }

    public static string Md5Hex16(string s)
    {
        using var md5 = MD5.Create();
        var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        string hex = sb.ToString();
        return hex.Length > 16 ? hex.Substring(0, 16) : hex;
    }

    public static bool IsHttp(string s) =>
        !string.IsNullOrEmpty(s) &&
        (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>按 ? 拆分 URL 的路径与查询。</summary>
    public static (string path, string query) SplitQuery(string url)
    {
        int i = (url ?? "").IndexOf('?');
        return i < 0 ? (url ?? "", "") : (url.Substring(0, i), url.Substring(i + 1));
    }

    public static string QueryWithout(string query, params string[] drop)
    {
        if (string.IsNullOrEmpty(query)) return "";
        var kept = new List<string>();
        foreach (string part in query.Split('&'))
        {
            if (string.IsNullOrEmpty(part)) continue;
            string name = part.Split('=')[0];
            bool skip = false;
            foreach (string d in drop)
                if (string.Equals(name, d, StringComparison.OrdinalIgnoreCase)) { skip = true; break; }
            if (!skip) kept.Add(part);
        }
        return kept.Count > 0 ? "?" + string.Join("&", kept) : "";
    }

    /// <summary>URL 的目录部分（含末尾 '/'，去掉 query）。</summary>
    public static string DirOf(string url)
    {
        var (path, _) = SplitQuery(url);
        int i = path.LastIndexOf('/');
        return i >= 0 ? path.Substring(0, i + 1) : "";
    }

    /// <summary>URL 的文件名（去 query），用于给「只粘链接」的行生成标题。</summary>
    public static string BaseNameOf(string url)
    {
        var (path, _) = SplitQuery(url);
        int i = path.LastIndexOf('/');
        string name = i >= 0 ? path.Substring(i + 1) : path;
        try { name = Uri.UnescapeDataString(name); } catch { }
        return name;
    }
}
