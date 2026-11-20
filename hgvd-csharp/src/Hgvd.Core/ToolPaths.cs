using System.IO;

namespace Hgvd.Core;

/// <summary>
/// ffmpeg / ffprobe 的可执行文件位置。
///
/// ★ 发行版（Avalonia 单文件 exe）把这两个 exe 一起打进 exe 里，
///   首次运行时自解压到本机缓存目录，然后把目录写进 <see cref="FfmpegDir"/>。
///   Python 版 / 命令行版不设置这里，就退回 PATH 里找（与原行为一致）。
/// </summary>
public static class ToolPaths
{
    /// <summary>
    /// 解压出来的 ffmpeg / ffprobe 所在目录；为空则走 PATH。
    /// </summary>
    public static string FfmpegDir { get; set; } = "";

    public static string Ffmpeg => Resolve("ffmpeg");

    public static string Ffprobe => Resolve("ffprobe");

    private static string Resolve(string name)
    {
        if (!string.IsNullOrEmpty(FfmpegDir))
        {
            string exe = Path.Combine(FfmpegDir, name + ".exe");
            if (File.Exists(exe)) return exe;
        }
        return name;   // 没有内置 → 交给系统在 PATH 里找
    }
}
