using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hgvd.Xqd.Engine;

/// <summary>
/// 兴趣岛下载引擎的唯一入口（移植自兴趣岛下载器内核，去掉 ADB / API / 授权依赖）。
///
/// 阶段固定顺序：解析 → 下载 → 封装 → 校验。
/// ★ 封装 / 校验阶段不发网络请求。
/// ★ 清理口径：失败/中止**必须保留**分片（跨会话续传的本钱）；成功才清（KeepParts 时也留）。
/// </summary>
public static class XqdDownloader
{
    public static async Task<XqdResult> DownloadAsync(
        XqdJob job, XqdOptions opt, string segDir,
        XqdCallbacks cb = null, XqdControl control = null, CancellationToken ct = default)
    {
        cb ??= new XqdCallbacks();
        control ??= new XqdControl();
        try
        {
            return await CoreAsync(job, opt, cb, control, ct, segDir).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            return Fail("已中止", job.OutPath);
        }
        catch (Exception e)
        {
            return Fail(e.Message, job.OutPath);
        }
    }

    private static async Task<XqdResult> CoreAsync(
        XqdJob job, XqdOptions opt, XqdCallbacks cb, XqdControl control,
        CancellationToken ct, string segDir)
    {
        string outPath = job.OutPath;
        double expect = job.ExpectDuration;
        int workers = opt.SegmentWorkers > 0 ? opt.SegmentWorkers : 12;

        cb.OnPhase("解析");
        var manifest = await ManifestParser.ParseAsync(job.Url, opt.Quality, ct).ConfigureAwait(false);
        if (manifest == null || manifest.Segments.Count == 0)
            return Fail(manifest != null ? manifest.Note : "清单解析失败", outPath);

        string outDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        // ── 直链媒体（音频课 / 单文件）：没有封装阶段 ──
        if (manifest.Kind == ManifestKind.Direct)
        {
            cb.OnPhase("下载");
            var direct = await DirectDownloader.DownloadAsync(
                job.Url, outPath, expect, cb, control, ct).ConfigureAwait(false);
            return new XqdResult
            {
                Ok = direct.ok,
                Message = direct.msg,
                OutPath = direct.final,
                Size = SafeSize(direct.final),
                Duration = expect,
            };
        }

        bool ownDir = string.IsNullOrEmpty(segDir);
        if (ownDir) segDir = Path.Combine(Path.GetTempPath(), "xqd_" + Guid.NewGuid().ToString("N"));
        else Directory.CreateDirectory(segDir);

        bool okResult = false;
        try
        {
            cb.OnPhase("下载");
            long totalBytes = await SegmentDownloader.SampleAsync(manifest.Segments, segDir, control, ct)
                                                     .ConfigureAwait(false);
            var dl = await SegmentDownloader.DownloadSegmentsAsync(
                manifest.Segments, segDir, workers, totalBytes, cb, control, ct).ConfigureAwait(false);
            if (!(dl.fail == 0 && dl.done > 0))
            {
                if (control.Cancelled) return Fail("已中止", outPath);
                return Fail("分片下载失败", outPath);
            }

            await SegmentDownloader.WaitPauseAsync(control, ct).ConfigureAwait(false);

            cb.OnPhase("封装");
            var mux = await Muxer.ConcatAndMuxAsync(segDir, outPath, ct).ConfigureAwait(false);
            if (!mux.ok) return Fail(mux.msg, outPath);

            cb.OnPhase("校验");
            double duration = manifest.TotalDuration;
            if (expect > 0.0)
            {
                double actual = MediaProbe.ProbeDuration(outPath);
                if (actual <= 0.0) return Fail("成品无法解析（可能损坏）", outPath);
                if (Math.Abs(actual - expect) > Math.Max(3.0, expect * 0.01))
                    return Fail("时长不符："
                        + actual.ToString("0.0", CultureInfo.InvariantCulture)
                        + "s vs 期望 " + expect.ToString("0.0", CultureInfo.InvariantCulture) + "s",
                        outPath);
                duration = actual;
            }

            okResult = true;
            return new XqdResult
            {
                Ok = true,
                Message = manifest.Note + "，" + mux.msg,
                OutPath = outPath,
                Size = SafeSize(outPath),
                Duration = duration,
            };
        }
        finally
        {
            if (ownDir || (okResult && !opt.KeepParts)) Muxer.Cleanup(segDir);
        }
    }

    private static long SafeSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0L; }
        catch { return 0L; }
    }

    private static XqdResult Fail(string msg, string outPath) => new()
    {
        Ok = false, Message = msg, OutPath = outPath, Size = SafeSize(outPath),
    };
}
