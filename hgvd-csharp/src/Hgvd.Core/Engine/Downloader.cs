using System.Collections.Generic;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Hgvd.Core.Engine;

/// <summary>下载请求。</summary>
public sealed class DownloadRequest
{
    public string Url = "";
    public string OutPath = "";
    public double ExpectDuration;
    public Dictionary<string, string> Headers = new();
    public string Cookie = "";
    /// <summary>CENC 内容密钥（hex）。非空则下载后用 ffmpeg 解密。</summary>
    public string ContentKey = "";
    /// <summary>
    /// 备用直链（同一清晰度的 backup_url）。
    /// ★ 实测（2026-11-14）：主 CDN 主机偶尔直接连不上
    ///   （「由于连接方在一段时间后没有正确答复…」），换 backup_url 就好了。
    /// </summary>
    public string BackupUrl = "";
}

/// <summary>下载结果。</summary>
public sealed class DownloadResult
{
    public bool Ok;
    public string OutPath = "";
    public long Size;
    public double Duration;
    public string Error = "";
    public bool Skipped;
}

/// <summary>进度回调。</summary>
public sealed class DownloadCallbacks
{
    public Action<string> OnPhase;
    public Action<long, long, double> OnProgress;
    /// <summary>
    /// 播放地址解析成功、**已知总大小**时回调一次。
    /// ★ 用途：下载开始前就能在列表里显示「≈ 29.1 MB」，
    ///   不用等下完才知道多大。
    /// </summary>
    public Action<long> OnResolvedSize;
    /// <summary>诊断日志（首字节耗时等），供界面排查「拿到大小到开下」的停顿。</summary>
    public Action<string> OnDiag;
}

/// <summary>下载引擎。</summary>
public static class Downloader
{
    /// <summary>下载一集（支持断点续传、进度回调、ffprobe 校验、CENC 解密）。</summary>
    public static async Task<DownloadResult> DownloadAsync(
        DownloadRequest req, DownloadCallbacks cb = null,
        CancellationToken ct = default, PauseGate gate = null)
    {
        cb ??= new DownloadCallbacks();
        var outFull = Path.GetFullPath(req.OutPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outFull)!);
        string part = outFull + ".part";

        cb.OnPhase?.Invoke("下载");

