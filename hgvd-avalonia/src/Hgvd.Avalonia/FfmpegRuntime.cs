using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using Hgvd.Core;

namespace Hgvd.Avalonia;

/// <summary>
/// ffmpeg / ffprobe 的定位与（单文件形态下的）自解压。
///
/// ★★ 两种发行形态（2026-11-22 修正）：
///   1. **目录版**（本地 build + CI 主推）：ffmpeg.exe / ffprobe.exe 就摊在
///      exe 同目录（pkg\），这里**直接用**，不解压、不写 LOCALAPPDATA；
///      可单独替换 ffmpeg 是目录版的核心好处。
///   2. **单文件版**（CI 备用）：外面没有附带文件，两个 exe 以 gzip 嵌进程序集
///      （LogicalName：ffmpeg.exe.gz / ffprobe.exe.gz），首次运行解压到
///      <c>%LOCALAPPDATA%\hgvd-downloader\runtime\ffmpeg-&lt;版本&gt;\</c>。
///   两者都没有时（Debug 开发构建）静默退回 PATH。
/// </summary>
internal static class FfmpegRuntime
{
    private static readonly string[] Names = { "ffmpeg", "ffprobe" };

    public static void Ensure()
    {
        try
        {
            // ★ 优先：目录版 —— exe 同目录就带着 ffmpeg/ffprobe，直接用
            string besideExe = AppContext.BaseDirectory;
            if (Names.All(n => File.Exists(Path.Combine(besideExe, n + ".exe"))))
            {
                ToolPaths.FfmpegDir = besideExe;
                return;
            }

            var asm = Assembly.GetExecutingAssembly();

            // 单文件版兜底：按版本解压到 LOCALAPPDATA（升级后重新解压，旧目录不动，不会被进程锁住）
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
            App.Log("ffmpeg 定位失败：" + e.Message);
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
