using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Hgvd.Core;

namespace Hgvd.Avalonia;

/// <summary>
/// 内置 ffmpeg / ffprobe 的自解压。
///
/// ★ 发行版把两个 exe 用 gzip 压好后**嵌进程序集**
///   （LogicalName：<c>ffmpeg.exe.gz</c> / <c>ffprobe.exe.gz</c>），
///   所以 exe 是单文件、目标机不用装 ffmpeg。
///   首次运行解压到 <c>%LOCALAPPDATA%\hgvd-downloader\runtime\ffmpeg-&lt;版本&gt;\</c>，
///   之后每次启动只做一次文件存在性检查。
///   Debug 构建没有嵌入资源时静默退回 PATH（不影响开发）。
/// </summary>
internal static class FfmpegRuntime
{
    private static readonly string[] Names = { "ffmpeg", "ffprobe" };

    public static void Ensure()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();

            // 按版本分目录：升级后重新解压，旧目录不动（不会被正在运行的进程锁住）
            string ver = asm.GetName().Version?.ToString() ?? "0.0.0.0";
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "hgvd-downloader", "runtime", "ffmpeg-" + ver);

            bool allPresent = Names.All(n => File.Exists(Path.Combine(dir, n + ".exe")));
            if (!allPresent)
            {
                Directory.CreateDirectory(dir);
                foreach (string n in Names)
                {
                    string target = Path.Combine(dir, n + ".exe");
                    if (File.Exists(target)) continue;
                    if (!Extract(asm, n, target)) return;   // 没打包 → 用 PATH 里的
                }
            }

            ToolPaths.FfmpegDir = dir;
        }
        catch (Exception e)
        {
            App.Log("ffmpeg 自解压失败：" + e.Message);
        }
    }

    /// <summary>把嵌入的 &lt;name&gt;.exe.gz 流式解压成 &lt;target&gt;。</summary>
    private static bool Extract(Assembly asm, string name, string target)
    {
        string res = asm.GetManifestResourceNames()
                        .FirstOrDefault(r => r.Equals(name + ".exe.gz", StringComparison.OrdinalIgnoreCase));
        if (res == null) return false;

        // 先写临时文件再改名：中途失败不会留下半截 exe
        string tmp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var raw = asm.GetManifestResourceStream(res)!)
            using (var gz = new GZipStream(raw, CompressionMode.Decompress))
            using (var fs = File.Create(tmp))
            {
                gz.CopyTo(fs);
            }
            File.Move(tmp, target, true);
            return true;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
