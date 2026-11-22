using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hgvd.Core;

/// <summary>清单来源标识（落盘用固定小写串，别用中文）。</summary>
public static class ManifestSource
{
    public const string Hongguo = "hongguo";
    public const string Xiqudao = "xiqudao";
}

/// <summary>
/// 统一清单里的一条任务 —— 两源共用一套字段。
/// ★ 冗余字段（CampId/TopicId/videoIdType…）都是重建任务所需的原样数据；
///   不为「省字段」做映射，否则重启后会出现「课还在、但下载解析不了」。
/// </summary>
public sealed class DownloadManifestRecord
{
    public string Id { get; set; } = "";
    public int Seq { get; set; }
    public string Title { get; set; } = "";
    public double ExpectDuration { get; set; }

    // 红果
    public int VideoIdType { get; set; } = -1;

    // 兴趣岛
    public string Url { get; set; } = "";
    public string Camp { get; set; } = "";
    public string CampId { get; set; } = "";
    public string PeriodId { get; set; } = "";
    public string TopicId { get; set; } = "";
    public string RoomId { get; set; } = "";
    public string LessonType { get; set; } = "";
    public string WhenDate { get; set; } = "";
    public string OutPath { get; set; } = "";
    public string State { get; set; } = JobState.Pending;
    public string Message { get; set; } = "";
    public long Size { get; set; }
    public double Duration { get; set; }
    public double AddedAt { get; set; }
}

/// <summary>统一清单里的一组（红果一部剧 / 兴趣岛一个营 / 链接任务的无名组）。</summary>
public sealed class DownloadManifestGroup
{
    public string Source { get; set; } = ManifestSource.Hongguo;
    public string GroupId { get; set; } = "";
    public string Title { get; set; } = "";
    public bool ShortDrama { get; set; }
    public List<DownloadManifestRecord> Items { get; set; } = new();
}

/// <summary>清单文件本体。版本号留给以后迁移用，当前只认 1。</summary>
public sealed class DownloadManifest
{
    public int Version { get; set; } = 1;
    /// <summary>旧 queue.json 是否已经迁移过（迁移后旧文件不再读，避免删空后又被复活）。</summary>
    public bool HongguoImported { get; set; }
    /// <summary>旧 xqd\jobs.json 是否已经迁移过。</summary>
    public bool XiqudaoImported { get; set; }
    public List<DownloadManifestGroup> Groups { get; set; } = new();
}

/// <summary>
/// 两源共用的清单存储：<c>%USERPROFILE%\.hgv_tool\downloads.json</c>。
///
/// ★ 以前红果写 queue.json、兴趣岛写 xqd\jobs.json：同一张右栏清单，重启后靠两个
///   load/save 各管一半，删空、迁移、备份都要做两遍，迟早出现「一边恢复一边丢」。
///   现在一个文件、一次原子写、两源各管自己的 section。
/// ★ 旧文件不删：首次加载时各自迁移一次并打 imported 标记；迁移失败/文件坏了不影响启动。
/// ★ 进程内按路径共享同一个实例 —— 两个 Store 同时写同一个文件时不会互相覆盖。
/// </summary>
public sealed class DownloadManifestStore
{
    private static readonly Dictionary<string, DownloadManifestStore> Instances =
        new(StringComparer.Ordinal);
    private static readonly object InstancesLock = new();

    public static DownloadManifestStore Shared(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        lock (InstancesLock)
        {
            if (Instances.TryGetValue(full, out var found)) return found;
            var created = new DownloadManifestStore(full);
            Instances[full] = created;
            return created;
        }
    }

    private DownloadManifestStore(string path) => Path = path;

    public string Path { get; }

    private readonly object _gate = new();
    private DownloadManifest _manifest;
    private bool _loaded;

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 取某一源的清单；第一次调用且旧文件存在时，用 <paramref name="importer"/> 迁移一次。
    /// ★ 迁移在锁内、只做一次；旧文件坏了 → importer 返回空清单，仍然打标记，
    ///   不会每次启动都去啃坏文件。
    /// ★ 若清单里已经有这一源的分组但还没打 imported（例如先 Save 后 Load），
    ///   迁移会把旧文件里的分组**合并**进来，而不是整段丢掉。
    /// </summary>
    public List<DownloadManifestGroup> ImportIfNeeded(
        string source, Func<List<DownloadManifestGroup>> importer)
    {
        lock (_gate)
        {
            LoadLocked();
            if (!IsImportedLocked(source))
            {
                var imported = importer?.Invoke() ?? new List<DownloadManifestGroup>();
                MergeLocked(source, imported);
                SetImportedLocked(source, true);
                SaveLocked();
            }
            return CloneGroups(GroupsLocked(source));
        }
    }

