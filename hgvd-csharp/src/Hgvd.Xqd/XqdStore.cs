using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hgvd.Core;

namespace Hgvd.Xqd;

/// <summary>
/// 兴趣岛侧的持久化 —— 清单现在写进两源共用的 <c>%USERPROFILE%\.hgv_tool\downloads.json</c>；
/// 分片缓存仍在 <c>.hgv_tool\xqd\parts\</c>。
///
/// ★ 旧的 <c>xqd\jobs.json</c> 只作一次性迁移来源：首次加载读一次，写进统一清单后打
///   imported 标记，之后不再读（否则清空队列会被旧文件复活）。
/// ★ 与参考项目原来的 <c>.xqd_tool</c> 隔离，避免两个工具互相覆盖配置。
/// </summary>
public sealed class XqdStore
{
    public XqdStore(string root = null)
    {
        bool isDefault = string.IsNullOrEmpty(root);
        Root = isDefault ? Path.Combine(Paths.ToolDir, "xqd") : root;

        // ★ 清单放在 xqd 的**上一级**（默认就是 .hgv_tool\downloads.json）：
        //   与红果 QueueStore 落在同一个文件，两源各写各的 section。
        string trimmed = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string parent = Path.GetDirectoryName(trimmed) ?? trimmed;
        ManifestPath = isDefault
            ? Paths.ManifestPath
            : Path.Combine(parent, "downloads.json");
        _manifest = DownloadManifestStore.Shared(ManifestPath);
    }

    public string Root { get; }
    public string PartsRoot => Path.Combine(Root, "parts");
    /// <summary>旧的 jobs.json（仅供迁移/排障）。</summary>
    public string JobsPath => Path.Combine(Root, "jobs.json");
    /// <summary>实际主存储（两源共用）。</summary>
    public string ManifestPath { get; }

    private readonly DownloadManifestStore _manifest;

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string PartsDirFor(string outPath)
    {
        string key = XqdUtil.Md5Hex16(Path.GetFullPath(outPath ?? ""));
        return Path.Combine(PartsRoot, key);
    }

    public List<XqdJob> Load()
    {
        var groups = _manifest.ImportIfNeeded(ManifestSource.Xiqudao, () => ToManifest(LoadLegacyJobs()));
        var list = FromManifest(groups);

        foreach (var j in list)
        {
            if (string.IsNullOrEmpty(j.Id)) j.Id = XqdJob.MakeId(j.Title, j.Url);
            // 上次没跑完的状态一律回到「待下载」，否则会永远显示「下载中」
            if (XqdState.IsRunning(j.State) || j.State == XqdState.Paused)
                j.State = XqdState.Pending;
        }
        // ★ 课表任务没有 URL（下载时才解析），不能按「有链接」筛掉
        return list.Where(j => !string.IsNullOrEmpty(j.Url) || j.FromCatalog).ToList();
    }

    public void Save(IEnumerable<XqdJob> jobs)
    {
        var list = jobs?.Where(j => j != null).ToList() ?? new List<XqdJob>();
        foreach (var j in list) j.AddedAt = j.AddedAt > 0 ? j.AddedAt : Now();
        _manifest.Store(ManifestSource.Xiqudao, ToManifest(list));
    }

    /// <summary>只读旧的 jobs.json；坏文件/不存在都返回空（不能影响启动）。</summary>
    private List<XqdJob> LoadLegacyJobs()
    {
        try
        {
            if (!File.Exists(JobsPath)) return new List<XqdJob>();
            var list = JsonSerializer.Deserialize<List<XqdJob>>(File.ReadAllText(JobsPath), Opts);
            if (list == null) return new List<XqdJob>();
            return list.Where(j => j != null).ToList();
        }
        catch { return new List<XqdJob>(); }
    }

    private static List<DownloadManifestGroup> ToManifest(IEnumerable<XqdJob> jobs)
    {
        var groups = new List<DownloadManifestGroup>();
        var byCamp = new Dictionary<string, DownloadManifestGroup>(StringComparer.Ordinal);
        foreach (var j in jobs)
        {
            string camp = j.Camp ?? "";
            if (!byCamp.TryGetValue(camp, out var g))
            {
                g = new DownloadManifestGroup
                {
                    Source = ManifestSource.Xiqudao,
                    GroupId = camp,
                    Title = camp,
                };
                byCamp[camp] = g;
                groups.Add(g);
            }
            g.Items.Add(new DownloadManifestRecord
            {
                Id = j.Id,
                Seq = j.Seq,
                Title = j.Title,
                ExpectDuration = j.ExpectDuration,
                Url = j.Url,
                Camp = j.Camp ?? "",
                CampId = j.CampId ?? "",
                PeriodId = j.PeriodId ?? "",
                TopicId = j.TopicId ?? "",
                RoomId = j.RoomId ?? "",
                LessonType = j.LessonType ?? "",
                WhenDate = j.WhenDate ?? "",
                OutPath = j.OutPath ?? "",
                State = j.State ?? XqdState.Pending,
                Message = j.Message ?? "",
                Size = j.Size,
                Duration = j.Duration,
                AddedAt = j.AddedAt,
            });
        }
        return groups;
    }

    private static List<XqdJob> FromManifest(List<DownloadManifestGroup> groups)
    {
        var list = new List<XqdJob>();
        if (groups == null) return list;
        foreach (var g in groups)
        {
            if (g == null) continue;
            foreach (var r in g.Items)
            {
                if (r == null) continue;
                list.Add(new XqdJob
                {
                    Id = r.Id,
                    Title = r.Title,
                    Url = r.Url,
                    Camp = string.IsNullOrEmpty(r.Camp) ? g.Title : r.Camp,
                    CampId = r.CampId,
                    PeriodId = r.PeriodId,
                    TopicId = r.TopicId,
                    RoomId = r.RoomId,
                    LessonType = r.LessonType,
                    Seq = r.Seq,
                    WhenDate = r.WhenDate,
                    OutPath = r.OutPath,
                    ExpectDuration = r.ExpectDuration,
                    State = r.State,
                    Message = r.Message,
                    Size = r.Size,
                    Duration = r.Duration,
                    AddedAt = r.AddedAt,
                });
            }
        }
        return list;
    }

    /// <summary>分片缓存占用（字节），给界面显示「缓存 x MB」。</summary>
    public long PartsBytes()
    {
        try
        {
            if (!Directory.Exists(PartsRoot)) return 0;
            long sum = 0;
            foreach (string f in Directory.EnumerateFiles(PartsRoot, "*", SearchOption.AllDirectories))
            {
                try { sum += new FileInfo(f).Length; } catch { }
            }
            return sum;
        }
        catch { return 0; }
    }

    /// <summary>清掉某个成品对应的分片缓存。</summary>
    public void PurgeParts(string outPath)
    {
        try
        {
            string dir = PartsDirFor(outPath);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { }
    }

    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}
