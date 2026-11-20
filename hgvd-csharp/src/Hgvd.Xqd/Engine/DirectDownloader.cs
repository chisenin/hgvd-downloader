using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Hgvd.Xqd.Engine;

/// <summary>
/// 直链媒体（音频课 / 单文件 MP4）下载。
///
/// ★ 保持原格式，不塞进 MP4。
/// ★ 扩展名靠 ffprobe 探真实容器决定（URL 后缀会骗人）。
/// ★ 临时文件名随机 —— 并发下同样例会互相覆盖、产出截断成品。
/// ★ 流式读，每块前检查暂停，进度按字节占比。
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
        string raw = Path.Combine(outDir, ".xqd_direct_" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var (got, _) = await StreamToRawAsync(url, raw, cb, control, ct).ConfigureAwait(false);
            if (got <= 0) return (false, "直链下载失败（内容为空）", outPath);

            string ext = MediaProbe.AudioExtFor(raw);
            string final = MediaProbe.SwapExt(outPath, ext);
            string dir = Path.GetDirectoryName(final);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.Move(raw, final, true);

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
        finally
        {
            try { if (File.Exists(raw)) File.Delete(raw); } catch { }
        }
    }

    private static async Task<(long got, long total)> StreamToRawAsync(
        string url, string raw, XqdCallbacks cb, XqdControl control, CancellationToken ct)
    {
        using var client = XqdNet.CreateClient(1800);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(1800));

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                                     .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? 0L;
        using var stream = await resp.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        using var fs = new FileStream(raw, FileMode.Create, FileAccess.Write, FileShare.Read, 65536);
        var buf = new byte[65536];
        long got = 0L;

        while (true)
        {
            await SegmentDownloader.WaitPauseAsync(control, ct).ConfigureAwait(false);
            int n = await stream.ReadAsync(buf, 0, buf.Length, linked.Token).ConfigureAwait(false);
            if (n <= 0) break;
            await fs.WriteAsync(buf, 0, n, linked.Token).ConfigureAwait(false);
            got += n;
            try
            {
                cb?.OnProgress(new XqdProgress
                {
                    DoneBytes = got,
                    TotalBytes = total > 0 ? total : got,
                    Unit = "byte",
                });
            }
            catch { }
        }
        return (got, total);
    }
}
