using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hgvd.Core.Auth;

/// <summary>
/// 从 MuMu 模拟器里提取红果 App 的**视频缓存**。
///
/// ★ 这是绕过服务端限流的可靠路径（2026-11-13 实测）：
///   App 播放过的视频会被 MediaDataLoader 缓存成**标准 MP4**：
///     /sdcard/Android/data/com.phoenix.read/cache/short/&lt;md5&gt;.mdl
///     /sdcard/Android/data/com.phoenix.read/cache/dataloader/&lt;md5&gt;.mdl
///   ★ <c>.mdl</c> 的文件头是 <c>ftypisom</c> —— 标准 MP4 容器，
///     里面是**标准 HEVC (hvc1) + AAC**，ffmpeg 直接可播。
///   ★ 每个 <c>.mdl</c> 配一个 <c>.mdlnodeconf</c>（136 字节，<c>ttmd</c> 索引），
///     含 <c>fkey</c> 缓存键，**不含明文 URL**。
///
/// ★★ 二进制文件**不能**用 <c>cat</c> 经 RPC 传输 —— 会破坏字节
///   （实测头部变成 <c>fiel</c> 而不是 <c>ftyp</c>，且被截断）。
///   必须走 <c>base64</c>，再在本地解码。
/// </summary>
public static class CacheExtract
{
    public const string Package = "com.phoenix.read";

    public static readonly string[] CacheDirs =
    {
        "/sdcard/Android/data/com.phoenix.read/cache/short",
        "/sdcard/Android/data/com.phoenix.read/cache/dataloader",
    };

    /// <summary>一条缓存记录。</summary>
    public sealed class CacheEntry
    {
        public string Name = "";
        public long Size;
        public string Mtime = "";
        public string Dir = "";
        public string RemotePath = "";
        public string LocalPath = "";
        public string Codec = "";
        public int Width;
        public int Height;
        public double Duration;
    }

