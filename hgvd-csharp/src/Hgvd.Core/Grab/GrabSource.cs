using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Hgvd.Core.Api;
using PbStream = Hgvd.Core.Api.Stream;

namespace Hgvd.Core.Grab;

/// <summary>一集待下载的任务（抓流产出）。</summary>
public sealed class GrabJob
{
    public string Vid = "";
    public string Url = "";
    public string BackupUrl = "";
    public string Book = "";
    public int Index;
    public string Title = "";
    public double Duration;
    public string Definition = "";
    public int Height;

    /// <summary>教学类**不加密**，所以这里永远是 false —— 不要为它准备解密分支。</summary>
    public bool Encrypted;
}

/// <summary>把抓到的 preload 响应变成下载任务，只认教学白名单。</summary>
/// <remarks>
/// ★ 判据是白名单，**不是** `mixed_video_id_map` 的键：
///   那个 `1001` 只表示「这是合集 ID」，推荐流里的宣发短片同样是 1001。
/// ★ 也不看 `encrypt_info` —— 教学类本来就不加密，用加密与否筛选会把短剧放进来。
/// </remarks>
public sealed class GrabSource
{
    private readonly Dictionary<string, WhitelistEntry> _whitelist;
    private readonly Action<GrabJob> _onJob;
    private readonly Action<string> _log;
    private readonly HashSet<string> _seen = new();

    public int Accepted { get; private set; }
    public int Rejected { get; private set; }

    public GrabSource(Dictionary<string, WhitelistEntry> whitelist,
                      Action<GrabJob> onJob, Action<string> log = null)
    {
        _whitelist = whitelist ?? new Dictionary<string, WhitelistEntry>();
        _onJob = onJob ?? (_ => { });
        _log = log ?? (_ => { });
    }

    /// <summary>处理一次抓到的请求/响应。非取流接口直接忽略。</summary>
    public void Handle(CapturedExchange ex)
    {
        var target = ex.Target ?? "";
        if (target.IndexOf("/multi_video_model/", StringComparison.Ordinal) < 0) return;

        using var doc = HttpPayload.Parse(ex.Response);
        if (doc == null)
        {
            _log($"· 取流响应解析失败：{target.Split('?')[0]}（{ex.Response.Length} 字节）");
            return;
        }

        var root = doc.RootElement;
        if (root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
            && codeEl.GetInt32() != 0)
        {
            _log($"· 取流接口返回 code={codeEl.GetInt32()}");
            return;
        }
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;

        foreach (var entry in data.EnumerateObject())
        {
            string vid = entry.Name;
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            if (!entry.Value.TryGetProperty("video_model", out var vmEl)) continue;
            if (vmEl.ValueKind != JsonValueKind.String) continue;

            PlaybackModel model;
            try
            {
                model = PlaybackModel.ParseModel(vmEl.GetString());
            }
            catch (Exception parseEx)
            {
                _log($"· {vid} 的 video_model 解析失败：{parseEx.Message}");
                continue;
            }
            if (model.Streams.Count == 0) continue;

            if (!_whitelist.TryGetValue(vid, out var ep))
            {
                Rejected++;
                continue;                       // 不在教学白名单：推荐流/短剧/别的课
            }
            if (_seen.Contains(vid)) continue;

            var best = Pick(model.Streams);
            if (best == null) continue;

            _seen.Add(vid);
            Accepted++;
            _onJob(new GrabJob
            {
                Vid = vid,
                Url = best.Url,
                BackupUrl = best.BackupUrl ?? "",
                Book = ep.Book,
                Index = ep.Index,
                Title = ep.Title,
                Duration = best.Duration > 0 ? best.Duration : ep.Duration,
                Definition = best.Definition ?? "",
                Height = best.Height,
                Encrypted = best.Encrypted,
            });
            _log($"＋ 抓到《{ep.Book}》{ep.Index}. {Trim(ep.Title)} "
                 + $"{best.Definition}{(best.Encrypted ? "（加密）" : "")}");
        }
    }

    private static PbStream Pick(List<PbStream> streams)
    {
        PbStream best = null;
        foreach (var s in streams)
        {
            if (string.IsNullOrEmpty(s.Url)) continue;
            if (best == null || s.Height > best.Height
                || (s.Height == best.Height && s.Bitrate > best.Bitrate))
                best = s;
        }
        return best;
    }

    private static string Trim(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= 28 ? s : s.Substring(0, 28) + "…";
    }
}