        long total = 0;
        long have = 0;
        try
        {
            using var httpReq = new HttpRequestMessage(HttpMethod.Get, req.Url);
            foreach (var kv in req.Headers)
                httpReq.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            if (!string.IsNullOrEmpty(req.Cookie))
                httpReq.Headers.TryAddWithoutValidation("Cookie", req.Cookie);

            // ★ 断点续传
            if (File.Exists(part))
            {
                have = new FileInfo(part).Length;
                httpReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            }

            HttpResponseMessage? resp = null;
            var swGet = Stopwatch.StartNew();
            try
            {
                // ★★ ResponseHeadersRead：收到响应头就返回，流式读 body。
                //   默认的 ResponseContentRead 会**缓冲完整个响应**才返回 —— 这就是
                //   「拿到大小到进度条启动」卡十几秒的原因：SendAsync 在后台缓冲整集。
                resp = await Net.Client.SendAsync(httpReq,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                cb.OnDiag?.Invoke($"GET 响应头 {swGet.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                // ★ 主 CDN 主机连不上 → 换备用直链再试一次
                //   （实测 2026-11-14：某集的 main_url 主机超时，backup_url 正常）
                if (string.IsNullOrEmpty(req.BackupUrl) || req.BackupUrl == req.Url)
                    throw;
                using var alt = new HttpRequestMessage(HttpMethod.Get, req.BackupUrl);
                foreach (var kv in req.Headers)
                    alt.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                if (!string.IsNullOrEmpty(req.Cookie))
                    alt.Headers.TryAddWithoutValidation("Cookie", req.Cookie);
                if (File.Exists(part))
                {
                    have = new FileInfo(part).Length;
                    alt.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                }
                resp = await Net.Client.SendAsync(alt,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                cb.OnDiag?.Invoke($"备用 GET 响应头 {swGet.ElapsedMilliseconds}ms");
            }
            using (resp)
            {
            if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // 本地已有完整文件
                have = new FileInfo(part).Length;
                File.Move(part, outFull, true);
            }
            else
            {
                resp.EnsureSuccessStatusCode();
                total = resp.Content.Headers.ContentLength ?? 0;
                if (total > 0 && have > 0) total += have;

                await using var fs = new FileStream(part, FileMode.Append, FileAccess.Write);
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                var buf = new byte[262144];
                var sw = Stopwatch.StartNew();
                long done = have;
                int n;
                bool firstByte = true;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    if (firstByte)
                    {
                        firstByte = false;
                        cb.OnDiag?.Invoke($"首字节 {swGet.ElapsedMilliseconds}ms（含响应头+第一批数据）");
                    }
                    // ★ 暂停闸门：在分块之间等，保留连接与已写进度
                    if (gate != null) await gate.WaitAsync(ct);
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    double speed = sw.Elapsed.TotalSeconds > 0 ? done / sw.Elapsed.TotalSeconds : 0;
                    cb.OnProgress?.Invoke(done, total, speed);
                }
                await fs.FlushAsync(ct);
            }
            }
        }
        catch (Exception e)
        {
            return new DownloadResult { Ok = false, OutPath = outFull, Error = $"下载失败：{e.Message}" };
        }

        long size = new FileInfo(part).Length;

        // ★ 原子改名
        try
        {
            if (File.Exists(outFull)) File.Delete(outFull);
            File.Move(part, outFull);
        }
        catch (Exception e)
        {
            return new DownloadResult { Ok = false, OutPath = outFull, Error = $"落盘失败：{e.Message}" };
        }

        // ★ CENC 解密
        if (!string.IsNullOrEmpty(req.ContentKey))
        {
            cb.OnPhase?.Invoke("解密");
            string dec = outFull + ".dec.mp4";
            if (!await DecryptAsync(outFull, dec, req.ContentKey, ct))
            {
                try { if (File.Exists(dec)) File.Delete(dec); } catch { }
                return new DownloadResult
                {
                    Ok = false, OutPath = outFull, Size = size,
                    Error = "解密失败（ffmpeg 调用出错）",
                };
            }
            try
            {
                if (File.Exists(outFull)) File.Delete(outFull);
                File.Move(dec, outFull, true);
            }
            catch { }
            size = new FileInfo(outFull).Length;
        }

        // ★ 时长校验
        cb.OnPhase?.Invoke("校验");
        double dur = Util.ProbeDuration(outFull);
        if (dur <= 0)
            return new DownloadResult
            {
                Ok = false, OutPath = outFull, Size = size,
                Error = "成品无法解析（可能损坏）",
            };
        if (req.ExpectDuration > 0)
        {
            double tol = Math.Max(3.0, req.ExpectDuration * 0.02);
            if (Math.Abs(dur - req.ExpectDuration) > tol)
                return new DownloadResult
                {
                    Ok = false, OutPath = outFull, Size = size, Duration = dur,
                    Error = $"时长不符（标称 {req.ExpectDuration:0.0}s，实测 {dur:0.0}s）",
                };
        }

        return new DownloadResult
        {
            Ok = true, OutPath = outFull, Size = size, Duration = dur,
        };
    }

    /// <summary>用 ffmpeg 解密 CENC 流。</summary>
    public static async Task<bool> DecryptAsync(
        string src, string dst, string keyHex, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(ToolPaths.Ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in new[]
            {
                "-y", "-v", "error",
                "-decryption_key", keyHex,
                "-i", src, "-c", "copy", "-movflags", "+faststart", dst,
            }) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0 && File.Exists(dst) && new FileInfo(dst).Length > 0;
        }
        catch { return false; }
    }
}
