using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Hgvd.Core;

namespace Hgvd.Xqd.Engine;

internal sealed class MediaInfo
{
    public string FormatName { get; set; } = "";
    public double Duration { get; set; }
    public bool HasVideo { get; set; }
    public bool HasAudio { get; set; }
    public List<string> Codecs { get; set; } = new List<string>();
    public long Size { get; set; }
}

/// <summary>
/// ffprobe 探测。可执行文件位置统一取 <see cref="ToolPaths"/>：
/// 发行版把 ffmpeg/ffprobe 嵌在 exe 里、首次运行自解压；命令行版退回 PATH。
/// </summary>
internal static class MediaProbe
{
    public static string FfmpegPath => ToolPaths.Ffmpeg;
    public static string FfprobePath => ToolPaths.Ffprobe;

    public static double ProbeDuration(string path)
    {
        try
        {
            var r = Proc.Run(FfprobePath, new[]
            {
                "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path,
            }, 180000);
            if (!r.Ok) return 0.0;
            return double.TryParse((r.Out ?? "").Trim(), NumberStyles.Any,
                                   CultureInfo.InvariantCulture, out var d) ? d : 0.0;
        }
        catch { return 0.0; }
    }

    /// <summary>
    /// 探测真实容器与编码。
    /// ★ 直链音频必须靠 ffprobe 探真实容器：Content-Type 一律 audio/mpeg 不可信；
    ///   mjpeg 只是 MP3 内嵌封面，不算视频轨。
    /// </summary>
    public static MediaInfo ProbeMedia(string path)
    {
        var info = new MediaInfo();
        try
        {
            var r = Proc.Run(FfprobePath, new[]
            {
                "-v", "error", "-show_entries",
                "format=format_name,duration,size:stream=codec_type,codec_name",
                "-of", "json", path,
            }, 180000);
            if (!r.Ok) return info;

            using var doc = JsonDocument.Parse(r.Out ?? "");
            var root = doc.RootElement;

            if (root.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.Object)
            {
                if (fmt.TryGetProperty("format_name", out var fn) && fn.ValueKind == JsonValueKind.String)
                    info.FormatName = fn.GetString() ?? "";
                if (fmt.TryGetProperty("duration", out var du)) info.Duration = ParseDouble(du);
                if (fmt.TryGetProperty("size", out var sz)) info.Size = ParseLong(sz);
            }

            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in streams.EnumerateArray())
                {
                    string codec = s.TryGetProperty("codec_name", out var cn) &&
                                   cn.ValueKind == JsonValueKind.String ? (cn.GetString() ?? "") : "";
                    string type = s.TryGetProperty("codec_type", out var ct) &&
                                  ct.ValueKind == JsonValueKind.String ? (ct.GetString() ?? "") : "";
                    info.Codecs.Add(codec);
                    if (type == "audio") info.HasAudio = true;
                    if (type == "video" && !string.Equals(codec, "mjpeg", StringComparison.OrdinalIgnoreCase))
                        info.HasVideo = true;
                }
            }
        }
        catch { }
        return info;
    }

    /// <summary>
    /// 按真实容器判定成品扩展名。
    /// ★ 原来只服务「音频课」，所以没有视频分支；合并进本工具后用户也可能直接粘 .mp4，
    ///   那种情况必须保住 .mp4 —— 否则会得到一个叫 xxx.mp3 的 MP4。
    /// </summary>
    public static string AudioExtFor(string path)
    {
        var info = ProbeMedia(path);
        if (info.HasVideo) return ".mp4";

        var codecs = info.Codecs.Select(c => (c ?? "").ToLowerInvariant()).ToList();
        if (codecs.Contains("mp3") || codecs.Contains("mp3float")) return ".mp3";
        if (codecs.Contains("aac") || codecs.Contains("mp4a")) return ".m4a";
        if ((info.FormatName ?? "").ToLowerInvariant().Contains("mp3")) return ".mp3";
        return ".m4a";
    }

    public static string SwapExt(string path, string ext) => Path.ChangeExtension(path, ext);

    private static double ParseDouble(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d)) return d;
        if (e.ValueKind == JsonValueKind.String &&
            double.TryParse(e.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var x)) return x;
        return 0.0;
    }

    private static long ParseLong(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n)) return n;
        if (e.ValueKind == JsonValueKind.String &&
            long.TryParse(e.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var x)) return x;
        return 0L;
    }
}
