using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Hgvd.Xqd.Engine;

/// <summary>清单种类（与移植前一致）。</summary>
internal enum ManifestKind
{
    Media,
    Master,
    Drm,
    Direct,
}

internal sealed class Segment
{
    public string Url { get; set; } = "";
    public double Duration { get; set; }

    public Segment() { }
    public Segment(string url, double duration) { Url = url; Duration = duration; }
}

internal sealed class ManifestInfo
{
    public List<Segment> Segments { get; set; } = new List<Segment>();
    public ManifestKind Kind { get; set; } = ManifestKind.Media;
    public string Note { get; set; } = "";
    public double TotalDuration { get; set; }
    public string SourceUrl { get; set; } = "";
}

/// <summary>
/// HLS 清单解析（移植自兴趣岛下载内核，规则已实测，不改）：
///   含 EXT-X-KEY        → drm   ：找同目录明文孪生，必须做时长校验
///   含 EXT-X-STREAM-INF → master：按短边 min(w,h) 选流（这批课多为竖屏）
///   其余                → media ：直接下分片
///   正文不是 #EXTM3U     → direct：直链媒体（音频课）
///
/// ★ DRM 孪生请求必须去掉 encdomain=cmv1，否则 403；
///   sign 只绑目录与 t、不绑文件名 ⇒ 同目录别的文件复用同一个 sign。
/// </summary>
internal static class ManifestParser
{
    private static readonly byte[] ExtM3U = Encoding.ASCII.GetBytes("#EXTM3U");
    private static readonly Regex ExtinfTotalRe = new(@"#EXTINF:([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex DurRe = new(@"([0-9.]+)", RegexOptions.Compiled);
    private static readonly Regex ResRe = new(@"RESOLUTION=([0-9]+x[0-9]+)", RegexOptions.Compiled);
    private static readonly Regex ShortSideRe = new(@"(\d+)x(\d+)", RegexOptions.Compiled);
    private static readonly Regex VideoNumRe = new(@"video_(\d+)_", RegexOptions.Compiled);
    private static readonly Regex TwinNameRe = new(@"v\.f(\d+)\.m3u8", RegexOptions.Compiled);

    private sealed class Variant
    {
        public string Res = "";
        public string Url = "";
        public int Short;
    }

    public static async Task<ManifestInfo> ParseAsync(string url, string quality, CancellationToken ct)
    {
        byte[] body;
        try { body = await XqdNet.GetBytesAsync(url, 30, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { return Failed("清单拉取失败 (" + e.Message + ")"); }

        if (!StartsWithExtM3U(body))
        {
            var info = new ManifestInfo { Kind = ManifestKind.Direct, Note = "直链媒体", SourceUrl = url };
            info.Segments.Add(new Segment(url, 0));
            return info;
        }

        string text = Encoding.UTF8.GetString(body);

        if (text.IndexOf("EXT-X-STREAM-INF", StringComparison.Ordinal) >= 0)
            return await ParseMasterAsync(text, url, quality, ct).ConfigureAwait(false);

        if (text.IndexOf("#EXT-X-KEY", StringComparison.Ordinal) >= 0)
            return await ParseDrmAsync(text, url, ct).ConfigureAwait(false);

        return Parsed(ParseMediaManifest(text, url), ManifestKind.Media, "标准 HLS", url);
    }

    private static async Task<ManifestInfo> ParseMasterAsync(string text, string url,
                                                             string quality, CancellationToken ct)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).ToList();
        var variants = new List<Variant>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)) continue;
            var m = ResRe.Match(lines[i]);
            string res = m.Success ? m.Groups[1].Value : "";
            for (int j = i + 1; j < lines.Count; j++)
            {
                string refLine = lines[j];
                if (refLine.Length > 0 && !refLine.StartsWith("#", StringComparison.Ordinal))
                {
                    variants.Add(new Variant { Res = res, Url = refLine, Short = ShortSide(res) });
                    break;
                }
            }
        }
        if (variants.Count == 0) return Failed("master 清单没有可用码率");

        variants = variants.OrderBy(v => v.Short).ToList();

        string q = string.IsNullOrEmpty(quality) ? "max" : quality;
        Variant pick;
        if (string.Equals(q, "max", StringComparison.OrdinalIgnoreCase))
        {
            pick = variants[variants.Count - 1];
        }
        else
        {
            int want = int.TryParse(q, NumberStyles.Any, CultureInfo.InvariantCulture, out var w) ? w : 720;
            pick = variants[variants.Count - 1];
            foreach (var v in variants)
                if (v.Short >= want) { pick = v; break; }
        }

        string directory = XqdUtil.DirOf(url);
        string src = pick.Url;
        if (!src.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            if (src.IndexOf('?') < 0 && url.IndexOf('?') >= 0)
                src += url.Substring(url.IndexOf('?'));
            src = directory + src;
        }

        byte[] sub;
        try { sub = await XqdNet.GetBytesAsync(src, 30, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { return Failed("子清单拉取失败 (" + e.Message + ")"); }
        if (!StartsWithExtM3U(sub)) return Failed("子清单拉取失败 (非 m3u8)");

        return Parsed(ParseMediaManifest(Encoding.UTF8.GetString(sub), src),
                      ManifestKind.Master, "多码率 " + pick.Res, src);
    }

    private static async Task<ManifestInfo> ParseDrmAsync(string text, string url, CancellationToken ct)
    {
        string num = ExtractTwinNum(url) ?? ExtractTwinNum(text);
        if (num == null) return Failed("无法从 URL 推断孪生编号");

        var (path, query) = XqdUtil.SplitQuery(url);
        int slash = path.LastIndexOf('/');
        if (slash < 0) return Failed("URL 目录层级不足");
        string directory = path.Substring(0, slash + 1);

        string twin = directory + "v.f" + num + ".m3u8" + XqdUtil.QueryWithout(query, "encdomain");

        byte[] body;
        try { body = await XqdNet.GetBytesAsync(twin, 30, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { return Failed("明文孪生拉取失败 (" + e.Message + ")"); }
        if (!StartsWithExtM3U(body)) return Failed("明文孪生拉取失败 (非 m3u8)");

        string t = Encoding.UTF8.GetString(body);
        if (t.IndexOf("EXT-X-KEY", StringComparison.Ordinal) >= 0)
            return Failed("明文孪生仍是加密清单");

        double want = ExtinfTotal(text);
        double got = ExtinfTotal(t);
        double tol = Math.Max(1.0, want * 0.005);
        if (want > 0 && (got <= 0 || Math.Abs(got - want) > tol))
            return Failed("明文孪生时长不符：" + got.ToString("0.0", CultureInfo.InvariantCulture)
                          + "s vs 期望 " + want.ToString("0.0", CultureInfo.InvariantCulture) + "s");

        return Parsed(ParseMediaManifest(t, twin), ManifestKind.Drm, "加密清单（明文孪生）", twin);
    }

    private static List<Segment> ParseMediaManifest(string text, string baseUrl)    {
        string directory = XqdUtil.DirOf(baseUrl);
        var segs = new List<Segment>();
        double cur = 0.0;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var m = DurRe.Match(line);
                cur = m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Any,
                          CultureInfo.InvariantCulture, out var d) ? d : 0.0;
            }
            else if (line.StartsWith("#", StringComparison.Ordinal)) continue;
            else
            {
                string full = line.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                              ? line : directory + line;
                segs.Add(new Segment(full, cur));
                cur = 0.0;
            }
        }
        return segs;
    }

    private static double ExtinfTotal(string text)
    {
        double total = 0.0;
        foreach (Match m in ExtinfTotalRe.Matches(text))
            if (double.TryParse(m.Groups[1].Value, NumberStyles.Any,
                                CultureInfo.InvariantCulture, out var v))
                total += v;
        return total;
    }

    private static int ShortSide(string res)
    {
        var m = ShortSideRe.Match(res ?? "");
        if (!m.Success) return 0;
        int w = int.TryParse(m.Groups[1].Value, NumberStyles.Any,
                             CultureInfo.InvariantCulture, out var a) ? a : 0;
        int h = int.TryParse(m.Groups[2].Value, NumberStyles.Any,
                             CultureInfo.InvariantCulture, out var b) ? b : 0;
        return Math.Min(w, h);
    }

    private static string ExtractTwinNum(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = VideoNumRe.Match(s);
        if (!m.Success) m = TwinNameRe.Match(s);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool StartsWithExtM3U(byte[] b)
    {
        if (b == null || b.Length < ExtM3U.Length) return false;
        for (int i = 0; i < ExtM3U.Length; i++)
            if (b[i] != ExtM3U[i]) return false;
        return true;
    }

    private static ManifestInfo Failed(string note) => new ManifestInfo { Note = note };

    private static ManifestInfo Parsed(List<Segment> segs, ManifestKind kind,
                                       string note, string srcUrl)
    {
        double total = 0.0;
        foreach (var s in segs) total += s.Duration;
        return new ManifestInfo
        {
            Segments = segs, Kind = kind, Note = note,
            TotalDuration = total, SourceUrl = srcUrl,
        };
    }

    // ── 离线回归用的纯函数入口（不联网）──
    internal static List<Segment> ParseMediaForTest(string text, string baseUrl)
        => ParseMediaManifest(text, baseUrl);

    internal static int ShortSideForTest(string res) => ShortSide(res);

    internal static string TwinNumForTest(string s) => ExtractTwinNum(s);
}
