using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hgvd.Core;

namespace Hgvd.Xqd.Engine;

/// <summary>
/// 拼接 + 转封装 + 临时目录清理。
///
/// ★ 必须**先按字节 cat 成 all.ts 再喂 ffmpeg**，不能用 concat demuxer：
///   demuxer 会重置时间戳，实测成品短 6 秒 → 时长校验失败。
/// ★ ffmpeg 的输出必须以 .mp4 结尾（.part 结尾报 "Error initializing the muxer"）。
/// ★ 清理逐个删，不用目录递归删：批量删除会触发安全删除阈值，失败时截断释放空间。
/// </summary>
internal static class Muxer
{
    public static async Task<(bool ok, string msg)> ConcatAndMuxAsync(
        string segDir, string outPath, CancellationToken ct)
    {
        // %05d_ 前缀保证字典序 == 播放序。
        // 排除 all.*（上次失败可能残留，绝不能拼进去）与清单/元数据文件；
        // 除 .ts 外也接受 .aac/.m4s 这类裸分片（通用 HLS 常见），仍按名字排序。
        var files = Directory.GetFiles(segDir)
            .Select(Path.GetFileName)
            .Where(n => !n.StartsWith("all.", StringComparison.OrdinalIgnoreCase)
                     && !n.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                     && !n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                     && !n.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0) return (false, "没有可用的分片");

        string catPath = Path.Combine(segDir, "all.ts");
        try
        {
            using (var outStream = new FileStream(catPath, FileMode.Create, FileAccess.Write,
                                                  FileShare.Read, 1 << 20))
            {
                foreach (string name in files)
                {
                    try
                    {
                        using var f = File.OpenRead(Path.Combine(segDir, name));
                        f.CopyTo(outStream, 1 << 20);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        catch (IOException e) { return (false, "分片拼接失败: " + e.Message); }

        string tmpOut = XqdNaming.TempPartPath(outPath);
        if (!tmpOut.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            tmpOut = outPath + ".part.mp4";

        var args = new[]
        {
            "-y", "-loglevel", "error",
            "-protocol_whitelist", "file,http,https,tcp,tls,crypto,httpproxy",
            "-i", catPath,
            "-c", "copy", "-bsf:a", "aac_adtstoasc",
            "-movflags", "+faststart",
            tmpOut,
        };

        Proc.RunResult r;
        try
        {
            r = await Proc.RunAsync(ToolPaths.Ffmpeg, args, 7200000, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { return (false, "ffmpeg 失败: " + e.Message); }

        if (r.Code != 0 || !File.Exists(tmpOut) || new FileInfo(tmpOut).Length < 10000)
        {
            string err = r.Err ?? "";
            if (err.Length > 300) err = err.Substring(err.Length - 300);
            return (false, string.IsNullOrEmpty(err) ? "ffmpeg rc=" + r.Code : err);
        }

        File.Move(tmpOut, outPath, true);
        try { File.Delete(catPath); } catch { }

        string sizeMsg = (new FileInfo(outPath).Length / 1048576.0)
            .ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        return (true, sizeMsg);
    }

    public static void Cleanup(string segDir)
    {
        try
        {
            foreach (string p in Directory.GetFiles(segDir))
            {
                string name = Path.GetFileName(p);
                if (name.StartsWith("all.", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(p); }
                catch
                {
                    try { using var fs = new FileStream(p, FileMode.Truncate, FileAccess.Write); }
                    catch { }
                }
            }
            try { Directory.Delete(segDir, false); } catch { }
        }
        catch { }
    }
}