    /// <summary>整源替换并落盘（正常的保存路径）。</summary>
    public void Store(string source, IEnumerable<DownloadManifestGroup> groups)
    {
        lock (_gate)
        {
            LoadLocked();
            ReplaceLocked(source, groups);
            // ★ 不在这里打 imported：Store 可能发生在首次 Load 之前，
            //   提前打标记会让旧 queue.json / jobs.json 永远迁不进来。
            SaveLocked();
        }
    }

    /// <summary>清掉某一源（不影响另一源）。用户显式清空，才允许压掉旧文件的迁移。</summary>
    public void Clear(string source)
    {
        lock (_gate)
        {
            LoadLocked();
            ReplaceLocked(source, Array.Empty<DownloadManifestGroup>());
            SetImportedLocked(source, true);
            SaveLocked();
        }
    }

    private void LoadLocked()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(Path))
            {
                var m = JsonSerializer.Deserialize<DownloadManifest>(File.ReadAllText(Path), Opts);
                if (m != null)
                {
                    if (m.Groups == null) m.Groups = new List<DownloadManifestGroup>();
                    foreach (var g in m.Groups)
                    {
                        if (g == null) continue;
                        if (g.Items == null) g.Items = new List<DownloadManifestRecord>();
                        if (string.IsNullOrEmpty(g.Source)) g.Source = ManifestSource.Hongguo;
                    }
                    _manifest = m;
                    return;
                }
            }
        }
        catch { /* 坏文件当成空清单，下一步用旧文件迁移兜底 */ }
        _manifest = new DownloadManifest();
    }

    private bool IsImportedLocked(string source)
        => source == ManifestSource.Hongguo ? _manifest.HongguoImported : _manifest.XiqudaoImported;

    private void SetImportedLocked(string source, bool value)
    {
        if (source == ManifestSource.Hongguo) _manifest.HongguoImported = value;
        else _manifest.XiqudaoImported = value;
    }

    private List<DownloadManifestGroup> GroupsLocked(string source)
        => _manifest.Groups.Where(g => g != null && g.Source == source).ToList();

    private void ReplaceLocked(string source, IEnumerable<DownloadManifestGroup> groups)
    {
        _manifest.Groups.RemoveAll(g => g != null && g.Source == source);
        if (groups != null)
        {
            foreach (var g in groups)
            {
                if (g == null) continue;
                g.Source = source;
                if (g.Items == null) g.Items = new List<DownloadManifestRecord>();
                _manifest.Groups.Add(g);
            }
        }
    }

    /// <summary>
    /// 把旧文件迁出来的分组合并进现有清单：同名分组按 Id 补缺，绝不覆盖现有行。
    /// ★ 兴趣岛链接任务的分组名可能为空，所以用「GroupId + 标题」配对，而不是只认 GroupId。
    /// </summary>
    private void MergeLocked(string source, IEnumerable<DownloadManifestGroup> groups)
    {
        var existing = GroupsLocked(source);
        foreach (var g in groups)
        {
            if (g == null) continue;
            g.Source = source;
            if (g.Items == null) g.Items = new List<DownloadManifestRecord>();
            var match = existing.FirstOrDefault(e =>
                string.Equals(e.GroupId, g.GroupId, StringComparison.Ordinal)
                && string.Equals(e.Title, g.Title, StringComparison.Ordinal));
            if (match == null)
            {
                _manifest.Groups.Add(g);
                existing.Add(g);
                continue;
            }
            var ids = new HashSet<string>(match.Items.Where(i => i != null).Select(i => i.Id),
                StringComparer.Ordinal);
            foreach (var it in g.Items)
            {
                if (it == null || ids.Contains(it.Id)) continue;
                match.Items.Add(it);
                ids.Add(it.Id);
            }
        }
    }

    private void SaveLocked()
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_manifest, Opts));
            File.Move(tmp, Path, true);
        }
        catch { /* 存不下不影响下载（沿用两源原来的容错口径） */ }
    }

    private static List<DownloadManifestGroup> CloneGroups(List<DownloadManifestGroup> groups)
    {
        // 清单规模是「几百条」，JSON 往返一次足够便宜；手抄 18 个字段更容易漏。
        try
        {
            return JsonSerializer.Deserialize<List<DownloadManifestGroup>>(
                JsonSerializer.Serialize(groups, Opts), Opts) ?? new List<DownloadManifestGroup>();
        }
        catch { return new List<DownloadManifestGroup>(); }
    }
}
