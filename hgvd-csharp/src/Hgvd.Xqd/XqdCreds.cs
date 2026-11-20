using System;
using System.IO;
using Hgvd.Core;

namespace Hgvd.Xqd;

/// <summary>
/// 兴趣岛授权（Cookie）在本机的存放位置。
/// ★ 与红果的 cookie.json 分开：两个 App 的登录态互不相干。
/// ★ 只在 <c>~/.hgv_tool/xqd/cookie.txt</c> 里存一行 Cookie 头，权限尽量收紧到本人可读。
/// </summary>
public static class XqdCreds
{
    private static string Root =>
        Path.Combine(Paths.ToolDir, "xqd");

    public static string CookiePath => Path.Combine(Root, "cookie.txt");

    public static string Load()
    {
        try
        {
            if (!File.Exists(CookiePath)) return "";
            string s = File.ReadAllText(CookiePath).Trim();
            // 至少要 userId + rsessionId 才算有效
            return s.Contains("userId=") && s.Contains("rsessionId=") ? s : "";
        }
        catch { return ""; }
    }

    public static bool Save(string cookie)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(CookiePath, cookie ?? "");
            try
            {
                var fi = new FileInfo(CookiePath);
                if (OperatingSystem.IsWindows())
                    fi.Attributes |= FileAttributes.Hidden;
            }
            catch { }
            return true;
        }
        catch { return false; }
    }

    public static void Clear()
    {
        try { if (File.Exists(CookiePath)) File.Delete(CookiePath); } catch { }
    }

    public static bool Has => Load().Length > 0;
}
