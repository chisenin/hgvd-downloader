using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Hgvd.Core;

/// <summary>杂项工具。</summary>
public static class Util
{
    public static string FmtSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {units[i]}";
    }

    public static string FmtDuration(double seconds)
    {
        if (seconds <= 0) return "--:--";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    /// <summary>把文件名里的非法字符换成全角或下划线。</summary>
    public static string SafeName(string name, int maxLen = 80)
    {
        if (string.IsNullOrEmpty(name)) return "未命名";
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
                sb.Append('_');
            else if (c < 0x20) sb.Append(' ');
            else sb.Append(c);
        }
        string s = sb.ToString().Trim();
        // 结尾的点或空格在 Windows 上不合法
        s = s.TrimEnd('.', ' ');
        if (s.Length > maxLen) s = s[..maxLen].TrimEnd('.', ' ');
        return string.IsNullOrEmpty(s) ? "未命名" : s;
    }

    /// <summary>用 ffprobe 读时长（秒）。失败返回 0。</summary>
    public static double ProbeDuration(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("ffprobe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in new[]
            {
                "-v", "error", "-show_entries", "format=duration",
                "-of", "default=nw=1:nk=1", path,
            }) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            string outp = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30000);
            if (double.TryParse(outp.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double d)) return d;
        }
        catch { }
        return 0;
    }

    /// <summary>检查 ffprobe 是否可用。</summary>
    public static bool HasFfprobe()
    {
        try
        {
            var psi = new ProcessStartInfo("ffprobe")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-version");
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(10000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }
}
