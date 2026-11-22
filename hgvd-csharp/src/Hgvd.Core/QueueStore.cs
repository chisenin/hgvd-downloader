using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hgvd.Core;

/// <summary>清单里的一集（只存重建界面与下载所需的最小字段）。</summary>
public sealed class QueueEpisode
{
    public string Vid { get; set; } = "";
    public int Index { get; set; }
    public string Title { get; set; } = "";
    public double Duration { get; set; }
    public int VideoIdType { get; set; } = -1;
}

/// <summary>清单里的一组（一部剧）。</summary>
public sealed class QueueGroup
{
    public string SeriesId { get; set; } = "";
    public string SeriesTitle { get; set; } = "";
    /// <summary>短剧（批量下载时默认跳过；重建后不能再从收藏夹推断，必须存）。</summary>
    public bool ShortDrama { get; set; }
    public List<QueueEpisode> Episodes { get; set; } = new();
}

/// <summary>
/// 红果侧的清单读写 —— 现在只是统一清单 <c>downloads.json</c> 的一个 section。
///
/// ★ 旧的 <c>queue.json</c> 保留作一次性迁移来源：首次加载时读一次，
///   写进 downloads.json 后打 imported 标记，之后不再读（否则清空清单会被旧文件复活）。
/// ★ 对外仍是 QueueGroup / QueueEpisode 的接口，界面层不需要知道存储换过。
/// </summary>
public sealed class QueueStore
{
    public QueueStore(string path = null)
    {
        bool isDefault = string.IsNullOrEmpty(path);
        Path = isDefault ? Paths.QueuePath : path;
        string dir = System.IO.Path.GetDirectoryName(Path) ?? ".";
        ManifestPath = isDefault ? Paths.ManifestPath : System.IO.Path.Combine(dir, "downloads.json");
        _manifest = DownloadManifestStore.Shared(ManifestPath);
    }

    /// <summary>旧的 queue.json 路径（仅供迁移/排障）。</summary>
    public string Path { get; }

    /// <summary>实际主存储（两源共用）。</summary>
    public string ManifestPath { get; }

    private readonly DownloadManifestStore _manifest;

    public List<QueueGroup> Load()
    {
        var groups = _manifest.ImportIfNeeded(ManifestSource.Hongguo, () => ToManifest(LoadLegacy()));
        return FromManifest(groups);
    }

    public void Save(IEnumerable<QueueGroup> groups)
    {
        _manifest.Store(ManifestSource.Hongguo, ToManifest(groups));
    }

    public void Clear() => _manifest.Clear(ManifestSource.Hongguo);

    /// <summary>只读旧的 queue.json；坏文件/不存在都返回空（不能影响启动）。</summary>
    private List<QueueGroup> LoadLegacy()
    {
        try
        {
            if (!File.Exists(Path)) return new List<QueueGroup>();
            var list = JsonSerializer.Deserialize<List<QueueGroup>>(File.ReadAllText(Path), Opts);
            if (list == null) return new List<QueueGroup>();
            return list
                .Where(g => g != null && !string.IsNullOrEmpty(g.SeriesId) && g.Episodes.Count > 0)
                .ToList();
        }
        catch { return new List<QueueGroup>(); }
    }

    private static List<DownloadManifestGroup> ToManifest(IEnumerable<QueueGroup> groups)
    {
        if (groups == null) return new List<DownloadManifestGroup>();
        return groups
            .Where(g => g != null && g.Episodes != null && g.Episodes.Count > 0)
            .Select(g => new DownloadManifestGroup
            {
                Source = ManifestSource.Hongguo,
                GroupId = g.SeriesId,
                Title = g.SeriesTitle,
                ShortDrama = g.ShortDrama,
                Items = g.Episodes.Select(e => new DownloadManifestRecord
                {
                    Id = e.Vid,
                    Seq = e.Index,
                    Title = e.Title,
                    ExpectDuration = e.Duration,
                    VideoIdType = e.VideoIdType,
                    State = JobState.Pending,
                }).ToList(),
            })
            .ToList();
    }

    private static List<QueueGroup> FromManifest(List<DownloadManifestGroup> groups)
    {
        if (groups == null) return new List<QueueGroup>();
        return groups
            .Where(g => g != null && !string.IsNullOrEmpty(g.GroupId) && g.Items.Count > 0)
            .Select(g => new QueueGroup
            {
                SeriesId = g.GroupId,
                SeriesTitle = g.Title,
                ShortDrama = g.ShortDrama,
                Episodes = g.Items.Select(r => new QueueEpisode
                {
                    Vid = r.Id,
                    Index = r.Seq,
                    Title = r.Title,
                    Duration = r.ExpectDuration,
                    VideoIdType = r.VideoIdType,
                }).ToList(),
            })
            .ToList();
    }

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
