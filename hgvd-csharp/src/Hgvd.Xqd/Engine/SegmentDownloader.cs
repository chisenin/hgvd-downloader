using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hgvd.Xqd.Engine;

/// <summary>
/// 并行分片下载。
///
/// ★ 总字节数用「均匀 12 片等距抽样」外推（取前 3 片会高估 30%+）；
///   抽样片写进缓存目录复用，不浪费请求。
/// ★ 进度按**内容时长**占比（每片 EXTINF 精确，总字节只是估算）。
/// ★ 分片本地名 <c>%05d_&lt;basename&gt;</c>：前缀保证字典序 == 播放序，
///   basename 跨会话稳定（只有 ?sign= 变），所以能跨进程续传。
/// </summary>
internal static class SegmentDownloader
{
    private const int SampleCount = 12;

    public static string SegName(string url, int i)
    {
        var (path, _) = XqdUtil.SplitQuery(url);
        int slash = path.LastIndexOf('/');
        string name = slash >= 0 ? path.Substring(slash + 1) : path;
        if (string.IsNullOrEmpty(name))
            name = "seg" + i.ToString("D5", CultureInfo.InvariantCulture);
        return i.ToString("D5", CultureInfo.InvariantCulture) + "_" + name;
    }

    public static async Task WaitPauseAsync(XqdControl control, CancellationToken ct)
    {
        if (control != null) await control.WaitIfPausedAsync(ct).ConfigureAwait(false);
        else ct.ThrowIfCancellationRequested();
    }

    /// <summary>抽样估算总字节（顺带把抽到的分片落盘）。</summary>
    public static async Task<long> SampleAsync(List<Segment> segments, string segDir,
                                               XqdControl control, CancellationToken ct)
    {
        int n = segments.Count;
        if (n == 0) return 0L;

        var idxs = new List<int>();
        if (n <= SampleCount)
        {
            for (int i = 0; i < n; i++) idxs.Add(i);
        }
        else
        {
            double step = (n - 1) / (double)(SampleCount - 1);
            var set = new SortedSet<int>();
            for (int i = 0; i < SampleCount; i++)
                set.Add((int)Math.Round(i * step, MidpointRounding.ToEven));
            idxs.AddRange(set);
        }

        var sizes = new List<long>();
        foreach (int i in idxs)
        {
            await WaitPauseAsync(control, ct).ConfigureAwait(false);
            string url = segments[i].Url;
            string local = Path.Combine(segDir, SegName(url, i));

            byte[] body = null;
            if (File.Exists(local))
            {
                try { body = File.ReadAllBytes(local); } catch { body = null; }
            }
            if (body == null || body.Length == 0)
            {
                try { body = await XqdNet.GetBytesAsync(url, 30, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { body = null; }
                if (body != null && body.Length > 0)
                {
                    try { File.WriteAllBytes(local, body); } catch { }
                }
            }
            if (body != null && body.Length > 0) sizes.Add(body.Length);
        }

        if (sizes.Count == 0) return 0L;
        double avg = sizes.Average();
        return (long)(avg * n);
    }

    public static async Task<(int done, int fail, long bytes)> DownloadSegmentsAsync(
        List<Segment> segments, string segDir, int workers, long totalBytes,
        XqdCallbacks cb, XqdControl control, CancellationToken ct)
    {
        int n = segments.Count;
        if (n == 0) return (0, 0, 0L);
        Directory.CreateDirectory(segDir);

        double totalDur = 0.0;
        foreach (var s in segments) totalDur += s.Duration;

        var gate = new object();
        long doneBytes = 0L;
        double doneDur = 0.0;
        int done = 0;
        int next = -1;

        void Report()
        {
            XqdProgress snap;
            lock (gate)
            {
                snap = new XqdProgress
                {
                    DoneBytes = doneBytes,
                    TotalBytes = totalBytes,
                    DoneDuration = doneDur,
                    TotalDuration = totalDur,
                    Unit = "dur",
                };
            }
            try { cb?.OnProgress(snap); } catch { }
        }

        async Task Worker()
        {
            while (true)
            {
                await WaitPauseAsync(control, ct).ConfigureAwait(false);
                int i = Interlocked.Increment(ref next);
                if (i >= n) return;

                string url = segments[i].Url;
                double dur = segments[i].Duration;
                string path = Path.Combine(segDir, SegName(url, i));

                // 断点续下：已下好的分片不再请求（跨会话续传的关键）
                if (File.Exists(path))
                {
                    long sz = 0L;
                    try { sz = new FileInfo(path).Length; } catch { }
                    if (sz > 0)
                    {
                        lock (gate) { done++; doneBytes += sz; doneDur += dur; }
                        Report();
                        continue;
                    }
                }

                for (int attempt = 0; attempt < 3; attempt++)
                {
                    await WaitPauseAsync(control, ct).ConfigureAwait(false);
                    byte[] body = null;
                    try { body = await XqdNet.GetBytesAsync(url, 40, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { body = null; }

                    if (body != null && body.Length > 0)
                    {
                        try
                        {
                            File.WriteAllBytes(path, body);
                            lock (gate) { done++; doneBytes += body.Length; doneDur += dur; }
                            break;
                        }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                    if (attempt < 2)
                        await Task.Delay(1000 * (attempt + 1), ct).ConfigureAwait(false);
                }
                Report();
            }
        }

        int w = Math.Max(1, workers);
        var tasks = new Task[w];
        for (int k = 0; k < w; k++) tasks[k] = Task.Run((Func<Task>)Worker);
        await Task.WhenAll(tasks).ConfigureAwait(false);

        return (done, n - done, doneBytes);
    }
}
