using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Hgvd.Core;
using Hgvd.Core.Engine;

namespace Hgvd.Xqd.Engine;

/// <summary>
/// 直链媒体（音频课 / 单文件 MP4）下载。
///
/// ★ 2026-11-22 起不再自己写 HTTP 循环，改用红果那套单文件引擎
///   （<see cref="Downloader"/>）：Range 断点续传、`.part` 原子落盘、流式进度。
///   旧的实现用随机临时文件，断一次就整段重下，且并发下同名会互相覆盖。
/// ★ 保持原格式，不塞进 MP4；扩展名仍靠 ffprobe 探真实容器决定（URL 后缀会骗人）。
/// ★ 网络层仍走 <see cref="XqdNet"/> 的客户端（okhttp UA + 宽松证书链），
///   通过 <see cref="DownloadRequest.Client"/> 注入，不把两源的 TLS/UA 混在一起。
/// ★ 时长容差保持原口径 `max(3s, 1%)`：直链课的「期望时长」来自课表元数据，
///   不像 HLS 的 EXTINF 那样精确，套 0.5% 会误杀正常成品。
/// </summary>
internal static class DirectDownloader
{
    public static async Task<(bool ok, string msg, string final)> DownloadAsync(
        string url, string outPath, double expectDuration,
        XqdCallbacks cb, XqdControl control, CancellationToken ct)
    {
        string outDir = Path.GetDirectoryName(outPath);
        if (string.IsNullOrEmpty(outDir)) outDir = ".";
        Directory.CreateDirectory(outDir);

        // ★ 确定性临时名（不是随机）：中断后 `xxx.download.part` 还在，
        //   下次同一条任务会 Range 续传；随机名等于每次从头下。
        string temp = outPath + ".download";

        using var client = XqdNet.CreateClient(1800);
        var req = new DownloadRequest
        {
            Url = url,
            OutPath = temp,
            ExpectDuration = 0,        // 时长校验留到换完扩展名之后（见下）
            Client = client,
        };
        var coreCb = new DownloadCallbacks
        {
            OnPhase = p => { try { cb?.OnPhase(p); } catch { } },
            OnProgress = (done, total, speed) =>
            {
                try
                {
                    cb?.OnProgress(new XqdProgress
                    {
                        DoneBytes = done,
                        TotalBytes = total > 0 ? total : done,
                        Unit = "byte",
                    });
                }
                catch { }
            },
        };

        var res = await Downloader.DownloadAsync(req, coreCb, ct, control).ConfigureAwait(false);
        if (!res.Ok)
        {
            // Core 会把取消吞成「下载失败」；这里必须还原成取消语义，
            // 否则排队/关窗时这一节会被记成「失败」而不是回到「待下载」。
            ct.ThrowIfCancellationRequested();
            if (control.Cancelled) throw new OperationCanceledException("已中止");
            return (false, res.Error, outPath);
        }

        string ext = MediaProbe.AudioExtFor(res.OutPath);
        string final = MediaProbe.SwapExt(outPath, ext);
        string dir = Path.GetDirectoryName(final);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        try
        {
            File.Move(res.OutPath, final, true);
        }
        catch (Exception e)
        {
            return (false, "落盘失败：" + e.Message, final);
        }

        if (expectDuration > 0.0)
        {
            double actual = MediaProbe.ProbeDuration(final);
            if (actual <= 0.0) return (false, "成品无法解析（可能损坏）", final);
            if (Math.Abs(actual - expectDuration) > Math.Max(3.0, expectDuration * 0.01))
                return (false, "时长不符："
                    + actual.ToString("0.0", CultureInfo.InvariantCulture)
                    + "s vs 期望 "
                    + expectDuration.ToString("0.0", CultureInfo.InvariantCulture) + "s", final);
        }

        string msg = (new FileInfo(final).Length / 1048576.0)
            .ToString("0.0", CultureInfo.InvariantCulture) + " MB（原格式 " + ext + "）";
        return (true, msg, final);
    }
}