    /// <summary>在模拟器里执行 shell 命令，返回 stdout。</summary>
    private static string Sh(string mumu, int index, string cmd, int timeoutMs = 300000)
    {
        var psi = new ProcessStartInfo(mumu)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in new[] { "sh", "-v", index.ToString(), "-c", cmd })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("启动 MuMuManager 失败");
        var sb = new StringBuilder();
        var err = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.Append(e.Data).Append('\n'); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) err.Append(e.Data).Append('\n'); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(); } catch { }
            throw new TimeoutException($"模拟器命令超时（{timeoutMs / 1000}s）");
        }
        if (sb.Length == 0 && err.Length > 0)
            throw new InvalidOperationException(err.ToString().Trim());
        return sb.ToString();
    }

    /// <summary>
    /// 列出 App 的视频缓存文件。
    /// ★ 解析 <c>ls -l</c> 的输出：多目录时会先打印 <c>/path:</c> 这样的目录头。
    /// </summary>
    public static List<CacheEntry> ListCache(string mumu, int instance)
    {
        string cmd = "ls -l " + string.Join(" ", CacheDirs) + " 2>/dev/null";
        string[] lines = Sh(mumu, instance, cmd).Split('\n');

        var entries = new List<CacheEntry>();
        string curDir = "";
        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            if (line.Length == 0) continue;

            // 目录头：/sdcard/.../short:
            if (line.EndsWith(":") && line.StartsWith("/"))
            {
                curDir = line.TrimEnd(':');
                continue;
            }

            var m = Regex.Match(line,
                @"^-(\S{9})\s+(\S+)\s+(\S+)\s+(\S+)\s+(\d+)\s+(\S+\s+\S+)\s+(.+)$");
            if (!m.Success) continue;

            string name = m.Groups[7].Value.Trim();
            if (!name.EndsWith(".mdl")) continue;

            string dir = string.IsNullOrEmpty(curDir) ? CacheDirs[0] : curDir;
            entries.Add(new CacheEntry
            {
                Name = name,
                Size = long.Parse(m.Groups[5].Value),
                Mtime = m.Groups[6].Value.Trim(),
                Dir = dir,
                RemotePath = $"{dir}/{name}",
            });
        }
        return entries;
    }

    /// <summary>
    /// 把模拟器里的文件拉到本机（走 base64，保证字节完整）。
    /// ★ 大文件（十几 MB）base64 会很长，但 base64 通道实测不会截断
    ///   —— 前提是**完整读完** stdout，不能提前结束进程。
    /// </summary>
    public static string Pull(string mumu, int instance, string remotePath, string dest,
        int timeoutMs = 1800000)
    {
        var psi = new ProcessStartInfo(mumu)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in new[] { "sh", "-v", instance.ToString(), "-c", $"base64 '{remotePath}'" })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var sb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) sb.Append(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(); } catch { }
            throw new TimeoutException("base64 传输超时");
        }

        string compact = Regex.Replace(sb.ToString(), @"\s+", "");
        byte[] data;
        try
        {
            data = Convert.FromBase64String(compact);
        }
        catch (FormatException e)
        {
            throw new InvalidOperationException(
                $"base64 解码失败（传输可能被截断，得 {compact.Length / 4} 字节）：{e.Message}");
        }
        if (data.Length == 0)
            throw new InvalidOperationException("传输结果为空");

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, data);
        return dest;
    }

    /// <summary>
    /// 用 ffprobe 读视频信息。
    /// ★ 用 JSON 输出，避免多条流时「同名 key 被后一条覆盖」
    ///   （实测横屏+竖屏混合的缓存里 video 和 audio 都有 duration）。
    /// </summary>
    public static Dictionary<string, object> Probe(string path)
    {
        var info = new Dictionary<string, object>();
        try
        {
            var psi = new ProcessStartInfo("ffprobe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (string a in new[] { "-v", "error", "-show_streams", "-show_format", "-of", "json", path })
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(60000);

            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(stdout) ? "{}" : stdout);
            var root = doc.RootElement;

            if (root.TryGetProperty("format", out var fmt))
            {
                if (fmt.TryGetProperty("duration", out var dur)
                    && double.TryParse(dur.ToString(), out double d))
                    info["duration"] = d;
                // ★ `size` 在 ffprobe 的 JSON 里是**字符串**（"3818515"），
                //   不是数字 —— 用 TryGetInt64 会抛 InvalidOperationException，
                //   而异常被 catch 吞掉会让整个 Probe 静默返回空字典
                //   （表现为 codec 显示 0x0）。
                if (fmt.TryGetProperty("size", out var sz)
                    && long.TryParse(sz.ToString(), out long s))
                    info["size"] = s;
            }

            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var st in streams.EnumerateArray())
                {
                    string kind = st.TryGetProperty("codec_type", out var k) ? k.ToString() : "";
                    string codec = st.TryGetProperty("codec_name", out var c) ? c.ToString() : "";
                    if (kind == "video" && !info.ContainsKey("codec_name"))
                    {
                        info["codec_name"] = codec;
                        if (st.TryGetProperty("width", out var w)
                            && int.TryParse(w.ToString(), out int wi)) info["width"] = wi;
                        if (st.TryGetProperty("height", out var h)
                            && int.TryParse(h.ToString(), out int hi)) info["height"] = hi;
                    }
                    else if (kind == "audio" && !info.ContainsKey("audio_codec"))
                    {
                        info["audio_codec"] = codec;
                    }
                }
            }
        }
        catch { }
        return info;
    }

    /// <summary>
    /// 把缓存里所有视频提取到本机并探测格式。
    /// </summary>
    /// <param name="minSize">小于这个大小的不提取（大多是碎片）。</param>
    /// <param name="limit">最多提取几个；0 = 不限。</param>
    public static List<CacheEntry> ExtractAll(
        string mumu, int instance, string outDir = null,
        long minSize = 100_000, int limit = 0, Action<string> log = null)
    {
        string dir = outDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "hgvd_cache");
        Directory.CreateDirectory(dir);

        var result = new List<CacheEntry>();
        var entries = ListCache(mumu, instance);

        // dataloader 与 short 里常有同名文件，去重
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniq = new List<CacheEntry>();
        foreach (var e in entries)
        {
            if (!seen.Add(e.Name)) continue;
            uniq.Add(e);
        }

        foreach (var e in uniq)
        {
            if (e.Size < minSize) continue;
            if (limit > 0 && result.Count >= limit) break;

            string dest = Path.Combine(dir, e.Name.Replace(".mdl", ".mp4"));
            try
            {
                Pull(mumu, instance, e.RemotePath, dest);
            }
            catch (Exception ex)
            {
                log?.Invoke($"  ! 提取失败 {e.Name}：{ex.Message}");
                continue;
            }
            if (new FileInfo(dest).Length == 0)
            {
                log?.Invoke($"  ! 提取为空 {e.Name}（RPC 传输可能截断）");
                continue;
            }

            var info = Probe(dest);
            var ce = new CacheEntry
            {
                Name = e.Name,
                Size = e.Size,
                Mtime = e.Mtime,
                Dir = e.Dir,
                RemotePath = e.RemotePath,
                LocalPath = dest,
                Codec = info.TryGetValue("codec_name", out var c) ? c?.ToString() ?? "" : "",
                Width = info.TryGetValue("width", out var w) && int.TryParse(w?.ToString(), out int wi) ? wi : 0,
                Height = info.TryGetValue("height", out var h) && int.TryParse(h?.ToString(), out int hi) ? hi : 0,
                Duration = info.TryGetValue("duration", out var d) && double.TryParse(d?.ToString(), out double dd) ? dd : 0,            };
            result.Add(ce);
            log?.Invoke($"  ✓ {e.Name} → {ce.Size} 字节  {ce.Codec} {ce.Width}x{ce.Height} {ce.Duration:F1}s");
        }
        return result;
    }
}
