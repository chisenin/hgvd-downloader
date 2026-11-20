using System;
using System.IO;
using Hgvd.Core;

namespace Hgvd.Xqd;

/// <summary>兴趣岛成品命名 —— 文件名规范只有这一处。</summary>
public static class XqdNaming
{
    /// <summary>
    /// 课名 → 安全文件名。只换 ASCII 非法字符；全角冒号「：」保留
    /// （课程名里很常见，例如「先导课：解谜自然之道」）。
    /// </summary>
    public static string SafeName(string name)
    {
        string s = Util.SafeName(name, 90);
        // 结尾的点和空格在 Windows 上无法创建
        s = s.TrimEnd('.', ' ');
        return string.IsNullOrEmpty(s) ? "未命名" : s;
    }

    /// <summary>
    /// 课名 → 成品主名：先去掉用户粘进来的媒体扩展名，再做安全化。
    /// ★ 不去的话「xxx.mp3」会变成「xxx.mp3.mp4」，直链音频再改扩展名就成了
    ///   「xxx.mp3.mp3」（实测踩到）。
    /// </summary>
    public static string StemFromTitle(string title)
    {
        string s = (title ?? "").Trim();
        foreach (string ext in new[]
                 { ".mp4", ".m3u8", ".mp3", ".m4a", ".aac", ".ts", ".flv", ".mkv", ".mov" })
        {
            if (s.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(0, s.Length - ext.Length);
                break;
            }
        }
        return SafeName(s);
    }

    /// <summary>
    /// ffmpeg 的临时输出必须以 .mp4 结尾（以 .part 结尾会让它认不出封装格式）。
    /// </summary>
    public static string TempPartPath(string outPath)
    {
        string dir = Path.GetDirectoryName(outPath) ?? "";
        string name = Path.GetFileName(outPath);
        string stem = name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            ? name.Substring(0, name.Length - 4) : name;
        return Path.Combine(dir, stem + ".part.mp4");
    }

    /// <summary>
    /// 目标文件名已被占用时追加 -2 / -3（只在「同名不同链接」时用；
    /// 同名同链接靠 Id 去重，不会走到这里）。
    /// </summary>
    public static string MakeUnique(string outPath, Func<string, bool> exists)
    {
        if (!exists(outPath)) return outPath;
        string dir = Path.GetDirectoryName(outPath) ?? "";
        string stem = Path.GetFileNameWithoutExtension(outPath);
        string ext = Path.GetExtension(outPath);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(dir, $"{stem}-{i}{ext}");
            if (!exists(candidate)) return candidate;
        }
        return outPath;
    }
}
